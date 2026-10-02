module Digga.Discogs

open System
open System.Net
open System.Net.Http
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Digga.Domain

/// Discogs refuses page > 100 on inventories other than your own.
let maxInventoryPages = 100
let inventoryPageSize = 100

type DiscogsError =
    | NotFound
    | Http of status: HttpStatusCode * body: string
    | Network of message: string
    | Malformed of message: string



module private Json =
    let prop (e: JsonElement) (name: string) =
        match e.ValueKind with
        | JsonValueKind.Object ->
            match e.TryGetProperty name with
            | true, v when v.ValueKind <> JsonValueKind.Null -> Some v
            | _ -> None
        | _ -> None

    let str e name =
        prop e name
        |> Option.filter (fun v -> v.ValueKind = JsonValueKind.String)
        |> Option.map _.GetString()
        |> Option.defaultValue ""

    let int e name =
        prop e name
        |> Option.filter (fun v -> v.ValueKind = JsonValueKind.Number)
        |> Option.map _.GetInt32()

    let float e name =
        prop e name
        |> Option.filter (fun v -> v.ValueKind = JsonValueKind.Number)
        |> Option.map _.GetDouble()

    let arr e name =
        prop e name
        |> Option.filter (fun v -> v.ValueKind = JsonValueKind.Array)
        |> Option.map (fun v -> v.EnumerateArray() |> List.ofSeq)
        |> Option.defaultValue []

    let path e names =
        names |> List.fold (fun acc n -> acc |> Option.bind (fun x -> prop x n)) (Some e)

let parseInventoryPage (root: JsonElement) : InventoryPage =
    let pagination = Json.prop root "pagination"

    let pageInfo name =
        pagination |> Option.bind (fun p -> Json.int p name) |> Option.defaultValue 0

    let listing (l: JsonElement) =
        let r = Json.prop l "release" |> Option.defaultValue l
        let community = Json.path r [ "stats"; "community" ]
        let communityCount name =
            community |> Option.bind (fun c -> Json.int c name) |> Option.defaultValue 0
        let price = Json.prop l "price"

        { ListingId = (Json.prop l "id" |> Option.map _.GetInt64() |> Option.defaultValue 0L)
          ReleaseId = (Json.prop r "id" |> Option.map _.GetInt64() |> Option.defaultValue 0L)
          Artist = Json.str r "artist"
          Title = Json.str r "title"
          Label = Json.str r "label"
          CatalogNumber = Json.str r "catalog_number"
          Format = Json.str r "format"
          Year = Json.int r "year" |> Option.filter (fun y -> y > 0)
          Thumbnail = Json.str r "thumbnail"
          Want = communityCount "in_wantlist"
          Have = communityCount "in_collection"
          Price = price |> Option.bind (fun p -> Json.float p "value") |> Option.defaultValue 0.0
          Currency = price |> Option.map (fun p -> Json.str p "currency") |> Option.defaultValue ""
          Condition = Json.str l "condition"
          SleeveCondition = Json.str l "sleeve_condition"
          Uri = Json.str l "uri"
          Posted = Json.str l "posted" }

    { Page = pageInfo "page"
      Pages = pageInfo "pages"
      Items = pageInfo "items"
      Listings = Json.arr root "listings" |> List.map listing |> List.filter (fun l -> l.ReleaseId > 0L) }

let parseRelease (root: JsonElement) : ReleaseDetails =
    let strings name =
        Json.arr root name
        |> List.filter (fun v -> v.ValueKind = JsonValueKind.String)
        |> List.map _.GetString()

    let video (v: JsonElement) =
        let embeddable =
            Json.prop v "embed"
            |> Option.forall (fun e -> e.ValueKind <> JsonValueKind.False)

        Names.youtubeId (Json.str v "uri")
        |> Option.filter (fun _ -> embeddable)
        |> Option.map (fun id ->
            { YoutubeId = id
              Title = Json.str v "title"
              DurationSeconds = Json.int v "duration" |> Option.defaultValue 0 })

    let community name =
        Json.prop root "community" |> Option.bind (fun c -> Json.int c name) |> Option.defaultValue 0

    { Genres = strings "genres"
      Styles = strings "styles"
      Videos = Json.arr root "videos" |> List.choose video |> List.distinctBy _.YoutubeId
      Want = community "want"
      Have = community "have" }

/// When the next Discogs request may go out. The one caller (the sync worker) threads this
/// through its loop, so pacing is a value passed along rather than state hidden in a client.
type RateLimit = { NotBefore: DateTimeOffset }

module RateLimit =
    let initial = { NotBefore = DateTimeOffset.MinValue }

type Client =
    { Http: HttpClient
      Token: string option
      Logger: ILogger }

let private maxAttempts = 3

/// How long to wait after a response, from the X-Discogs-Ratelimit headers
/// (60/min with a token, 25/min without).
let pacing (limit: int option) (remaining: int option) : TimeSpan * string =
    match limit, remaining with
    | _, Some r when r <= 1 -> TimeSpan.FromSeconds 60.0, "window exhausted"
    | Some l, _ when l > 0 -> TimeSpan.FromSeconds(60.0 / float l), "steady pacing"
    | _ -> TimeSpan.FromSeconds 2.5, "no rate-limit headers; assuming 25/min"

let private headerInt (response: HttpResponseMessage) (name: string) =
    match response.Headers.TryGetValues name with
    | true, values ->
        values
        |> Seq.tryHead
        |> Option.bind (fun v ->
            match Int32.TryParse v with
            | true, n -> Some n
            | _ -> None)
    | _ -> None

let rec private getJson
    (client: Client)
    (rate: RateLimit)
    (path: string)
    (attempt: int)
    (ct: CancellationToken)
    : Task<RateLimit * Result<JsonDocument, DiscogsError>> =
    task {
        let delay = rate.NotBefore - DateTimeOffset.UtcNow
        if delay > TimeSpan.Zero then
            do! Task.Delay(delay, ct)

        use request = new HttpRequestMessage(HttpMethod.Get, path)
        client.Token
        |> Option.iter (fun t -> request.Headers.TryAddWithoutValidation("Authorization", $"Discogs token={t}") |> ignore)
        let started = DateTimeOffset.UtcNow

        let! sent =
            task {
                try
                    let! r = client.Http.SendAsync(request, ct)
                    return Ok r
                with ex when not (ex :? OperationCanceledException) ->
                    return Error ex.Message
            }

        match sent with
        | Error message ->
            client.Logger.LogWarning("Discogs GET {Path} failed before a response: {Error}; pausing 5s", path, message)
            return { NotBefore = DateTimeOffset.UtcNow.AddSeconds 5.0 }, Error(Network message)
        | Ok response ->
            use response = response
            let remaining = headerInt response "X-Discogs-Ratelimit-Remaining"
            let wait, reason = pacing (headerInt response "X-Discogs-Ratelimit") remaining
            // The limit counts requests per minute, so space requests from when each was *sent*.
            // Measuring from the response instead would add the 1–4s latency to every gap.
            let next = { NotBefore = started + wait }

            client.Logger.LogDebug(
                "Discogs GET {Path} -> {Status} in {ElapsedMs}ms; ratelimit remaining {Remaining}; next request in {WaitMs}ms ({PacingReason})",
                path, int response.StatusCode, (DateTimeOffset.UtcNow - started).TotalMilliseconds,
                Option.toNullable remaining, wait.TotalMilliseconds, reason)

            match response.StatusCode with
            | HttpStatusCode.OK ->
                let! body = response.Content.ReadAsStringAsync ct
                try
                    return next, Ok(JsonDocument.Parse body)
                with :? JsonException as ex ->
                    return next, Error(Malformed ex.Message)
            | HttpStatusCode.NotFound -> return next, Error NotFound
            | HttpStatusCode.TooManyRequests when attempt < maxAttempts ->
                client.Logger.LogWarning(
                    "Discogs 429 on {Path}, attempt {Attempt}/{MaxAttempts}; backing off 60s", path, attempt, maxAttempts)
                return! getJson client { NotBefore = DateTimeOffset.UtcNow.AddSeconds 60.0 } path (attempt + 1) ct
            | status ->
                let! body = response.Content.ReadAsStringAsync ct
                return next, Error(Http(status, body))
    }

let private getParsed client rate path parse ct =
    task {
        let! next, result = getJson client rate path 1 ct
        return next, result |> Result.map (fun doc -> using doc (fun d -> parse d.RootElement))
    }

let inventoryPage client rate (seller: string) (page: int) ct =
    getParsed
        client
        rate
        $"users/{Uri.EscapeDataString seller}/inventory?status=For%%20Sale&sort=listed&sort_order=desc&per_page={inventoryPageSize}&page={page}"
        parseInventoryPage
        ct

let release client rate (releaseId: int64) ct =
    getParsed client rate $"releases/{releaseId}" parseRelease ct

/// Whether retrying the same request could succeed.
let isPermanent =
    function
    | NotFound -> true
    | Http(status, _) -> int status >= 400 && int status < 500
    | Network _
    | Malformed _ -> false

let describe =
    function
    | NotFound -> "not found"
    | Http(status, body) -> $"HTTP {int status}: {body}"
    | Network message -> $"network: {message}"
    | Malformed message -> $"malformed JSON: {message}"

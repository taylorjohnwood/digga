module Digga.Api

// Giraffe's routef infers its PrintfFormat's unused type arguments as obj; FS3559 is noise there.
#nowarn "3559"

open System
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading.Channels
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open Giraffe

type Env =
    { Db: Db.Paths
      Requests: ChannelWriter<Worker.Request>
      /// The worker's latest schedule: which sellers are syncing right now.
      Schedule: unit -> Scheduler.State
      ListenBrainz: ListenBrainz.Client
      DiscogsAuthenticated: bool
      Logger: ILogger
      Now: unit -> DateTimeOffset }

/// YoutubeId null (it must be present: FSharp.SystemTextJson requires every field) queues the whole release, fetched first if its videos aren't known yet.
type QueueRequest = { ReleaseId: int64; YoutubeId: string option }

type RateRequest = { Verdict: Domain.Verdict }

[<CLIMutable>]
type ListenRequest =
    { ReleaseId: int64
      YoutubeId: string
      /// Unix seconds when playback started — what ListenBrainz calls listened_at.
      StartedAt: int64 }

let private withDb env f =
    use conn = Db.connect env.Db
    f conn

/// Discogs usernames: letters, digits, '.', '_' and '-'.
let validSeller (s: string) =
    match s |> Option.ofObj |> Option.map _.Trim() with
    | Some u when Regex.IsMatch(u, @"^[A-Za-z0-9._-]{1,64}$") -> Ok u
    | _ -> Error "Seller must be a Discogs username (letters, digits, '.', '_' or '-')."

let private badRequest (message: string) = RequestErrors.BAD_REQUEST {| error = message |}

let private config env : HttpHandler =
    fun next ctx ->
        json
            {| discogsAuthenticated = env.DiscogsAuthenticated
               listenBrainzConfigured = env.ListenBrainz.Token.IsSome
               catalog = Catalog.summary env.Db.Catalog |}
            next
            ctx

type SellerView =
    { Seller: string
      /// The last recorded run; None for a seller whose first sync hasn't started yet.
      LatestRun: Domain.SyncRun option
      Listings: int
      Releases: int
      GenresUnknown: int
      /// In the worker's rotation right now.
      Syncing: bool }

/// Recorded history from the database, merged with what the worker is doing this moment.
let sellerViews (recorded: Db.SellerSummary list) (schedule: Scheduler.State) : SellerView list =
    let fromDb =
        recorded
        |> List.map (fun s ->
            { Seller = s.Seller
              LatestRun = Some s.LatestRun
              Listings = s.Listings
              Releases = s.Releases
              GenresUnknown = s.GenresUnknown
              Syncing = Scheduler.isSyncing s.Seller schedule })

    let notYetStarted =
        schedule.Syncs
        |> List.filter (fun a -> not (recorded |> List.exists (fun r -> String.Equals(r.Seller, a.Seller, StringComparison.OrdinalIgnoreCase))))
        |> List.map (fun a ->
            { Seller = a.Seller
              LatestRun = None
              Listings = 0
              Releases = 0
              GenresUnknown = 0
              Syncing = true })

    notYetStarted @ fromDb

let private sellers env : HttpHandler =
    fun next ctx -> json (sellerViews (withDb env Db.sellers) (env.Schedule())) next ctx

let private requestSync env (seller: string) : HttpHandler =
    match validSeller seller with
    | Error e -> badRequest e
    | Ok s ->
        let alreadySyncing = Scheduler.isSyncing s (env.Schedule())
        env.Requests.TryWrite(Worker.Sync s) |> ignore
        env.Logger.LogInformation("Sync of {Seller} requested; already syncing {AlreadySyncing}", s, alreadySyncing)
        Successful.ACCEPTED {| seller = s; alreadySyncing = alreadySyncing |}

/// Cancels the seller's sync if one is running and deletes what's been synced for it. Waits for
/// the worker, which finishes its current request first (normally a few seconds).
let private forgetSeller env (seller: string) : HttpHandler =
    fun next ctx ->
        task {
            match validSeller seller with
            | Error e -> return! badRequest e next ctx
            | Ok s ->
                let wasSyncing = Scheduler.isSyncing s (env.Schedule())
                let reply = TaskCompletionSource<Db.ForgetSummary>(TaskCreationOptions.RunContinuationsAsynchronously)
                env.Requests.TryWrite(Worker.Forget(s, reply)) |> ignore
                env.Logger.LogInformation("Forget {Seller} requested; was syncing {WasSyncing}", s, wasSyncing)

                // Worst case the worker is sitting out a full rate-limit minute before its next request.
                let! summary = reply.Task.WaitAsync(TimeSpan.FromMinutes 2.0, ctx.RequestAborted)
                return! json {| seller = s; wasSyncing = wasSyncing; deleted = summary |} next ctx
        }

let private queryString (ctx: HttpContext) (name: string) =
    ctx.TryGetQueryStringValue name |> Option.filter (String.IsNullOrWhiteSpace >> not)

let private queryInt (ctx: HttpContext) name fallback low high =
    queryString ctx name
    |> Option.bind (fun v ->
        match Int32.TryParse v with
        | true, n -> Some n
        | _ -> None)
    |> Option.defaultValue fallback
    |> max low
    |> min high

/// `tags` is the query's groups as JSON, e.g. [["Deep House","House"],["Techno"]]: tag names
/// contain commas and ampersands, so no delimiter is safe. The last group is the one being built
/// and may be empty, meaning a new OR group has just been started.
let private tagGroups (ctx: HttpContext) : Result<string list list, string> =
    match queryString ctx "tags" with
    | None -> Ok []
    | Some raw ->
        try
            match JsonSerializer.Deserialize<string array array> raw with
            | null -> Error "tags must be a JSON array of arrays of tag names"
            | groups when groups |> Array.exists (fun g -> isNull g || g |> Array.exists String.IsNullOrWhiteSpace) ->
                Error "tags must not contain null groups or blank tag names"
            | groups -> groups |> Array.map List.ofArray |> List.ofArray |> Ok
        with :? JsonException as ex ->
            Error $"tags must be a JSON array of arrays of tag names: {ex.Message}"

let private releases env (seller: string) : HttpHandler =
    fun next ctx ->
        match tagGroups ctx with
        | Error e ->
            env.Logger.LogDebug("Rejected tag query for {Seller}: {Error}", seller, e)
            badRequest e next ctx
        | Ok groups ->
            let filter: Ranking.Filter =
                { Tags = groups |> List.filter (List.isEmpty >> not)
                  HideHeard = queryString ctx "hideHeard" = Some "true"
                  HideRated = queryString ctx "hideRated" = Some "true" }

            let building = groups |> List.tryLast |> Option.defaultValue []

            let offset = queryInt ctx "offset" 0 0 Int32.MaxValue
            let limit = queryInt ctx "limit" 50 1 200

            let page =
                withDb env (fun c ->
                    let prior = Score.priorRatio (Db.listedReleaseStats c)
                    Db.sellerReleases c seller |> Ranking.page prior filter building offset limit)

            let needDetails =
                page.Items |> List.map _.Release |> List.filter (fun r -> r.DetailsStatus = Domain.Pending) |> List.map _.ReleaseId

            if not needDetails.IsEmpty then
                env.Requests.TryWrite(Worker.FetchDetails needDetails) |> ignore

            env.Logger.LogDebug(
                "Ranked {Seller} with {Filter} building {Building} offset {Offset} limit {Limit}: {Matching}/{Total} match, {GenresUnknown} genres unknown, prior {Prior}; requested details for {DetailsRequested}",
                seller, filter, building, offset, limit, page.Matching, page.Total, page.GenresUnknown, page.PriorRatio, needDetails.Length)

            json page next ctx

let private queue env : HttpHandler =
    fun next ctx ->
        let entries = withDb env Db.queue

        // Re-asked on every read, like viewed pages: a fetch that failed transiently is retried
        // while the queue is on screen, without the worker keeping its own retry list.
        let awaiting =
            entries
            |> List.choose (fun e ->
                match e.Item with
                | Db.AwaitingVideos -> Some e.Release.ReleaseId
                | Db.Track _
                | Db.NoVideos -> None)

        if not awaiting.IsEmpty then
            env.Requests.TryWrite(Worker.FetchQueued awaiting) |> ignore

        env.Logger.LogDebug("Queue read: {Count} entries, {Awaiting} releases awaiting videos {AwaitingIds}", entries.Length, awaiting.Length, awaiting)
        json entries next ctx

let private enqueue env : HttpHandler =
    fun next ctx ->
        task {
            let! req = ctx.BindJsonAsync<QueueRequest>()

            match req.YoutubeId with
            | Some youtubeId ->
                let outcome = withDb env (fun c -> Db.enqueue c req.ReleaseId youtubeId (env.Now()))
                env.Logger.LogInformation("Enqueue {YoutubeId} from release {ReleaseId}: {Outcome}", youtubeId, req.ReleaseId, outcome)

                return!
                    match outcome with
                    | Db.Added -> Successful.CREATED {| queued = true |} next ctx
                    | Db.AlreadyQueued -> Successful.OK {| queued = true |} next ctx
                    | Db.UnknownVideo -> RequestErrors.NOT_FOUND {| error = "No such video on that release." |} next ctx
            | None ->
                let outcome = withDb env (fun c -> Db.enqueueRelease c req.ReleaseId (env.Now()))
                env.Logger.LogInformation("Enqueue whole release {ReleaseId}: {Outcome}", req.ReleaseId, outcome)

                return!
                    match outcome with
                    | Db.QueuedTracks _ -> Successful.OK {| queued = true |} next ctx
                    | Db.QueuedAwaitingVideos _ ->
                        env.Requests.TryWrite(Worker.FetchQueued [ req.ReleaseId ]) |> ignore
                        Successful.ACCEPTED {| queued = true |} next ctx
                    | Db.NothingToPlay ->
                        RequestErrors.CONFLICT {| error = "Discogs has no videos for that release." |} next ctx
                    | Db.UnknownRelease -> RequestErrors.NOT_FOUND {| error = "No such release." |} next ctx
        }

let private dequeue env (queueItemId: int64) : HttpHandler =
    let removed = withDb env (fun c -> Db.dequeue c queueItemId)
    env.Logger.LogInformation("Dequeue item {QueueItemId}: removed {Removed}", queueItemId, removed)
    Successful.NO_CONTENT

let private rate env (releaseId: int64) : HttpHandler =
    fun next ctx ->
        task {
            let! req = ctx.BindJsonAsync<RateRequest>()
            let outcome = withDb env (fun c -> Db.rate c releaseId req.Verdict (env.Now()))
            env.Logger.LogInformation("Rate release {ReleaseId} {Verdict}: {Outcome}", releaseId, req.Verdict, outcome)

            return!
                match outcome with
                | Db.Rated -> Successful.OK {| rating = req.Verdict |} next ctx
                | Db.NoSuchRelease -> RequestErrors.NOT_FOUND {| error = "No such release." |} next ctx
        }

let private unrate env (releaseId: int64) : HttpHandler =
    let removed = withDb env (fun c -> Db.unrate c releaseId)
    env.Logger.LogInformation("Unrate release {ReleaseId}: removed {Removed}", releaseId, removed)
    Successful.NO_CONTENT

let private listen env : HttpHandler =
    fun next ctx ->
        task {
            let! req = ctx.BindJsonAsync<ListenRequest>()

            match withDb env (fun c -> Db.listenSubject c req.ReleaseId req.YoutubeId) with
            | None -> return! RequestErrors.NOT_FOUND {| error = "No such video on that release." |} next ctx
            | Some subject ->
                let startedAt = DateTimeOffset.FromUnixTimeSeconds req.StartedAt
                let! outcome = ListenBrainz.submit env.ListenBrainz subject startedAt ctx.RequestAborted
                let listenId = withDb env (fun c -> Db.recordListen c subject startedAt outcome)
                env.Logger.LogInformation(
                    "Listen {ListenId}: {YoutubeId} from release {ReleaseId} started {StartedAt}; ListenBrainz {Outcome}",
                    listenId, subject.YoutubeId, subject.ReleaseId, startedAt, outcome)
                return! Successful.CREATED {| listenId = listenId; listenBrainz = outcome |} next ctx
        }

let routes env : HttpHandler =
    choose
        [ GET >=> route "/api/config" >=> config env
          GET >=> route "/api/sellers" >=> sellers env
          POST >=> routef "/api/sellers/%s/sync" (requestSync env)
          DELETE >=> routef "/api/sellers/%s" (forgetSeller env)
          GET >=> routef "/api/sellers/%s/releases" (releases env)
          GET >=> route "/api/queue" >=> queue env
          POST >=> route "/api/queue" >=> enqueue env
          DELETE >=> routef "/api/queue/%d" (dequeue env)
          POST >=> route "/api/listens" >=> listen env
          PUT >=> routef "/api/releases/%d/rating" (rate env)
          DELETE >=> routef "/api/releases/%d/rating" (unrate env)
          RequestErrors.NOT_FOUND {| error = "Not found" |} ]

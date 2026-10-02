module Digga.Program

open System
open System.IO
open System.Net.Http
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Channels
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Giraffe

let private env (name: string) =
    Environment.GetEnvironmentVariable name |> Option.ofObj |> Option.filter (String.IsNullOrWhiteSpace >> not)

let private httpClient (baseAddress: string option) =
    let http = new HttpClient()
    baseAddress |> Option.iter (fun a -> http.BaseAddress <- Uri a)
    http.DefaultRequestHeaders.UserAgent.ParseAdd "digga/0.1 (+personal record-digging tool)"
    http.Timeout <- TimeSpan.FromSeconds 30.0
    http

/// API JSON: camelCase, and unions as { "kind": "Case", ...namedFields } so the React side can
/// switch on `kind` like a TypeScript discriminated union.
let private serializer =
    let fsharp =
        JsonFSharpOptions
            .Default()
            .WithUnionInternalTag()
            .WithUnionNamedFields()
            .WithUnionTagName("kind")

    let options = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
    Json.FsharpFriendlySerializer(fsharp, options)

/// The app database and the catalog live side by side in data/ unless overridden.
let private dbPaths (contentRoot: string) : Db.Paths =
    let resolve variable file =
        env variable
        |> Option.defaultValue (Path.Combine(contentRoot, "..", "data", file))
        |> Path.GetFullPath

    let paths: Db.Paths = { App = resolve "DIGGA_DB" "digga.db"; Catalog = resolve "DIGGA_CATALOG" "catalog.db" }
    Directory.CreateDirectory(Path.GetDirectoryName paths.App) |> ignore
    Directory.CreateDirectory(Path.GetDirectoryName paths.Catalog) |> ignore
    paths

/// `import-catalog [latest | <url> | <path.xml.gz>]`: rebuild catalog.db from a Discogs data export.
let private importCatalog (paths: Db.Paths) (source: string) =
    use loggers = LoggerFactory.Create(fun b -> b.AddSimpleConsole(fun o -> o.SingleLine <- true).SetMinimumLevel LogLevel.Information |> ignore)
    let logger = loggers.CreateLogger "Digga.Catalog"
    use http = httpClient None
    http.Timeout <- Threading.Timeout.InfiniteTimeSpan

    let resolved =
        match source with
        | "latest" -> Catalog.latestDumpUrl http DateTimeOffset.UtcNow |> _.Result
        | s -> Some s

    match resolved with
    | None ->
        logger.LogError("No releases dump found on data.discogs.com")
        1
    | Some url ->
        logger.LogInformation("Importing {Source} into {Catalog}; streamed, nothing saved but the catalog", url, paths.Catalog)
        use xml = (Catalog.openSource http url).Result
        let summary = Catalog.import logger xml (Catalog.dumpName url) paths.Catalog (fun () -> DateTimeOffset.UtcNow)
        logger.LogInformation("Imported {Releases} releases from {Dump}", summary.Releases, summary.Dump)
        0

let private serve (args: string array) =
    let builder = WebApplication.CreateBuilder args
    let paths = dbPaths builder.Environment.ContentRootPath
    Catalog.ensure paths.Catalog

    let discogsToken = env "DISCOGS_TOKEN"
    let listenBrainzToken = env "LISTENBRAINZ_TOKEN"
    let requests = Channel.CreateUnbounded<Worker.Request>()
    let schedule = ref Scheduler.empty
    let now () = DateTimeOffset.UtcNow
    let discogsHttp = httpClient (Some "https://api.discogs.com/")
    let listenBrainzHttp = httpClient None

    builder.WebHost.UseUrls(env "DIGGA_URLS" |> Option.defaultValue "http://localhost:5080") |> ignore
    builder.Services.AddGiraffe() |> ignore
    builder.Services.AddSingleton<Json.ISerializer>(serializer) |> ignore

    builder.Services.AddHostedService(fun sp ->
        let loggers = sp.GetRequiredService<ILoggerFactory>()

        new Worker.SyncService(
            { Discogs =
                { Http = discogsHttp
                  Token = discogsToken
                  Logger = loggers.CreateLogger "Digga.Discogs" }
              Db = paths
              Logger = loggers.CreateLogger "Digga.Worker"
              Requests = requests.Reader
              Published = schedule
              Now = now }))
    |> ignore

    let app = builder.Build()
    let loggers = app.Services.GetRequiredService<ILoggerFactory>()
    let logger = loggers.CreateLogger "Digga.Program"

    using (Db.connect paths) (fun conn ->
        Db.migrate conn
        let interrupted = Db.failInterruptedRuns conn (now ())
        logger.LogInformation("Database {DbPath} ready; closed {Interrupted} sync runs interrupted by a previous shutdown", paths.App, interrupted))

    match Catalog.summary paths.Catalog with
    | Some c -> logger.LogInformation("Catalog {Dump}: {Releases} releases, imported {ImportedAt}", c.Dump, c.Releases, c.ImportedAt)
    | None ->
        logger.LogWarning(
            "Catalog {Catalog} is empty: genres come from the API one release at a time. Run `dotnet run -- import-catalog latest`",
            paths.Catalog)

    logger.LogInformation(
        "Discogs token {DiscogsConfigured}; ListenBrainz token {ListenBrainzConfigured}",
        (if discogsToken.IsSome then "set" else "NOT set"),
        (if listenBrainzToken.IsSome then "set" else "NOT set"))

    app.UseGiraffe(
        Api.routes
            { Db = paths
              Requests = requests.Writer
              Schedule = fun () -> schedule.Value
              ListenBrainz =
                { Http = listenBrainzHttp
                  Token = listenBrainzToken
                  Logger = loggers.CreateLogger "Digga.ListenBrainz" }
              DiscogsAuthenticated = discogsToken.IsSome
              Logger = loggers.CreateLogger "Digga.Api"
              Now = now }
    )

    app.Run()
    0

[<EntryPoint>]
let main args =
    DotNetEnv.Env.TraversePath().Load() |> ignore

    match args |> List.ofArray with
    | "import-catalog" :: rest ->
        dbPaths (Directory.GetCurrentDirectory()) |> fun p -> importCatalog p (List.tryHead rest |> Option.defaultValue "latest")
    | _ -> serve args

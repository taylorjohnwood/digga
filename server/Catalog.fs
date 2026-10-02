/// Genres and styles for every Discogs release, from Discogs' monthly CC0 data export
/// (https://data.discogs.com). Lives in its own SQLite file so a re-import builds a fresh copy and
/// swaps it in, never touching the app database or leaving a half-imported catalog visible.
module Digga.Catalog

open System
open System.IO
open System.IO.Compression
open System.Net.Http
open System.Text.RegularExpressions
open System.Xml
open Microsoft.Data.Sqlite
open Microsoft.Extensions.Logging

type CatalogRelease =
    { ReleaseId: int64
      Genres: string list
      Styles: string list }

type ImportSummary =
    { Dump: string
      ImportedAt: DateTimeOffset
      Releases: int64 }

let private schema =
    """
CREATE TABLE IF NOT EXISTS tag (
    id   INTEGER PRIMARY KEY,
    kind TEXT    NOT NULL CHECK (kind IN ('genre', 'style')),
    name TEXT    NOT NULL,
    UNIQUE (kind, name)
);
CREATE TABLE IF NOT EXISTS release_tag (
    release_id INTEGER NOT NULL,
    tag_id     INTEGER NOT NULL REFERENCES tag (id),
    PRIMARY KEY (release_id, tag_id)
) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS meta (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
"""

let private open' (path: string) =
    let conn = new SqliteConnection($"Data Source={path}")
    conn.Open()
    conn

let private exec (conn: SqliteConnection) (sql: string) =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- sql
    cmd.ExecuteNonQuery() |> ignore

/// An empty catalog, so the app's queries work before the first import.
let ensure (path: string) =
    use conn = open' path
    exec conn schema

let summary (path: string) : ImportSummary option =
    use conn = open' path
    use cmd = conn.CreateCommand()
    cmd.CommandText <- "SELECT key, value FROM meta"
    use r = cmd.ExecuteReader()
    let meta = Map [ while r.Read() do yield r.GetString 0, r.GetString 1 ]

    match Map.tryFind "dump" meta, Map.tryFind "imported_at" meta, Map.tryFind "releases" meta with
    | Some dump, Some at, Some n ->
        Some
            { Dump = dump
              ImportedAt = DateTimeOffset.Parse at
              Releases = int64 n }
    | _ -> None

// ---- parsing ---------------------------------------------------------------------------------

/// One <release> element's genres and styles. Everything else in it is skipped unread.
///
/// XmlReader trap: ReadElementContentAsString and Skip both leave the reader on the *next* node.
/// Calling Read() after them would step over that node, silently dropping every second
/// consecutive <genre>. `advanced` records whether the reader has already moved on.
let private readRelease (releaseId: int64) (sub: XmlReader) : CatalogRelease =
    let rec go genres styles advanced =
        match (if advanced then not sub.EOF else sub.Read()) with
        | false -> { ReleaseId = releaseId; Genres = List.rev genres; Styles = List.rev styles }
        | true ->
            match sub.NodeType, sub.Depth, sub.Name with
            | XmlNodeType.Element, 2, "genre" -> go (sub.ReadElementContentAsString() :: genres) styles true
            | XmlNodeType.Element, 2, "style" -> go genres (sub.ReadElementContentAsString() :: styles) true
            | XmlNodeType.Element, 1, ("genres" | "styles") -> go genres styles false
            | XmlNodeType.Element, 1, _ ->
                sub.Skip()
                go genres styles true
            | _ -> go genres styles false

    sub.Read() |> ignore // onto <release> itself
    go [] [] false

/// Streams releases from a (decompressed) releases.xml dump without loading it into memory.
let releases (xml: Stream) : seq<CatalogRelease> =
    seq {
        let settings =
            XmlReaderSettings(IgnoreWhitespace = true, IgnoreComments = true, DtdProcessing = DtdProcessing.Prohibit)

        use reader = XmlReader.Create(xml, settings)

        while reader.ReadToFollowing "release" do
            match Int64.TryParse(reader.GetAttribute "id") with
            | true, id ->
                use sub = reader.ReadSubtree()
                yield readRelease id sub
            | false, _ -> ()
    }

// ---- source ----------------------------------------------------------------------------------

let private dumpPattern = Regex(@"discogs_(\d{8})_releases\.xml\.gz")

/// The newest releases dump listed on data.discogs.com, as a download URL.
let latestDumpUrl (http: HttpClient) (now: DateTimeOffset) =
    task {
        let listing year =
            http.GetStringAsync($"https://data.discogs.com/?prefix=data/{year}/")

        let! thisYear = listing now.Year
        let! lastYear = listing (now.Year - 1)

        return
            dumpPattern.Matches(thisYear + lastYear)
            |> Seq.map (fun m -> m.Groups[1].Value)
            |> Seq.distinct
            |> Seq.sortDescending
            |> Seq.tryHead
            |> Option.map (fun date ->
                $"https://data.discogs.com/?download=data%%2F{date.Substring(0, 4)}%%2Fdiscogs_{date}_releases.xml.gz")
    }

/// A local .xml.gz path, or an http(s) URL streamed straight through without saving the file.
let openSource (http: HttpClient) (source: string) =
    task {
        let! raw =
            match Uri.TryCreate(source, UriKind.Absolute) with
            | true, u when u.Scheme = "http" || u.Scheme = "https" -> http.GetStreamAsync u
            | _ -> Threading.Tasks.Task.FromResult(File.OpenRead source :> Stream)

        return new GZipStream(new BufferedStream(raw, 1 <<< 20), CompressionMode.Decompress) :> Stream
    }

let dumpName (source: string) =
    dumpPattern.Match(Uri.UnescapeDataString source) |> fun m -> if m.Success then m.Value else Path.GetFileName source

// ---- import ----------------------------------------------------------------------------------

/// Builds a new catalog beside `targetPath` from `xml`, then swaps it in. Returns what was imported.
let import (logger: ILogger) (xml: Stream) (dump: string) (targetPath: string) (now: unit -> DateTimeOffset) =
    let building = targetPath + ".importing"
    File.Delete building
    let started = now ()

    let count =
        use conn = open' building
        // Bulk load into a throwaway file: durability buys nothing until the swap.
        exec conn "PRAGMA journal_mode = OFF; PRAGMA synchronous = OFF; PRAGMA cache_size = -262144;"
        exec conn schema
        exec conn "BEGIN"

        use insertTag = conn.CreateCommand()
        insertTag.CommandText <- "INSERT INTO tag (kind, name) VALUES ($kind, $name) RETURNING id"
        let tagKind = insertTag.Parameters.Add("$kind", SqliteType.Text)
        let tagName = insertTag.Parameters.Add("$name", SqliteType.Text)

        use insertReleaseTag = conn.CreateCommand()
        insertReleaseTag.CommandText <- "INSERT OR IGNORE INTO release_tag (release_id, tag_id) VALUES ($release, $tag)"
        let rtRelease = insertReleaseTag.Parameters.Add("$release", SqliteType.Integer)
        let rtTag = insertReleaseTag.Parameters.Add("$tag", SqliteType.Integer)

        /// Tag ids, threaded through the fold: the few hundred genres/styles get an id on first sight.
        let tagId (ids: Map<string * string, int64>) kind name =
            match Map.tryFind (kind, name) ids with
            | Some id -> ids, id
            | None ->
                tagKind.Value <- kind
                tagName.Value <- name
                let id = insertTag.ExecuteScalar() |> unbox<int64>
                Map.add (kind, name) id ids, id

        let importRelease (ids, n: int64) (release: CatalogRelease) =
            let tags =
                (release.Genres |> List.map (fun g -> "genre", g)) @ (release.Styles |> List.map (fun s -> "style", s))

            let ids' =
                tags
                |> List.fold
                    (fun ids (kind, name) ->
                        let ids', id = tagId ids kind name
                        rtRelease.Value <- release.ReleaseId
                        rtTag.Value <- id
                        insertReleaseTag.ExecuteNonQuery() |> ignore
                        ids')
                    ids

            if (n + 1L) % 500_000L = 0L then
                let elapsed = now () - started
                logger.LogInformation(
                    "Catalog import {Dump}: {Releases} releases, {Tags} distinct tags, {Rate:F0} releases/s, at release id {ReleaseId}",
                    dump, n + 1L, Map.count ids', float (n + 1L) / elapsed.TotalSeconds, release.ReleaseId)

            ids', n + 1L

        let ids, count = releases xml |> Seq.fold importRelease (Map.empty, 0L)

        use insertMeta = conn.CreateCommand()
        insertMeta.CommandText <- "INSERT OR REPLACE INTO meta (key, value) VALUES ('dump', $dump), ('imported_at', $at), ('releases', $n)"
        insertMeta.Parameters.AddWithValue("$dump", dump) |> ignore
        insertMeta.Parameters.AddWithValue("$at", now().ToString "O") |> ignore
        insertMeta.Parameters.AddWithValue("$n", count) |> ignore
        insertMeta.ExecuteNonQuery() |> ignore
        exec conn "COMMIT"
        logger.LogInformation("Catalog import {Dump}: parsed {Releases} releases with {Tags} distinct tags", dump, count, Map.count ids)
        count

    // The server opens unpooled connections and ATTACHes the catalog on each, so its next
    // request sees the new file; connections already open finish on the old one.
    SqliteConnection.ClearAllPools()
    File.Move(building, targetPath, true)
    logger.LogInformation("Catalog import {Dump}: swapped into {Path} after {Elapsed}", dump, targetPath, now () - started)

    { Dump = dump
      ImportedAt = now ()
      Releases = count }

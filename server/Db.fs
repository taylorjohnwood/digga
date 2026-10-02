/// SQLite persistence. The file is also the seam for the Python recommender: it reads releases,
/// listens and (later) ratings from here and writes weights back.
module Digga.Db

open System
open System.Globalization
open Microsoft.Data.Sqlite
open Digga.Domain

// ---- plumbing -------------------------------------------------------------------------------

let private iso (d: DateTimeOffset) = d.ToString("O", CultureInfo.InvariantCulture)
let private parseIso (s: string) = DateTimeOffset.Parse(s, CultureInfo.InvariantCulture)

let private dbValue (v: obj) = if isNull v then box DBNull.Value else v
let private opt (v: 'a option) = v |> Option.map box |> Option.toObj

let private command (conn: SqliteConnection) (sql: string) (parameters: (string * obj) list) =
    let cmd = conn.CreateCommand()
    cmd.CommandText <- sql
    parameters |> List.iter (fun (k, v) -> cmd.Parameters.AddWithValue(k, dbValue v) |> ignore)
    cmd

let private exec conn sql parameters =
    use cmd = command conn sql parameters
    cmd.ExecuteNonQuery()

let private scalar<'a> conn sql parameters : 'a =
    use cmd = command conn sql parameters
    cmd.ExecuteScalar() |> unbox<'a>

let private query conn sql parameters (read: SqliteDataReader -> 'a) : 'a list =
    use cmd = command conn sql parameters
    use r = cmd.ExecuteReader()
    [ while r.Read() do
          yield read r ]

let private optString (r: SqliteDataReader) (i: int) = if r.IsDBNull i then None else Some(r.GetString i)
let private optInt (r: SqliteDataReader) (i: int) = if r.IsDBNull i then None else Some(r.GetInt32 i)

/// Runs `f` in one transaction: every write inside is all-or-nothing.
let private inTransaction (conn: SqliteConnection) (f: unit -> 'a) : 'a =
    exec conn "BEGIN IMMEDIATE" [] |> ignore
    try
        let result = f ()
        exec conn "COMMIT" [] |> ignore
        result
    with _ ->
        exec conn "ROLLBACK" [] |> ignore
        reraise ()

type Paths = { App: string; Catalog: string }

/// Unpooled, so each connection ATTACHes whichever catalog file is current: a re-import swaps
/// the file underneath a running server.
let connect (paths: Paths) =
    let conn = new SqliteConnection($"Data Source={paths.App};Foreign Keys=True;Pooling=False")
    conn.Open()
    exec conn "PRAGMA busy_timeout = 5000" [] |> ignore
    exec conn "PRAGMA journal_mode = WAL" [] |> ignore
    exec conn "ATTACH DATABASE $catalog AS catalog" [ "$catalog", box paths.Catalog ] |> ignore
    conn

// ---- schema ---------------------------------------------------------------------------------

let private schema =
    """
CREATE TABLE IF NOT EXISTS release (
    id                 INTEGER PRIMARY KEY,           -- Discogs release id
    artist             TEXT    NOT NULL,
    title              TEXT    NOT NULL,
    label              TEXT    NOT NULL,
    catalog_number     TEXT    NOT NULL,
    format             TEXT    NOT NULL,
    year               INTEGER,
    thumbnail          TEXT    NOT NULL,
    want               INTEGER NOT NULL,              -- Discogs community in_wantlist
    have               INTEGER NOT NULL,              -- Discogs community in_collection
    stats_updated_at   TEXT    NOT NULL,
    details_status     TEXT    NOT NULL DEFAULT 'pending'
                       CHECK (details_status IN ('pending', 'fetched', 'unavailable')),
    details_updated_at TEXT
);

-- Genres/styles from the API, for releases newer than the catalog export (catalog.db).
CREATE TABLE IF NOT EXISTS release_genre (
    release_id INTEGER NOT NULL REFERENCES release (id),
    genre      TEXT    NOT NULL,
    PRIMARY KEY (release_id, genre)
);

CREATE TABLE IF NOT EXISTS release_style (
    release_id INTEGER NOT NULL REFERENCES release (id),
    style      TEXT    NOT NULL,
    PRIMARY KEY (release_id, style)
);

-- The playable unit. The same YouTube video can appear on several releases (reissues).
CREATE TABLE IF NOT EXISTS video (
    release_id       INTEGER NOT NULL REFERENCES release (id),
    youtube_id       TEXT    NOT NULL,
    title            TEXT    NOT NULL,
    duration_seconds INTEGER NOT NULL,
    position         INTEGER NOT NULL,
    PRIMARY KEY (release_id, youtube_id)
);

CREATE TABLE IF NOT EXISTS sync_run (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    seller         TEXT    NOT NULL COLLATE NOCASE,
    started_at     TEXT    NOT NULL,
    status         TEXT    NOT NULL CHECK (status IN ('running', 'completed', 'failed')),
    finished_at    TEXT,
    error          TEXT,
    listings_total INTEGER,
    listings_seen  INTEGER NOT NULL DEFAULT 0,
    CHECK ((status = 'running'   AND finished_at IS NULL     AND error IS NULL)
        OR (status = 'completed' AND finished_at IS NOT NULL AND error IS NULL)
        OR (status = 'failed'    AND finished_at IS NOT NULL AND error IS NOT NULL))
);

-- What a seller currently has for sale: replaced wholesale by each completed sync.
CREATE TABLE IF NOT EXISTS listing (
    id               INTEGER PRIMARY KEY,             -- Discogs listing id
    seller           TEXT    NOT NULL COLLATE NOCASE,
    release_id       INTEGER NOT NULL REFERENCES release (id),
    price            REAL    NOT NULL,
    currency         TEXT    NOT NULL,
    condition        TEXT    NOT NULL,
    sleeve_condition TEXT    NOT NULL,
    uri              TEXT    NOT NULL,
    posted           TEXT    NOT NULL,
    seen_in_run      INTEGER NOT NULL REFERENCES sync_run (id)
);
CREATE INDEX IF NOT EXISTS listing_seller ON listing (seller);
CREATE INDEX IF NOT EXISTS listing_release ON listing (release_id);

CREATE TABLE IF NOT EXISTS queue_item (
    release_id INTEGER NOT NULL,
    youtube_id TEXT    NOT NULL,
    added_at   TEXT    NOT NULL,
    PRIMARY KEY (release_id, youtube_id),
    FOREIGN KEY (release_id, youtube_id) REFERENCES video (release_id, youtube_id)
);

CREATE TABLE IF NOT EXISTS listen (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    release_id          INTEGER NOT NULL REFERENCES release (id),
    youtube_id          TEXT    NOT NULL,
    listened_at         TEXT    NOT NULL,              -- when playback started
    listenbrainz_status TEXT    NOT NULL CHECK (listenbrainz_status IN ('not_configured', 'submitted', 'failed')),
    listenbrainz_error  TEXT,
    CHECK ((listenbrainz_status = 'failed') = (listenbrainz_error IS NOT NULL))
);
CREATE INDEX IF NOT EXISTS listen_youtube ON listen (youtube_id);
"""

/// Schema versions, applied in order past PRAGMA user_version. Append only; never edit one that
/// has shipped.
/// v2: a queue item can be a whole release (youtube_id NULL) whose videos aren't known yet. When
/// they arrive it's replaced by one item per video, so a placeholder only outlives the fetch when
/// Discogs has no videos for the release.
let private queueReleases =
    """
CREATE TABLE queue_item_v2 (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    release_id INTEGER NOT NULL REFERENCES release (id),
    youtube_id TEXT,                                   -- NULL: the whole release, videos not known yet
    added_at   TEXT    NOT NULL,
    -- SQLite skips a composite foreign key when any of its columns is NULL, as placeholders need.
    FOREIGN KEY (release_id, youtube_id) REFERENCES video (release_id, youtube_id)
);
INSERT INTO queue_item_v2 (release_id, youtube_id, added_at)
    SELECT release_id, youtube_id, added_at FROM queue_item ORDER BY added_at;
DROP TABLE queue_item;
ALTER TABLE queue_item_v2 RENAME TO queue_item;
CREATE UNIQUE INDEX queue_item_unique ON queue_item (release_id, ifnull(youtube_id, ''));
"""

/// v3: thumbs up/down per record. One verdict per release: it's the record you'd buy.
let private ratings =
    """
CREATE TABLE rating (
    release_id INTEGER PRIMARY KEY REFERENCES release (id),
    verdict    TEXT    NOT NULL CHECK (verdict IN ('like', 'dislike')),
    rated_at   TEXT    NOT NULL
);
"""

let private migrations = [ 1, schema; 2, queueReleases; 3, ratings ]

/// Applies pending migrations, each atomically. Foreign keys are off while they run, as SQLite's
/// table-rebuild procedure requires (https://www.sqlite.org/lang_altertable.html#otheralter), and
/// checked before each commits.
let migrate conn =
    let current = scalar<int64> conn "PRAGMA user_version" [] |> int

    migrations
    |> List.filter (fun (version, _) -> version > current)
    |> List.iter (fun (version, sql) ->
        exec conn "PRAGMA foreign_keys = OFF" [] |> ignore

        inTransaction conn (fun () ->
            exec conn sql [] |> ignore

            match query conn "PRAGMA foreign_key_check" [] (fun r -> r.GetString 0) with
            | [] -> ()
            | tables -> failwithf "migration %d broke foreign keys in %A" version tables

            exec conn $"PRAGMA user_version = {version}" [] |> ignore)

        exec conn "PRAGMA foreign_keys = ON" [] |> ignore)

// ---- sync runs ------------------------------------------------------------------------------

let private readStatus (status: string) (finishedAt: string option) (error: string option) =
    match status, finishedAt, error with
    | "running", None, None -> Running
    | "completed", Some at, None -> Completed(parseIso at)
    | "failed", Some at, Some e -> Failed(parseIso at, e)
    | other -> failwithf "sync_run row violates its CHECK constraint: %A" other

/// Transition: (nothing) → Running. Returns the new run's id.
let startRun conn (seller: string) (now: DateTimeOffset) : int64 =
    scalar<int64>
        conn
        "INSERT INTO sync_run (seller, started_at, status) VALUES ($seller, $at, 'running') RETURNING id"
        [ "$seller", box seller; "$at", box (iso now) ]

/// Transition: Running → Completed.
let completeRun conn (runId: int64) (now: DateTimeOffset) =
    exec conn "UPDATE sync_run SET status = 'completed', finished_at = $at WHERE id = $id AND status = 'running'" [ "$id", box runId; "$at", box (iso now) ]
    |> ignore

/// Transition: Running → Failed.
let failRun conn (runId: int64) (now: DateTimeOffset) (error: string) =
    exec
        conn
        "UPDATE sync_run SET status = 'failed', finished_at = $at, error = $error WHERE id = $id AND status = 'running'"
        [ "$id", box runId; "$at", box (iso now); "$error", box error ]
    |> ignore

/// The worker's rotation is in memory, so a restart abandons any run in progress: record that.
/// Returns how many were closed.
let failInterruptedRuns conn (now: DateTimeOffset) =
    exec
        conn
        "UPDATE sync_run SET status = 'failed', finished_at = $at, error = 'interrupted: server restarted mid-sync' WHERE status = 'running'"
        [ "$at", box (iso now) ]

/// Upserts one page of inventory: release stats, the listings, and the run's progress, atomically.
let recordInventoryPage conn (runId: int64) (seller: string) (page: InventoryPage) (now: DateTimeOffset) =
    inTransaction conn (fun () ->
        page.Listings
        |> List.iter (fun l ->
            exec
                conn
                """
                INSERT INTO release (id, artist, title, label, catalog_number, format, year, thumbnail, want, have, stats_updated_at)
                VALUES ($id, $artist, $title, $label, $catno, $format, $year, $thumb, $want, $have, $at)
                ON CONFLICT (id) DO UPDATE SET
                    artist = excluded.artist, title = excluded.title, label = excluded.label,
                    catalog_number = excluded.catalog_number, format = excluded.format, year = excluded.year,
                    thumbnail = excluded.thumbnail, want = excluded.want, have = excluded.have,
                    stats_updated_at = excluded.stats_updated_at
                """
                [ "$id", box l.ReleaseId; "$artist", box l.Artist; "$title", box l.Title; "$label", box l.Label
                  "$catno", box l.CatalogNumber; "$format", box l.Format; "$year", opt l.Year
                  "$thumb", box l.Thumbnail; "$want", box l.Want; "$have", box l.Have; "$at", box (iso now) ]
            |> ignore

            exec
                conn
                """
                INSERT INTO listing (id, seller, release_id, price, currency, condition, sleeve_condition, uri, posted, seen_in_run)
                VALUES ($id, $seller, $release, $price, $currency, $condition, $sleeve, $uri, $posted, $run)
                ON CONFLICT (id) DO UPDATE SET
                    price = excluded.price, currency = excluded.currency, condition = excluded.condition,
                    sleeve_condition = excluded.sleeve_condition, seen_in_run = excluded.seen_in_run
                """
                [ "$id", box l.ListingId; "$seller", box seller; "$release", box l.ReleaseId; "$price", box l.Price
                  "$currency", box l.Currency; "$condition", box l.Condition; "$sleeve", box l.SleeveCondition
                  "$uri", box l.Uri; "$posted", box l.Posted; "$run", box runId ]
            |> ignore)

        exec
            conn
            "UPDATE sync_run SET listings_total = $total, listings_seen = listings_seen + $n WHERE id = $id"
            [ "$total", box page.Items; "$n", box page.Listings.Length; "$id", box runId ]
        |> ignore)

/// Listings not seen by this run have sold or been withdrawn. Returns how many were removed.
let removeUnseenListings conn (runId: int64) (seller: string) =
    exec conn "DELETE FROM listing WHERE seller = $seller AND seen_in_run <> $run" [ "$seller", box seller; "$run", box runId ]

type ForgetSummary =
    { Runs: int
      Listings: int
      /// Releases deleted because nothing else needed them. Ones another seller lists, or that are
      /// in the queue or have listens or ratings, stay: those facts outlive the seller.
      Releases: int }

/// Deletes everything synced for a seller: its runs, its listings, and the releases only it listed.
/// The worker calls this between steps, so no page for the seller can land afterwards.
let forgetSeller conn (seller: string) : ForgetSummary =
    let ps = [ "$seller", box seller ]

    inTransaction conn (fun () ->
        let listed =
            query conn "SELECT DISTINCT release_id FROM listing WHERE seller = $seller" ps (fun r -> r.GetInt64 0)

        let listings = exec conn "DELETE FROM listing WHERE seller = $seller" ps
        let runs = exec conn "DELETE FROM sync_run WHERE seller = $seller" ps

        let orphans =
            query
                conn
                """
                SELECT value FROM json_each($ids)
                WHERE value NOT IN (SELECT release_id FROM listing)
                  AND value NOT IN (SELECT release_id FROM queue_item)
                  AND value NOT IN (SELECT release_id FROM listen)
                  AND value NOT IN (SELECT release_id FROM rating)
                """
                [ "$ids", box (Text.Json.JsonSerializer.Serialize listed) ]
                (fun r -> r.GetInt64 0)

        let orphanPs = [ "$ids", box (Text.Json.JsonSerializer.Serialize orphans) ]

        [ "video"; "release_genre"; "release_style" ]
        |> List.iter (fun table ->
            exec conn $"DELETE FROM {table} WHERE release_id IN (SELECT value FROM json_each($ids))" orphanPs |> ignore)

        let releases = exec conn "DELETE FROM release WHERE id IN (SELECT value FROM json_each($ids))" orphanPs

        { Runs = runs; Listings = listings; Releases = releases })

/// A listed release is in the catalog if the data export has tags for it.
let private inCatalog (releaseIdColumn: string) =
    $"EXISTS (SELECT 1 FROM catalog.release_tag ct WHERE ct.release_id = {releaseIdColumn})"

type SellerSummary =
    { Seller: string
      LatestRun: SyncRun
      Listings: int
      Releases: int
      /// Releases newer than the catalog export whose genres haven't been fetched from the API yet.
      GenresUnknown: int }

let sellers conn : SellerSummary list =
    query
        conn
        $"""
        SELECT r.id, r.seller, r.started_at, r.status, r.finished_at, r.error, r.listings_total, r.listings_seen,
               (SELECT COUNT(*) FROM listing l WHERE l.seller = r.seller),
               (SELECT COUNT(DISTINCT l.release_id) FROM listing l WHERE l.seller = r.seller),
               (SELECT COUNT(DISTINCT l.release_id) FROM listing l JOIN release rel ON rel.id = l.release_id
                 WHERE l.seller = r.seller AND rel.details_status = 'pending' AND NOT {inCatalog "rel.id"})
        FROM sync_run r
        WHERE r.id = (SELECT MAX(id) FROM sync_run x WHERE x.seller = r.seller)
        ORDER BY r.started_at DESC
        """
        []
        (fun r ->
            { Seller = r.GetString 1
              LatestRun =
                { Id = r.GetInt64 0
                  Seller = r.GetString 1
                  StartedAt = parseIso (r.GetString 2)
                  Status = readStatus (r.GetString 3) (optString r 4) (optString r 5)
                  ListingsTotal = optInt r 6
                  ListingsSeen = r.GetInt32 7 }
              Listings = r.GetInt32 8
              Releases = r.GetInt32 9
              GenresUnknown = r.GetInt32 10 })

// ---- release details ------------------------------------------------------------------------

/// (want, have) for every release currently listed by anyone: the population the prior pools over.
let listedReleaseStats conn : (int * int) list =
    query
        conn
        "SELECT want, have FROM release WHERE id IN (SELECT release_id FROM listing)"
        []
        (fun r -> r.GetInt32 0, r.GetInt32 1)

/// Listed releases the catalog export doesn't cover and whose details haven't been fetched, as
/// (id, want, have): the only releases the worker fetches unprompted, to learn their genres.
let releasesMissingGenres conn : (int64 * int * int) list =
    query
        conn
        $"""
        SELECT id, want, have FROM release
        WHERE details_status = 'pending' AND id IN (SELECT release_id FROM listing) AND NOT {inCatalog "release.id"}
        """
        []
        (fun r -> r.GetInt64 0, r.GetInt32 1, r.GetInt32 2)

/// Which of `releaseIds` still need their details fetched, in the order given.
let pendingDetails conn (releaseIds: int64 list) : int64 list =
    let pending =
        query
            conn
            $"""SELECT id FROM release WHERE details_status = 'pending' AND id IN ({releaseIds |> List.map string |> String.concat ","})"""
            []
            (fun r -> r.GetInt64 0)
        |> Set.ofList

    releaseIds |> List.filter pending.Contains

let saveReleaseDetails conn (releaseId: int64) (details: ReleaseDetails) (now: DateTimeOffset) =
    let ps = [ "$id", box releaseId ]

    inTransaction conn (fun () ->
        exec conn "DELETE FROM release_genre WHERE release_id = $id" ps |> ignore
        exec conn "DELETE FROM release_style WHERE release_id = $id" ps |> ignore
        // Videos are only deleted when not queued; the queue's foreign key protects the rest. The
        // IS NOT NULL matters: `x NOT IN (..., NULL)` is NULL, never true, so a queued whole-release
        // placeholder would otherwise silently stop every stale video being deleted.
        exec
            conn
            """
            DELETE FROM video WHERE release_id = $id
            AND youtube_id NOT IN (SELECT youtube_id FROM queue_item WHERE release_id = $id AND youtube_id IS NOT NULL)
            """
            ps
        |> ignore

        details.Genres
        |> List.iter (fun g ->
            exec conn "INSERT OR IGNORE INTO release_genre VALUES ($id, $g)" [ "$id", box releaseId; "$g", box g ] |> ignore)

        details.Styles
        |> List.iter (fun s ->
            exec conn "INSERT OR IGNORE INTO release_style VALUES ($id, $s)" [ "$id", box releaseId; "$s", box s ] |> ignore)

        details.Videos
        |> List.iteri (fun i v ->
            exec
                conn
                """
                INSERT INTO video (release_id, youtube_id, title, duration_seconds, position) VALUES ($id, $yt, $title, $dur, $pos)
                ON CONFLICT (release_id, youtube_id) DO UPDATE SET
                    title = excluded.title, duration_seconds = excluded.duration_seconds, position = excluded.position
                """
                [ "$id", box releaseId; "$yt", box v.YoutubeId; "$title", box v.Title
                  "$dur", box v.DurationSeconds; "$pos", box i ]
            |> ignore)

        // A queued whole release becomes one queue item per video, keeping its place in the queue.
        exec
            conn
            """
            INSERT OR IGNORE INTO queue_item (release_id, youtube_id, added_at)
            SELECT v.release_id, v.youtube_id, q.added_at
            FROM queue_item q JOIN video v ON v.release_id = q.release_id
            WHERE q.release_id = $id AND q.youtube_id IS NULL
            ORDER BY v.position
            """
            ps
        |> ignore

        exec
            conn
            "DELETE FROM queue_item WHERE release_id = $id AND youtube_id IS NULL AND EXISTS (SELECT 1 FROM video WHERE release_id = $id)"
            ps
        |> ignore

        exec
            conn
            """
            UPDATE release SET want = $want, have = $have, stats_updated_at = $at,
                               details_status = 'fetched', details_updated_at = $at
            WHERE id = $id
            """
            [ "$id", box releaseId; "$want", box details.Want; "$have", box details.Have; "$at", box (iso now) ]
        |> ignore)

let markDetailsUnavailable conn (releaseId: int64) (now: DateTimeOffset) =
    exec
        conn
        "UPDATE release SET details_status = 'unavailable', details_updated_at = $at WHERE id = $id"
        [ "$id", box releaseId; "$at", box (iso now) ]
    |> ignore

// ---- ratings --------------------------------------------------------------------------------

let private verdictText =
    function
    | Like -> "like"
    | Dislike -> "dislike"

let private readVerdict (r: SqliteDataReader) (i: int) : Verdict option =
    match r.IsDBNull i with
    | true -> None
    | false ->
        match r.GetString i with
        | "like" -> Some Like
        | "dislike" -> Some Dislike
        | other -> failwithf "unknown verdict %s" other

type RateOutcome =
    | Rated
    | NoSuchRelease

/// Sets (or changes) your verdict on a record.
let rate conn (releaseId: int64) (verdict: Verdict) (now: DateTimeOffset) : RateOutcome =
    let ps = [ "$release", box releaseId; "$verdict", box (verdictText verdict); "$at", box (iso now) ]

    match scalar<int64> conn "SELECT EXISTS (SELECT 1 FROM release WHERE id = $release)" ps with
    | 0L -> NoSuchRelease
    | _ ->
        exec
            conn
            """
            INSERT INTO rating (release_id, verdict, rated_at) VALUES ($release, $verdict, $at)
            ON CONFLICT (release_id) DO UPDATE SET verdict = excluded.verdict, rated_at = excluded.rated_at
            """
            ps
        |> ignore

        Rated

/// Returns whether there was a rating to remove.
let unrate conn (releaseId: int64) : bool =
    exec conn "DELETE FROM rating WHERE release_id = $release" [ "$release", box releaseId ] > 0

// ---- read model for the ranked list ---------------------------------------------------------

type ListingView =
    { ListingId: int64
      Price: float
      Currency: string
      Condition: string
      SleeveCondition: string
      Uri: string }

type VideoView =
    { YoutubeId: string
      Title: string
      DurationSeconds: int
      Heard: bool
      Queued: bool }

/// What's known of a release's genres and styles: from the catalog export, or the API.
type Tags =
    | Unknown
    | Known of genres: string list * styles: string list

type ReleaseView =
    { ReleaseId: int64
      Artist: string
      Title: string
      Label: string
      CatalogNumber: string
      Format: string
      Year: int option
      Thumbnail: string
      Want: int
      Have: int
      /// Whether videos (and fresh want/have) have been fetched from the API.
      DetailsStatus: DetailsStatus
      /// The whole release is in the digging queue, waiting for its videos to become tracks.
      QueuedAwaitingVideos: bool
      Rating: Verdict option
      Tags: Tags
      Videos: VideoView list
      Listings: ListingView list }

let private readDetailsStatus =
    function
    | "pending" -> Pending
    | "fetched" -> Fetched
    | "unavailable" -> Unavailable
    | other -> failwithf "unknown details_status %s" other

/// Everything a seller has listed, one entry per release. Unranked: ranking is the caller's job.
let sellerReleases conn (seller: string) : ReleaseView list =
    let ps = [ "$seller", box seller ]
    let listedBySeller = "SELECT release_id FROM listing WHERE seller = $seller"

    let byRelease rows =
        rows |> List.groupBy fst |> List.map (fun (k, vs) -> k, List.map snd vs) |> Map.ofList

    let lookup key map = map |> Map.tryFind key |> Option.defaultValue []

    let listings =
        query
            conn
            "SELECT release_id, id, price, currency, condition, sleeve_condition, uri FROM listing WHERE seller = $seller ORDER BY price"
            ps
            (fun r ->
                r.GetInt64 0,
                { ListingId = r.GetInt64 1
                  Price = r.GetDouble 2
                  Currency = r.GetString 3
                  Condition = r.GetString 4
                  SleeveCondition = r.GetString 5
                  Uri = r.GetString 6 })
        |> byRelease

    // Catalog export first; the API's answer for releases newer than the export. UNION dedupes.
    let tags kind table column =
        query
            conn
            $"""
            SELECT ct.release_id, t.name FROM catalog.release_tag ct JOIN catalog.tag t ON t.id = ct.tag_id
            WHERE t.kind = '{kind}' AND ct.release_id IN ({listedBySeller})
            UNION
            SELECT release_id, {column} FROM {table} WHERE release_id IN ({listedBySeller})
            """
            ps
            (fun r -> r.GetInt64 0, r.GetString 1)
        |> byRelease

    let genres = tags "genre" "release_genre" "genre"
    let styles = tags "style" "release_style" "style"

    let videos =
        query
            conn
            $"""
            SELECT v.release_id, v.youtube_id, v.title, v.duration_seconds,
                   EXISTS (SELECT 1 FROM listen l WHERE l.youtube_id = v.youtube_id),
                   EXISTS (SELECT 1 FROM queue_item q WHERE q.release_id = v.release_id AND q.youtube_id = v.youtube_id)
            FROM video v
            WHERE v.release_id IN ({listedBySeller})
            ORDER BY v.release_id, v.position
            """
            ps
            (fun r ->
                r.GetInt64 0,
                { YoutubeId = r.GetString 1
                  Title = r.GetString 2
                  DurationSeconds = r.GetInt32 3
                  Heard = r.GetBoolean 4
                  Queued = r.GetBoolean 5 })
        |> byRelease

    query
        conn
        $"""
        SELECT id, artist, title, label, catalog_number, format, year, thumbnail, want, have, details_status,
               {inCatalog "release.id"},
               EXISTS (SELECT 1 FROM queue_item q WHERE q.release_id = release.id AND q.youtube_id IS NULL),
               (SELECT verdict FROM rating rt WHERE rt.release_id = release.id)
        FROM release WHERE id IN ({listedBySeller})
        """
        ps
        (fun r ->
            let id = r.GetInt64 0
            let detailsStatus = readDetailsStatus (r.GetString 10)

            { ReleaseId = id
              Artist = r.GetString 1
              Title = r.GetString 2
              Label = r.GetString 3
              CatalogNumber = r.GetString 4
              Format = r.GetString 5
              Year = optInt r 6
              Thumbnail = r.GetString 7
              Want = r.GetInt32 8
              Have = r.GetInt32 9
              DetailsStatus = detailsStatus
              QueuedAwaitingVideos = r.GetBoolean 12
              Rating = readVerdict r 13
              Tags =
                match r.GetBoolean 11, detailsStatus with
                | true, _
                | false, Fetched -> Known(lookup id genres, lookup id styles)
                | false, (Pending | Unavailable) -> Unknown
              Videos = lookup id videos
              Listings = lookup id listings })

// ---- queue and listens ----------------------------------------------------------------------

type QueuedRelease =
    { ReleaseId: int64
      Artist: string
      ReleaseTitle: string
      Label: string
      Thumbnail: string
      Rating: Verdict option }

type QueuedVideo =
    { YoutubeId: string
      Title: string
      DurationSeconds: int }

type QueueItem =
    /// Playable now.
    | Track of video: QueuedVideo
    /// A whole release queued before its videos were fetched; becomes tracks when they arrive.
    | AwaitingVideos
    /// A whole release Discogs turned out to have no videos for: nothing to play, left for you to remove.
    | NoVideos

type QueueEntry =
    { Id: int64
      Release: QueuedRelease
      AddedAt: DateTimeOffset
      Item: QueueItem }

let queue conn : QueueEntry list =
    query
        conn
        """
        SELECT q.id, q.release_id, r.artist, r.title, r.label, r.thumbnail, q.added_at,
               q.youtube_id, v.title, v.duration_seconds, r.details_status, rt.verdict
        FROM queue_item q
        JOIN release r ON r.id = q.release_id
        LEFT JOIN video v ON v.release_id = q.release_id AND v.youtube_id = q.youtube_id
        LEFT JOIN rating rt ON rt.release_id = q.release_id
        ORDER BY q.added_at, v.position, q.id
        """
        []
        (fun r ->
            { Id = r.GetInt64 0
              Release =
                { ReleaseId = r.GetInt64 1
                  Artist = r.GetString 2
                  ReleaseTitle = r.GetString 3
                  Label = r.GetString 4
                  Thumbnail = r.GetString 5
                  Rating = readVerdict r 11 }
              AddedAt = parseIso (r.GetString 6)
              Item =
                match r.IsDBNull 7, readDetailsStatus (r.GetString 10) with
                | false, _ ->
                    Track
                        { YoutubeId = r.GetString 7
                          Title = r.GetString 8
                          DurationSeconds = r.GetInt32 9 }
                | true, Pending -> AwaitingVideos
                | true, (Fetched | Unavailable) -> NoVideos })

type QueueAddOutcome =
    | Added
    | AlreadyQueued
    | UnknownVideo

let enqueue conn (releaseId: int64) (youtubeId: string) (now: DateTimeOffset) : QueueAddOutcome =
    let ps = [ "$release", box releaseId; "$yt", box youtubeId; "$at", box (iso now) ]
    let videoExists =
        scalar<int64> conn "SELECT EXISTS (SELECT 1 FROM video WHERE release_id = $release AND youtube_id = $yt)" ps = 1L

    match videoExists with
    | false -> UnknownVideo
    | true ->
        match exec conn "INSERT OR IGNORE INTO queue_item (release_id, youtube_id, added_at) VALUES ($release, $yt, $at)" ps with
        | 0 -> AlreadyQueued
        | _ -> Added

type ReleaseQueueOutcome =
    /// Its videos were known: each one not already queued was added.
    | QueuedTracks of added: int
    /// Its videos aren't fetched yet: queued as a placeholder the worker fetches first.
    | QueuedAwaitingVideos of newly: bool
    /// Discogs has no videos for it, so there's nothing to listen to.
    | NothingToPlay
    | UnknownRelease

/// Queues every video of a release, or the release itself if its videos aren't known yet.
let enqueueRelease conn (releaseId: int64) (now: DateTimeOffset) : ReleaseQueueOutcome =
    let ps = [ "$release", box releaseId; "$at", box (iso now) ]

    let status =
        query conn "SELECT details_status FROM release WHERE id = $release" ps (fun r -> readDetailsStatus (r.GetString 0))
        |> List.tryHead

    let videos = scalar<int64> conn "SELECT count(*) FROM video WHERE release_id = $release" ps

    match status, videos with
    | None, _ -> UnknownRelease
    | Some _, n when n > 0L ->
        exec
            conn
            """
            INSERT OR IGNORE INTO queue_item (release_id, youtube_id, added_at)
            SELECT release_id, youtube_id, $at FROM video WHERE release_id = $release ORDER BY position
            """
            ps
        |> QueuedTracks
    | Some Pending, _ ->
        exec conn "INSERT OR IGNORE INTO queue_item (release_id, youtube_id, added_at) VALUES ($release, NULL, $at)" ps > 0
        |> QueuedAwaitingVideos
    | Some(Fetched | Unavailable), _ -> NothingToPlay

/// Returns whether anything was removed.
let dequeue conn (queueItemId: int64) : bool =
    exec conn "DELETE FROM queue_item WHERE id = $id" [ "$id", box queueItemId ] > 0

type ListenSubject =
    { ReleaseId: int64
      YoutubeId: string
      Artist: string
      ReleaseTitle: string
      VideoTitle: string
      DurationSeconds: int }

let listenSubject conn (releaseId: int64) (youtubeId: string) : ListenSubject option =
    query
        conn
        """
        SELECT v.release_id, v.youtube_id, r.artist, r.title, v.title, v.duration_seconds
        FROM video v JOIN release r ON r.id = v.release_id
        WHERE v.release_id = $release AND v.youtube_id = $yt
        """
        [ "$release", box releaseId; "$yt", box youtubeId ]
        (fun r ->
            { ReleaseId = r.GetInt64 0
              YoutubeId = r.GetString 1
              Artist = r.GetString 2
              ReleaseTitle = r.GetString 3
              VideoTitle = r.GetString 4
              DurationSeconds = r.GetInt32 5 })
    |> List.tryHead

let recordListen conn (subject: ListenSubject) (listenedAt: DateTimeOffset) (outcome: ListenBrainzOutcome) : int64 =
    let status, error =
        match outcome with
        | NotConfigured -> "not_configured", None
        | Submitted -> "submitted", None
        | SubmitFailed e -> "failed", Some e

    scalar<int64>
        conn
        """
        INSERT INTO listen (release_id, youtube_id, listened_at, listenbrainz_status, listenbrainz_error)
        VALUES ($release, $yt, $at, $status, $error) RETURNING id
        """
        [ "$release", box subject.ReleaseId; "$yt", box subject.YoutubeId; "$at", box (iso listenedAt)
          "$status", box status; "$error", opt error ]

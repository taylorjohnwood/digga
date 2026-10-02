module Digga.Tests

open System
open System.Net
open System.Text.Json
open Xunit
open Digga
open Digga.Domain

module ScoreTests =
    [<Fact>]
    let ``sparse release is pulled towards the prior instead of topping the list`` () =
        let prior = 0.5
        let sparse = Score.wantHave prior 3 0 // raw ratio: infinite
        let established = Score.wantHave prior 2000 400 // raw ratio: 5.0
        Assert.True(established > sparse, $"established {established} should outrank sparse {sparse}")

    [<Fact>]
    let ``release with no data scores the prior`` () =
        Assert.Equal(0.7, Score.wantHave 0.7 0 0, 10)

    [<Fact>]
    let ``well-evidenced release scores close to its own ratio`` () =
        Assert.Equal(3.0, Score.wantHave 0.5 30000 10000, 2)

    [<Fact>]
    let ``prior pools wants over haves`` () =
        Assert.Equal(0.5, Score.priorRatio [ 10, 10; 0, 10 ], 10)

    [<Fact>]
    let ``prior falls back to 1 when nobody has anything`` () =
        Assert.Equal(1.0, Score.priorRatio [], 10)

module NamesTests =
    [<Theory>]
    [<InlineData("Genius*", "Genius")>]
    [<InlineData("Shed (2)", "Shed")>]
    [<InlineData("Various", "Various")>]
    [<InlineData("Move D*, Benjamin Brunn", "Move D, Benjamin Brunn")>]
    let ``cleanArtist strips Discogs markers`` (input: string, expected: string) =
        Assert.Equal(expected, Names.cleanArtist input)

    [<Theory>]
    [<InlineData("GZA", "GZA - I Gotcha Back", "I Gotcha Back")>]
    [<InlineData("Carly Simon", "Why - Carly Simon", "Why")>]
    [<InlineData("Carly Simon", "Carly Simon - Why (12\" Version)", "Why (12\" Version)")>]
    [<InlineData("Shed (2)", "Shed - Warped", "Warped")>]
    [<InlineData("x", "I Gotcha Back", "I Gotcha Back")>]
    [<InlineData("x", "A - B - C", "B - C")>]
    let ``trackNameFromVideoTitle drops the artist side`` (artist: string, input: string, expected: string) =
        Assert.Equal(expected, Names.trackNameFromVideoTitle artist input)

    [<Theory>]
    [<InlineData("https://www.youtube.com/watch?v=pSQieA85arg", "pSQieA85arg")>]
    [<InlineData("https://youtu.be/pSQieA85arg", "pSQieA85arg")>]
    [<InlineData("https://m.youtube.com/watch?feature=x&v=abc", "abc")>]
    let ``youtubeId extracts the id`` (uri: string, expected: string) =
        Assert.Equal(Some expected, Names.youtubeId uri)

    [<Theory>]
    [<InlineData("https://vimeo.com/123")>]
    [<InlineData("https://www.youtube.com/watch")>]
    [<InlineData("not a url")>]
    let ``youtubeId rejects non-YouTube or id-less uris`` (uri: string) =
        Assert.Equal(None, Names.youtubeId uri)

module DiscogsTests =
    // Trimmed from a real response: GET /users/rushhour/inventory?per_page=1
    let inventoryJson =
        """
        { "pagination": { "page": 1, "pages": 4142, "per_page": 1, "items": 4142 },
          "listings": [ {
            "id": 4385707776, "uri": "https://www.discogs.com/sell/item/4385707776",
            "condition": "Very Good Plus (VG+)", "sleeve_condition": "Very Good Plus (VG+)",
            "posted": "2026-09-30T05:51:19-07:00",
            "price": { "value": 13.07, "currency": "EUR" },
            "release": { "thumbnail": "", "artist": "Genius*", "format": "12\", Promo", "title": "I Gotcha' Back",
                         "year": 1994, "id": 1090583, "label": "RCA, Loud Records", "catalog_number": "RDAB 62966-1",
                         "stats": { "community": { "in_wantlist": 78, "in_collection": 193 } } } } ] }
        """

    [<Fact>]
    let ``parses an inventory page`` () =
        use doc = JsonDocument.Parse inventoryJson
        let page = Discogs.parseInventoryPage doc.RootElement
        Assert.Equal(4142, page.Pages)
        let l = Assert.Single page.Listings
        Assert.Equal(1090583L, l.ReleaseId)
        Assert.Equal(78, l.Want)
        Assert.Equal(193, l.Have)
        Assert.Equal(Some 1994, l.Year)
        Assert.Equal("EUR", l.Currency)

    [<Fact>]
    let ``parses release details, keeping embeddable YouTube videos only, deduplicated`` () =
        use doc =
            JsonDocument.Parse
                """
                { "genres": ["Hip Hop"], "community": { "want": 80, "have": 190 },
                  "videos": [
                    { "uri": "https://www.youtube.com/watch?v=aaa", "title": "GZA - I Gotcha Back", "duration": 178, "embed": true },
                    { "uri": "https://www.youtube.com/watch?v=aaa", "title": "dupe", "duration": 178, "embed": true },
                    { "uri": "https://www.youtube.com/watch?v=bbb", "title": "blocked", "duration": 10, "embed": false },
                    { "uri": "https://vimeo.com/1", "title": "elsewhere", "duration": 10, "embed": true } ] }
                """

        let r = Discogs.parseRelease doc.RootElement
        Assert.Equal<string list>([ "Hip Hop" ], r.Genres)
        Assert.Empty r.Styles
        let v = Assert.Single r.Videos
        Assert.Equal("aaa", v.YoutubeId)
        Assert.Equal(80, r.Want)

    [<Fact>]
    let ``pacing waits out an exhausted window`` () =
        Assert.Equal(TimeSpan.FromSeconds 60.0, fst (Discogs.pacing (Some 60) (Some 0)))

    [<Fact>]
    let ``pacing spreads requests across the minute`` () =
        Assert.Equal(TimeSpan.FromSeconds 1.0, fst (Discogs.pacing (Some 60) (Some 30)))

    [<Fact>]
    let ``client errors are permanent, server and network errors are not`` () =
        Assert.True(Discogs.isPermanent Discogs.NotFound)
        Assert.True(Discogs.isPermanent (Discogs.Http(HttpStatusCode.Forbidden, "")))
        Assert.False(Discogs.isPermanent (Discogs.Http(HttpStatusCode.BadGateway, "")))
        Assert.False(Discogs.isPermanent (Discogs.Network "timeout"))

module RankingTests =
    let release id want have genres (videos: Db.VideoView list) : Db.ReleaseView =
        { ReleaseId = id
          Artist = "a"
          Title = "t"
          Label = "l"
          CatalogNumber = ""
          Format = ""
          Year = None
          Thumbnail = ""
          Want = want
          Have = have
          DetailsStatus = Pending
          QueuedAwaitingVideos = false
          Rating = None
          Tags = (if List.isEmpty genres then Db.Unknown else Db.Known(genres, []))
          Videos = videos
          Listings = [] }

    let video heard : Db.VideoView =
        { YoutubeId = Guid.NewGuid().ToString()
          Title = ""
          DurationSeconds = 0
          Heard = heard
          Queued = false }

    let noFilter: Ranking.Filter = { Tags = []; HideHeard = false; HideRated = false }

    [<Fact>]
    let ``ranks by smoothed want/have, highest first`` () =
        let releases = [ release 1L 10 100 [] []; release 2L 300 100 [] []; release 3L 100 100 [] [] ]
        let page = Ranking.page 0.5 noFilter [] 0 10 releases
        Assert.Equal<int64 list>([ 2L; 3L; 1L ], page.Items |> List.map _.Release.ReleaseId)

    [<Fact>]
    let ``genre filter excludes releases whose genres are unknown`` () =
        let releases = [ release 1L 1 1 [ "Electronic" ] []; release 2L 1 1 [] [] ]
        let page = Ranking.page 0.5 { noFilter with Tags = [ [ "Electronic" ] ] } [ "Electronic" ] 0 10 releases
        Assert.Equal(1, page.Matching)
        Assert.Equal(1, page.GenresUnknown)

    [<Fact>]
    let ``no filter includes releases whose genres are unknown`` () =
        let releases = [ release 1L 1 1 [ "Electronic" ] []; release 2L 1 1 [] [] ]
        Assert.Equal(2, (Ranking.page 0.5 noFilter [] 0 10 releases).Matching)

    [<Fact>]
    let ``hideHeard hides only releases where every video was heard`` () =
        let releases =
            [ release 1L 1 1 [] [ video true; video true ]
              release 2L 1 1 [] [ video true; video false ]
              release 3L 1 1 [] [] ]

        let page = Ranking.page 0.5 { noFilter with HideHeard = true } [] 0 10 releases
        Assert.Equal<int64 list>([ 2L; 3L ], page.Items |> List.map _.Release.ReleaseId |> List.sort)

    [<Fact>]
    let ``hideRated leaves only records you haven't rated`` () =
        let releases =
            [ { release 1L 1 1 [] [] with Rating = Some Like }
              { release 2L 1 1 [] [] with Rating = Some Dislike }
              release 3L 1 1 [] [] ]

        let page = Ranking.page 0.5 { noFilter with HideRated = true } [] 0 10 releases
        Assert.Equal<int64 list>([ 3L ], page.Items |> List.map _.Release.ReleaseId)
        Assert.Equal(3, page.Total)

    let tagged id (styles: string list) =
        { release id 1 1 [ "Electronic" ] [] with Tags = Db.Known([ "Electronic" ], styles) }

    let tagCatalogue =
        [ tagged 1L [ "Deep House"; "House" ]
          tagged 2L [ "Deep House" ]
          tagged 3L [ "Techno" ]
          tagged 4L [ "House"; "Techno" ]
          release 5L 1 1 [] [] ]

    let matchingIds query =
        (Ranking.page 0.5 { noFilter with Tags = query } [] 0 10 tagCatalogue).Items
        |> List.map _.Release.ReleaseId
        |> List.sort

    [<Fact>]
    let ``a group needs every one of its tags`` () =
        Assert.Equal<int64 list>([ 1L ], matchingIds [ [ "Deep House"; "House" ] ])

    [<Fact>]
    let ``any group will do: (Deep House AND House) OR Techno`` () =
        Assert.Equal<int64 list>([ 1L; 3L; 4L ], matchingIds [ [ "Deep House"; "House" ]; [ "Techno" ] ])

    [<Fact>]
    let ``genres and styles are matched alike`` () =
        Assert.Equal<int64 list>([ 3L; 4L ], matchingIds [ [ "Electronic"; "Techno" ] ])

    [<Fact>]
    let ``facet counts narrow within the group being built`` () =
        let page = Ranking.page 0.5 noFilter [ "Deep House" ] 0 10 tagCatalogue
        let count name = page.Styles |> List.tryFind (fun f -> f.Name = name) |> Option.map _.Count
        Assert.Equal(Some 2, count "Deep House")
        Assert.Equal(Some 1, count "House")
        Assert.Equal(None, count "Techno")

    [<Fact>]
    let ``pages through the ranked list`` () =
        let releases = [ for i in 1..5 -> release (int64 i) (i * 10) 10 [] [] ]
        let page = Ranking.page 0.5 noFilter [] 2 2 releases
        Assert.Equal<int64 list>([ 3L; 2L ], page.Items |> List.map _.Release.ReleaseId)
        Assert.Equal(5, page.Matching)

module ListenBrainzTests =
    [<Fact>]
    let ``payload follows the submit-listens schema`` () =
        let subject: Db.ListenSubject =
            { ReleaseId = 1090583L
              YoutubeId = "pSQieA85arg"
              Artist = "Genius*"
              ReleaseTitle = "I Gotcha' Back"
              VideoTitle = "GZA - I Gotcha Back"
              DurationSeconds = 178 }

        use doc = JsonDocument.Parse(ListenBrainz.payload subject (DateTimeOffset.FromUnixTimeSeconds 1700000000L))
        let root = doc.RootElement
        Assert.Equal("single", root.GetProperty("listen_type").GetString())
        let listen = root.GetProperty("payload").[0]
        Assert.Equal(1700000000L, listen.GetProperty("listened_at").GetInt64())
        let meta = listen.GetProperty "track_metadata"
        Assert.Equal("Genius", meta.GetProperty("artist_name").GetString())
        Assert.Equal("I Gotcha Back", meta.GetProperty("track_name").GetString())
        Assert.Equal(178000, meta.GetProperty("additional_info").GetProperty("duration_ms").GetInt32())

    [<Fact>]
    let ``payload omits unknown duration`` () =
        let subject: Db.ListenSubject =
            { ReleaseId = 1L
              YoutubeId = "x"
              Artist = "a"
              ReleaseTitle = "r"
              VideoTitle = "v"
              DurationSeconds = 0 }

        use doc = JsonDocument.Parse(ListenBrainz.payload subject DateTimeOffset.UnixEpoch)
        let info = doc.RootElement.GetProperty("payload").[0].GetProperty("track_metadata").GetProperty("additional_info")
        Assert.False(fst (info.TryGetProperty "duration_ms"))

module CatalogTests =
    let parse (xml: string) =
        use stream = new IO.MemoryStream(Text.Encoding.UTF8.GetBytes xml)
        Catalog.releases stream |> List.ofSeq

    [<Fact>]
    let ``reads consecutive genres and styles without skipping any`` () =
        // Consecutive elements with no whitespace between them are where a stray Read() drops every
        // second one — the dump is exactly this shape once whitespace is ignored.
        let releases =
            parse
                """<releases><release id="1"><title>Stockholm</title><labels><label name="Svek" catno="SK032" id="5"/></labels><genres><genre>Electronic</genre><genre>Jazz</genre><genre>Funk / Soul</genre></genres><styles><style>Deep House</style><style>Fusion</style></styles><tracklist><track><title>genre</title></track></tracklist></release><release id="2"><genres><genre>Rock</genre></genres></release></releases>"""

        let expected: Catalog.CatalogRelease list =
            [ { ReleaseId = 1L; Genres = [ "Electronic"; "Jazz"; "Funk / Soul" ]; Styles = [ "Deep House"; "Fusion" ] }
              { ReleaseId = 2L; Genres = [ "Rock" ]; Styles = [] } ]

        Assert.Equal<Catalog.CatalogRelease list>(expected, releases)

    [<Fact>]
    let ``ignores genre-named elements outside genres`` () =
        let releases =
            parse """<releases><release id="3"><notes><genre>not a tag</genre></notes><genres><genre>Pop</genre></genres></release></releases>"""

        Assert.Equal<string list>([ "Pop" ], (List.exactlyOne releases).Genres)

module SchedulerTests =
    open Digga.Scheduler

    let sellersInRotation (s: State) = s.Syncs |> List.map _.Seller

    [<Fact>]
    let ``a new seller starts on the next request instead of waiting`` () =
        let s = empty |> requestSync "plattenrillehamburg" |> syncStepped "plattenrillehamburg" (Advanced(1L, 2)) |> requestSync "HardwaxBerlin"
        Assert.Equal(SyncPage { Seller = "HardwaxBerlin"; Progress = NotStarted }, next s)

    [<Fact>]
    let ``two sellers take turns a page at a time`` () =
        let s0 = empty |> requestSync "a" |> requestSync "b"
        let s1 = s0 |> syncStepped "b" (Advanced(1L, 2))
        let s2 = s1 |> syncStepped "a" (Advanced(2L, 2))
        Assert.Equal<string list>([ "b"; "a" ], sellersInRotation s0)
        Assert.Equal<string list>([ "a"; "b" ], sellersInRotation s1)
        Assert.Equal<string list>([ "b"; "a" ], sellersInRotation s2)

    [<Fact>]
    let ``asking again for a syncing seller changes nothing`` () =
        let s = empty |> requestSync "HardwaxBerlin" |> syncStepped "HardwaxBerlin" (Advanced(1L, 5))
        Assert.Equal(s, s |> requestSync "hardwaxberlin" |> requestSync "HardwaxBerlin")

    [<Fact>]
    let ``a finished sync leaves the rotation`` () =
        let s = empty |> requestSync "a" |> requestSync "b" |> syncStepped "b" Finished
        Assert.Equal<string list>([ "a" ], sellersInRotation s)
        Assert.False(isSyncing "b" s)

    [<Fact>]
    let ``syncs and viewed releases alternate`` () =
        let s0 = empty |> requestSync "a" |> requestDetails [ 10L; 11L ]
        Assert.Equal(SyncPage { Seller = "a"; Progress = NotStarted }, next s0)
        let s1 = s0 |> syncStepped "a" (Advanced(1L, 2))
        Assert.Equal(Details 10L, next s1)
        let s2 = s1 |> detailsStepped 10L
        Assert.Equal(SyncPage { Seller = "a"; Progress = InProgress(1L, 2) }, next s2)

    [<Fact>]
    let ``repeated detail requests are deduplicated`` () =
        let s = empty |> requestDetails [ 1L; 2L ] |> requestDetails [ 2L; 3L ]
        Assert.Equal<int64 list>([ 1L; 2L; 3L ], s.Wanted)

    [<Fact>]
    let ``queued releases jump ahead of syncs and viewed pages`` () =
        let s0 = empty |> requestSync "a" |> requestDetails [ 10L ] |> requestQueuedDetails [ 20L; 21L ]
        Assert.Equal(Details 20L, next s0)
        let s1 = s0 |> detailsStepped 20L
        Assert.Equal(Details 21L, next s1)
        // Jumping the line doesn't use up the viewed page's turn: the sync still goes first.
        let s2 = s1 |> detailsStepped 21L
        Assert.Equal(SyncPage { Seller = "a"; Progress = NotStarted }, next s2)

    [<Fact>]
    let ``cancelling a sync mid-way drops it from the rotation and leaves the others`` () =
        let s = empty |> requestSync "a" |> requestSync "b" |> syncStepped "b" (Advanced(1L, 7)) |> cancelSync "B"
        Assert.Equal<string list>([ "a" ], sellersInRotation s)
        Assert.False(isSyncing "b" s)

    [<Fact>]
    let ``nothing asked for means unprompted work`` () =
        Assert.Equal(Unprompted, next empty)

module SyncRunTests =
    let withTempDb (f: Microsoft.Data.Sqlite.SqliteConnection -> unit) =
        let dir = IO.Path.Combine(IO.Path.GetTempPath(), $"digga-test-{Guid.NewGuid()}")
        IO.Directory.CreateDirectory dir |> ignore
        let paths: Db.Paths = { App = IO.Path.Combine(dir, "app.db"); Catalog = IO.Path.Combine(dir, "catalog.db") }
        Catalog.ensure paths.Catalog

        try
            using (Db.connect paths) (fun conn ->
                Db.migrate conn
                f conn)
        finally
            IO.Directory.Delete(dir, true)

    let at minutes = DateTimeOffset(2026, 10, 1, 12, minutes, 0, TimeSpan.Zero)

    let statusOf conn seller =
        (Db.sellers conn |> List.find (fun x -> x.Seller = seller)).LatestRun.Status

    [<Fact>]
    let ``runs record their outcome`` () =
        withTempDb (fun conn ->
            let a = Db.startRun conn "a" (at 0)
            let b = Db.startRun conn "b" (at 1)
            Assert.Equal(Running, statusOf conn "a")
            Db.completeRun conn a (at 2)
            Db.failRun conn b (at 3) "boom"
            Assert.Equal(Completed(at 2), statusOf conn "a")
            Assert.Equal(Failed(at 3, "boom"), statusOf conn "b"))

    [<Fact>]
    let ``restart records runs that were cut off`` () =
        withTempDb (fun conn ->
            Db.startRun conn "a" (at 0) |> ignore
            Assert.Equal(1, Db.failInterruptedRuns conn (at 1))
            Assert.Equal(Failed(at 1, "interrupted: server restarted mid-sync"), statusOf conn "a"))

    [<Fact>]
    let ``migrating twice is a no-op`` () =
        withTempDb (fun conn -> Db.migrate conn)

module SellerViewTests =
    [<Fact>]
    let ``a seller whose first sync hasn't started is listed as syncing`` () =
        let views = Api.sellerViews [] (Scheduler.empty |> Scheduler.requestSync "HardwaxBerlin")
        let v = Assert.Single views
        Assert.Equal("HardwaxBerlin", v.Seller)
        Assert.True v.Syncing
        Assert.Equal(None, v.LatestRun)

module QueueTests =
    open SyncRunTests

    let listing releaseId : InventoryListing =
        { ListingId = releaseId * 10L; ReleaseId = releaseId; Artist = "A"; Title = "T"; Label = "L"
          CatalogNumber = "C"; Format = "LP"; Year = None; Thumbnail = ""; Want = 1; Have = 1; Price = 10.0
          Currency = "EUR"; Condition = "VG+"; SleeveCondition = "VG+"; Uri = ""; Posted = "" }

    let withRelease releaseId f =
        withTempDb (fun conn ->
            let run = Db.startRun conn "s" (at 0)
            Db.recordInventoryPage conn run "s" { Page = 1; Pages = 1; Items = 1; Listings = [ listing releaseId ] } (at 0)
            f conn)

    let details videos : ReleaseDetails =
        { Genres = [ "Electronic" ]; Styles = []; Want = 1; Have = 1
          Videos = videos |> List.map (fun id -> { YoutubeId = id; Title = id; DurationSeconds = 60 }) }

    let items conn = Db.queue conn |> List.map _.Item

    [<Fact>]
    let ``a release can be queued before its videos are known, and becomes its tracks when they arrive`` () =
        withRelease 1L (fun conn ->
            Assert.Equal(Db.QueuedAwaitingVideos true, Db.enqueueRelease conn 1L (at 1))
            Assert.Equal(Db.QueuedAwaitingVideos false, Db.enqueueRelease conn 1L (at 2))
            Assert.Equal<Db.QueueItem list>([ Db.AwaitingVideos ], items conn)

            Db.saveReleaseDetails conn 1L (details [ "b"; "a" ]) (at 3)

            let tracks = Db.queue conn
            Assert.Equal<string list>([ "b"; "a" ], tracks |> List.map (fun e -> match e.Item with Db.Track v -> v.YoutubeId | other -> string other))
            Assert.All(tracks, fun e -> Assert.Equal(at 1, e.AddedAt)))

    [<Fact>]
    let ``a queued release with no videos stays visible as nothing to play`` () =
        withRelease 1L (fun conn ->
            Db.enqueueRelease conn 1L (at 1) |> ignore
            Db.saveReleaseDetails conn 1L (details []) (at 2)
            Assert.Equal<Db.QueueItem list>([ Db.NoVideos ], items conn)
            Assert.Equal(Db.NothingToPlay, Db.enqueueRelease conn 1L (at 3)))

    [<Fact>]
    let ``queueing a fetched release adds the tracks not already queued`` () =
        withRelease 1L (fun conn ->
            Db.saveReleaseDetails conn 1L (details [ "a"; "b" ]) (at 1)
            Assert.Equal(Db.Added, Db.enqueue conn 1L "a" (at 2))
            Assert.Equal(Db.QueuedTracks 1, Db.enqueueRelease conn 1L (at 3))
            Assert.Equal(2, (Db.queue conn).Length))

    [<Fact>]
    let ``refetching details still drops stale videos while a placeholder is queued`` () =
        withRelease 1L (fun conn ->
            Db.saveReleaseDetails conn 1L (details [ "old" ]) (at 1)
            // A placeholder alongside fetched videos: only reachable via the migration, but NOT IN must survive it.
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "INSERT INTO queue_item (release_id, youtube_id, added_at) VALUES (1, NULL, '2026-10-01T12:00:00Z')"
            cmd.ExecuteNonQuery() |> ignore
            Db.saveReleaseDetails conn 1L (details [ "new" ]) (at 2)
            let videos = (Db.sellerReleases conn "s" |> List.exactlyOne).Videos |> List.map _.YoutubeId
            Assert.Equal<string list>([ "new" ], videos))

    [<Fact>]
    let ``items are removed by id`` () =
        withRelease 1L (fun conn ->
            Db.enqueueRelease conn 1L (at 1) |> ignore
            let id = (Db.queue conn |> List.exactlyOne).Id
            Assert.True(Db.dequeue conn id)
            Assert.Empty(Db.queue conn))

module ForgetSellerTests =
    open SyncRunTests
    open QueueTests

    let page listings : InventoryPage = { Page = 1; Pages = 1; Items = List.length listings; Listings = listings }

    [<Fact>]
    let ``forgetting a seller deletes its runs and listings and the releases nothing else needs`` () =
        withTempDb (fun conn ->
            let gone = Db.startRun conn "gone" (at 0)
            // 1: only "gone" lists it. 2: "kept" lists it too. 3: queued. 4: listened to.
            Db.recordInventoryPage conn gone "gone" (page [ listing 1L; listing 2L; listing 3L; listing 4L ]) (at 0)
            let kept = Db.startRun conn "kept" (at 1)
            Db.recordInventoryPage conn kept "kept" (page [ { listing 2L with ListingId = 99L } ]) (at 1)
            Db.saveReleaseDetails conn 1L (details [ "v1" ]) (at 2)
            Db.enqueueRelease conn 3L (at 2) |> ignore
            Db.saveReleaseDetails conn 4L (details [ "v4" ]) (at 2)
            let subject = (Db.listenSubject conn 4L "v4").Value
            Db.recordListen conn subject (at 3) NotConfigured |> ignore

            let summary = Db.forgetSeller conn "GONE"

            Assert.Equal(({ Runs = 1; Listings = 4; Releases = 1 }: Db.ForgetSummary), summary)
            Assert.Equal<string list>([ "kept" ], Db.sellers conn |> List.map _.Seller)
            Assert.Equal(1, (Db.sellerReleases conn "kept").Length)
            Assert.Equal<Db.QueueItem list>([ Db.AwaitingVideos ], items conn)
            Assert.True((Db.listenSubject conn 4L "v4").IsSome))

module RatingTests =
    open SyncRunTests
    open QueueTests

    let ratingOf conn releaseId =
        (Db.sellerReleases conn "s" |> List.find (fun r -> r.ReleaseId = releaseId)).Rating

    [<Fact>]
    let ``a record's rating can be set, changed and cleared`` () =
        withRelease 1L (fun conn ->
            Assert.Equal(Db.Rated, Db.rate conn 1L Like (at 1))
            Assert.Equal(Some Like, ratingOf conn 1L)
            Db.rate conn 1L Dislike (at 2) |> ignore
            Assert.Equal(Some Dislike, ratingOf conn 1L)
            Assert.True(Db.unrate conn 1L)
            Assert.Equal(None, ratingOf conn 1L)
            Assert.False(Db.unrate conn 1L)
            Assert.Equal(Db.NoSuchRelease, Db.rate conn 999L Like (at 3)))

    [<Fact>]
    let ``forgetting a seller keeps the records you rated`` () =
        withRelease 1L (fun conn ->
            Db.rate conn 1L Like (at 1) |> ignore
            let summary = Db.forgetSeller conn "s"
            Assert.Equal(0, summary.Releases))

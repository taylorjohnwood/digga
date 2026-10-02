/// The only caller of Discogs. One request per step: the Scheduler picks the work, this module
/// performs it and feeds the outcome back. Rate-limit pacing and the schedule are values threaded
/// through the loop; the schedule is also published so the API can show what's syncing.
module Digga.Worker

open System
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Digga.Discogs
open Digga.Scheduler

type Request =
    | Sync of seller: string
    /// Releases on a page someone is viewing: fetch their videos ahead of anything unprompted.
    | FetchDetails of releaseIds: int64 list
    /// Releases in the digging queue still waiting for videos: fetch before anything else.
    | FetchQueued of releaseIds: int64 list
    /// Stop syncing the seller and delete what's been synced for it, replying when done. Done by
    /// the worker, between steps, because it is the one writing the seller's pages.
    | Forget of seller: string * reply: TaskCompletionSource<Db.ForgetSummary>

type Env =
    { Discogs: Discogs.Client
      Db: Db.Paths
      Logger: ILogger
      Requests: ChannelReader<Request>
      /// The latest schedule, for the API to read. Written only by the worker loop; each write
      /// replaces one immutable State with another, so readers never see a half-made one.
      Published: State ref
      Now: unit -> DateTimeOffset }

let private withDb env f =
    use conn = Db.connect env.Db
    f conn

let private pauseAfterFault env = { NotBefore = env.Now().AddSeconds 5.0 }

type private Drained =
    { State: State
      /// Forget requests, oldest first, to carry out now that the schedule no longer has them.
      Forgets: (string * TaskCompletionSource<Db.ForgetSummary>) list }

let private apply (drained: Drained) =
    function
    | Sync seller -> { drained with State = requestSync seller drained.State }
    | FetchDetails ids -> { drained with State = requestDetails ids drained.State }
    | FetchQueued ids -> { drained with State = requestQueuedDetails ids drained.State }
    | Forget(seller, reply) ->
        { State = cancelSync seller drained.State
          Forgets = drained.Forgets @ [ seller, reply ] }

/// Folds every request waiting in the channel into the schedule.
let rec private drain (requests: ChannelReader<Request>) (drained: Drained) =
    match requests.TryRead() with
    | true, request -> drain requests (apply drained request)
    | false, _ -> drained

/// Runs between steps, so the seller has no page in flight that could land after the delete.
let private forget env (seller: string, reply: TaskCompletionSource<Db.ForgetSummary>) =
    try
        let summary = withDb env (fun c -> Db.forgetSeller c seller)
        env.Logger.LogInformation(
            "Forgot {Seller}: deleted {Runs} sync runs, {Listings} listings, {Releases} releases nothing else needed",
            seller, summary.Runs, summary.Listings, summary.Releases)
        reply.TrySetResult summary |> ignore
    with ex ->
        env.Logger.LogError(ex, "Forgetting {Seller} failed; its data is untouched", seller)
        reply.TrySetException ex |> ignore

// ---- one inventory page ---------------------------------------------------------------------

let private syncPage env rate (sync: ActiveSync) ct : Task<RateLimit * SyncOutcome> =
    task {
        let runId, page =
            match sync.Progress with
            | NotStarted ->
                let runId = withDb env (fun c -> Db.startRun c sync.Seller (env.Now()))
                env.Logger.LogInformation("Sync {RunId} {Seller}: started", runId, sync.Seller)
                runId, 1
            | InProgress(runId, page) -> runId, page

        let! rate', result =
            task {
                try
                    let! rate', result = Discogs.inventoryPage env.Discogs rate sync.Seller page ct
                    return rate', result |> Result.mapError Discogs.describe
                with ex when not (ex :? OperationCanceledException) ->
                    env.Logger.LogError(ex, "Sync {RunId} {Seller}: page {Page} faulted", runId, sync.Seller, page)
                    return pauseAfterFault env, Error ex.Message
            }

        match result with
        | Error message ->
            withDb env (fun c -> Db.failRun c runId (env.Now()) message)
            env.Logger.LogWarning("Sync {RunId} {Seller}: failed on page {Page}: {Error}", runId, sync.Seller, page, message)
            return rate', Finished
        | Ok p ->
            withDb env (fun c -> Db.recordInventoryPage c runId sync.Seller p (env.Now()))
            let lastPage = min p.Pages Discogs.maxInventoryPages

            env.Logger.LogInformation(
                "Sync {RunId} {Seller}: page {Page}/{LastPage} with {Count} listings ({Items} for sale in total)",
                runId, sync.Seller, page, lastPage, p.Listings.Length, p.Items)

            if page = 1 && p.Pages > Discogs.maxInventoryPages then
                env.Logger.LogWarning(
                    "Sync {RunId} {Seller}: {Items} listings exceed the API's {MaxPages}-page cap; only the newest {Reachable} are reachable",
                    runId, sync.Seller, p.Items, Discogs.maxInventoryPages, Discogs.maxInventoryPages * Discogs.inventoryPageSize)

            match page < lastPage with
            | true -> return rate', Advanced(runId, page + 1)
            | false ->
                // Only a completed run knows what's gone: a paused or failed one leaves listings be.
                let removed =
                    withDb env (fun c ->
                        let removed = Db.removeUnseenListings c runId sync.Seller
                        Db.completeRun c runId (env.Now())
                        removed)

                env.Logger.LogInformation(
                    "Sync {RunId} {Seller}: completed; {Total} for sale, {Removed} listings gone since last sync",
                    runId, sync.Seller, p.Items, removed)

                return rate', Finished
    }

// ---- one release's details ------------------------------------------------------------------

let private fetchDetails env rate (releaseId: int64) ct : Task<RateLimit> =
    task {
        let! rate', result = Discogs.release env.Discogs rate releaseId ct

        match result with
        | Ok details ->
            withDb env (fun c -> Db.saveReleaseDetails c releaseId details (env.Now()))
            env.Logger.LogDebug(
                "Release {ReleaseId}: {Genres} / {Styles}, {VideoCount} videos, want {Want} have {Have}",
                releaseId, details.Genres, details.Styles, details.Videos.Length, details.Want, details.Have)
        | Error e when Discogs.isPermanent e ->
            withDb env (fun c -> Db.markDetailsUnavailable c releaseId (env.Now()))
            env.Logger.LogWarning("Release {ReleaseId}: marked unavailable, won't retry: {Error}", releaseId, Discogs.describe e)
        | Error e ->
            env.Logger.LogWarning("Release {ReleaseId}: transient failure, left pending for retry: {Error}", releaseId, Discogs.describe e)

        return rate'
    }

/// The listed release the catalog lacks genres for with the highest want/have, if any.
let private topReleaseMissingGenres env : int64 option =
    withDb env (fun c ->
        let prior = Score.priorRatio (Db.listedReleaseStats c)

        Db.releasesMissingGenres c
        |> List.sortByDescending (fun (_, want, have) -> Score.wantHave prior want have)
        |> List.tryHead
        |> Option.map (fun (id, _, _) -> id))

// ---- main loop ------------------------------------------------------------------------------

let private step env (rate: RateLimit) (state: State) ct : Task<RateLimit * State> =
    match next state with
    | SyncPage sync ->
        task {
            let! rate', outcome = syncPage env rate sync ct
            return rate', syncStepped sync.Seller outcome state
        }
    | Details releaseId ->
        task {
            let reason = if List.contains releaseId state.Queued then "queued" else "on a viewed page"

            // Pages are re-requested while they fill in: skip what's been fetched meanwhile.
            match withDb env (fun c -> Db.pendingDetails c [ releaseId ]) with
            | [] ->
                env.Logger.LogDebug("Release {ReleaseId} ({Reason}): details already fetched, skipping", releaseId, reason)
                return rate, detailsStepped releaseId state
            | _ ->
                env.Logger.LogDebug("Release {ReleaseId} ({Reason}): fetching details", releaseId, reason)
                let! rate' = fetchDetails env rate releaseId ct
                return rate', detailsStepped releaseId state
        }
    | Unprompted ->
        match topReleaseMissingGenres env with
        | Some releaseId ->
            task {
                env.Logger.LogDebug("Unprompted: fetching genres for release {ReleaseId}, which the catalog lacks", releaseId)
                let! rate' = fetchDetails env rate releaseId ct
                return rate', state
            }
        | None ->
            task {
                env.Logger.LogDebug("Worker idle: nothing syncing, nothing viewed needs videos, every listed release has genres")
                let! _ = env.Requests.WaitToReadAsync ct
                return rate, state
            }

/// Never returns until cancelled. `async`, not `task`: async's `return!` is a real tail call,
/// task's is not, so an endless recursive task loop would grow without bound.
let rec private loop env (rate: RateLimit) (state: State) : Async<unit> =
    async {
        let! ct = Async.CancellationToken
        let drained = drain env.Requests { State = state; Forgets = [] }
        let state = drained.State
        env.Published.Value <- state
        drained.Forgets |> List.iter (forget env)

        let! rate', state' =
            async {
                try
                    return! Async.AwaitTask(step env rate state ct)
                with
                | :? OperationCanceledException -> return raise (OperationCanceledException ct)
                | ex ->
                    env.Logger.LogError(ex, "Worker step {Work} faulted; pausing before continuing", next state)
                    // Drop whatever faulted so a persistent fault can't wedge the loop on it.
                    return
                        pauseAfterFault env,
                        match next state with
                        | SyncPage sync -> syncStepped sync.Seller Finished state
                        | Details releaseId -> detailsStepped releaseId state
                        | Unprompted -> state
            }

        env.Published.Value <- state'
        return! loop env rate' state'
    }

type SyncService(env: Env) =
    inherit BackgroundService()

    override _.ExecuteAsync(ct: CancellationToken) =
        env.Logger.LogInformation(
            "Worker starting; Discogs {Auth}",
            (match env.Discogs.Token with
             | Some _ -> "authenticated (60 req/min)"
             | None -> "unauthenticated (25 req/min) — set DISCOGS_TOKEN"))

        Async.StartAsTask(loop env RateLimit.initial empty, cancellationToken = ct) :> Task

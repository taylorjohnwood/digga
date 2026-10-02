/// What the worker does with its next Discogs request. Pure: an immutable state, the transitions
/// requests and finished work make to it, and `next` choosing the work. Lives in memory only —
/// what's queued is scheduling, not a fact worth persisting.
module Digga.Scheduler

open System

type SyncProgress =
    | NotStarted
    | InProgress of runId: int64 * nextPage: int

type ActiveSync = { Seller: string; Progress: SyncProgress }

/// Whose turn it is when both syncs and viewed releases are waiting: they alternate, so a list
/// fills in while the videos for what's on screen load.
type Turn =
    | SyncTurn
    | DetailsTurn

type State =
    { /// Releases in the digging queue still waiting for their videos. Ahead of everything else:
      /// you've asked to listen to these, so they shouldn't wait behind syncs or browsing.
      Queued: int64 list
      /// Rotation order: the head gets the next inventory page, then goes to the back. Every
      /// active sync makes progress — two tabs on two sellers both fill in, sharing the rate limit.
      Syncs: ActiveSync list
      /// Releases on pages someone is viewing that need videos, oldest request first.
      Wanted: int64 list
      Turn: Turn }

type Work =
    | SyncPage of ActiveSync
    | Details of releaseId: int64
    /// Nothing asked for: fill in genres for releases the catalog lacks.
    | Unprompted

type SyncOutcome =
    | Advanced of runId: int64 * nextPage: int
    | Finished

let empty = { Queued = []; Syncs = []; Wanted = []; Turn = SyncTurn }

let private sameSeller (a: string) (b: string) =
    String.Equals(a, b, StringComparison.OrdinalIgnoreCase)

let isSyncing (seller: string) (state: State) =
    state.Syncs |> List.exists (fun s -> sameSeller s.Seller seller)

/// A new seller goes to the front so it starts on the very next request; asking for one that's
/// already syncing changes nothing.
let requestSync (seller: string) (state: State) =
    match isSyncing seller state with
    | true -> state
    | false -> { state with Syncs = { Seller = seller; Progress = NotStarted } :: state.Syncs }

let private appendNew (existing: int64 list) (ids: int64 list) =
    existing @ (ids |> List.filter (fun id -> not (List.contains id existing))) |> List.distinct

/// Drops a seller from the rotation, wherever it is in its sync. Its recorded progress is the
/// worker's to delete; this only stops further pages being asked for.
let cancelSync (seller: string) (state: State) =
    { state with Syncs = state.Syncs |> List.filter (fun s -> not (sameSeller s.Seller seller)) }

let requestDetails (releaseIds: int64 list) (state: State) =
    { state with Wanted = appendNew state.Wanted releaseIds }

let requestQueuedDetails (releaseIds: int64 list) (state: State) =
    { state with Queued = appendNew state.Queued releaseIds }

let next (state: State) : Work =
    match state.Queued, state.Syncs, state.Wanted, state.Turn with
    | releaseId :: _, _, _, _ -> Details releaseId
    | [], sync :: _, [], _
    | [], sync :: _, _ :: _, SyncTurn -> SyncPage sync
    | [], _, releaseId :: _, _ -> Details releaseId
    | [], [], [], _ -> Unprompted

/// After one inventory page for `seller`: rotate it to the back, or drop it once finished.
let syncStepped (seller: string) (outcome: SyncOutcome) (state: State) =
    let others = state.Syncs |> List.filter (fun s -> not (sameSeller s.Seller seller))

    { state with
        Syncs =
            match outcome with
            | Advanced(runId, page) -> others @ [ { Seller = seller; Progress = InProgress(runId, page) } ]
            | Finished -> others
        Turn = DetailsTurn }

let detailsStepped (releaseId: int64) (state: State) =
    { state with
        Queued = state.Queued |> List.filter ((<>) releaseId)
        Wanted = state.Wanted |> List.filter ((<>) releaseId)
        // A queued release jumped the line rather than taking the details turn.
        Turn =
            match List.contains releaseId state.Queued with
            | true -> state.Turn
            | false -> SyncTurn }

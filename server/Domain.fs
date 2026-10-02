module Digga.Domain

open System

/// The recorded outcome of a sync run. Whether a sync is waiting or how syncs are scheduled is the
/// worker's in-memory concern, not a fact stored here.
type SyncStatus =
    | Running
    | Completed of finishedAt: DateTimeOffset
    | Failed of finishedAt: DateTimeOffset * error: string

type SyncRun =
    { Id: int64
      Seller: string
      StartedAt: DateTimeOffset
      Status: SyncStatus
      /// Listings the seller has for sale, as Discogs reports it. Unknown until the first page arrives.
      ListingsTotal: int option
      ListingsSeen: int }

/// Whether the per-release details (genres, styles, videos) have been fetched.
type DetailsStatus =
    | Pending
    | Fetched
    /// Discogs returned 404 or similar; retrying will not help.
    | Unavailable

/// Your verdict on a record. No rating is the absence of one, not a third case.
type Verdict =
    | Like
    | Dislike

type ListenBrainzOutcome =
    | NotConfigured
    | Submitted
    | SubmitFailed of error: string

/// One marketplace listing from a seller's inventory, with the release summary Discogs embeds in it.
type InventoryListing =
    { ListingId: int64
      ReleaseId: int64
      Artist: string
      Title: string
      Label: string
      CatalogNumber: string
      Format: string
      Year: int option
      Thumbnail: string
      Want: int
      Have: int
      Price: float
      Currency: string
      Condition: string
      SleeveCondition: string
      Uri: string
      Posted: string }

type InventoryPage =
    { Page: int
      Pages: int
      Items: int
      Listings: InventoryListing list }

type Video =
    { YoutubeId: string
      Title: string
      DurationSeconds: int }

type ReleaseDetails =
    { Genres: string list
      Styles: string list
      Videos: Video list
      Want: int
      Have: int }

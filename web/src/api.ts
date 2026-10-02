// Mirrors the F# server's JSON. Unions arrive as { kind: "Case", ...fields }.

import type { TagQuery } from './tagQuery'

export type SyncStatus =
  | { kind: 'Running' }
  | { kind: 'Completed'; finishedAt: string }
  | { kind: 'Failed'; finishedAt: string; error: string }

export type DetailsStatus = { kind: 'Pending' } | { kind: 'Fetched' } | { kind: 'Unavailable' }

/** Genres/styles from the catalog export or the API; Unknown until one of them has answered. */
export type Tags = { kind: 'Unknown' } | { kind: 'Known'; genres: string[]; styles: string[] }

/** Your thumbs up/down on a record; null when unrated. */
export type Verdict = { kind: 'Like' } | { kind: 'Dislike' }

export type ListenBrainzOutcome =
  | { kind: 'NotConfigured' }
  | { kind: 'Submitted' }
  | { kind: 'SubmitFailed'; error: string }

export type SyncRun = {
  id: number
  seller: string
  startedAt: string
  status: SyncStatus
  listingsTotal: number | null
  listingsSeen: number
}

export type SellerSummary = {
  seller: string
  /** Null until the seller's first sync has fetched a page. */
  latestRun: SyncRun | null
  /** In the server's sync rotation right now (shared fairly with any other syncing sellers). */
  syncing: boolean
  listings: number
  releases: number
  /** Listed releases newer than the catalog export whose genres haven't been fetched yet. */
  genresUnknown: number
}

export type Listing = {
  listingId: number
  price: number
  currency: string
  condition: string
  sleeveCondition: string
  uri: string
}

export type Video = {
  youtubeId: string
  title: string
  durationSeconds: number
  heard: boolean
  queued: boolean
}

export type Release = {
  releaseId: number
  artist: string
  title: string
  label: string
  catalogNumber: string
  format: string
  year: number | null
  thumbnail: string
  want: number
  have: number
  /** Whether videos have been fetched from the API. */
  detailsStatus: DetailsStatus
  /** The whole release is in the queue, waiting for its videos. */
  queuedAwaitingVideos: boolean
  rating: Verdict | null
  tags: Tags
  videos: Video[]
  listings: Listing[]
}

export type Facet = { name: string; count: number }

export type ReleasePage = {
  total: number
  matching: number
  genresUnknown: number
  priorRatio: number
  genres: Facet[]
  styles: Facet[]
  items: { release: Release; score: number }[]
}

export type QueuedRelease = {
  releaseId: number
  artist: string
  releaseTitle: string
  label: string
  thumbnail: string
  rating: Verdict | null
}

export type QueuedVideo = { youtubeId: string; title: string; durationSeconds: number }

export type QueueItem =
  | { kind: 'Track'; video: QueuedVideo }
  /** A whole release queued before its videos were known; the server fetches them first. */
  | { kind: 'AwaitingVideos' }
  /** A whole release Discogs has no videos for. */
  | { kind: 'NoVideos' }

export type QueueEntry = { id: number; release: QueuedRelease; addedAt: string; item: QueueItem }

export type Track = QueueEntry & { item: Extract<QueueItem, { kind: 'Track' }> }

export const isTrack = (e: QueueEntry): e is Track => e.item.kind === 'Track'

export type CatalogSummary = { dump: string; importedAt: string; releases: number }

export type Config = {
  discogsAuthenticated: boolean
  listenBrainzConfigured: boolean
  catalog: CatalogSummary | null
}

export type Filter = { tags: TagQuery; hideHeard: boolean; hideRated: boolean }

export const pageSize = 50

const request = async <T>(path: string, init?: RequestInit): Promise<T> => {
  const response = await fetch(path, {
    ...init,
    headers: init?.body ? { 'Content-Type': 'application/json' } : undefined,
  })
  if (!response.ok) {
    const body = await response.text()
    throw new Error(`${init?.method ?? 'GET'} ${path} → ${response.status}: ${body}`)
  }
  return response.status === 204 ? (undefined as T) : ((await response.json()) as T)
}

const query = (params: Record<string, string | number | boolean | null>) =>
  new URLSearchParams(
    Object.entries(params)
      .filter(([, v]) => v !== null && v !== false)
      .map(([k, v]) => [k, String(v)]),
  ).toString()

export const api = {
  config: () => request<Config>('/api/config'),
  sellers: () => request<SellerSummary[]>('/api/sellers'),
  sync: (seller: string) =>
    request<{ seller: string; alreadySyncing: boolean }>(`/api/sellers/${encodeURIComponent(seller)}/sync`, {
      method: 'POST',
    }),
  /** Cancels the seller's sync if running and deletes what's been synced for it. */
  forget: (seller: string) =>
    request<{ seller: string; wasSyncing: boolean; deleted: { runs: number; listings: number; releases: number } }>(
      `/api/sellers/${encodeURIComponent(seller)}`,
      { method: 'DELETE' },
    ),
  releases: (seller: string, filter: Filter, offset: number) =>
    request<ReleasePage>(
      `/api/sellers/${encodeURIComponent(seller)}/releases?${query({
        // JSON: tag names contain commas and ampersands, so no delimiter is safe.
        tags: filter.tags.length > 0 ? JSON.stringify(filter.tags) : null,
        hideHeard: filter.hideHeard,
        hideRated: filter.hideRated,
        offset,
        limit: pageSize,
      })}`,
    ),
  queue: () => request<QueueEntry[]>('/api/queue'),
  enqueue: (releaseId: number, youtubeId: string) =>
    request<{ queued: boolean }>('/api/queue', { method: 'POST', body: JSON.stringify({ releaseId, youtubeId }) }),
  /** Every video of the release, or the release itself if its videos aren't fetched yet. */
  enqueueRelease: (releaseId: number) =>
    request<{ queued: boolean }>('/api/queue', { method: 'POST', body: JSON.stringify({ releaseId, youtubeId: null }) }),
  dequeue: (queueItemId: number) => request<void>(`/api/queue/${queueItemId}`, { method: 'DELETE' }),
  rate: (releaseId: number, verdict: Verdict) =>
    request<{ rating: Verdict }>(`/api/releases/${releaseId}/rating`, { method: 'PUT', body: JSON.stringify({ verdict }) }),
  unrate: (releaseId: number) => request<void>(`/api/releases/${releaseId}/rating`, { method: 'DELETE' }),
  listen: (releaseId: number, youtubeId: string, startedAt: number) =>
    request<{ listenId: number; listenBrainz: ListenBrainzOutcome }>('/api/listens', {
      method: 'POST',
      body: JSON.stringify({ releaseId, youtubeId, startedAt }),
    }),
}

export const isSyncing = (s: SellerSummary) => s.syncing

export const tagsOf = (r: Release) => (r.tags.kind === 'Known' ? [...r.tags.genres, ...r.tags.styles] : [])

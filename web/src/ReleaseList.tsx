import { useMutation, useQueryClient } from '@tanstack/react-query'
import { api, tagsOf, type Release, type ReleasePage, type Video } from './api'
import { formatDuration, formatPrice } from './format'
import { Thumbs } from './Thumbs'

function VideoRow({ release, video }: { release: Release; video: Video }) {
  const client = useQueryClient()
  const enqueue = useMutation({
    mutationFn: () => api.enqueue(release.releaseId, video.youtubeId),
    onSuccess: () => client.invalidateQueries({ queryKey: ['queue'] }).then(() =>
      client.invalidateQueries({ queryKey: ['releases'] })),
  })

  return (
    <li className={video.heard ? 'video heard' : 'video'}>
      <button
        className="add"
        disabled={video.queued || enqueue.isPending}
        onClick={() => enqueue.mutate()}
        title={video.queued ? 'In your digging queue' : 'Add to digging queue'}
      >
        {video.queued ? '✓' : '+'}
      </button>
      <span className="video-title">{video.title}</span>
      <span className="duration">{formatDuration(video.durationSeconds)}</span>
      {video.heard && <span className="heard-mark" title="You've listened to this">heard</span>}
    </li>
  )
}

type ReleaseQueueState = 'can-queue' | 'awaiting-videos' | 'all-queued' | 'nothing-to-play'

const releaseQueueState = (r: Release): ReleaseQueueState => {
  if (r.queuedAwaitingVideos) return 'awaiting-videos'
  switch (r.detailsStatus.kind) {
    case 'Pending':
      return 'can-queue'
    case 'Unavailable':
      return 'nothing-to-play'
    case 'Fetched':
      if (r.videos.length === 0) return 'nothing-to-play'
      return r.videos.every((v) => v.queued) ? 'all-queued' : 'can-queue'
  }
}

function QueueRelease({ release }: { release: Release }) {
  const client = useQueryClient()
  const enqueue = useMutation({
    mutationFn: () => api.enqueueRelease(release.releaseId),
    onSuccess: () => client.invalidateQueries({ queryKey: ['queue'] }).then(() =>
      client.invalidateQueries({ queryKey: ['releases'] })),
  })

  const state = releaseQueueState(release)
  if (state === 'nothing-to-play') return null
  const label = {
    'can-queue': '+ Queue release',
    'awaiting-videos': '✓ Queued — fetching its videos first',
    'all-queued': '✓ All tracks queued',
  }[state]

  return (
    <button
      className="queue-release"
      disabled={state !== 'can-queue' || enqueue.isPending}
      onClick={() => enqueue.mutate()}
      title={state === 'can-queue' ? 'Add every track of this release to the digging queue' : undefined}
    >
      {label}
    </button>
  )
}

function Videos({ release }: { release: Release }) {
  switch (release.detailsStatus.kind) {
    case 'Pending':
      return <p className="muted">Fetching videos…</p>
    case 'Unavailable':
      return <p className="muted">Discogs has no details for this release.</p>
    case 'Fetched':
      return release.videos.length === 0 ? (
        <p className="muted">No videos on Discogs — nothing to preview.</p>
      ) : (
        <ul className="videos">
          {release.videos.map((v) => (
            <VideoRow key={v.youtubeId} release={release} video={v} />
          ))}
        </ul>
      )
  }
}

function ReleaseRow({ release, score, rank }: { release: Release; score: number; rank: number }) {
  const cheapest = release.listings[0]
  const details = [release.label, release.catalogNumber, release.year, release.format].filter(Boolean).join(' · ')

  return (
    <article className="release">
      <div className="rank">{rank}</div>
      {release.thumbnail ? (
        <img className="thumb" src={release.thumbnail} alt="" loading="lazy" />
      ) : (
        <div className="thumb placeholder" aria-hidden />
      )}
      <div className="body">
        <header>
          <a href={`https://www.discogs.com/release/${release.releaseId}`} target="_blank" rel="noreferrer">
            <span className="artist">{release.artist}</span> — {release.title}
          </a>
          <Thumbs releaseId={release.releaseId} rating={release.rating} />
          <span
            className="score"
            title={`Want/have, smoothed towards the prior so thinly-owned releases don't dominate.\n${release.want} want · ${release.have} have`}
          >
            {score.toFixed(2)}
            <small>
              {release.want} / {release.have}
            </small>
          </span>
        </header>
        <p className="details">{details}</p>
        {release.tags.kind === 'Unknown' ? (
          <p className="tags muted">genre not known yet</p>
        ) : (
          <p className="tags">
            {tagsOf(release).map((t) => (
              <span key={t}>{t}</span>
            ))}
          </p>
        )}
        {cheapest && (
          <p className="listing">
            <a href={cheapest.uri} target="_blank" rel="noreferrer">
              {formatPrice(cheapest.price, cheapest.currency)} · {cheapest.condition} / {cheapest.sleeveCondition}
            </a>
            {release.listings.length > 1 && <span className="muted"> +{release.listings.length - 1} more copies</span>}
          </p>
        )}
        <Videos release={release} />
        <QueueRelease release={release} />
      </div>
    </article>
  )
}

type Props = {
  pages: ReleasePage[]
  /** The seller is still syncing, so an empty list may just not have arrived yet. */
  waiting: boolean
  loading: boolean
  error: Error | null
  hasMore: boolean
  loadingMore: boolean
  onLoadMore: () => void
}

export function ReleaseList({ pages, waiting, loading, error, hasMore, loadingMore, onLoadMore }: Props) {
  const items = pages.flatMap((p) => p.items)
  const matching = pages[0]?.matching ?? 0

  if (error) return <p className="empty error">{error.message}</p>
  if (loading) return <p className="empty">Loading…</p>
  if (items.length === 0)
    return <p className="empty">{waiting ? 'Waiting for the first page of listings…' : 'Nothing matches.'}</p>

  return (
    <>
      <p className="summary">
        {matching} releases, ranked by want/have
      </p>
      {items.map(({ release, score }, i) => (
        <ReleaseRow key={release.releaseId} release={release} score={score} rank={i + 1} />
      ))}
      {hasMore && (
        <button className="more" onClick={onLoadMore} disabled={loadingMore}>
          {loadingMore ? 'Loading…' : `Show more (${items.length} of ${matching})`}
        </button>
      )}
    </>
  )
}

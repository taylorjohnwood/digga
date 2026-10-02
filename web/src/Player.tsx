import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useReducer, useState } from 'react'
import YouTube from 'react-youtube'
import { api, isTrack, type QueueEntry, type Track } from './api'
import { initialPlayback, listenThreshold, playbackReducer, shouldLog, type Playback } from './playback'
import { formatDuration } from './format'
import { Thumbs } from './Thumbs'

// YouTube IFrame API error codes 101 and 150 both mean "the owner disallows embedding".
const youtubeError = (code: number) =>
  code === 101 || code === 150 ? "This video can't be played outside YouTube." : `YouTube error ${code}.`

const listenLabel = (p: Playback, duration: number): { text: string; tone: '' | 'good' | 'bad' } => {
  if (p.kind !== 'listening') return { text: '', tone: '' }
  switch (p.logging.kind) {
    case 'not-yet':
      return {
        text: `Logs as a listen at ${formatDuration(Math.ceil(listenThreshold(duration)))} played (${formatDuration(p.played)} so far)`,
        tone: '',
      }
    case 'sending':
      return { text: 'Logging listen…', tone: '' }
    case 'failed':
      return { text: "Couldn't log this listen", tone: 'bad' }
    case 'saved': {
      const lb = p.logging.listenBrainz
      switch (lb.kind) {
        case 'Submitted':
          return { text: 'Listen logged to ListenBrainz', tone: 'good' }
        case 'NotConfigured':
          return { text: 'Listen saved in digga (ListenBrainz not set up)', tone: '' }
        case 'SubmitFailed':
          return { text: `Saved in digga, but ListenBrainz refused it: ${lb.error}`, tone: 'bad' }
      }
    }
  }
}

function NowPlaying({ current, onStarted }: { current: Track; onStarted: () => void }) {
  const client = useQueryClient()
  const [playback, dispatch] = useReducer(playbackReducer, initialPlayback)
  const [error, setError] = useState<string | null>(null)
  const release = current.release
  const video = current.item.video

  // Count seconds actually played.
  const running = playback.kind === 'listening' && playback.running
  useEffect(() => {
    if (!running) return
    const id = setInterval(() => dispatch({ type: 'tick', seconds: 1 }), 1000)
    return () => clearInterval(id)
  }, [running])

  // Log the listen once it passes the scrobble threshold.
  useEffect(() => {
    if (!shouldLog(playback, video.durationSeconds)) return
    dispatch({ type: 'logging', state: { kind: 'sending' } })
    api.listen(release.releaseId, video.youtubeId, playback.startedAt).then(
      ({ listenBrainz }) => {
        if (listenBrainz.kind === 'SubmitFailed')
          console.warn('ListenBrainz refused the listen', { youtubeId: video.youtubeId, error: listenBrainz.error })
        dispatch({ type: 'logging', state: { kind: 'saved', listenBrainz } })
        client.invalidateQueries({ queryKey: ['releases'] })
      },
      (e: Error) => {
        console.error('Logging listen failed', { queueItemId: current.id, youtubeId: video.youtubeId, error: e.message })
        dispatch({ type: 'logging', state: { kind: 'failed' } })
      },
    )
  }, [playback, current.id, release.releaseId, video, client])

  const advance = useMutation({
    mutationFn: () => api.dequeue(current.id),
    onSuccess: () => client.invalidateQueries({ queryKey: ['queue'] }).then(() =>
      client.invalidateQueries({ queryKey: ['releases'] })),
  })

  const listen = listenLabel(playback, video.durationSeconds)

  return (
    <section className="now-playing">
      <YouTube
        className="video-frame"
        videoId={video.youtubeId}
        opts={{ width: '100%', height: '100%', playerVars: { autoplay: 1 } }}
        onPlay={() => {
          onStarted()
          dispatch({ type: 'playing', now: Math.floor(Date.now() / 1000) })
        }}
        onPause={() => dispatch({ type: 'stopped' })}
        onStateChange={(e) => e.data === 3 && dispatch({ type: 'stopped' })}
        onEnd={() => advance.mutate()}
        onError={(e) => setError(youtubeError(e.data))}
      />
      <div className="now-meta">
        <strong>{video.title}</strong>
        <span>
          {release.artist} — {release.releaseTitle}
        </span>
        <span className="muted">{release.label}</span>
        <Thumbs releaseId={release.releaseId} rating={release.rating} />
        <span className={`listen-state ${listen.tone}`}>{listen.text}</span>
        {error && <span className="error">{error}</span>}
        <button onClick={() => advance.mutate()} disabled={advance.isPending}>
          Next ⏭
        </button>
      </div>
    </section>
  )
}

const entryLabel = (entry: QueueEntry) => {
  switch (entry.item.kind) {
    case 'Track':
      return <span className="video-title">{entry.item.video.title}</span>
    case 'AwaitingVideos':
      return (
        <span className="video-title muted">
          {entry.release.artist} — {entry.release.releaseTitle} · fetching videos…
        </span>
      )
    case 'NoVideos':
      return (
        <span className="video-title muted">
          {entry.release.artist} — {entry.release.releaseTitle} · no videos on Discogs
        </span>
      )
  }
}

function QueueRow({ entry }: { entry: QueueEntry }) {
  const client = useQueryClient()
  const remove = useMutation({
    mutationFn: () => api.dequeue(entry.id),
    onSuccess: () => client.invalidateQueries({ queryKey: ['queue'] }).then(() =>
      client.invalidateQueries({ queryKey: ['releases'] })),
  })

  return (
    <li>
      {entryLabel(entry)}
      <span className="muted">{entry.release.label}</span>
      <button className="link" onClick={() => remove.mutate()} disabled={remove.isPending} title="Remove from queue">
        ✕
      </button>
    </li>
  )
}

export function Player() {
  const client = useQueryClient()
  const queue = useQuery({
    queryKey: ['queue'],
    queryFn: api.queue,
    // Poll while a queued release waits for its videos; reading the queue is also what keeps
    // the server fetching them first.
    refetchInterval: (q) => (q.state.data?.some((e) => e.item.kind === 'AwaitingVideos') ? 2000 : false),
  })
  const entries = queue.data ?? []
  // Once a track starts it keeps playing even if an earlier-queued release gets its videos and
  // lands ahead of it. Otherwise: the first playable track, skipping releases still waiting.
  const [started, setStarted] = useState<number | null>(null)
  const current = entries.filter(isTrack).find((e) => e.id === started) ?? entries.find(isTrack)
  const upNext = entries.filter((e) => e.id !== current?.id)

  // When a waiting release turns into tracks, its row in the list should show them too.
  const awaiting = entries.filter((e) => e.item.kind === 'AwaitingVideos').length
  useEffect(() => {
    client.invalidateQueries({ queryKey: ['releases'] })
  }, [awaiting, client])

  return (
    <>
      <h2>Digging queue</h2>
      {current ? (
        <NowPlaying key={current.id} current={current} onStarted={() => setStarted(current.id)} />
      ) : (
        <p className="muted">
          {entries.length > 0 ? 'Waiting for videos…' : 'Press + next to a track or release to queue it.'}
        </p>
      )}
      {upNext.length > 0 && (
        <ol className="queue">
          {upNext.map((e) => (
            <QueueRow key={e.id} entry={e} />
          ))}
        </ol>
      )}
    </>
  )
}

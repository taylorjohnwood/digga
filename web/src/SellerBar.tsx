import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { api, type Config, type SellerSummary } from './api'

const syncDescription = (s: SellerSummary, othersSyncing: number) => {
  const run = s.latestRun
  if (run === null) return 'Starting sync…'
  const seen = run.listingsTotal === null ? `${run.listingsSeen}` : `${run.listingsSeen} of ${run.listingsTotal}`
  const sharing = othersSyncing > 0 ? ` (taking turns with ${othersSyncing} other seller${othersSyncing > 1 ? 's' : ''})` : ''
  if (s.syncing && run.status.kind !== 'Running') return `Starting sync…${sharing}`
  switch (run.status.kind) {
    case 'Running':
      return `Syncing… ${seen} listings${sharing}`
    case 'Completed':
      return run.listingsTotal !== null && run.listingsSeen < run.listingsTotal
        ? `Synced ${seen} listings — Discogs only lets other people's inventories be read 10,000 deep`
        : `Synced ${new Date(run.status.finishedAt).toLocaleString()}`
    case 'Failed':
      return `Sync failed: ${run.status.error}`
  }
}

type Props = {
  sellers: SellerSummary[]
  current: SellerSummary | undefined
  config: Config | undefined
  onChoose: (seller: string) => void
  /** The seller this tab shows was removed. */
  onForgotten: () => void
}

export function SellerBar({ sellers, current, config, onChoose, onForgotten }: Props) {
  const [draft, setDraft] = useState('')
  const client = useQueryClient()
  const sync = useMutation({
    mutationFn: api.sync,
    onSuccess: ({ seller }) => {
      onChoose(seller)
      setDraft('')
      client.invalidateQueries({ queryKey: ['sellers'] })
    },
  })

  const forget = useMutation({
    mutationFn: api.forget,
    onSuccess: () => {
      onForgotten()
      client.invalidateQueries({ queryKey: ['sellers'] })
      client.invalidateQueries({ queryKey: ['releases'] })
      client.invalidateQueries({ queryKey: ['queue'] })
    },
  })

  const confirmForget = (s: SellerSummary) => {
    const what = s.syncing ? `Stop syncing ${s.seller} and remove` : `Remove ${s.seller} and`
    if (window.confirm(`${what} its ${s.releases} synced releases? Your queue and listens are kept.`))
      forget.mutate(s.seller)
  }

  const submit = (e: FormEvent) => {
    e.preventDefault()
    if (draft.trim()) sync.mutate(draft.trim())
  }

  return (
    <header className="bar">
      <h1>digga</h1>
      <form onSubmit={submit}>
        <input
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          placeholder="Discogs seller, e.g. rushhour"
          aria-label="Discogs seller"
        />
        <button type="submit" disabled={sync.isPending}>
          Dig
        </button>
      </form>
      {sellers.length > 0 && (
        <select value={current?.seller ?? ''} onChange={(e) => onChoose(e.target.value)} aria-label="Synced sellers">
          {sellers.map((s) => (
            <option key={s.seller} value={s.seller}>
              {s.seller} ({s.releases})
            </option>
          ))}
        </select>
      )}
      {current && (
        <span className={`status status-${current.syncing ? 'running' : (current.latestRun?.status.kind.toLowerCase() ?? '')}`}>
          {syncDescription(current, sellers.filter((s) => s.syncing && s.seller !== current.seller).length)}
          {current.genresUnknown > 0 && ` · fetching genres for ${current.genresUnknown} releases the catalog lacks`}
          {!current.syncing && (
            <button className="link" onClick={() => sync.mutate(current.seller)}>
              resync
            </button>
          )}
          <button
            className="link"
            onClick={() => confirmForget(current)}
            disabled={forget.isPending}
            title={current.syncing ? 'Cancel the sync and remove this seller' : 'Remove this seller'}
          >
            {forget.isPending ? 'removing…' : current.syncing ? 'cancel & remove' : 'remove'}
          </button>
        </span>
      )}
      {sync.error && <span className="status status-failed">{sync.error.message}</span>}
      {forget.error && <span className="status status-failed">{forget.error.message}</span>}
      {config && !config.catalog && (
        <span className="warn" title="Run `dotnet run -- import-catalog latest` in server/ to load genres for every release">
          No catalog: genres arrive slowly
        </span>
      )}
      {config && !config.discogsAuthenticated && (
        <span className="warn" title="Set DISCOGS_TOKEN in .env for 60 requests/min instead of 25">
          No Discogs token: slow mode
        </span>
      )}
      {config && !config.listenBrainzConfigured && (
        <span className="warn" title="Set LISTENBRAINZ_TOKEN in .env to submit listens">
          Listens kept locally only
        </span>
      )}
    </header>
  )
}

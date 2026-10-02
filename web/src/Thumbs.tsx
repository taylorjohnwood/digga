import { useMutation, useQueryClient } from '@tanstack/react-query'
import type { ReactNode } from 'react'
import { api, type Verdict } from './api'

// Outline thumbs from Lucide (ISC licence), drawn in the current text colour so they stay flat
// and take the theme's colours rather than the platform's glossy emoji.
const thumbUp = (
  <>
    <path d="M7 10v12" />
    <path d="M15 5.88 14 10h5.83a2 2 0 0 1 1.92 2.56l-2.33 8A2 2 0 0 1 17.5 22H4a2 2 0 0 1-2-2v-8a2 2 0 0 1 2-2h2.76a2 2 0 0 0 1.79-1.11L12 2a3.13 3.13 0 0 1 3 3.88Z" />
  </>
)
const thumbDown = (
  <>
    <path d="M17 14V2" />
    <path d="M9 18.12 10 14H4.17a2 2 0 0 1-1.92-2.56l2.33-8A2 2 0 0 1 6.5 2H20a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2h-2.76a2 2 0 0 0-1.79 1.11L12 22a3.13 3.13 0 0 1-3-3.88Z" />
  </>
)

const choices: { verdict: Verdict; icon: ReactNode; label: string }[] = [
  { verdict: { kind: 'Like' }, icon: thumbUp, label: 'Like this record' },
  { verdict: { kind: 'Dislike' }, icon: thumbDown, label: 'Dislike this record' },
]

/** Thumbs up/down for a record. Pressing the current verdict again clears it. */
export function Thumbs({ releaseId, rating }: { releaseId: number; rating: Verdict | null }) {
  const client = useQueryClient()
  const change = useMutation({
    mutationFn: async (verdict: Verdict | null) => {
      if (verdict === null) await api.unrate(releaseId)
      else await api.rate(releaseId, verdict)
    },
    onSuccess: () => {
      client.invalidateQueries({ queryKey: ['releases'] })
      client.invalidateQueries({ queryKey: ['queue'] })
    },
  })

  return (
    <span className="thumbs">
      {choices.map(({ verdict, icon, label }) => {
        const chosen = rating?.kind === verdict.kind
        return (
          <button
            key={verdict.kind}
            className={`rate ${verdict.kind.toLowerCase()}${chosen ? ' chosen' : ''}`}
            aria-pressed={chosen}
            title={chosen ? 'Clear rating' : label}
            disabled={change.isPending}
            onClick={() => change.mutate(chosen ? null : verdict)}
          >
            <svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden>
              {icon}
            </svg>
          </button>
        )
      })}
    </span>
  )
}

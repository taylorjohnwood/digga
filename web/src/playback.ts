// What the player is doing with the track at the head of the queue. Pure: a reducer plus the
// scrobble rule. One Playback per track: the component holding it is keyed by the track, so a new
// track gets a fresh state rather than this type tracking which track it belongs to.

import type { ListenBrainzOutcome } from './api'

/** Logging this listen: digga saving it, and what ListenBrainz said when digga passed it on. */
export type Logging =
  | { kind: 'not-yet' }
  | { kind: 'sending' }
  | { kind: 'saved'; listenBrainz: ListenBrainzOutcome }
  /** digga itself couldn't record it. */
  | { kind: 'failed' }

export type Playback =
  | { kind: 'cued' }
  | {
      kind: 'listening'
      /** Unix seconds when playback first started — ListenBrainz's listened_at. */
      startedAt: number
      /** Seconds actually played, not wall-clock: pauses and buffering don't count. */
      played: number
      running: boolean
      logging: Logging
    }

export type PlaybackAction =
  | { type: 'playing'; now: number }
  | { type: 'stopped' }
  | { type: 'tick'; seconds: number }
  | { type: 'logging'; state: Logging }

export const initialPlayback: Playback = { kind: 'cued' }

export const playbackReducer = (state: Playback, action: PlaybackAction): Playback => {
  switch (action.type) {
    case 'playing':
      return state.kind === 'cued'
        ? { kind: 'listening', startedAt: action.now, played: 0, running: true, logging: { kind: 'not-yet' } }
        : { ...state, running: true }
    case 'stopped':
      return state.kind === 'listening' ? { ...state, running: false } : state
    case 'tick':
      return state.kind === 'listening' && state.running ? { ...state, played: state.played + action.seconds } : state
    case 'logging':
      return state.kind === 'listening' ? { ...state, logging: action.state } : state
  }
}

/** The Last.fm/ListenBrainz scrobble rule: half the track or four minutes, whichever comes first. */
export const listenThreshold = (durationSeconds: number) =>
  durationSeconds > 0 ? Math.min(durationSeconds / 2, 240) : 240

type Listening = Extract<Playback, { kind: 'listening' }>

export const shouldLog = (state: Playback, durationSeconds: number): state is Listening =>
  state.kind === 'listening' && state.logging.kind === 'not-yet' && state.played >= listenThreshold(durationSeconds)

import { useSyncExternalStore } from 'react'

// A query-string parameter as React state, with the URL as the only copy: each tab keeps its own,
// it survives reloads, and back/forward work.

const subscribe = (onChange: () => void) => {
  window.addEventListener('popstate', onChange)
  return () => window.removeEventListener('popstate', onChange)
}

export function useUrlParam(name: string): [string | null, (value: string | null) => void] {
  const value = useSyncExternalStore(subscribe, () => new URLSearchParams(window.location.search).get(name))

  const setValue = (next: string | null) => {
    const params = new URLSearchParams(window.location.search)
    if (next === null) params.delete(name)
    else params.set(name, next)
    window.history.pushState(null, '', `?${params}`)
    window.dispatchEvent(new PopStateEvent('popstate'))
  }

  return [value, setValue]
}

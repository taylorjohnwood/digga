import { QueryClient, QueryClientProvider, keepPreviousData, useInfiniteQuery, useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { api, isSyncing, type Filter } from './api'
import { Filters } from './Filters'
import { Player } from './Player'
import { ReleaseList } from './ReleaseList'
import { SellerBar } from './SellerBar'
import { emptyQuery } from './tagQuery'
import { useUrlParam } from './url'

const queryClient = new QueryClient({ defaultOptions: { queries: { refetchOnWindowFocus: false } } })

export default function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <Digga />
    </QueryClientProvider>
  )
}

const noFilter: Filter = { tags: emptyQuery, hideHeard: false, hideRated: false }

function Digga() {
  const config = useQuery({ queryKey: ['config'], queryFn: api.config })
  const sellers = useQuery({
    queryKey: ['sellers'],
    queryFn: api.sellers,
    refetchInterval: (q) => (q.state.data?.some((s) => isSyncing(s) || s.genresUnknown > 0) ? 3000 : false),
  })

  // This tab's seller lives in its URL; with none chosen, the most recently synced one (derived).
  const [chosenSeller, setChosenSeller] = useUrlParam('seller')
  const seller = chosenSeller ?? sellers.data?.[0]?.seller ?? null
  const summary = sellers.data?.find((s) => s.seller.toLowerCase() === seller?.toLowerCase())
  const syncing = summary !== undefined && isSyncing(summary)

  const [filter, setFilter] = useState<Filter>(noFilter)
  const chooseSeller = (s: string) => {
    setChosenSeller(s)
    setFilter(noFilter)
  }

  const releases = useInfiniteQuery({
    queryKey: ['releases', seller, filter],
    enabled: seller !== null,
    queryFn: ({ pageParam }) => api.releases(seller!, filter, pageParam),
    initialPageParam: 0,
    getNextPageParam: (last, pages) => {
      const loaded = pages.reduce((n, p) => n + p.items.length, 0)
      return loaded < last.matching ? loaded : undefined
    },
    placeholderData: keepPreviousData,
    // Poll while the sync runs or anything on screen is still waiting for its videos.
    refetchInterval: (q) =>
      syncing || q.state.data?.pages.some((p) => p.items.some((i) => i.release.detailsStatus.kind === 'Pending'))
        ? 4000
        : false,
  })

  const firstPage = releases.data?.pages[0]

  return (
    <div className="app">
      <SellerBar
        sellers={sellers.data ?? []}
        current={summary}
        config={config.data}
        onChoose={chooseSeller}
        onForgotten={() => {
          // Back to the default: the most recently synced seller still here.
          setChosenSeller(null)
          setFilter(noFilter)
        }}
      />
      <aside className="filters">
        {firstPage && <Filters page={firstPage} filter={filter} onChange={setFilter} />}
      </aside>
      <main className="releases">
        {seller === null ? (
          <p className="empty">Enter a Discogs seller above to start digging.</p>
        ) : (
          <ReleaseList
            pages={releases.data?.pages ?? []}
            waiting={syncing}
            loading={releases.isLoading}
            error={releases.error}
            hasMore={releases.hasNextPage}
            loadingMore={releases.isFetchingNextPage}
            onLoadMore={() => releases.fetchNextPage()}
          />
        )}
      </main>
      <aside className="player">
        <Player />
      </aside>
    </div>
  )
}

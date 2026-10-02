import type { Facet, Filter, ReleasePage } from './api'
import { building, removeTag, startOr, toggleTag, type TagQuery } from './tagQuery'

type FacetListProps = {
  title: string
  facets: Facet[]
  selected: string[]
  onSelect: (name: string) => void
}

function FacetList({ title, facets, selected, onSelect }: FacetListProps) {
  return (
    <section>
      <h2>{title}</h2>
      <ul>
        {facets.map((f) => (
          <li key={f.name}>
            <button
              className={selected.includes(f.name) ? 'facet selected' : 'facet'}
              onClick={() => onSelect(f.name)}
            >
              <span>{f.name}</span>
              <span className="count">{f.count}</span>
            </button>
          </li>
        ))}
      </ul>
    </section>
  )
}

/** The query as chips: tags in a group joined by "and", groups by "or". */
function QueryBar({ query, onChange }: { query: TagQuery; onChange: (q: TagQuery) => void }) {
  if (query.length === 0)
    return <p className="note">Pick genres and styles below. They combine with “and”; “or” starts another group.</p>

  const last = query.length - 1
  return (
    <section className="query">
      <h2>Showing</h2>
      <div className="query-groups">
        {query.map((group, i) => (
          <span key={i} className="query-row">
            {i > 0 && <span className="op">or</span>}
            <span className={i === last ? 'query-group building' : 'query-group'}>
              {group.length === 0 && <span className="muted">pick a tag…</span>}
              {group.map((tag, j) => (
                <span key={tag} className="query-tag">
                  {j > 0 && <span className="op">and</span>}
                  <button className="chip" title={`Remove ${tag}`} onClick={() => onChange(removeTag(query, i, tag))}>
                    {tag} <span aria-hidden>×</span>
                  </button>
                </span>
              ))}
            </span>
          </span>
        ))}
      </div>
      <div className="query-actions">
        <button className="link" onClick={() => onChange(startOr(query))} disabled={building(query).length === 0}>
          + or
        </button>
        <button className="link" onClick={() => onChange([])}>
          clear
        </button>
      </div>
    </section>
  )
}

type Props = { page: ReleasePage; filter: Filter; onChange: (f: Filter) => void }

export function Filters({ page, filter, onChange }: Props) {
  return (
    <>
      <label className="toggle">
        <input
          type="checkbox"
          checked={filter.hideHeard}
          onChange={(e) => onChange({ ...filter, hideHeard: e.target.checked })}
        />
        Hide fully heard
      </label>
      <label className="toggle" title="Hide records you've given a thumbs up or down, to dig only new ones">
        <input
          type="checkbox"
          checked={filter.hideRated}
          onChange={(e) => onChange({ ...filter, hideRated: e.target.checked })}
        />
        Hide rated
      </label>
      <QueryBar query={filter.tags} onChange={(tags) => onChange({ ...filter, tags })} />
      {page.genresUnknown > 0 && (
        <p className="note">
          {page.genresUnknown} of {page.total} releases have no genre yet (newer than the catalog export, or the
          catalog isn't imported). They're left out while genres or styles are picked.
        </p>
      )}
      <FacetList
        title="Genre"
        facets={page.genres}
        selected={building(filter.tags)}
        onSelect={(tag) => onChange({ ...filter, tags: toggleTag(filter.tags, tag) })}
      />
      <FacetList
        title="Style"
        facets={page.styles}
        selected={building(filter.tags)}
        onSelect={(tag) => onChange({ ...filter, tags: toggleTag(filter.tags, tag) })}
      />
    </>
  )
}

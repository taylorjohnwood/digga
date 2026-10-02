// The genre/style query: OR of AND groups, as the server takes it. [['Deep House', 'House'],
// ['Techno']] is (Deep House AND House) OR Techno. Pure. The last group is the one clicks add to;
// it's empty only right after "or" starts a new one.

export type TagQuery = string[][]

export const emptyQuery: TagQuery = []

/** The group facet clicks add to, and that the server narrows facet counts within. */
export const building = (q: TagQuery): string[] => q.at(-1) ?? []

/** Removes a tag from a group, dropping the group if that empties it. */
export const removeTag = (q: TagQuery, groupIndex: number, tag: string): TagQuery =>
  q.flatMap((group, i) => {
    if (i !== groupIndex) return [group]
    const rest = group.filter((t) => t !== tag)
    return rest.length > 0 ? [rest] : []
  })

/** A facet click: adds the tag to the group being built, or takes it out if it's already there. */
export const toggleTag = (q: TagQuery, tag: string): TagQuery => {
  if (q.length === 0) return [[tag]]
  const last = q.length - 1
  return building(q).includes(tag) ? removeTag(q, last, tag) : q.map((g, i) => (i === last ? [...g, tag] : g))
}

/** Starts a new OR group; a no-op while the current one is still empty. */
export const startOr = (q: TagQuery): TagQuery => (building(q).length === 0 ? q : [...q, []])

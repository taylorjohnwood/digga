/// Filtering, faceting and ranking a seller's releases. Pure: takes the read model, returns a page.
module Digga.Ranking

open Digga.Db

/// Genres and styles a release must carry: any one group, with every tag in that group — OR of
/// ANDs (disjunctive normal form). [["Deep House"; "House"]; ["Techno"]] is
/// (Deep House AND House) OR Techno. No groups matches everything; groups are never empty.
type TagQuery = string list list

type Filter =
    { Tags: TagQuery
      /// Hide releases where every video has already been listened to.
      HideHeard: bool
      /// Only what's left to dig: hide records you've given a thumbs up or down.
      HideRated: bool }

type Ranked = { Release: ReleaseView; Score: float }

type Facet = { Name: string; Count: int }

type Page =
    { Total: int
      Matching: int
      /// Releases whose genres/styles aren't known yet. A genre or style filter can't include them.
      GenresUnknown: int
      PriorRatio: float
      Genres: Facet list
      Styles: Facet list
      Items: Ranked list }

let fullyHeard (r: ReleaseView) =
    not r.Videos.IsEmpty && r.Videos |> List.forall _.Heard

let genres (r: ReleaseView) =
    match r.Tags with
    | Known(genres, _) -> genres
    | Unknown -> []

let styles (r: ReleaseView) =
    match r.Tags with
    | Known(_, styles) -> styles
    | Unknown -> []

let tagsOf (r: ReleaseView) = genres r @ styles r

let private hasAll (group: string list) (r: ReleaseView) =
    let tags = tagsOf r
    group |> List.forall (fun t -> List.contains t tags)

/// No query matches everything; a query only matches releases whose tags are known.
let matchesTags (query: TagQuery) (r: ReleaseView) =
    query.IsEmpty || query |> List.exists (fun group -> hasAll group r)

let matches (filter: Filter) (r: ReleaseView) =
    matchesTags filter.Tags r
    && not (filter.HideHeard && fullyHeard r)
    && not (filter.HideRated && r.Rating.IsSome)

let private facets (tags: ReleaseView -> string list) (releases: ReleaseView list) =
    releases
    |> List.collect tags
    |> List.countBy id
    |> List.sortBy (fun (name, count) -> -count, name)
    |> List.map (fun (name, count) -> { Name = name; Count = count })

/// `building` is the group being added to, which the facet counts narrow within: each count is
/// how many releases that group would match with the tag added. Empty when starting a new group.
let page (prior: float) (filter: Filter) (building: string list) (offset: int) (limit: int) (releases: ReleaseView list) : Page =
    let matching =
        releases
        |> List.filter (matches filter)
        |> List.map (fun r -> { Release = r; Score = Score.wantHave prior r.Want r.Have })
        |> List.sortByDescending _.Score

    let narrowed = releases |> List.filter (hasAll building)

    { Total = releases.Length
      Matching = matching.Length
      GenresUnknown = releases |> List.filter (fun r -> r.Tags = Unknown) |> List.length
      PriorRatio = prior
      Genres = facets genres narrowed
      Styles = facets styles narrowed
      Items = matching |> List.skip (min offset matching.Length) |> List.truncate limit }

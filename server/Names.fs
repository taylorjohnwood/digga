/// Turning Discogs' display strings into values other systems understand.
module Digga.Names

open System
open System.Text.RegularExpressions

/// Discogs marks an artist name variation with a trailing '*' and disambiguates duplicates with
/// " (2)". Neither belongs in a name sent to ListenBrainz.
let cleanArtist (name: string) =
    Regex.Replace(name, @"(\*|\s\(\d+\))(?=$|,|\s)", "").Trim()

/// Discogs video titles are free text: usually "Artist - Track", sometimes "Track - Artist".
/// Heuristic: split on the first " - " and drop whichever side is the release artist; if neither
/// is, assume "Artist - Track". No separator: the whole title.
let trackNameFromVideoTitle (releaseArtist: string) (title: string) =
    let isArtist (s: string) =
        String.Equals(s.Trim(), cleanArtist releaseArtist, StringComparison.OrdinalIgnoreCase)

    match title.IndexOf(" - ", StringComparison.Ordinal) with
    | -1 -> title.Trim()
    | i ->
        let left, right = title.Substring(0, i), title.Substring(i + 3)
        (if isArtist right && not (isArtist left) then left else right).Trim()

let youtubeId (uri: string) : string option =
    match Uri.TryCreate(uri, UriKind.Absolute) with
    | false, _ -> None
    | true, u ->
        match u.Host.ToLowerInvariant() with
        | "youtu.be" -> Some(u.AbsolutePath.Trim('/'))
        | "www.youtube.com"
        | "youtube.com"
        | "m.youtube.com" ->
            Web.HttpUtility.ParseQueryString(u.Query).["v"] |> Option.ofObj
        | _ -> None
        |> Option.filter (String.IsNullOrWhiteSpace >> not)

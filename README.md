# digga

Dig through a Discogs seller's stock, ranked by how likely it is to be worth hearing, and listen
through a queue of the releases' YouTube videos. Listens are logged locally and to ListenBrainz.

## Run

```sh
cp .env.example .env        # add DISCOGS_TOKEN (and LISTENBRAINZ_TOKEN)
cd server && dotnet run     # API + background worker on http://localhost:5080
cd web && npm install && npm run dev   # UI on http://localhost:5173
```

### Genres: the catalog

Genres and styles come from Discogs' monthly CC0 data export, imported into `data/catalog.db`:

```sh
cd server && dotnet run -- import-catalog latest   # or a URL, or a local .xml.gz path
```

It streams the ~10 GB export without saving it (~20 min) and swaps the new catalog in when done,
so it is safe to run against a live server. Re-run monthly. Releases newer than the export get
their genres from the API in the background.

## How it works

- **Sync** (`POST /api/sellers/{seller}/sync`): reads the seller's inventory, 100 listings per
  request. Each listing carries the release's want/have counts, so ranking works immediately.
  Discogs only serves the first 100 pages of other people's inventories: 10,000 listings max.
- **Ranking**: want/have ratio with Gamma-Poisson smoothing (`server/Score.fs`) — releases with
  few owners are pulled towards the pooled ratio rather than topping the list on 3 wants / 0 haves.
- **Videos** are fetched from the API only for releases on the page being viewed.
- **Listens** count after half the track or 4 minutes of actual playback (the scrobbling rule).

## Layout

- `server/` — F# (Giraffe, SQLite). Pure core (`Score`, `Ranking`, `Names`, parsers) with the
  Discogs rate limit threaded through the worker loop as a value.
- `web/` — React + TypeScript (Vite, TanStack Query, react-youtube).
- `tests/` — `dotnet test`.
- `data/digga.db` — app state. Also the seam for the planned Python recommender, which will read
  releases/listens/ratings from it and write weights back.

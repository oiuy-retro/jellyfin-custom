# Changelog

All notable changes to this custom build of Jellyfin Server are documented here.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

All custom releases keep upstream's server version **12.3.0** and are built from upstream
`release-12.z` at `06de749d43` (the `v12.3` tag). For changes in Jellyfin itself, see the
[upstream project](https://github.com/jellyfin/jellyfin).

## [Unreleased] - 2026-10-22

### Added
- Server setting `ShuffleSkipPlayed` (`ServerConfiguration`, default `true`). When on, shuffled
  queues from `GET /Shows/{seriesId}/Episodes?sortBy=Random` and `GET /Items?sortBy=Random` leave
  out played items. Unplayed in-progress items and the `startItemId` item stay. If every item is
  played, all items are shuffled. Music, music videos, audiobooks, books, audio playlists and items
  in Music, Music Videos or Books libraries are never filtered.
- `ShuffleSkipPlayed` helper (`MediaBrowser.Controller/TV/ShuffleSkipPlayed.cs`).
- Resume from Continue Watching / Next Up with active shuffle skips played episodes in the rest of
  the saved queue; Next Up for a shuffled series returns the first unplayed episode in the queue.
- Unit tests for skip-played filtering, the all-played fallback, music exclusion, and Next Up.
- `ISeriesShuffleManager` (`MediaBrowser.Controller/TV/ISeriesShuffleManager.cs`) and
  `SeriesShuffleManager` (`Emby.Server.Implementations/TV/SeriesShuffleManager.cs`) to persist
  per-`(userId, seriesId)` shuffle state and shuffled episode queues to
  `<DataPath>/series-shuffle-state.json`.
- Unit tests in `tests/Jellyfin.Server.Implementations.Tests/TV/SeriesShuffleManagerTests.cs`
  covering shuffle queue preservation on resume, disk persistence across restarts, Next Up queue
  advancement when episodes finish, in-player shuffle toggle handling, and `TvShowsController`
  episode ordering.
- Documentation for the custom build: `README.md`, `CHANGELOG.md`, `docs/BUILDING.md`,
  `docs/CUSTOM_CHANGES.md`, `docs/UPDATING_FROM_UPSTREAM.md`, and `docs/UPSTREAM_README.md`.

### Changed
- `TvShowsController.GetEpisodes` (`Jellyfin.Api/Controllers/TvShowsController.cs`):
  - Records the shuffled episode sequence when `sortBy=Random` is requested (and keeps `startItemId`
    at index `0` if both `startItemId` and `sortBy=Random` are supplied).
  - When `startItemId` or `adjacentTo` is requested without `sortBy` and shuffle is active for
    `(userId, seriesId)`, places `startItemId` at index `0` and orders all remaining episodes in the
    saved shuffled order rather than falling back to sequential season/episode order.
  - Clears active shuffle state when sequential playback is requested (`sortBy=SortName` or a
    playback request with `Fields=MediaSources` and no `startItemId`, `adjacentTo`, or `sortBy=Random`),
    while leaving shuffle active during UI browsing queries.
- `ItemsController.GetItems` (`Jellyfin.Api/Controllers/ItemsController.cs`): records series/season
  shuffle state when episodes are shuffled via `GET /Items?parentId={seriesOrSeasonId}&sortBy=Random`.
- `SessionManager` (`Emby.Server.Implementations/Session/SessionManager.cs`):
  - Activates or updates series shuffle state when a client reports `PlaybackOrder.Shuffle` on
    playback start or progress.
  - Clears series shuffle state only when a client explicitly switches from `PlaybackOrder.Shuffle`
    to `PlaybackOrder.Default` on the same item during an active playback session, so resuming from
    the home screen (which starts with `PlaybackOrder.Default`) never clears shuffle.
  - Advances the finished episode to the end of the saved shuffle queue when `OnPlaybackStopped`
    reports `playedToCompletion == true`.
- `TVSeriesManager` (`Emby.Server.Implementations/TV/TVSeriesManager.cs`): resolves `GET /Shows/NextUp`
  from the active shuffled queue for shuffled series so the Next Up card on the home screen also
  follows the random order.
- `global.json`: updated SDK version from `10.0.0` to `10.0.100` (`rollForward: latestMinor`) so
  .NET 10.0.4xx SDK hosts accept `global.json`.

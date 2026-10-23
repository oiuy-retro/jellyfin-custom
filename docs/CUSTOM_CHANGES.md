# Custom changes: technical details

This page lists what the custom build changes in the code, file by file. For the user-facing
description, see the [README](../README.md).

All changes are on the branch `fix/shuffle-resume`, based on upstream tag `v12.3` (`06de749d43`).

## Summary of modified and added files

| File | Change |
|---|---|
| `MediaBrowser.Controller/TV/ISeriesShuffleManager.cs` | **New file.** Interface defining per-`(userId, seriesId)` shuffle state tracking, resume episode ordering, adjacency ordering, playback progress/stop hooks, and Next Up resolution. |
| `Emby.Server.Implementations/TV/SeriesShuffleManager.cs` | **New file.** Thread-safe implementation of `ISeriesShuffleManager` backed by `<DataPath>/series-shuffle-state.json`. |
| `Emby.Server.Implementations/ApplicationHost.cs` | Registers `ISeriesShuffleManager` -> `SeriesShuffleManager` as a singleton in the DI container. |
| `Jellyfin.Api/Controllers/TvShowsController.cs` | Injects `ISeriesShuffleManager` and updates `GetEpisodes` (`GET /Shows/{seriesId}/Episodes`) to record shuffle queues on `sortBy=Random`, return shuffled queues with `startItemId` at index `0` when resuming, order adjacency queries by the shuffled queue, and clear shuffle state on explicit sequential playback queries. |
| `Jellyfin.Api/Controllers/ItemsController.cs` | Injects `ISeriesShuffleManager` and records series/season shuffle order when episodes are shuffled via `GET /Items?parentId={id}&sortBy=Random`. |
| `Emby.Server.Implementations/Session/SessionManager.cs` | Injects `ISeriesShuffleManager` and hooks `OnPlaybackStart`, `OnPlaybackProgress`, and `OnPlaybackStopped` to activate shuffle when `PlaybackOrder.Shuffle` is reported, clear shuffle only when toggled from `Shuffle` to `Default` on the same item in an active session, and rotate completed episodes to the back of the shuffle queue on stop. |
| `Emby.Server.Implementations/TV/TVSeriesManager.cs` | Injects `ISeriesShuffleManager` and updates `GetNextUp` / `GetNextUpBatched` so Next Up returns the next episode from the active shuffled queue. |
| `tests/Jellyfin.Server.Implementations.Tests/TV/SeriesShuffleManagerTests.cs` | **New file.** Unit tests for `SeriesShuffleManager` and `TvShowsController.GetEpisodes`. |
| `global.json` | Changes `sdk.version` from `"10.0.0"` to `"10.0.100"` (`rollForward: "latestMinor"`) so `.NET 10.0.4xx` SDK hosts validate `global.json` without error. |

## How the resume flow works

1. **Shuffle activation (`TvShowsController.GetEpisodes` / `ItemsController.GetItems` / `SessionManager`):**
   - When a client requests `GET /Shows/{seriesId}/Episodes?sortBy=Random`, `TvShowsController` shuffles the episode list (keeping `startItemId` at index `0` if supplied) and calls `_seriesShuffleManager.RecordSeriesShuffle(userId, seriesId, seriesPresentationUniqueKey, seasonId, episodeIds)`.
   - `SeriesShuffleManager` stores a `SeriesShuffleStateEntry` keyed by `(userId, seriesId)` with `IsActive = true` and `EpisodeIds` in the shuffled order, and writes `<DataPath>/series-shuffle-state.json` atomically via a `.tmp` file.
   - If a user instead turns on shuffle mid-playback via the player UI, `SessionManager.OnPlaybackStart` / `OnPlaybackProgress` receives `PlaybackOrder.Shuffle` and calls `_seriesShuffleManager.OnPlaybackReported(...)`, which builds a shuffled queue starting with the playing episode followed by `NowPlayingQueue` and any remaining episodes of the series.

2. **Stopping playback (`SessionManager.OnPlaybackStopped`):**
   - When playback stops, `SessionManager` computes `playedToCompletion` via `_userDataManager.UpdatePlayState(...)` and calls `_seriesShuffleManager.OnEpisodePlaybackStopped(userId, seriesId, primaryEpisodeId, playedToCompletion)`.
   - If `playedToCompletion == false` (stopped partway through), `primaryEpisodeId` stays at `EpisodeIds[0]`.
   - If `playedToCompletion == true` (episode finished), `primaryEpisodeId` is rotated from `EpisodeIds[0]` to the end of `EpisodeIds`, so `EpisodeIds[0]` becomes the next random episode in the queue.

3. **Home screen Next Up (`TVSeriesManager.GetNextUpBatched`):**
   - `TVSeriesManager` merges any active shuffled `SeriesPresentationUniqueKey`s for the user into `nextUpSeriesKeys` and calls `_seriesShuffleManager.TryGetNextUpEpisode(...)`.
   - `TryGetNextUpEpisode` resolves the first valid episode in `EpisodeIds`. If that episode has `PlaybackPositionTicks > 0` and `enableResumable == false`, it returns `null` (preventing duplication between Continue Watching and Next Up); otherwise it returns that episode as Next Up.

4. **Resuming from Continue Watching or Next Up (`TvShowsController.GetEpisodes`):**
   - Clicking an episode card on the home screen sends `GET /Shows/{seriesId}/Episodes?startItemId={episodeId}` with `sortBy = null`.
   - `TvShowsController.GetEpisodes` sees `startItemId.HasValue`, `!sortBy.HasValue`, and `_seriesShuffleManager.IsShuffleActive(user.Id, seriesId) == true`.
   - It calls `_seriesShuffleManager.OrderEpisodesForResume(user.Id, seriesId, episodes, startId)`, which reconciles the episode list, rotates `EpisodeIds` so `startId` is at index `0` (moving episodes that preceded `startId` to the end of the queue), saves the updated order, and returns the episodes in that order.

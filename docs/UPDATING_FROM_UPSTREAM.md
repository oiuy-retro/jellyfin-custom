# Updating from upstream

This page explains how to bring new Jellyfin versions into this custom build.

## Branches and remotes

- `origin` is <https://github.com/oiuy-retro/jellyfin-custom> (this fork).
- `upstream` is <https://github.com/jellyfin/jellyfin>. Its stable 12.x branch is `release-12.z`
  (with tags like `v12.3`), and its development branch is `master`.
- `main` and `release-12.z` on `origin` are kept clean (matching upstream).
- All custom changes and documentation live on the branch `fix/shuffle-resume`.

## Steps

1. Add the upstream remote (only once) and fetch:

   ```bash
   git remote add upstream https://github.com/jellyfin/jellyfin.git
   git fetch upstream --tags
   ```

2. See what is new upstream on `release-12.z` (or a new release tag):

   ```bash
   git log --oneline fix/shuffle-resume..upstream/release-12.z
   ```

3. Merge the new upstream release into `fix/shuffle-resume`:

   ```bash
   git checkout fix/shuffle-resume
   git merge upstream/release-12.z   # or a tag, e.g. git merge v12.4
   ```

4. Resolve any conflicts (see the table below).

5. Build and run the unit tests (see [BUILDING.md](BUILDING.md)):

   ```bash
   dotnet build Jellyfin.Server --configuration Debug
   dotnet test tests/Jellyfin.Server.Implementations.Tests/Jellyfin.Server.Implementations.Tests.csproj \
     --configuration Release \
     --filter "FullyQualifiedName~SeriesShuffleManagerTests"
   ```

6. Update the header of [README.md](../README.md) (upstream commit, tag, and version) and add an
   entry to [CHANGELOG.md](../CHANGELOG.md).

7. Push `fix/shuffle-resume` to `origin`:

   ```bash
   git push origin fix/shuffle-resume
   ```

## Files that may conflict

| File | Resolution |
|---|---|
| `Jellyfin.Api/Controllers/TvShowsController.cs` | Keep the `ISeriesShuffleManager` injection and the `GetEpisodes` shuffle/resume branching around `startItemId`, `adjacentTo`, and `sortBy`. |
| `Jellyfin.Api/Controllers/ItemsController.cs` | Keep the `ISeriesShuffleManager` injection and the `RecordSeriesShuffle` call before returning `GetItems`. |
| `Emby.Server.Implementations/Session/SessionManager.cs` | Keep `ISeriesShuffleManager` injection and the calls in `OnPlaybackStart`, `OnPlaybackProgress`, and `OnPlaybackStopped`. |
| `Emby.Server.Implementations/TV/TVSeriesManager.cs` | Keep `ISeriesShuffleManager` injection, active series key merging in `GetNextUp`, and `TryGetNextUpEpisode` in `GetNextUpBatched`. |
| `Emby.Server.Implementations/ApplicationHost.cs` | Keep `serviceCollection.AddSingleton<ISeriesShuffleManager, SeriesShuffleManager>();`. |
| `global.json` | Keep `"version": "10.0.100"` (or the minimum valid `.100` feature band for the target .NET SDK). |
| `README.md` | Keep this repository's custom build README and copy any upstream README updates into `docs/UPSTREAM_README.md`. |

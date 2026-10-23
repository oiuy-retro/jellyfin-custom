# Jellyfin — Custom Build

A personal build of the Jellyfin server that remembers when a TV show is being watched on shuffle and keeps the episode queue randomized when you stop playback, go to the main menu, and resume from Continue Watching or Next Up.

> **This is an unofficial custom build.** It is based on
> [Jellyfin](https://github.com/jellyfin/jellyfin) by the Jellyfin contributors, and is
> distributed under the same [GNU General Public License v2.0](LICENSE). It is not affiliated
> with or supported by the upstream project — please don't report problems with this build to them.
>
> For what Jellyfin itself is and does, see [docs/ABOUT_JELLYFIN.md](docs/ABOUT_JELLYFIN.md).

- Based on upstream `release-12.z` at commit `06de749d43` (the `v12.3` tag, 2026-10-22).
- Server version: **12.3.0** (assembly and file version unchanged from upstream `v12.3`).
- Custom changes are on the `fix/shuffle-resume` branch.
- See [CHANGELOG.md](CHANGELOG.md) for the history of the custom releases.

## What's different from upstream

### 1. Resuming a shuffled show from the main menu keeps playing randomly

In upstream Jellyfin, clicking **Shuffle** on a TV show (`GET /Shows/{seriesId}/Episodes?sortBy=Random`) creates a temporary shuffled queue in the client. If you stop an episode partway through, go to the main menu, and resume that episode from **Continue Watching** or **Next Up**, the client asks the server for episodes starting at that episode (`GET /Shows/{seriesId}/Episodes?startItemId={episodeId}`) with no `sortBy` parameter. Upstream then returns the remaining episodes in sequential season-and-episode order (`S02E05`, `S02E06`, `S02E07`, ...).

This build fixes that on the server so it works across all Jellyfin clients (Jellyfin Web, Jellyfin Media Player, Android TV, Android, Roku, Swiftfin, and others):

- **Tracks per-user, per-series shuffle state:** When a user shuffles a series or season (`GET /Shows/{seriesId}/Episodes?sortBy=Random` or `GET /Items?parentId={seriesId}&sortBy=Random`), `SeriesShuffleManager` records that the series is in shuffle mode for that user and saves the shuffled episode ID sequence.
- **Resumes the exact episode first, followed by the shuffled queue:** When a client resumes an episode from Continue Watching or Next Up (`GET /Shows/{seriesId}/Episodes?startItemId={episodeId}` without `sortBy`), if shuffle is active for that user and series:
  - The resumed episode is placed at index `0` (`episodes[0]`) so playback starts immediately at your saved timestamp.
  - All other episodes in the series (or season, if a single season was shuffled) follow in the saved shuffled order after the resumed episode, wrapping around so earlier seasons and episodes are included rather than dropped by `SkipWhile`.
  - Episodes that were earlier in the shuffle sequence are rotated to the end of the queue, so already-watched episodes from the current shuffle run do not repeat until the rest of the queue has played.
- **Supports adjacency queries (`adjacentTo`):** Clients such as Swiftfin that query `GET /Shows/{seriesId}/Episodes?adjacentTo={episodeId}` for previous/next episodes receive neighbours from the saved shuffled order when shuffle is active.
- **Survives server restarts:** Active shuffle states and episode queues are saved to `series-shuffle-state.json` inside Jellyfin's `DataPath`.

### 2. In-player shuffle toggle and playback tracking

- **Turning shuffle on inside the player:** If you start an episode normally and turn on shuffle using the player's own shuffle button (`PlaybackOrder.Shuffle` reported to `POST /Sessions/Playing` or `POST /Sessions/Playing/Progress`), the server activates shuffle mode for that user and series and generates a shuffled queue for the remaining episodes.
- **Finishing an episode advances the shuffle queue:** When an episode finishes playing (`playedToCompletion == true` in `SessionManager.OnPlaybackStopped`), the server rotates that completed episode to the back of the saved shuffle queue so the next random episode is at the front.
- **Stopping mid-episode keeps the episode at the front:** If you stop an episode before finishing it (`playedToCompletion == false`), that episode stays at index `0` of the saved shuffle queue until you finish it.

### 3. Next Up on the main menu follows the shuffled order

- **Random Next Up (`GET /Shows/NextUp`):** In `TVSeriesManager`, when a series has shuffle active for a user, `GET /Shows/NextUp` returns the current front episode of the user's shuffled queue rather than the next sequential episode after the highest-numbered watched episode.
- **No duplicate card when `enableResumable=false`:** If the front episode of the shuffle queue is currently in progress (`PlaybackPositionTicks > 0`) and the client requests `GET /Shows/NextUp?enableResumable=false` (to avoid duplicating the Continue Watching row), Next Up returns `null` for that series, matching upstream behavior.
- **Works when rewatching completed shows:** Even if every episode of a series is already marked watched (`Played == true`), or if shuffle happens to play the series finale (`S05E10`), the series remains in Next Up with the next episode from the shuffled queue.

### 4. Switching a show back to sequential order

Shuffle mode stays active for a series until you explicitly switch back to sequential playback:

- **Starting sequential series or season playback:** Calling `GET /Shows/{seriesId}/Episodes` with an explicit non-random sort order (such as `sortBy=SortName`, used by Android TV when clicking Play on a series or season) or calling `GET /Shows/{seriesId}/Episodes` for playback (`Fields` containing `MediaSources`) without `startItemId`, `adjacentTo`, or `sortBy=Random` clears the series' shuffle state for that user.
- **Turning shuffle off in the player controls:** If a client is actively playing an episode with `PlaybackOrder.Shuffle` and switches to `PlaybackOrder.Default` on the same item during that session, the server clears the series' shuffle state for that user.
- **Browsing does not clear shuffle:** Opening a series, season, or episode details page in the UI (`GET /Shows/{seriesId}/Episodes` without `MediaSources`) leaves the active shuffle state untouched and still lists episodes in normal season/episode order on the details page.

## Install / Run

Requirements:

- **Windows, Linux, or macOS** (64-bit)
- **[jellyfin-ffmpeg](https://github.com/jellyfin/jellyfin-ffmpeg)** and **`jellyfin-web`** (from an existing Jellyfin 12.3 installation or built separately)

### Option A: Use a prebuilt release archive

1. Download the latest archive for your platform from this repository's [Releases](https://github.com/oiuy-retro/jellyfin-custom/releases) page and verify it against `SHA256SUMS` (`sha256sum -c SHA256SUMS`).
2. **To run over an existing Jellyfin 12.3 installation on Windows:**
   - Stop the `JellyfinServer` Windows service or tray app.
   - Back up your existing `C:\Program Files\Jellyfin\Server` directory (and your data directory under `%ProgramData%\Jellyfin\Server` or `%LocalAppData%\jellyfin`).
   - Copy the built server files from the release archive into `C:\Program Files\Jellyfin\Server` (keeping the existing `jellyfin-web` folder and `ffmpeg.exe`).
   - Start Jellyfin again.
3. **To run alongside an existing installation:**
   - Extract the archive to its own folder and pass custom `--datadir`, `--configdir`, `--cachedir`, `--logdir`, and `--webdir` paths when starting `jellyfin.exe` (or `./jellyfin` on Linux).

### Option B: Run directly from a local build

```bash
dotnet run --project Jellyfin.Server --configuration Release -- --webdir "/path/to/jellyfin-web"
```

## Building from source

Short version (details and troubleshooting are in [docs/BUILDING.md](docs/BUILDING.md)):

- **.NET 10.0 SDK** (`10.0.100` or newer; `global.json` rolls forward to the latest installed `10.0.x` SDK).

```bash
git clone https://github.com/oiuy-retro/jellyfin-custom.git
cd jellyfin-custom
git checkout fix/shuffle-resume
dotnet publish Jellyfin.Server --configuration Release --output ./publish
```

For a self-contained Windows x64 build:

```bash
dotnet publish Jellyfin.Server --configuration Release --runtime win-x64 --self-contained true --output ./publish-win-x64
```

The server executable is written to `./publish/jellyfin` (or `./publish-win-x64/jellyfin.exe` on Windows).

## Known limitations

- **Server-side queue ordering:** When resuming from Continue Watching or Next Up, the client itself starts the playback session with its own local shuffle icon in its default (unhighlighted) state, because the client launched a single episode card rather than the series Shuffle button. Even though the client's shuffle icon is not lit, the queue of upcoming episodes loaded from the server is already in the saved random order.
- **Season-level vs series-level shuffle:** If you click Shuffle on a specific season, resuming an episode from that season shuffles the remaining episodes of that season. If you later start an episode from a different season of the same show, the shuffle queue expands to the full series.
- **Sequential Play button on a Series page in Jellyfin Web:** In Jellyfin Web, clicking the main Play button on a Series page first queries `GET /Shows/NextUp` and then calls `GET /Shows/{seriesId}/Episodes?startItemId={nextUpId}` with the same parameters as resuming an episode from the home screen. When shuffle is active for that series, clicking Play on the Series page in Jellyfin Web therefore resumes the shuffled queue. To switch a series back to sequential playback in Jellyfin Web, click Play on a Season page or toggle the shuffle button on and off inside the player controls.
- **Testing:** Verified with unit tests in `SeriesShuffleManagerTests` (covering shuffle queue rotation on resume, JSON persistence across restarts, Next Up resolution on mid-episode stop and episode completion, in-player shuffle activation/deactivation, and `TvShowsController.GetEpisodes` behavior) and full Debug/Release builds of `Jellyfin.Server`.

## Staying in sync with upstream

This fork keeps upstream's git history, so new upstream versions can be merged in. Upstream's stable 12.x branch is `release-12.z` (tagged `v12.3`, etc.) and its development branch is `master`. The short version:

```bash
git remote add upstream https://github.com/jellyfin/jellyfin.git   # once
git fetch upstream --tags
git checkout fix/shuffle-resume
git merge upstream/release-12.z   # or a release tag, e.g. v12.4
# resolve conflicts, build, run tests, push
```

The full walkthrough is in [docs/UPDATING_FROM_UPSTREAM.md](docs/UPDATING_FROM_UPSTREAM.md).

## More documentation

- [CHANGELOG.md](CHANGELOG.md): what changed in each custom release
- [docs/BUILDING.md](docs/BUILDING.md): build, publish, test, and troubleshooting instructions
- [docs/CUSTOM_CHANGES.md](docs/CUSTOM_CHANGES.md): technical details of the custom server changes
- [docs/UPDATING_FROM_UPSTREAM.md](docs/UPDATING_FROM_UPSTREAM.md): merging new upstream versions
- [docs/ABOUT_JELLYFIN.md](docs/ABOUT_JELLYFIN.md): house-style overview of upstream Jellyfin and links to upstream docs
- [docs/UPSTREAM_README.md](docs/UPSTREAM_README.md): the original upstream README

## License and credits

Jellyfin is © the Jellyfin contributors, licensed under the
[GNU General Public License v2.0](LICENSE). This custom build is a modified version of it and is
distributed under the same license. Source files keep their original copyright headers. All credit
for Jellyfin itself goes to the upstream authors: <https://github.com/jellyfin/jellyfin>.

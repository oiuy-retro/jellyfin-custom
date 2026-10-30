# About Jellyfin

This page describes the server that this custom build is based on: what Jellyfin is, what it does, and where to find upstream's own documentation. For what this build changes, see the [README](../README.md).

## What Jellyfin is

Jellyfin is a free, open-source media system that indexes, manages, and streams personal media collections (movies, TV shows, music, books, and photos) to client apps across platforms. It is developed by the Jellyfin contributors at <https://github.com/jellyfin/jellyfin>.

- It is descended from Emby 3.5.2 and ported to the cross-platform .NET runtime.
- It requires no cloud account, central authentication server, or subscription; all media metadata, user accounts, and playback state stay on your own server.
- It runs on Windows, Linux, macOS, and Docker containers.

## Features

All of these work in this custom build too. Where this build behaves differently, it is noted.

- **Media libraries and metadata**: Organizes TV shows, movies, music, live TV/DVR, books, and photos, fetching metadata and artwork from configured providers or local NFO files.
- **Direct Play, Direct Stream, and Transcoding**: Streams compatible media directly to clients or transcodes video and audio on the fly using `jellyfin-ffmpeg`, including hardware acceleration (NVENC, QuickSync, VA-API, AMF).
- **Multi-user playback tracking**: Tracks watched state, resume timestamps (`PlaybackPositionTicks`), Continue Watching, and Next Up per user account. In this build, the server also tracks per-user, per-series shuffle state so resuming an in-progress episode from Continue Watching or Next Up keeps playing the series in shuffled order (see the [README](../README.md)).
- **Client ecosystem**: Serves the bundled `jellyfin-web` browser interface and provides a REST/WebSocket API used by official and third-party clients (Jellyfin Media Player, Android, Android TV, iOS/Swiftfin, Roku, Kodi, and others).
- **Plugin system**: Supports server plugins for extra metadata providers, authentication methods, notifications, and playback integrations.

## Upstream documentation

These resources are maintained by the Jellyfin project and describe the official server:

- [Jellyfin upstream README](https://github.com/jellyfin/jellyfin/blob/release-12.z/README.md): upstream's overview, architecture links, and community channels
- [Official Jellyfin Documentation](https://jellyfin.org/docs/): administration, hardware acceleration, networking, and client guides
- [Contributing guidelines](https://jellyfin.org/docs/general/contributing/): upstream's rules for code and documentation contributions

Upstream's issue tracker and forums are for the official server. Please do not report problems with this custom build there.

## License

Jellyfin is licensed under the [GNU General Public License v2.0](../LICENSE). This custom build is a modified version and is distributed under the same license.

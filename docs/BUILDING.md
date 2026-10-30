# Building Jellyfin Custom

This page explains how to build, test, and package the custom Jellyfin server from source.

## What you need

| Tool | Version | Notes |
|---|---|---|
| .NET SDK | 10.0 (`10.0.100` or newer) | Required to build and run the server and unit tests. `global.json` rolls forward to any installed `10.0.x` SDK. |
| Git | 2.x | For cloning and keeping in sync with upstream. |
| jellyfin-web | 12.3.x | Static web UI files (needed only at runtime when hosting the web UI, not during `dotnet build`). |
| jellyfin-ffmpeg | 7.x | FFmpeg binaries for transcoding at runtime. |

## Build the server

```bash
git clone https://github.com/oiuy-retro/jellyfin-custom.git
cd jellyfin-custom
git checkout fix/shuffle-resume
dotnet build Jellyfin.Server --configuration Release
```

The compiled server assemblies are written to `Jellyfin.Server/bin/Release/net10.0/`.

## Publish a distributable folder

### Framework-dependent publish (portable across platforms with .NET 10 runtime installed)

```bash
dotnet publish Jellyfin.Server --configuration Release --output ./publish
```

### Self-contained Windows x64 publish

```bash
dotnet publish Jellyfin.Server --configuration Release --runtime win-x64 --self-contained true --output ./publish-win-x64
```

### Self-contained Linux x64 publish

```bash
dotnet publish Jellyfin.Server --configuration Release --runtime linux-x64 --self-contained true --output ./publish-linux-x64
```

Note: `publish*` and `out/` directories are ignored by `.gitignore`. Never commit built binaries or release archives to the repository.

## Run the unit tests

To run the shuffle-on-resume unit tests:

```bash
dotnet test tests/Jellyfin.Server.Implementations.Tests/Jellyfin.Server.Implementations.Tests.csproj \
  --configuration Release \
  --filter "FullyQualifiedName~SeriesShuffleManagerTests"
```

To run the full test suite:

```bash
dotnet test Jellyfin.sln --configuration Release
```

## Publishing a GitHub release

Archive the published folder, generate `SHA256SUMS`, and upload both to a GitHub release:

```bash
tar -czf jellyfin-custom-v12.3-custom.1-linux-x64.tar.gz -C publish-linux-x64 .
sha256sum jellyfin-custom-v12.3-custom.1-* > SHA256SUMS
gh release create v12.3-custom.1 jellyfin-custom-v12.3-custom.1-* SHA256SUMS \
  --repo oiuy-retro/jellyfin-custom --target fix/shuffle-resume --prerelease
```

## Troubleshooting

- **`global.json` SDK version error:** Make sure you have a .NET 10.0 SDK (`10.0.100` or newer) installed (`dotnet --list-sdks`).
- **Warnings treated as errors in Debug mode:** `Directory.Build.props` sets `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` and runs StyleCop and `BannedSymbols.txt` analyzers in `Debug` builds. Note that `Guid == Guid` and `Guid != Guid` are banned in this codebase; always use `guid.Equals(other)` and `guid.IsEmpty()`.
- **Web UI not found when launching `jellyfin`:** Pass `--webdir /path/to/jellyfin-web` (for example `C:\Program Files\Jellyfin\Server\jellyfin-web` on Windows) or `--nowebclient` if connecting only from external apps.

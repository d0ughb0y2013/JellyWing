# Building and running JellyWing

## What you need

| Tool | Version | Used for |
|---|---|---|
| .NET SDK | 10 | The server |
| Node.js and npm | Node 24 or later, npm 11 or later | The web client |
| ffmpeg | Jellyfin's build is recommended | Playback, scanning, optimized versions |

Jellyfin publishes its ffmpeg builds at <https://repo.jellyfin.org/files/ffmpeg/>. Its
build includes chromaprint, which intro detection needs; a stock ffmpeg often does not.

## Build

From the repository root:

```bash
dotnet build Jellyfin.Server
```

```bash
cd webclient && npm ci && npm run build:production
```

The web client build lands in `webclient/dist`.

## Run

```bash
dotnet Jellyfin.Server/bin/Debug/net10.0/jellyfin.dll --datadir /path/to/data --webdir webclient/dist --ffmpeg /path/to/ffmpeg
```

Then open <http://localhost:8096>. A new data folder starts the setup wizard.

Useful options:

| Option | Meaning |
|---|---|
| `--datadir` | Where the server keeps its database, configuration, plugins and optimized versions |
| `--cachedir` | Where it keeps caches and live conversion output |
| `--webdir` | The built web client to serve |
| `--ffmpeg` | The ffmpeg to use; ffprobe is expected beside it |

## Test

```bash
dotnet test tests/Jellyfin.Server.Implementations.Tests
```

```bash
dotnet test tests/Jellyfin.Api.Tests
```

```bash
dotnet test tests/Jellyfin.Server.Tests
```

For the web client, from `webclient/`:

```bash
npm run build:check
```

```bash
npx eslint src
```

The build treats analyzer findings as errors. Two that come up often: compare `Guid`
values with `.Equals`, not `==`, and do not call synchronous database or file methods
inside an `async` method.

## After changing the web client

A browser that has loaded the web client before keeps a cached copy. After rebuilding,
reload without the cache, or clear the site's service worker and caches, to see the new
build.

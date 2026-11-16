# How the fork is maintained

## Layout

| Path | What it is |
|---|---|
| Everything outside `webclient/` | The Jellyfin server, with JellyWing's changes |
| `webclient/` | The Jellyfin web client, with JellyWing's changes |
| `docs/jellywing/` | JellyWing's own documentation |
| `README.jellyfin.md` | The Jellyfin server's original README |

The web client is not under `web/` because the server's `.gitignore` ignores that folder:
its build scripts put build output there. New files under `web/` would silently not be
tracked.

## Where JellyWing's server code lives

| Area | Location |
|---|---|
| Watchlist | `Jellyfin.Server.Implementations/Users/WatchlistManager.cs`, `Jellyfin.Server.Implementations/Users/WatchlistWatchedCleaner.cs`, `Jellyfin.Api/Controllers/UserWatchlistController.cs` |
| Cloud twin publishing | `Emby.Server.Implementations/CloudTwin/`, `Emby.Server.Implementations/ScheduledTasks/Tasks/CloudTwinPublishTask.cs` |
| Viewing stats | `Jellyfin.Server.Implementations/Users/ViewingStatsService.cs`, `Jellyfin.Api/Controllers/UserStatsController.cs` |
| Optimized versions | `Emby.Server.Implementations/Library/OptimizedVersions/`, `MediaBrowser.Controller/Library/OptimizedVersion.cs`, `Jellyfin.Api/Controllers/OptimizedVersionsController.cs` |
| Version ordering for playback | `Jellyfin.Api/Helpers/MediaInfoHelper.cs` |
| Optimized versions task | `Emby.Server.Implementations/ScheduledTasks/Tasks/OptimizedVersionsTask.cs` |
| Plugins at first setup | `Emby.Server.Implementations/Updates/RecommendedPluginInstaller.cs` |
| Ancestor row repair | `Jellyfin.Server/Migrations/Routines/20261115120000_RebuildMissingAncestorIds.cs` |
| Ancestor row guard | `Jellyfin.Server.Implementations/Item/ItemPersistenceService.cs` |

In the web client: `webclient/src/lib/watchlist.js`, `webclient/src/lib/optimizedVersion.js`,
`webclient/src/lib/viewingStats.js`, `webclient/src/apps/legacy/controllers/watchlist.js`,
`webclient/src/apps/legacy/controllers/stats.js`, `webclient/src/components/itemContextMenu.js`
and the logo files in `webclient/src/assets/img/jellywing/`.

The 12 web client has two home pages, `apps/legacy/controllers/home.js` and
`apps/modern/routes/home.tsx`. The modern one is the default. A tab added to one must be
added to the other.

## Logo and icons

The logo files are SVGs in `webclient/src/assets/img/jellywing/`. The browser tab and
home-screen icons in its `favicons/` folder are rendered from `app-icon.svg`. After
changing that file, from `webclient/`:

```bash
python3 scripts/makeJellyWingIcons.py
```

It needs `rsvg-convert` from librsvg.

## Taking in upstream changes

The server follows Jellyfin's `release-12.z` branch:

```bash
git fetch https://github.com/jellyfin/jellyfin.git release-12.z
```

```bash
git merge FETCH_HEAD
```

The web client was imported as a squashed subtree and is updated the same way:

```bash
git subtree pull --prefix=webclient https://github.com/jellyfin/jellyfin-web.git release-12.z --squash
```

## Rules that keep merges easy

- **No database migrations of our own.** Jellyfin adds migrations often, and each one
  rewrites the model snapshot. A JellyWing migration would conflict with every one of
  them and leave JellyWing databases on a schema Jellyfin does not know. New data goes in
  existing tables or in files under the data folder.
- **Additions over edits.** New behavior goes in new files where it can. Edits to
  Jellyfin's own files are kept small and local.
- **Plugins are installed, not copied in.** The recommended plugins are GPL version 3;
  the server is GPL version 2. Their code is downloaded by the server at runtime and is
  never added to this repository.
- **Keep the server's product name.** Apps recognize a compatible server by the product
  name "Jellyfin Server". Branding belongs in the web client.

## Commit messages

- Do not write `owner/repository#number` in a commit message. GitHub turns that into a
  public link on the other project's issue, pointing back here.
- Describe an upstream bug in words instead of by number.

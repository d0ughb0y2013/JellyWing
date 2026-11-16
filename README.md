# JellyWing

JellyWing is a media server you run yourself. It is a fork of
[Jellyfin](https://github.com/jellyfin/jellyfin) 12 that adds the conveniences people
usually pay a subscription for, and keeps everything free.

It is not affiliated with or endorsed by the Jellyfin project. The original Jellyfin
README is kept in [README.jellyfin.md](README.jellyfin.md).

## What JellyWing adds

| Feature | What you get |
|---|---|
| Watchlist | A per-user list of things to watch, with its own tab on the home page. |
| Optimized versions | A copy of each awkward video that plays on any device, made ahead of time, so playback does not convert it live. |
| Optimized downloads | Ask for a compatible copy of one video and download it when it is ready. |
| Ready-to-use plugins | A new server installs webhooks, playback statistics and intro skipping during first setup. |
| Reliable library data | A repair for the "show stuck on watched" problem, and a guard against the save path that causes it. |
| One repository | The web client lives here too, under `webclient/`. |

Each feature is described in [docs/jellywing/features.md](docs/jellywing/features.md).

## Getting started

- [Build and run it](docs/jellywing/building.md)
- [API added by JellyWing](docs/jellywing/api.md)
- [How the fork is maintained](docs/jellywing/maintaining.md)
- [Cloud twin design](docs/jellywing/cloud-twin.md), for remote access without router setup (not built yet)

## Branches

- `jellywing-12` is the main line. It follows Jellyfin's stable 12 release branch.
- `master` follows Jellyfin's development branch and carries only the earliest changes.

## License

JellyWing is free software under the GNU General Public License, version 2, the same
license as the Jellyfin server it is built on. See [LICENSE](LICENSE). The web client in
`webclient/` is licensed under the GPL, version 2 or later; see `webclient/LICENSE`.

Jellyfin is a project of its contributors. JellyWing uses its own name and logo and does
not use Jellyfin's branding for itself.

# JellyWing features

Everything here is in addition to what Jellyfin 12 already does.

## Watchlist

Every user has their own watchlist, separate from favorites. A movie, a series, a season
or a single episode can go on it.

- **Add or remove:** open an item's menu (the three dots) and choose *Add to watchlist*
  or *Remove from watchlist*.
- **See it:** the home page has a *Watchlist* tab beside *Home* and *Favorites*. The
  toolbar and the side drawer link to it. Items are listed most recently added first.
- **Access:** the list only shows items the user can currently see. An item hidden later
  by parental controls or library access drops out of view and comes back if access
  returns.
- **Deleted items** leave every watchlist when they are deleted from the library.

Entries are stored in a table the server already has, so the watchlist needs no database
migration.

## Optimized versions

Some videos cannot be played directly by most devices: the wrong container, a video codec
the device lacks, surround audio the device cannot decode, or a resolution above 1080p.
Jellyfin converts those live, every time they are played. JellyWing can do the conversion
once, ahead of time.

- **What is made:** an MP4 with H.264 video and stereo AAC audio, at most 1080p. Streams
  that already fit are copied, not re-encoded, so a file with only the wrong container
  takes seconds.
- **How to run it:** *Dashboard → Scheduled Tasks → Create optimized versions*. It has no
  default schedule. Start it by hand, or give it a schedule there.
- **Where copies live:** under the server's data folder in `optimized-versions/`, never
  inside a media library.
- **Staying current:** a copy records the size and modified time of the file it was made
  from. Once the original changes, the copy is ignored and remade on the next run. Copies
  of deleted items are removed at the start of each run.
- **Playback:** when the original cannot play directly on a device and the copy can, the
  server offers the copy first and the web client plays it. The session shows as direct
  play, with no conversion running.
- **Subtitles and audio tracks:** a copy keeps one audio track (the default one) and no
  subtitles. Whenever subtitles are on, or a different audio track is selected, the
  original is used instead so nothing is lost.

Limits today: HDR videos are skipped, encoding is done in software, alternate versions of
an item do not get their own copy, and there is no per-library setting.

## Optimized downloads

For taking one video with you:

1. Open the video's menu.
2. Choose *Prepare optimized download*. The server makes the copy in the background.
3. When the same menu shows *Download optimized version*, choose it.

A video that already plays everywhere shows only the normal *Download*. Requests are
handled one at a time. Preparing and downloading need the same permission as downloading
the original file. The list of waiting requests is kept in memory, so a request that had
not started when the server restarts needs to be made again.

## Plugins installed at first setup

When a new server completes the setup wizard for the first time, it installs:

| Plugin | Purpose | Source |
|---|---|---|
| Webhook | Send notifications to other services | Jellyfin's plugin repository |
| Playback Reporting | Viewing statistics | Jellyfin's plugin repository |
| Chapter Segments Provider | Skip buttons from chapter markers named "Intro" or "Credits" | Jellyfin's plugin repository |
| Intro Skipper | Detects intros and credits from the audio itself | Its own repository, which is added to the server |

They load after the next restart, like any plugin. Existing servers are not touched. To
turn this off, start the server with the setting `plugins:installRecommended=false`.

Intro Skipper needs an ffmpeg built with chromaprint. Jellyfin's own ffmpeg builds and
its container images include it.

## Library data repair

Jellyfin 12 counts a show's watched and unwatched episodes through a table of each item's
ancestors. When rows are missing from that table, a show reads as fully watched and
cannot be marked unwatched.

- **Repair:** on the first start after upgrading to JellyWing, a one-time step re-adds
  every ancestor row that an item's parent chain implies. It only adds rows.
- **Prevention:** saving an item no longer deletes its stored ancestor rows when one of
  its parents could not be loaded at that moment.

## Branding

The web client shows a JellyWing logo and name on the sign-in page, in the header and
drawer, and on the splash screen. The server still reports its product
name as "Jellyfin Server", because existing apps check that name to recognize a
compatible server.

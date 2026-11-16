# Cloud twin: design

Status: design only. Nothing described here is built yet. Two decisions are still open;
they are listed at the end.

## Goal

A JellyWing server at home should be reachable from anywhere with no router setup, and
streaming to people outside the home should not load the home server or its upload
connection for every viewer.

The twin does that by keeping a published copy of the library in the cloud. It is not a
second JellyWing server. Remote devices talk to the twin; the home server does the
thinking once and publishes the results.

## Why not run the server itself in the cloud

The server assumes one long-lived process on one machine:

- Live conversion launches a local ffmpeg process, keeps the job list in memory, and
  writes segments to a local folder that is wiped at startup.
- Sessions, live connections and group-watch state live in memory.
- Libraries are folders on local disk, watched by a file system watcher. There is no
  abstraction for object storage.
- Item and play-state caches have no cross-instance invalidation.
- Scheduled work runs on in-process timers with no coordination between instances.

Running a second instance, or one that starts cold for each request, breaks each of
these. Publishing a copy avoids all of them.

## Parts

| Part | Where | Job |
|---|---|---|
| JellyWing server | Home | Scans files, fetches metadata, makes optimized versions. The source of truth. |
| Sync agent | Home, inside or beside the server | Publishes changes to the twin and pulls play state back. |
| Media store | Cloud object storage | Holds one playable file per published video, plus artwork and subtitles. |
| Catalog | Cloud, small pay-per-request database | Items, their hierarchy, users, play state, watchlists. |
| Twin API | Cloud functions | The part of the server's API that apps need to browse and play. |
| Delivery | Content delivery network | Serves media straight from storage to the device. |

Nothing in the cloud runs between requests. The cost is storage and data transfer, not
compute.

## What the home server already provides

- **A playable file per video.** Optimized versions are exactly the file the twin needs:
  H.264 and stereo AAC in an MP4, at most 1080p, tone mapped if the source was HDR. A
  video that already plays everywhere is published as it is. The twin never converts.
- **A stable id per file.** The optimized version has its own media source id.
- **Change detection.** Items carry a last-saved date, and the item list can be filtered
  by it, so the agent can ask for what changed since its last run.
- **Play state and watchlists** through the existing API.

## Publishing

The agent keeps a small state file: for each published item, the size and modified time
of what it uploaded. On each run it:

1. Lists items changed since the last run, and items deleted.
2. For each changed video, makes sure a playable file exists (requesting an optimized
   version if needed), uploads it if it is new or changed, then uploads artwork and
   subtitles.
3. Writes the item's catalog record. The record goes last, so an item never appears in
   the twin before its file is there.
4. Removes records and files for deleted items.

Uploads are resumable and one at a time by default, so the home connection stays usable.
The first run for a large library takes days; that is expected, and the twin is usable
for whatever has been published so far.

## Playing from the twin

1. The app signs in to the twin and browses the catalog.
2. It asks what to play. The twin answers with one media source whose location is a
   signed, short-lived link to the file in storage.
3. The app plays the file from the delivery network. It reports progress to the twin.

Because every published file is a plain MP4 that plays directly, there is no conversion
step and nothing to scale.

## Play state

Progress and watched state are recorded per user and item with a timestamp. The agent
syncs both ways and the newer timestamp wins. Watchlists sync the same way.

## Accounts

Users and their password hashes are published with the catalog, so the same sign-in
works at home and away. The twin issues its own access tokens. Parental controls and
library access are evaluated by the home server at publish time and stored on each
record as the list of users who may see it.

An invitation is a record in the twin that lets a new person set a password; the agent
creates the matching user at home on its next run.

## API the twin needs

Enough for the web client and the common apps to browse and play:

| Area | Endpoints |
|---|---|
| Server | `GET /System/Info/Public` |
| Sign-in | `POST /Users/AuthenticateByName`, `GET /Users/Me` |
| Browse | `GET /UserViews`, `GET /Items`, `GET /Items/{id}`, `GET /Shows/{id}/Episodes`, `GET /Shows/NextUp`, `GET /Items/Latest`, `GET /Items/Resume` |
| Artwork | `GET /Items/{id}/Images/{type}` |
| Play | `POST /Items/{id}/PlaybackInfo`, `POST /Sessions/Playing`, `/Progress`, `/Stopped` |
| State | `POST` and `DELETE /UserPlayedItems/{id}`, `/UserFavoriteItems/{id}`, `/UserWatchlistItems/{id}` |

Everything else answers "not available here". Administration stays at home.

## What stays at home only

Live TV, group watch, live conversion for devices that cannot play the published file,
alternate audio tracks, burned-in subtitles, and all administration. An app that needs
one of these uses the home server.

## Cost shape

There is no idle compute cost. The two that matter:

- **Storage** grows with what is published. Publishing optimized versions instead of
  originals cuts it substantially for 4K and remux libraries.
- **Data transfer out** is charged per gigabyte by most providers and is the larger risk
  for a library that is watched a lot. Some object stores charge nothing for it.

Before any build, both should be priced for the actual library size and expected viewing
with the provider's current price list.

## Licensing

The sync agent is part of, or a plugin to, the server, so it is under the server's
license. The twin API is a separate program that shares no code with the server and can
carry its own license.

## Build order

1. Publish one library to storage and write catalog records. No API yet; verify by
   inspecting the store.
2. Twin API for sign-in, browsing and artwork. Verify with the web client pointed at it.
3. Playback through signed links, and progress reporting.
4. Play state and watchlist sync in both directions.
5. Invitations.

## Open decisions

| Decision | Options | Effect |
|---|---|---|
| Storage provider | A general cloud with per-gigabyte transfer charges, or an object store with free transfer | Sets the running cost, and which function and database services the twin uses |
| What to publish | The whole library, or chosen libraries | Sets the storage size and how long the first upload takes |

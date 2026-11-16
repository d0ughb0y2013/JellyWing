# API added by JellyWing

All of Jellyfin 12's API is unchanged. These endpoints are additions. Authentication works
as it does for the rest of the API.

## Watchlist

| Method | Path | Result |
|---|---|---|
| `GET` | `/UserWatchlistItems` | The user's watchlist items, most recently added first |
| `POST` | `/UserWatchlistItems/{itemId}` | Adds the item; `204`. Adding an item already listed changes nothing |
| `DELETE` | `/UserWatchlistItems/{itemId}` | Removes the item; `204` |

All three accept `userId`; without it, the signed-in user is used.

`GET` also accepts `startIndex`, `limit`, `fields`, `enableImages`, `enableUserData`,
`imageTypeLimit` and `enableImageTypes`, with the same meaning as on other item lists. It
returns the usual `{ Items, TotalRecordCount, StartIndex }` shape. Only items the user can
access are returned, and `TotalRecordCount` counts those.

`POST` returns `404` when the item does not exist or the user cannot see it.

## Optimized versions

| Method | Path | Result |
|---|---|---|
| `GET` | `/Items/{itemId}/OptimizedVersion` | Where the item stands |
| `POST` | `/Items/{itemId}/OptimizedVersion` | Asks for a copy to be made in the background; returns where the item stands |
| `GET` | `/Items/{itemId}/OptimizedVersion/Download` | The copy, as a file download |

The status is `{ "State": "...", "Size": 12345 }`. `Size` is present when a copy exists.

| State | Meaning |
|---|---|
| `None` | The item needs a copy and has none |
| `NotNeeded` | The file already plays everywhere, or no copy can be made of it |
| `Queued` | A copy was asked for and is waiting its turn |
| `Running` | A copy is being made now |
| `Ready` | A copy exists |
| `Failed` | Making a copy was tried and did not work; it can be asked for again |

`POST` and the download need the download permission. The download returns `404` until
the state is `Ready`. The file is named after the original, with an `.mp4` extension.

## Playback

Two existing behaviors change when an item has an optimized copy:

- `POST /Items/{itemId}/PlaybackInfo` lists the copy as an extra media source named
  "Optimized". It comes first when it can play directly on the requesting device and the
  original cannot, unless subtitles are on or another audio track is selected.
- When the request names the item's own media source by id, the copy is included
  alongside it instead of being filtered out.

The copy's media source id differs from the item id and stays the same for as long as the
item exists. It streams through the usual `/Videos/{itemId}/stream` endpoints.

## Scheduled task

`Create optimized versions` has the key `OptimizedVersions` and no default trigger. It can
be started and scheduled through the existing scheduled task endpoints.

## Startup setting

| Setting | Default | Effect |
|---|---|---|
| `plugins:installRecommended` | `true` | Whether a new server installs the recommended plugins when setup first completes |

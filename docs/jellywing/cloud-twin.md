# Cloud twin: design

Status: publishing is built, and so is a twin API that signs users in, lets them browse,
and plays by handing out links to the files. The web client is published too, so opening
the twin's address in a browser gives the same client as at home. What is watched on the
twin comes home and the other way round, along with favorites and watchlists. An
administrator can invite someone with a link. It can be deployed to AWS with the templates
in `deploy/cloud-twin/`. See
[Build order](#build-order) for where things stand.

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

Each user has one file in the store, `state/<user id>.json`, holding for each item whether
it was watched, where it stopped, whether it is a favorite, and whether it is on the
watchlist. The twin changes it as people watch. Resume, next up and stats on the twin are
worked out from it.

The home server syncs every 15 minutes (the scheduled task *Sync play state with cloud
twin*). It remembers what each side looked like after the last sync, so it can tell which
side changed:

- Changed on the twin only: brought home.
- Changed at home only: sent to the twin.
- Changed on both: the twin's change stands unless the item was played at home after it.

Only published items are synced. Both sides write the file with a check that it has not
changed since it was read, and try again if it has, so a report from a player and a sync
at the same moment do not overwrite each other. `twin:playState=false` turns the sync off.

## Accounts

Users and their password hashes are published with the catalog, so the same sign-in
works at home and away. The twin issues its own access tokens.

Library access comes from home: the published list names, for each person, the published
libraries they may see, and the twin shows them only those. The list is sent again on
every sync, so a change at home takes effect on the twin within about 15 minutes.

The twin cannot apply the finer limits a home account can have: a parental rating, allowed
or blocked tags, blocked unrated items, an access schedule, or a ban on playing. Rather
than give such a person more on the twin than they have at home, the server leaves them
out of the published list, so they cannot sign in to the twin. `twin:includeLimitedUsers=true`
publishes them anyway, limited by library only. A person who is not allowed to connect to
the home server remotely is never published.

An invitation is a single-use link an administrator makes on the twin, at `/invite`. The
person who opens it picks a name and a password and can watch at once. On its next sync
the home server makes the matching account with the same password. That account is never
an administrator and sees only the libraries the administrator chose for the invitation. `twin:invitations=false` stops the
home server from making accounts this way. `twin/README.md` has the details.

Who is an administrator is published with the user list. That list is sent again on every
sync, so someone removed, disabled or given a new password at home is treated the same on
the twin within about 15 minutes.

## API the twin needs

Enough for the web client and the common apps to browse and play:

| Area | Endpoints |
|---|---|
| Server | `GET /System/Info/Public` |
| Sign-in | `POST /Users/AuthenticateByName`, `GET /Users/Me` |
| Browse | `GET /UserViews`, `GET /Items`, `GET /Items/{id}`, `GET /Shows/{id}/Episodes`, `GET /Shows/NextUp`, `GET /Items/Latest`, `GET /UserItems/Resume` |
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

## Deploying the API on AWS

`deploy/cloud-twin/twin-api.yaml` runs the twin as a Lambda function with a public function
URL. From the repository root, with the bucket from `s3-store.yaml`:

```bash
aws cloudformation package --template-file deploy/cloud-twin/twin-api.yaml --s3-bucket <bucket> --s3-prefix deploy-artifacts --output-template-file twin-api.packaged.yaml
```

```bash
aws cloudformation deploy --stack-name jellywing-twin-api --template-file twin-api.packaged.yaml --capabilities CAPABILITY_IAM --parameter-overrides BucketName=<bucket>
```

The stack's `Url` output is the server address to give apps. The function may read the
bucket and write only under `state/`, where play state is kept. The secret that signs access tokens is generated in Secrets Manager.

### Deploying from the repository

`deploy/cloud-twin/pipeline.yaml` sets up a small pipeline, so that a push to the branch is
what deploys the twin. It needs no access to the repository beyond what anyone has to a
public one. Create it once:

```bash
aws cloudformation deploy --stack-name jellywing-twin-pipeline --template-file deploy/cloud-twin/pipeline.yaml --capabilities CAPABILITY_IAM --parameter-overrides RepositoryUrl=https://github.com/<owner>/<name>
```

Every ten minutes it asks the repository where the branch is. For a commit it has not tried
it starts a build of exactly that commit, which runs the twin's tests, deploys the store
and the twin API, checks that the twin answers and still asks for a sign-in, and records
the commit. Nothing is deployed if a step fails, and a commit that failed is not tried
again; push a fix, or start the build by hand to retry.

Where things stand is in two parameters, `/jellywing-twin-pipeline/attempted` and
`/jellywing-twin-pipeline/deployed`: the same commit in both means the branch is live. The
build's log is in CodeBuild.

What it may do is fixed by this stack, not by the branch. The build can change the two twin
stacks and their resources and nothing else in the account, and it can only give the
twin's function a role that carries a boundary: read the bucket, write under `state/`, and
log. The pipeline does not update itself; changing `pipeline.yaml` takes the command above
again. It does not build or publish the web client; the home server does that.

Things to know about this deployment:

- The address is public and sign-in is the only protection. Wrong passwords are limited to
  10 per user name and 20 per caller address in 15 minutes; see `twin/README.md`.
- Links to files are signed with the function's temporary credentials, so a link can stop
  working before its six hours are up, when those credentials rotate.
- The function reads the catalog and the user list at most once a minute, so a publish
  can take a minute to show.
- The web client's files come through the function, compressed. Video and pictures do not;
  they are fetched straight from storage.

## Licensing

The sync agent is part of, or a plugin to, the server, so it is under the server's
license. The twin API is a separate program that shares no code with the server and can
carry its own license.

## Build order

1. **Done.** Publish chosen libraries to storage and write catalog records. See
   [Publishing today](#publishing-today).
2. **Done.** Twin API for sign-in, browsing and artwork, in `twin/`, deployable as a
   Lambda function. The web client is published with the library and served by the twin;
   sign-in, browsing, search and playing have been checked in a browser.
3. **Done.** Playback through signed links, with progress kept.
4. **Done.** Play state, favorites and watchlists sync in both directions. What it does not
   do: push a change to other open devices as it happens.
5. **Done.** Invitations: a single-use link made on the twin; the account is made at home
   on the next sync.

## Decisions made

| Decision | Choice | Effect |
|---|---|---|
| Storage | An object store that does not charge for data transfer out, reached through the S3-compatible API | Running cost is storage only. Any S3-compatible store works; nothing is tied to one provider |
| What to publish | Libraries named in the settings; nothing by default | Storage size and first upload time are whatever the chosen libraries need |

## Publishing today

The scheduled task *Publish to cloud twin* sends the named libraries to the store. It has
no default schedule and does nothing until it is set up.

| Setting | Meaning |
|---|---|
| `twin:libraries` | Names of the libraries to publish, separated by commas |
| `twin:s3:endpoint` | Address of the S3-compatible store, for example `https://<account>.r2.cloudflarestorage.com` |
| `twin:s3:bucket` | The bucket to publish into |
| `twin:s3:accessKeyId`, `twin:s3:secretAccessKey` | The store's credentials. They need to write and delete everywhere, and to read under `state/` |
| `twin:s3:sessionToken` | The session token, when the credentials are temporary ones |
| `twin:s3:region` | The region; `auto` if not set, which suits stores without regions |
| `twin:folder` | A local folder to publish into instead of a store, for trying it out |
| `twin:webClient` | Set to `false` to leave the web client out. It is published otherwise |
| `twin:playState` | Set to `false` to stop syncing play state. It is synced otherwise |
| `twin:invitations` | Set to `false` to stop making accounts for people invited on the twin |
| `twin:includeLimitedUsers` | Set to `true` to let people use the twin whose home account is limited by rating, tag, schedule or a ban on playing. Those limits do not apply there |

Settings are read from the environment with the prefix `JELLYFIN_` and `__` between the
parts, for example `JELLYFIN_twin__s3__bucket`. Keep the credentials in the environment of
the server process; they do not belong in any file in this repository.

For Amazon S3, `deploy/cloud-twin/s3-store.yaml` is a CloudFormation template for a
private, encrypted bucket; its outputs are the values for the endpoint, bucket and region
settings. S3 charges for data transferred out, so it suits trying the twin out more than
serving a busy library.

What lands in the store:

| Key | Content |
|---|---|
| `media/<item id>.mp4` | The playable file: the optimized version, or the original when it already plays everywhere |
| `images/<item id>.<ext>` | The item's poster or thumbnail |
| `catalog/<item id>.json` | The item's record: name, type, series and season, numbers, description, running time, genres, and the keys above |
| `catalog/index.json` | The ids of every published item |
| `catalog/all.json` | Every record in one file, which is what the twin API reads |
| `auth/users.json` | Each published user's id, name, password hash, whether they are an administrator, and the published libraries they may see. Never a password |
| `web/...` | The server's web client, file for file, which the twin serves at `/web/` |
| `state/<user id>.json` | That user's play state and watchlist. Written by the twin and by the server's sync |
| `state/invitations.json` | Invitations by the hash of their code, and the people who accepted one. Written by the twin and by the server's sync |
| `state/limits/...` | Counts of recent failed sign-ins, written by the twin. Safe to delete; the template's bucket removes them after a day |

Movies, series, seasons and episodes are published. A video that still needs an optimized
version is asked for one and published on a later run. Only what changed since the last
run is sent, and items that left the library are removed from the store. What was
published is remembered in `cloud-twin/state.json` under the data folder, and the web
client's files in `cloud-twin/web-state.json`; pointing the server at a different store
starts from nothing.

Limits today: a file larger than 5 GB is not uploaded, since that needs a multipart
upload; uploads are not resumable; nothing is encrypted beyond the connection.

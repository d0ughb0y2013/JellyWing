# JellyWing cloud twin

A small API that lets apps and the JellyWing web client sign in, browse and play a library
that a JellyWing server has published to object storage. Nothing here converts video or scans files; the home server
does that and publishes the results. See [the design](../docs/jellywing/cloud-twin.md).

It has no dependencies and uses only web-standard `Request`, `Response`, `fetch` and Web
Crypto, so the same code runs in Node, behind a cloud function, or in a worker.

## Run it locally

Point it at a folder the server published to (`twin:folder` on the server):

```bash
TWIN_FOLDER=/path/to/published TWIN_TOKEN_SECRET=<16 or more random characters> npm start
```

or at an S3-compatible store:

```bash
TWIN_S3_ENDPOINT=https://s3.us-west-2.amazonaws.com TWIN_S3_BUCKET=<bucket> TWIN_S3_REGION=us-west-2 TWIN_TOKEN_SECRET=<secret> npm start
```

with `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY` and, for temporary credentials,
`AWS_SESSION_TOKEN` in the environment. It listens on port 8097 unless `PORT` says otherwise.
Set `TWIN_LOG=1` to print each request it could not answer.

## The web client

When the server has published its web client (it does unless `twin:webClient` is `false`),
opening the twin's address in a browser gives the same client as at home: sign in, browse,
search and play. The twin serves those files from `web/` in the store without a sign-in,
since they are the same public program for everyone.

Watching on the twin is remembered: where a video stopped, what was watched, favorites and
the watchlist, with resume, next up and stats worked out from them. The home server brings
those changes home and sends its own, every 15 minutes. Live updates are the one thing
missing: a change made on another device shows after a reload.

## Play state

Each user has one file in the store, `state/<user id>.json`. The twin and the home server
both change it, so a write names the version it started from and is retried if the file
changed in between. That needs a store that honors `If-Match` and `If-None-Match` on
writes, which S3 and the common S3-compatible stores do, and credentials that may write
under `state/`. With a store that cannot be written, browsing and playing still work and
nothing is remembered.

## Test

```bash
npm test
```

## What it answers

| Area | Endpoints |
|---|---|
| Web client | `GET /web/...`, served from the store; `/` leads there |
| Invitations | `GET /invite`, `GET /invite/accept`, and the `/Twin/Invitations` calls above |
| Server | `GET /System/Info/Public`, `GET /System/Info`, `GET /System/Ping`, `GET /Branding/Configuration` |
| Sign-in | `POST /Users/AuthenticateByName`, `GET /Users/Me`, `GET /Users/{id}`, `POST /Sessions/Logout` |
| Browse | `GET /UserViews`, `GET /Items`, `GET /Items/{id}`, `GET /Items/Latest`, `GET /Items/{id}/Ancestors`, `GET /Shows/{id}/Seasons`, `GET /Shows/{id}/Episodes` |
| Pictures | `GET /Items/{id}/Images/{type}`, which redirects to a short-lived link |
| Play | `POST /Items/{id}/PlaybackInfo`, `GET /Videos/{id}/stream`, which redirects to a short-lived link to the file |
| Progress | `POST /Sessions/Playing`, `/Progress`, `/Stopped` |
| Watched state | `POST` and `DELETE /UserPlayedItems/{id}`, `/UserFavoriteItems/{id}`, `/UserWatchlistItems/{id}`; `GET /UserItems/Resume`, `/Shows/NextUp`, `/UserWatchlistItems`, `/UserStats` |
| Answered as "none" | Similar, genres, people, display preferences, live TV, group watch |

Everything else answers 404. `/Items` understands `ParentId`, `IncludeItemTypes`,
`Recursive`, `Ids`, `SearchTerm`, `Filters` (favorite, played, unplayed, resumable), `SortBy`,
`SortOrder`, `StartIndex` and `Limit`.

## Invitations

An administrator who is signed in to the web client on the twin opens `/invite` and makes a
link. The link works once and for a set time (7 days unless changed). Whoever opens it
picks a name and a password and can sign in and watch straight away.

The home server makes the matching account on its next sync: same name, same password,
never an administrator, and able to see only the published libraries. From then on that
account is the real one. A device the person was already signed in on carries on, and what
they watched in the meantime comes along. If the home server cannot make the account, for
instance because the name is in use there, the person can no longer sign in and needs a
new invitation.

What is kept, in `state/invitations.json`: a hash of each link's code, never the code, and
for each person waiting a hash of their password in the media server's own format, never
the password. The code travels after the `#` in the link, so it is not in request logs.

Anyone removed or disabled at home stops being able to use the twin within a minute of the
next publish, tokens included.

| Call | Who | Does |
|---|---|---|
| `GET /Twin/Invitations` | administrator | lists invitations and their state |
| `POST /Twin/Invitations` with `{ "Days": 7 }` | administrator | makes one; the answer holds the code, once |
| `DELETE /Twin/Invitations/{id}` | administrator | withdraws one nobody has used |
| `GET /Twin/Invitations/Check/{code}` | anyone | says whether a code still works |
| `POST /Twin/Invitations/Accept` with `Code`, `Name`, `Password` | anyone with a code | makes the person |

Trying codes is limited per caller address like sign-in is.

## Sign-in limits

Wrong passwords are counted per user name and per caller address. After 10 for one name,
or 20 from one address, in 15 minutes, further attempts for that name or from that address
get a 429 with `Retry-After`, and the password is not looked at, until the window has
passed. A sign-in that works clears the count for that name. Unknown names count the same
as known ones. An IPv6 caller is counted by its /64.

The counts are kept in the store under `state/limits/`, by a hash of the name or address,
so every running copy shares them. Each copy also counts on its own, so the limit still
stands if the store cannot be read or written. Whoever runs the twin passes in the
caller's address as the host saw it: `twin.handle(request, { clientAddress })`. Without
one, all callers share a single limit.

Someone who knows a user name can keep that name locked by guessing wrong on purpose. The
lock only affects signing in; devices already signed in keep working.

## How sign-in works

The server publishes each user's name and password hash, never the password. The twin
checks a sign-in against the hash the same way the server does, then issues an access
token it signs with `TWIN_TOKEN_SECRET`. Tokens last 30 days. Changing the secret signs
everyone out. A video is listed only once its file has been uploaded.

## Other files

- `src/lambda.mjs` wraps the same API as an AWS Lambda handler for a function URL or an
  HTTP API. The function's role supplies the credentials.
- `src/local.mjs` is the local server above.

## License

MIT; see [LICENSE](LICENSE). This program shares no code with the media server and talks
only to the published files, so it carries its own license. The web client it can serve is
not part of this program: the server publishes it, and it stays under its own license.

# JellyWing cloud twin

A small API that lets apps sign in, browse and play a library that a JellyWing server has
published to object storage. Nothing here converts video or scans files; the home server
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

## Test

```bash
npm test
```

## What it answers

| Area | Endpoints |
|---|---|
| Server | `GET /System/Info/Public` |
| Sign-in | `POST /Users/AuthenticateByName`, `GET /Users/Me`, `GET /Users/{id}` |
| Browse | `GET /UserViews`, `GET /Items`, `GET /Items/{id}`, `GET /Shows/{id}/Seasons`, `GET /Shows/{id}/Episodes` |
| Pictures | `GET /Items/{id}/Images/{type}`, which redirects to a short-lived link |
| Play | `POST /Items/{id}/PlaybackInfo`, `GET /Videos/{id}/stream`, which redirects to a short-lived link to the file |
| Progress | `POST /Sessions/Playing`, `/Progress`, `/Stopped` are accepted and not stored yet |

Everything else answers 404. `/Items` understands `ParentId`, `IncludeItemTypes`,
`Recursive`, `Ids`, `SearchTerm`, `SortBy`, `SortOrder`, `StartIndex` and `Limit`.

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
only to the published files, so it carries its own license.

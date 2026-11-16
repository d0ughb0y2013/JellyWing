// Runs the twin on this machine, for trying it out.
//
//   TWIN_TOKEN_SECRET   secret that signs access tokens (required, 16 characters or more)
//   TWIN_FOLDER         a folder the server published to, or
//   TWIN_S3_ENDPOINT, TWIN_S3_BUCKET, TWIN_S3_REGION, AWS_ACCESS_KEY_ID,
//   AWS_SECRET_ACCESS_KEY, AWS_SESSION_TOKEN   an S3-compatible store
//   PORT                defaults to 8097
//   TWIN_LOG            set to log requests the twin could not answer (optional)

import { createReadStream } from 'node:fs';
import { stat } from 'node:fs/promises';
import { createServer } from 'node:http';
import { join, normalize, resolve, sep } from 'node:path';

import { FolderStore, S3Store } from './stores.mjs';
import { createTwin } from './twin.mjs';

const port = parseInt(process.env.PORT || '8097', 10);
const origin = `http://localhost:${port}`;
const folder = process.env.TWIN_FOLDER && resolve(process.env.TWIN_FOLDER);

const store = folder
    ? new FolderStore(folder, `${origin}/__files`)
    : new S3Store({
        endpoint: process.env.TWIN_S3_ENDPOINT,
        bucket: process.env.TWIN_S3_BUCKET,
        region: process.env.TWIN_S3_REGION,
        accessKeyId: process.env.AWS_ACCESS_KEY_ID,
        secretAccessKey: process.env.AWS_SECRET_ACCESS_KEY,
        sessionToken: process.env.AWS_SESSION_TOKEN
    });

const twin = createTwin({ store, tokenSecret: process.env.TWIN_TOKEN_SECRET, serverName: process.env.TWIN_NAME });

// Stands in for the store's own file delivery when publishing to a folder.
async function serveFile(request, response) {
    const path = normalize(join(folder, decodeURIComponent(new URL(request.url, origin).pathname.slice('/__files/'.length))));
    if (!path.startsWith(folder + sep)) {
        response.writeHead(403).end();
        return;
    }

    let size;
    try {
        size = (await stat(path)).size;
    } catch {
        response.writeHead(404).end();
        return;
    }

    const type = path.endsWith('.mp4') ? 'video/mp4' : path.endsWith('.png') ? 'image/png' : path.endsWith('.json') ? 'application/json' : 'image/jpeg';
    const range = /^bytes=(\d*)-(\d*)$/.exec(request.headers.range || '');
    const headers = { 'Content-Type': type, 'Accept-Ranges': 'bytes', 'Access-Control-Allow-Origin': '*' };
    if (!range) {
        response.writeHead(200, { ...headers, 'Content-Length': size });
        createReadStream(path).pipe(response);
        return;
    }

    const start = range[1] ? parseInt(range[1], 10) : Math.max(size - parseInt(range[2], 10), 0);
    const end = range[1] && range[2] ? Math.min(parseInt(range[2], 10), size - 1) : size - 1;
    response.writeHead(206, { ...headers, 'Content-Length': end - start + 1, 'Content-Range': `bytes ${start}-${end}/${size}` });
    createReadStream(path, { start, end }).pipe(response);
}

createServer(async (request, response) => {
    if (folder && request.url.startsWith('/__files/')) {
        await serveFile(request, response);
        return;
    }

    const chunks = [];
    for await (const chunk of request) chunks.push(chunk);
    const hasBody = request.method !== 'GET' && request.method !== 'HEAD';
    const answer = await twin.handle(new Request(origin + request.url, {
        method: request.method,
        headers: request.headers,
        body: hasBody ? Buffer.concat(chunks) : undefined
    }));

    if (process.env.TWIN_LOG && answer.status >= 400) {
        console.log(`${answer.status} ${request.method} ${request.url.split('?')[0]}`);
    }

    response.writeHead(answer.status, Object.fromEntries(answer.headers));
    response.end(Buffer.from(await answer.arrayBuffer()));
}).listen(port, () => console.log(`JellyWing twin listening on ${origin}`));

// Runs the twin as an AWS Lambda function behind a function URL or an HTTP API.
//
//   TWIN_TOKEN_SECRET, TWIN_S3_BUCKET   required
//   TWIN_S3_ENDPOINT                    defaults to S3 in the function's own region
//   TWIN_NAME                           the server name apps show
//
// The function's own role supplies the credentials.

import { gzipSync } from 'node:zlib';

import { S3Store } from './stores.mjs';
import { createTwin } from './twin.mjs';

const region = process.env.TWIN_S3_REGION || process.env.AWS_REGION;

const twin = createTwin({
    store: new S3Store({
        endpoint: process.env.TWIN_S3_ENDPOINT || `https://s3.${region}.amazonaws.com`,
        bucket: process.env.TWIN_S3_BUCKET,
        region,
        accessKeyId: process.env.AWS_ACCESS_KEY_ID,
        secretAccessKey: process.env.AWS_SECRET_ACCESS_KEY,
        sessionToken: process.env.AWS_SESSION_TOKEN
    }),
    tokenSecret: process.env.TWIN_TOKEN_SECRET,
    serverName: process.env.TWIN_NAME
});

export async function handler(event) {
    const method = event.requestContext.http.method;
    const url = `https://${event.requestContext.domainName}${event.rawPath}${event.rawQueryString ? '?' + event.rawQueryString : ''}`;
    const hasBody = event.body && method !== 'GET' && method !== 'HEAD';
    const answer = await twin.handle(new Request(url, {
        method,
        headers: event.headers,
        body: hasBody ? Buffer.from(event.body, event.isBase64Encoded ? 'base64' : 'utf8') : undefined
    }), { clientAddress: event.requestContext.http.sourceIp });

    const headers = Object.fromEntries(answer.headers);
    let bytes = Buffer.from(await answer.arrayBuffer());
    const isText = /^text\/|json|javascript|svg/.test(headers['content-type'] || '');

    // The web client's scripts are large and the function URL does not compress.
    if (isText && bytes.length > 1024 && /\bgzip\b/.test(event.headers?.['accept-encoding'] || '')) {
        bytes = gzipSync(bytes);
        headers['content-encoding'] = 'gzip';
        headers.vary = 'Accept-Encoding';
    } else if (isText) {
        return { statusCode: answer.status, headers, body: bytes.toString('utf8') };
    }

    return { statusCode: answer.status, headers, body: bytes.toString('base64'), isBase64Encoded: true };
}

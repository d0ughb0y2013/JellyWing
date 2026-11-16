// AWS Signature Version 4 for S3 and S3-compatible stores, using only Web Crypto so the
// same code runs in Node, in a cloud function and in a worker.

const encoder = new TextEncoder();
const hex = bytes => [...new Uint8Array(bytes)].map(b => b.toString(16).padStart(2, '0')).join('');

async function sha256(text) {
    return hex(await crypto.subtle.digest('SHA-256', encoder.encode(text)));
}

async function hmac(key, text) {
    const imported = await crypto.subtle.importKey('raw', key, { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
    return new Uint8Array(await crypto.subtle.sign('HMAC', imported, encoder.encode(text)));
}

async function sign(secretAccessKey, date, region, stringToSign) {
    let key = await hmac(encoder.encode('AWS4' + secretAccessKey), date);
    for (const part of [region, 's3', 'aws4_request']) {
        key = await hmac(key, part);
    }

    return hex(await hmac(key, stringToSign));
}

const timestampOf = now => now.toISOString().replace(/[-:]|\.\d{3}/g, '');

// encodeURIComponent leaves a few characters alone that the signature rules want escaped.
const escape = text => encodeURIComponent(text).replace(/[!'()*]/g, c => '%' + c.charCodeAt(0).toString(16).toUpperCase());

/** Encodes a key for use as a URL path, keeping the slashes between its parts. */
export const encodeKey = key => key.split('/').map(escape).join('/');

export const EMPTY_PAYLOAD_HASH = 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855';

/**
 * Builds a link to an object that works without credentials until it expires.
 * @param {string} objectUrl The object's address, with no query string.
 * @param {{region: string, accessKeyId: string, secretAccessKey: string, sessionToken?: string}} credentials
 * @param {number} expiresSeconds How long the link works.
 * @param {Date} [now]
 * @returns {Promise<string>}
 */
export async function presignUrl(objectUrl, credentials, expiresSeconds, now = new Date()) {
    const url = new URL(objectUrl);
    const timestamp = timestampOf(now);
    const date = timestamp.slice(0, 8);
    const scope = `${date}/${credentials.region}/s3/aws4_request`;

    const query = {
        'X-Amz-Algorithm': 'AWS4-HMAC-SHA256',
        'X-Amz-Credential': `${credentials.accessKeyId}/${scope}`,
        'X-Amz-Date': timestamp,
        'X-Amz-Expires': String(expiresSeconds),
        'X-Amz-SignedHeaders': 'host'
    };
    if (credentials.sessionToken) {
        query['X-Amz-Security-Token'] = credentials.sessionToken;
    }

    const canonicalQuery = Object.keys(query).sort().map(k => `${escape(k)}=${escape(query[k])}`).join('&');
    const canonicalRequest = ['GET', url.pathname, canonicalQuery, `host:${url.host}\n`, 'host', 'UNSIGNED-PAYLOAD'].join('\n');
    const stringToSign = ['AWS4-HMAC-SHA256', timestamp, scope, await sha256(canonicalRequest)].join('\n');
    const signature = await sign(credentials.secretAccessKey, date, credentials.region, stringToSign);

    return `${url.origin}${url.pathname}?${canonicalQuery}&X-Amz-Signature=${signature}`;
}

/** The SHA-256 of some text or bytes, in hex. */
export async function sha256Hex(content) {
    return hex(await crypto.subtle.digest('SHA-256', typeof content === 'string' ? encoder.encode(content) : content));
}

/**
 * Builds the headers that sign a request.
 * @param {string} method GET, PUT or DELETE.
 * @param {string} objectUrl The object's address, with no query string.
 * @param {{region: string, accessKeyId: string, secretAccessKey: string, sessionToken?: string}} credentials
 * @param {string} [payloadHash] The SHA-256 of the body in hex; the hash of nothing if there is no body.
 * @param {Date} [now]
 * @returns {Promise<Record<string, string>>}
 */
export async function signedHeaders(method, objectUrl, credentials, payloadHash = EMPTY_PAYLOAD_HASH, now = new Date()) {
    const url = new URL(objectUrl);
    const timestamp = timestampOf(now);
    const date = timestamp.slice(0, 8);
    const scope = `${date}/${credentials.region}/s3/aws4_request`;

    const headers = { host: url.host, 'x-amz-content-sha256': payloadHash, 'x-amz-date': timestamp };
    if (credentials.sessionToken) {
        headers['x-amz-security-token'] = credentials.sessionToken;
    }

    const names = Object.keys(headers).sort();
    const canonicalRequest = [method, url.pathname, '', names.map(n => `${n}:${headers[n]}\n`).join(''), names.join(';'), payloadHash].join('\n');
    const stringToSign = ['AWS4-HMAC-SHA256', timestamp, scope, await sha256(canonicalRequest)].join('\n');
    const signature = await sign(credentials.secretAccessKey, date, credentials.region, stringToSign);

    delete headers.host;
    headers.Authorization = `AWS4-HMAC-SHA256 Credential=${credentials.accessKeyId}/${scope},SignedHeaders=${names.join(';')},Signature=${signature}`;
    return headers;
}

/** Builds the headers that sign a GET request with an empty body. */
export function signedGetHeaders(objectUrl, credentials, now = new Date()) {
    return signedHeaders('GET', objectUrl, credentials, EMPTY_PAYLOAD_HASH, now);
}

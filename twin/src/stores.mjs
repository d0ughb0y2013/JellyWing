// Where the twin reads the published library from.

import { encodeKey, presignUrl, sha256Hex, signedGetHeaders, signedHeaders } from './sigv4.mjs';

/** An S3-compatible store, addressed by path. */
export class S3Store {
    /**
     * @param {{endpoint: string, bucket: string, region?: string, accessKeyId: string, secretAccessKey: string, sessionToken?: string}} options
     */
    constructor(options) {
        this.base = `${options.endpoint.replace(/\/+$/, '')}/${options.bucket}`;
        this.credentials = {
            region: options.region || 'auto',
            accessKeyId: options.accessKeyId,
            secretAccessKey: options.secretAccessKey,
            sessionToken: options.sessionToken
        };
    }

    url(key) {
        return `${this.base}/${encodeKey(key)}`;
    }

    async getJson(key) {
        const url = this.url(key);
        const response = await fetch(url, { headers: await signedGetHeaders(url, this.credentials) });
        if (response.status === 404) return null;
        if (!response.ok) throw new Error(`The store answered ${response.status} for ${key}`);
        return response.json();
    }

    /** The file's bytes, or null when there is no such file. */
    async getBytes(key) {
        const url = this.url(key);
        const response = await fetch(url, { headers: await signedGetHeaders(url, this.credentials) });
        if (response.status === 404 || response.status === 403) return null;
        if (!response.ok) throw new Error(`The store answered ${response.status} for ${key}`);
        return new Uint8Array(await response.arrayBuffer());
    }

    /** A text file and the version it is at, or null when there is no such file. */
    async getText(key) {
        const url = this.url(key);
        const response = await fetch(url, { headers: await signedGetHeaders(url, this.credentials) });
        if (response.status === 404) return null;
        if (!response.ok) throw new Error(`The store answered ${response.status} for ${key}`);
        return { text: await response.text(), tag: response.headers.get('etag') };
    }

    /**
     * Writes a text file only if it is still at the version given, or does not exist when none is given.
     * @returns {Promise<{written: boolean, tag?: string}>}
     */
    async putText(key, text, tag) {
        const url = this.url(key);
        const body = new TextEncoder().encode(text);
        const headers = await signedHeaders('PUT', url, this.credentials, await sha256Hex(body));
        headers['Content-Type'] = 'application/json';
        if (tag) {
            headers['If-Match'] = tag;
        } else {
            headers['If-None-Match'] = '*';
        }

        const response = await fetch(url, { method: 'PUT', headers, body });
        if (response.status === 412 || response.status === 409) return { written: false };
        if (!response.ok) throw new Error(`The store answered ${response.status} to a write of ${key}`);
        return { written: true, tag: response.headers.get('etag') };
    }

    signedUrl(key, expiresSeconds) {
        return presignUrl(this.url(key), this.credentials, expiresSeconds);
    }
}

/** A folder on disk in the same layout, for running the twin locally. Files are served by the local server. */
export class FolderStore {
    constructor(root, filesBaseUrl) {
        this.root = root;
        this.filesBaseUrl = filesBaseUrl;
    }

    async getJson(key) {
        const { readFile } = await import('node:fs/promises');
        const { join } = await import('node:path');
        try {
            return JSON.parse(await readFile(join(this.root, key), 'utf8'));
        } catch (error) {
            if (error.code === 'ENOENT') return null;
            throw error;
        }
    }

    async getBytes(key) {
        const { readFile } = await import('node:fs/promises');
        const { join } = await import('node:path');
        try {
            return new Uint8Array(await readFile(join(this.root, key)));
        } catch (error) {
            if (error.code === 'ENOENT' || error.code === 'EISDIR') return null;
            throw error;
        }
    }

    async getText(key) {
        const bytes = await this.getBytes(key);
        return bytes && { text: new TextDecoder().decode(bytes), tag: await sha256Hex(bytes) };
    }

    // Good enough for one local process: the check and the write are not one step.
    async putText(key, text, tag) {
        const { mkdir, rename, writeFile } = await import('node:fs/promises');
        const { dirname, join } = await import('node:path');
        const current = await this.getText(key);
        if ((current?.tag ?? null) !== (tag ?? null)) return { written: false };

        const path = join(this.root, key);
        await mkdir(dirname(path), { recursive: true });
        await writeFile(path + '.partial', text);
        await rename(path + '.partial', path);
        return { written: true, tag: await sha256Hex(new TextEncoder().encode(text)) };
    }

    async signedUrl(key) {
        return `${this.filesBaseUrl}/${encodeKey(key)}`;
    }
}

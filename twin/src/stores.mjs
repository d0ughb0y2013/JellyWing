// Where the twin reads the published library from.

import { encodeKey, presignUrl, signedGetHeaders } from './sigv4.mjs';

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

    async signedUrl(key) {
        return `${this.filesBaseUrl}/${encodeKey(key)}`;
    }
}

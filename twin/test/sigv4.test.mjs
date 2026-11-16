import assert from 'node:assert/strict';
import { test } from 'node:test';

import { encodeKey, presignUrl, signedGetHeaders } from '../src/sigv4.mjs';

// The key pair and requests below are the examples in Amazon's Signature Version 4
// documentation for S3. They are public sample data, not credentials.
const example = { region: 'us-east-1', accessKeyId: 'AKIAIOSFODNN7EXAMPLE', secretAccessKey: 'wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY' };
const when = new Date(Date.UTC(2013, 4, 24));

test('presignUrl reproduces Amazon\'s published presigned URL', async () => {
    const url = await presignUrl('https://examplebucket.s3.amazonaws.com/test.txt', example, 86400, when);

    assert.equal(
        url,
        'https://examplebucket.s3.amazonaws.com/test.txt?X-Amz-Algorithm=AWS4-HMAC-SHA256'
        + '&X-Amz-Credential=AKIAIOSFODNN7EXAMPLE%2F20130524%2Fus-east-1%2Fs3%2Faws4_request'
        + '&X-Amz-Date=20130524T000000Z&X-Amz-Expires=86400&X-Amz-SignedHeaders=host'
        + '&X-Amz-Signature=aeeed9bbccd4d02ee5c0109b86d86835f995330da4c265957d157751f604d404');
});

test('a session token is carried in the link and changes the signature', async () => {
    const plain = await presignUrl('https://store.example/bucket/media/a.mp4', example, 60, when);
    const temporary = await presignUrl('https://store.example/bucket/media/a.mp4', { ...example, sessionToken: 'tok/en+1' }, 60, when);

    assert.match(temporary, /X-Amz-Security-Token=tok%2Fen%2B1/);
    assert.notEqual(new URL(plain).searchParams.get('X-Amz-Signature'), new URL(temporary).searchParams.get('X-Amz-Signature'));
});

test('signedGetHeaders signs the date, the empty body and the host', async () => {
    const headers = await signedGetHeaders('https://store.example/bucket/catalog/all.json', example, when);

    assert.equal(headers['x-amz-date'], '20130524T000000Z');
    assert.match(headers.Authorization, /SignedHeaders=host;x-amz-content-sha256;x-amz-date,Signature=[0-9a-f]{64}$/);
    assert.equal(headers.host, undefined);
});

test('encodeKey escapes each part and keeps the slashes', () => {
    assert.equal(encodeKey('images/a b+c(1).jpg'), 'images/a%20b%2Bc%281%29.jpg');
});

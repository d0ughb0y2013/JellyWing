import assert from 'node:assert/strict';
import { pbkdf2Sync } from 'node:crypto';
import { test } from 'node:test';

import { createTwin, verifyPassword } from '../src/twin.mjs';

const SERIES = 'aaaaaaaa000000000000000000000001';
const SEASON = 'aaaaaaaa000000000000000000000002';
const EPISODE_1 = 'aaaaaaaa000000000000000000000003';
const EPISODE_2 = 'aaaaaaaa000000000000000000000004';
const MOVIE = 'bbbbbbbb000000000000000000000001';
const WAITING = 'bbbbbbbb000000000000000000000002';

function hashOf(password) {
    const salt = Buffer.from('0123456789abcdef', 'hex');
    return `$PBKDF2-SHA512$iterations=1000$${salt.toString('hex').toUpperCase()}$${pbkdf2Sync(password, salt, 1000, 64, 'sha512').toString('hex').toUpperCase()}`;
}

const files = {
    'auth/users.json': [{ Id: 'cccccccc-0000-0000-0000-000000000001', Name: 'Alice', Password: hashOf('correct horse') }],
    'catalog/all.json': [
        { Id: SERIES, Type: 'Series', Name: 'A Show', Library: 'TV', ImageKey: `images/${SERIES}.jpg` },
        { Id: SEASON, Type: 'Season', Name: 'Season 1', Library: 'TV', SeriesId: SERIES, IndexNumber: 1 },
        { Id: EPISODE_2, Type: 'Episode', Name: 'Second', Library: 'TV', SeriesId: SERIES, SeasonId: SEASON, IndexNumber: 2, ParentIndexNumber: 1, MediaKey: `media/${EPISODE_2}.mp4`, MediaSize: 20 },
        { Id: EPISODE_1, Type: 'Episode', Name: 'First', Library: 'TV', SeriesId: SERIES, SeasonId: SEASON, IndexNumber: 1, ParentIndexNumber: 1, MediaKey: `media/${EPISODE_1}.mp4`, MediaSize: 10 },
        { Id: MOVIE, Type: 'Movie', Name: 'A Film', Library: 'Movies', MediaKey: `media/${MOVIE}.mp4`, MediaSize: 30, RunTimeTicks: 600000000 },
        { Id: WAITING, Type: 'Movie', Name: 'Not Uploaded Yet', Library: 'Movies' }
    ]
};

const store = {
    getJson: async key => files[key] ?? null,
    signedUrl: async (key, seconds) => `https://cdn.example/${key}?expires=${seconds}`
};

function newTwin(now) {
    return createTwin({ store, tokenSecret: 'a-test-secret-of-enough-length', now });
}

async function call(twin, path, { method = 'GET', token, body } = {}) {
    const headers = token ? { Authorization: `MediaBrowser Client="test", Token="${token}"` } : {};
    const response = await twin.handle(new Request('https://twin.example' + path, { method, headers, body: body && JSON.stringify(body) }));
    const text = await response.text();
    return { status: response.status, headers: response.headers, body: text ? JSON.parse(text) : null };
}

async function signIn(twin) {
    const answer = await call(twin, '/Users/AuthenticateByName', { method: 'POST', body: { Username: 'alice', Pw: 'correct horse' } });
    assert.equal(answer.status, 200);
    return answer.body.AccessToken;
}

test('verifyPassword accepts the right password and only that', async () => {
    assert.equal(await verifyPassword('correct horse', hashOf('correct horse')), true);
    assert.equal(await verifyPassword('wrong', hashOf('correct horse')), false);
    assert.equal(await verifyPassword('anything', 'not a hash'), false);
});

test('the public server info names a compatible server', async () => {
    const info = (await call(newTwin(), '/System/Info/Public')).body;

    assert.equal(info.ProductName, 'Jellyfin Server');
    assert.equal(info.StartupWizardCompleted, true);
    assert.match(info.Id, /^[0-9a-f]{32}$/);
});

test('sign-in works with the right password, whatever the case of the name', async () => {
    const answer = await call(newTwin(), '/Users/AuthenticateByName', { method: 'POST', body: { Username: 'ALICE', Pw: 'correct horse' } });

    assert.equal(answer.status, 200);
    assert.equal(answer.body.User.Name, 'Alice');
    assert.equal(answer.body.User.Id, 'cccccccc000000000000000000000001');
    assert.ok(answer.body.AccessToken);
});

test('sign-in refuses a wrong password and an unknown user the same way', async () => {
    const twin = newTwin();
    const wrong = await call(twin, '/Users/AuthenticateByName', { method: 'POST', body: { Username: 'alice', Pw: 'nope' } });
    const unknown = await call(twin, '/Users/AuthenticateByName', { method: 'POST', body: { Username: 'mallory', Pw: 'correct horse' } });

    assert.equal(wrong.status, 401);
    assert.deepEqual(unknown, wrong);
});

test('browsing needs a valid token', async () => {
    const twin = newTwin();
    const token = await signIn(twin);

    assert.equal((await call(twin, '/Items')).status, 401);
    assert.equal((await call(twin, '/Items', { token: token.slice(0, -2) + 'xx' })).status, 401);
    assert.equal((await call(twin, '/Items', { token: 'x' + token })).status, 401);
    assert.equal((await call(twin, '/Items', { token })).status, 200);
});

test('a token stops working when it expires', async () => {
    let time = Date.UTC(2026, 0, 1);
    const twin = newTwin(() => time);
    const token = await signIn(twin);

    time += 31 * 24 * 3600 * 1000;

    assert.equal((await call(twin, '/Users/Me', { token })).status, 401);
});

test('libraries, then their top level, then seasons, then episodes in order', async () => {
    const twin = newTwin();
    const token = await signIn(twin);

    const views = (await call(twin, '/UserViews', { token })).body.Items;
    assert.deepEqual(views.map(v => [v.Name, v.CollectionType]), [['Movies', 'movies'], ['TV', 'tvshows']]);

    const tv = views.find(v => v.Name === 'TV');
    const shows = (await call(twin, `/Items?ParentId=${tv.Id}`, { token })).body;
    assert.deepEqual(shows.Items.map(i => i.Name), ['A Show']);
    assert.equal(shows.Items[0].ChildCount, 1);
    assert.equal(shows.Items[0].RecursiveItemCount, 2);

    const seasons = (await call(twin, `/Shows/${SERIES}/Seasons`, { token })).body;
    assert.deepEqual(seasons.Items.map(i => i.Name), ['Season 1']);

    const episodes = (await call(twin, `/Shows/${SERIES}/Episodes?seasonId=${SEASON}`, { token })).body;
    assert.deepEqual(episodes.Items.map(i => i.Name), ['First', 'Second']);
    assert.equal(episodes.Items[0].SeriesName, 'A Show');
    assert.equal(episodes.Items[0].MediaType, 'Video');
});

test('a video that has not been uploaded yet is not listed or playable', async () => {
    const twin = newTwin();
    const token = await signIn(twin);
    const movies = (await call(twin, '/Items?IncludeItemTypes=Movie&Recursive=true', { token })).body;

    assert.deepEqual(movies.Items.map(i => i.Name), ['A Film']);
    assert.equal((await call(twin, `/Items/${WAITING}/PlaybackInfo`, { method: 'POST', token })).status, 404);
    assert.equal((await call(twin, `/Videos/${WAITING}/stream.mp4`, { token })).status, 404);
});

test('paging, searching and item lookup by id with or without dashes', async () => {
    const twin = newTwin();
    const token = await signIn(twin);

    const page = (await call(twin, '/Items?Recursive=true&IncludeItemTypes=Episode&StartIndex=1&Limit=1', { token })).body;
    assert.equal(page.TotalRecordCount, 2);
    assert.deepEqual(page.Items.map(i => i.Name), ['Second']);

    const found = (await call(twin, '/Items?Recursive=true&SearchTerm=film', { token })).body;
    assert.deepEqual(found.Items.map(i => i.Name), ['A Film']);

    const dashed = 'bbbbbbbb-0000-0000-0000-000000000001';
    assert.equal((await call(twin, `/Users/anyone/Items/${dashed}`, { token })).body.Name, 'A Film');
});

test('playback hands out one directly playable source and a short-lived link to the file', async () => {
    const twin = newTwin();
    const token = await signIn(twin);

    const info = (await call(twin, `/Items/${MOVIE}/PlaybackInfo`, { method: 'POST', token })).body;
    assert.equal(info.MediaSources.length, 1);
    assert.equal(info.MediaSources[0].SupportsDirectPlay, true);
    assert.equal(info.MediaSources[0].SupportsTranscoding, false);

    const stream = await call(twin, `/Videos/${MOVIE}/stream.mp4?static=true&ApiKey=${token}`);
    assert.equal(stream.status, 302);
    assert.equal(stream.headers.get('Location'), `https://cdn.example/media/${MOVIE}.mp4?expires=21600`);
    assert.equal((await call(twin, `/Videos/${MOVIE}/stream.mp4?static=true`)).status, 401);
});

test('pictures redirect to the store without a token; missing ones are 404', async () => {
    const twin = newTwin();

    const picture = await call(twin, `/Items/${SERIES}/Images/Primary`);
    assert.equal(picture.status, 302);
    assert.match(picture.headers.get('Location'), new RegExp(`images/${SERIES}\\.jpg`));
    assert.equal((await call(twin, `/Items/${MOVIE}/Images/Primary`)).status, 404);
});

test('progress reports are accepted, and anything else is refused clearly', async () => {
    const twin = newTwin();
    const token = await signIn(twin);

    assert.equal((await call(twin, '/Sessions/Playing/Progress', { method: 'POST', token, body: {} })).status, 204);
    assert.equal((await call(twin, '/System/Configuration', { token })).status, 404);
    assert.equal((await twin.handle(new Request('https://twin.example/Items', { method: 'OPTIONS' }))).status, 204);
});

test('a short token secret is refused', () => {
    assert.throws(() => createTwin({ store, tokenSecret: 'short' }));
});

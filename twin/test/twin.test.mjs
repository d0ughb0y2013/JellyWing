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

const webFiles = {
    'web/index.html': '<!doctype html><title>web client</title>',
    'web/main.0123456789abcdef0123.chunk.js': 'console.log(1)',
    'web/themes/dark/theme.css': 'body{}'
};
const bytesRead = [];

// Text files with versions, the way a real store keeps them.
const texts = new Map();
const writes = [];
let failNextWrite = false;
let refuseWrites = false;
let refuseReads = false;

const store = {
    getText: async key => {
        if (refuseReads) throw new Error('The store answered 500');
        return texts.has(key) ? { ...texts.get(key) } : null;
    },
    putText: async (key, text, tag) => {
        if (refuseWrites) throw new Error('The store answered 403');
        if (failNextWrite) {
            // Someone else wrote first.
            failNextWrite = false;
            const other = JSON.parse(texts.get(key)?.text || '{"Items":{},"Watchlist":{}}');
            other.Watchlist[SERIES] = { On: true, Changed: 1 };
            texts.set(key, { text: JSON.stringify(other), tag: 'other-' + writes.length });
        }

        if ((texts.get(key)?.tag ?? null) !== (tag ?? null)) return { written: false };
        writes.push(key);
        texts.set(key, { text, tag: 'v' + writes.length });
        return { written: true, tag: 'v' + writes.length };
    },
    getJson: async key => files[key] ?? null,
    getBytes: async key => {
        bytesRead.push(key);
        return key in webFiles ? new TextEncoder().encode(webFiles[key]) : null;
    },
    signedUrl: async (key, seconds) => `https://cdn.example/${key}?expires=${seconds}`
};

function newTwin(now, { keep = false } = {}) {
    if (!keep) {
        texts.clear();
        writes.length = 0;
        refuseWrites = false;
        refuseReads = false;
    }

    return createTwin({ store, tokenSecret: 'a-test-secret-of-enough-length', now });
}

async function call(twin, path, { method = 'GET', token, body, from } = {}) {
    const headers = token ? { Authorization: `MediaBrowser Client="test", Token="${token}"` } : {};
    const response = await twin.handle(
        new Request('https://twin.example' + path, { method, headers, body: body && JSON.stringify(body) }),
        { clientAddress: from });
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

test('the web client is served from the store without signing in', async () => {
    const twin = newTwin();
    const raw = path => twin.handle(new Request('https://twin.example' + path));

    for (const path of ['/', '/web']) {
        const answer = await raw(path);
        assert.equal(answer.status, 302);
        assert.equal(answer.headers.get('location'), '/web/');
    }

    const page = await raw('/web/');
    assert.equal(page.status, 200);
    assert.match(page.headers.get('content-type'), /^text\/html/);
    assert.equal(page.headers.get('cache-control'), 'no-cache');
    assert.match(await page.text(), /web client/);

    const script = await raw('/web/main.0123456789abcdef0123.chunk.js');
    assert.match(script.headers.get('content-type'), /^text\/javascript/);
    assert.match(script.headers.get('cache-control'), /immutable/);

    assert.match((await raw('/web/themes/dark/theme.css')).headers.get('content-type'), /^text\/css/);
    assert.equal((await raw('/web/themes/dark/theme.css')).headers.get('cache-control'), 'no-cache');
    assert.match((await raw('/web/themes/dark/theme.css?0123456789abcdef0123')).headers.get('cache-control'), /immutable/);
    assert.equal((await raw('/web/missing.js')).status, 404);
});

test('a web client path cannot reach the rest of the store', async () => {
    const twin = newTwin();
    bytesRead.length = 0;
    for (const path of ['/web/..%2Fauth/users.json', '/web/%2E%2E/auth/users.json', '/web/a/..%5C..%5Cauth/users.json', '/web//auth/users.json', '/web/%ZZ']) {
        const answer = await twin.handle(new Request('https://twin.example' + path));
        // Some of these are collapsed by URL parsing before the twin sees them, and then need a sign-in.
        assert.ok(answer.status === 404 || answer.status === 401, `${path} answered ${answer.status}`);
    }

    assert.ok(bytesRead.every(key => key.startsWith('web/') && !key.includes('..')), bytesRead.join(' '));
});

test('what the web client asks for before sign-in is answered', async () => {
    const twin = newTwin();
    assert.equal((await call(twin, '/System/Ping')).status, 200);
    assert.equal((await call(twin, '/QuickConnect/Enabled')).body, false);
    assert.equal((await call(twin, '/Branding/Configuration')).status, 200);
    assert.equal((await call(twin, '/System/Info')).status, 401);
});

test('the newest items in a library are listed', async () => {
    const twin = newTwin();
    const token = await signIn(twin);
    const views = await call(twin, '/UserViews', { token });
    const tv = views.body.Items.find(v => v.Name === 'TV');

    const latest = await call(twin, `/Items/Latest?ParentId=${tv.Id}`, { token });
    assert.equal(latest.status, 200);
    assert.ok(Array.isArray(latest.body));
    assert.deepEqual(latest.body.map(i => i.Name), ['A Show']);

    const old = await call(twin, `/Users/someone/Items/Latest?ParentId=${tv.Id}`, { token });
    assert.deepEqual(old.body.map(i => i.Name), ['A Show']);
});

test('things the twin does not hold are answered as none, in the shape an app expects', async () => {
    const twin = newTwin();
    const token = await signIn(twin);

    for (const path of [`/Items/${MOVIE}/Similar`, '/Genres', '/LiveTv/Programs', `/Items/${MOVIE}/Collections`]) {
        const answer = await call(twin, path, { token });
        assert.equal(answer.status, 200, path);
        assert.deepEqual(answer.body.Items, [], path);
    }

    for (const path of ['/Sessions', '/SyncPlay/List', `/Items/${MOVIE}/SpecialFeatures`]) {
        assert.deepEqual((await call(twin, path, { token })).body, [], path);
    }

    assert.equal((await call(twin, '/DisplayPreferences/usersettings?client=emby', { token })).body.Id, 'usersettings');
    assert.equal((await call(twin, '/DisplayPreferences/usersettings?client=emby', { token, method: 'POST', body: {} })).status, 204);
    assert.equal((await call(twin, `/Items/${MOVIE}/OptimizedVersion`, { token })).body.State, 'NotNeeded');
    assert.equal((await call(twin, '/Users', { token })).body[0].Name, 'Alice');
    assert.equal((await call(twin, '/System/Info', { token })).body.ProductName, 'Jellyfin Server');
    assert.equal((await call(twin, '/Sessions/Logout', { token, method: 'POST' })).status, 204);
    assert.equal((await call(twin, '/Library/Refresh', { token, method: 'POST' })).status, 404);
});

test('the item path of an episode leads up to its library', async () => {
    const twin = newTwin();
    const token = await signIn(twin);
    const chain = await call(twin, `/Items/${EPISODE_1}/Ancestors`, { token });
    assert.deepEqual(chain.body.map(i => i.Name), ['Season 1', 'A Show', 'TV']);
});

test('a connection test gets the size it asks for, up to a limit', async () => {
    const twin = newTwin();
    const token = await signIn(twin);
    const sized = path => twin.handle(new Request('https://twin.example' + path, { headers: { Authorization: `MediaBrowser Token="${token}"` } }))
        .then(async answer => (await answer.arrayBuffer()).byteLength);

    assert.equal(await sized('/Playback/BitrateTest?Size=5000'), 5000);
    assert.equal(await sized('/Playback/BitrateTest?Size=900000000'), 1000000);
});

const MINUTE = 600000000;
const report = (twin, token, kind, itemId, positionTicks) =>
    call(twin, '/Sessions/Playing' + kind, { token, method: 'POST', body: { ItemId: itemId, PositionTicks: positionTicks } });
const names = answer => answer.body.Items.map(i => i.Name);

test('with nothing watched, the lists that depend on it are empty', async () => {
    const twin = newTwin();
    const token = await signIn(twin);
    for (const path of ['/UserItems/Resume', '/Shows/NextUp', '/UserWatchlistItems', '/Items?Filters=IsFavorite&Recursive=true']) {
        assert.deepEqual((await call(twin, path, { token })).body.Items, [], path);
    }

    assert.equal((await call(twin, '/UserStats', { token })).body.Episodes, 0);
});

test('a video stopped part way can be resumed, and one watched to the end is marked watched', async () => {
    let clock = Date.UTC(2026, 10, 16);
    const twin = newTwin(() => clock);
    const token = await signIn(twin);
    const movie = { ...files['catalog/all.json'].find(r => r.Id === MOVIE) };
    assert.equal(movie.RunTimeTicks, MINUTE);

    // A one minute film is too short to resume: stopping anywhere past the start counts as watched.
    await report(twin, token, '', MOVIE, 0);
    await report(twin, token, '/Stopped', MOVIE, MINUTE / 2);
    let data = (await call(twin, `/Items/${MOVIE}`, { token })).body.UserData;
    assert.equal(data.Played, true);
    assert.equal(data.PlaybackPositionTicks, 0);
    assert.equal(data.PlayCount, 1);

    assert.equal((await call(twin, `/UserPlayedItems/${MOVIE}`, { token, method: 'DELETE' })).body.Played, false);

    // Barely started: not worth resuming.
    clock += 60000;
    await report(twin, token, '/Stopped', MOVIE, MINUTE / 50);
    data = (await call(twin, `/Items/${MOVIE}`, { token })).body.UserData;
    assert.equal(data.Played, false);
    assert.equal(data.PlaybackPositionTicks, 0);
    assert.deepEqual(names(await call(twin, '/UserItems/Resume', { token })), []);
});

test('progress is kept while playing, but not every report is written', async () => {
    let clock = Date.UTC(2026, 10, 16);
    const twin = newTwin(() => clock);
    const token = await signIn(twin);

    await report(twin, token, '/Progress', EPISODE_1, 5 * MINUTE);
    await report(twin, token, '/Progress', EPISODE_1, 6 * MINUTE);
    assert.equal(writes.length, 1);

    clock += 20000;
    await report(twin, token, '/Progress', EPISODE_1, 7 * MINUTE);
    assert.equal(writes.length, 2);

    const resume = await call(twin, '/UserItems/Resume', { token });
    assert.deepEqual(names(resume), ['First']);
    assert.equal(resume.body.Items[0].UserData.PlaybackPositionTicks, 7 * MINUTE);
    assert.deepEqual(names(await call(twin, '/Items?Filters=IsResumable&Recursive=true', { token })), ['First']);

    // The same list is asked for once per kind of media; a video is not something to keep listening to.
    assert.deepEqual(names(await call(twin, '/UserItems/Resume?MediaTypes=Video', { token })), ['First']);
    assert.deepEqual(names(await call(twin, '/UserItems/Resume?MediaTypes=Audio', { token })), []);
    assert.deepEqual(names(await call(twin, '/UserItems/Resume?mediaTypes=Book', { token })), []);
    assert.deepEqual(names(await call(twin, '/Items?MediaTypes=Audio&Recursive=true', { token })), []);
});

test('next up is the first unwatched episode after the last watched one', async () => {
    const twin = newTwin();
    const token = await signIn(twin);
    assert.deepEqual(names(await call(twin, '/Shows/NextUp', { token })), []);

    assert.equal((await call(twin, `/UserPlayedItems/${EPISODE_1}`, { token, method: 'POST' })).body.Played, true);
    assert.deepEqual(names(await call(twin, '/Shows/NextUp', { token })), ['Second']);

    const series = (await call(twin, `/Items/${SERIES}`, { token })).body.UserData;
    assert.equal(series.UnplayedItemCount, 1);
    assert.equal(series.Played, false);

    // Marking the season watched marks what is in it, and nothing is up next.
    assert.equal((await call(twin, `/UserPlayedItems/${SEASON}`, { token, method: 'POST' })).body.Played, true);
    assert.deepEqual(names(await call(twin, '/Shows/NextUp', { token })), []);
    assert.equal((await call(twin, `/Items/${SERIES}`, { token })).body.UserData.Played, true);
    assert.deepEqual(names(await call(twin, `/Items?Filters=IsUnplayed&Recursive=true&IncludeItemTypes=Episode`, { token })), []);
});

test('favorites and the watchlist are kept per user', async () => {
    const twin = newTwin();
    const token = await signIn(twin);

    assert.equal((await call(twin, `/UserFavoriteItems/${SERIES}`, { token, method: 'POST' })).body.IsFavorite, true);
    assert.deepEqual(names(await call(twin, '/Items?Filters=IsFavorite&Recursive=true', { token })), ['A Show']);
    assert.equal((await call(twin, `/UserFavoriteItems/${SERIES}`, { token, method: 'DELETE' })).body.IsFavorite, false);

    assert.equal((await call(twin, `/UserWatchlistItems/${MOVIE}`, { token, method: 'POST' })).status, 204);
    assert.equal((await call(twin, `/UserWatchlistItems/${SERIES}`, { token, method: 'POST' })).status, 204);
    assert.deepEqual(names(await call(twin, '/UserWatchlistItems', { token })).sort(), ['A Film', 'A Show']);
    assert.equal((await call(twin, `/UserWatchlistItems/${MOVIE}`, { token, method: 'DELETE' })).status, 204);
    assert.deepEqual(names(await call(twin, '/UserWatchlistItems', { token })), ['A Show']);
    assert.equal((await call(twin, '/UserWatchlistItems/not-an-item', { token, method: 'POST' })).status, 404);

    assert.deepEqual([...texts.keys()], ['state/cccccccc000000000000000000000001.json']);
});

test('a change made elsewhere at the same moment is not lost', async () => {
    const twin = newTwin();
    const token = await signIn(twin);
    await call(twin, `/UserFavoriteItems/${MOVIE}`, { token, method: 'POST' });

    failNextWrite = true;
    assert.equal((await call(twin, `/UserPlayedItems/${MOVIE}`, { token, method: 'POST' })).body.Played, true);

    const saved = JSON.parse(texts.get('state/cccccccc000000000000000000000001.json').text);
    assert.equal(saved.Items[MOVIE].Played, true);
    assert.equal(saved.Items[MOVIE].IsFavorite, true);
    assert.equal(saved.Watchlist[SERIES].On, true);
});

test('stats count what was watched in the year', async () => {
    const twin = newTwin(() => Date.UTC(2026, 4, 3));
    const token = await signIn(twin);
    await call(twin, `/UserPlayedItems/${MOVIE}`, { token, method: 'POST' });
    await call(twin, `/UserPlayedItems/${SERIES}`, { token, method: 'POST' });

    const stats = (await call(twin, '/UserStats', { token })).body;
    assert.equal(stats.Year, 2026);
    assert.equal(stats.Movies, 1);
    assert.equal(stats.Episodes, 2);
    assert.equal(stats.BusiestMonth, 5);
    assert.equal(stats.WatchTimeTicks, MINUTE);
    assert.deepEqual(stats.TopSeries.map(s => [s.Name, s.Count]), [['A Show', 2]]);
});

const attempt = (twin, name, password, from = '203.0.113.7') =>
    call(twin, '/Users/AuthenticateByName', { method: 'POST', body: { Username: name, Pw: password }, from });

test('too many wrong passwords for a name lock it for a while, even for the right password', async () => {
    let clock = Date.UTC(2026, 10, 16);
    const twin = newTwin(() => clock);

    for (let i = 0; i < 10; i++) {
        assert.equal((await attempt(twin, 'Alice', 'wrong ' + i, `198.51.100.${i}`)).status, 401);
        clock += 1000;
    }

    const refused = await attempt(twin, 'alice', 'correct horse', '192.0.2.50');
    assert.equal(refused.status, 429);
    assert.ok(Number(refused.headers.get('retry-after')) > 14 * 60);
    assert.match(refused.body.error, /15 minutes/);

    // Someone else's name is not affected, and the lock ends on its own.
    assert.equal((await attempt(twin, 'bob', 'x', '192.0.2.50')).status, 401);
    clock += 15 * 60 * 1000;
    assert.equal((await attempt(twin, 'alice', 'correct horse', '192.0.2.50')).status, 200);
});

test('one caller guessing across many names is stopped too', async () => {
    const twin = newTwin();
    for (let i = 0; i < 20; i++) {
        assert.equal((await attempt(twin, 'name' + i, 'guess')).status, 401);
    }

    assert.equal((await attempt(twin, 'alice', 'correct horse')).status, 429);
    assert.equal((await attempt(twin, 'alice', 'correct horse', '203.0.113.99')).status, 200);
});

test('a caller with a block of IPv6 addresses counts as one', async () => {
    const twin = newTwin();
    for (let i = 0; i < 20; i++) {
        await attempt(twin, 'name' + i, 'guess', `2001:db8:1:2::${i.toString(16)}`);
    }

    assert.equal((await attempt(twin, 'alice', 'correct horse', '2001:db8:1:2:ffff::1')).status, 429);
    assert.equal((await attempt(twin, 'alice', 'correct horse', '2001:db8:1:3::1')).status, 200);
});

test('a sign-in that works clears the count for that name', async () => {
    const twin = newTwin();
    for (let round = 0; round < 3; round++) {
        for (let i = 0; i < 6; i++) {
            assert.equal((await attempt(twin, 'alice', 'wrong', `198.51.100.${round * 10 + i}`)).status, 401);
        }

        assert.equal((await attempt(twin, 'alice', 'correct horse', '192.0.2.50')).status, 200);
    }
});

test('every copy of the twin sees the same count', async () => {
    const first = newTwin();
    const second = newTwin(undefined, { keep: true });
    for (let i = 0; i < 10; i++) {
        await attempt(i % 2 ? first : second, 'alice', 'wrong', `198.51.100.${i}`);
    }

    assert.equal((await attempt(first, 'alice', 'correct horse', '192.0.2.50')).status, 429);
    assert.equal((await attempt(second, 'alice', 'correct horse', '192.0.2.50')).status, 429);
    assert.ok([...texts.keys()].every(key => key.startsWith('state/limits/')));
    assert.ok(![...texts.values()].some(entry => /alice|198\.51/i.test(entry.text)), 'no names or addresses are stored');
});

test('the limit still stands when the store cannot be written or read', async () => {
    for (const breakStore of [() => { refuseWrites = true; }, () => { refuseReads = true; }]) {
        const twin = newTwin();
        breakStore();
        for (let i = 0; i < 10; i++) {
            assert.equal((await attempt(twin, 'alice', 'wrong', `198.51.100.${i}`)).status, 401);
        }

        assert.equal((await attempt(twin, 'alice', 'correct horse', '192.0.2.50')).status, 429);
    }

    refuseWrites = false;
    refuseReads = false;
});

test('callers with no known address share one limit', async () => {
    const twin = newTwin();
    for (let i = 0; i < 20; i++) {
        await call(twin, '/Users/AuthenticateByName', { method: 'POST', body: { Username: 'name' + i, Pw: 'guess' } });
    }

    assert.equal((await call(twin, '/Users/AuthenticateByName', { method: 'POST', body: { Username: 'alice', Pw: 'correct horse' } })).status, 429);
});

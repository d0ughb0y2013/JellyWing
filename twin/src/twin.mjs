// The cloud twin's API: the part of the media server's API that apps need to sign in,
// browse and play, answered from a library published to object storage.
//
// It is written against web-standard Request and Response so the same code runs behind
// a cloud function, in a worker, and in the local server in local.mjs.

import { createPlayState, episodesUnder, nextUp, resumeItems, userDataOf, viewingStats, watchlistItems } from './playstate.mjs';

const encoder = new TextEncoder();
const VIDEO_TYPES = ['Movie', 'Episode'];
const CACHE_MS = 60_000;
const TOKEN_LIFETIME_MS = 30 * 24 * 3600 * 1000;
const MEDIA_LINK_SECONDS = 6 * 3600;

const toHex = bytes => [...new Uint8Array(bytes)].map(b => b.toString(16).padStart(2, '0')).join('');
const fromHex = text => new Uint8Array((text.match(/../g) || []).map(pair => parseInt(pair, 16)));
const toBase64Url = bytes => btoa(String.fromCharCode(...new Uint8Array(bytes))).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
const fromBase64Url = text => Uint8Array.from(atob(text.replace(/-/g, '+').replace(/_/g, '/')), c => c.charCodeAt(0));
const sameId = (a, b) => !!a && !!b && a.replace(/-/g, '').toLowerCase() === b.replace(/-/g, '').toLowerCase();
const cleanId = id => (id || '').replace(/-/g, '').toLowerCase();

async function sha256Hex(text) {
    return toHex(await crypto.subtle.digest('SHA-256', encoder.encode(text)));
}

function constantTimeEqual(a, b) {
    if (a.length !== b.length) return false;
    let difference = 0;
    for (let i = 0; i < a.length; i++) difference |= a[i] ^ b[i];
    return difference === 0;
}

/**
 * Checks a password against a hash in the media server's format:
 * $PBKDF2-SHA512$iterations=N$<salt hex>$<hash hex>.
 */
export async function verifyPassword(password, stored) {
    const parts = (stored || '').split('$');
    if (parts.length !== 5) return false;

    const hash = { 'PBKDF2-SHA512': 'SHA-512', 'PBKDF2-SHA256': 'SHA-256', PBKDF2: 'SHA-1' }[parts[1]];
    const iterations = parseInt((parts[2].match(/iterations=(\d+)/) || [])[1], 10);
    const expected = fromHex(parts[4]);
    if (!hash || !iterations || !expected.length) return false;

    const key = await crypto.subtle.importKey('raw', encoder.encode(password), 'PBKDF2', false, ['deriveBits']);
    const derived = await crypto.subtle.deriveBits({ name: 'PBKDF2', hash, salt: fromHex(parts[3]), iterations }, key, expected.length * 8);
    return constantTimeEqual(new Uint8Array(derived), expected);
}

/**
 * Creates the twin.
 * @param {object} options
 * @param {{getJson: Function, signedUrl: Function}} options.store Where the published library is.
 * @param {string} options.tokenSecret Secret that signs access tokens. Changing it signs everyone out.
 * @param {string} [options.serverName]
 * @param {() => number} [options.now]
 * @returns {{handle: (request: Request) => Promise<Response>}}
 */
export function createTwin({ store, tokenSecret, serverName = 'JellyWing', now = Date.now }) {
    if (!tokenSecret || tokenSecret.length < 16) {
        throw new Error('The twin needs a token secret of at least 16 characters.');
    }

    const cache = {};
    const playState = createPlayState({ store, now });
    const NO_STATE = { Items: {}, Watchlist: {} };
    let serverId;

    async function cached(key) {
        const entry = cache[key];
        if (entry && now() - entry.at < CACHE_MS) return entry.value;
        const value = (await store.getJson(key)) || [];
        cache[key] = { at: now(), value };
        return value;
    }

    async function getServerId() {
        serverId ??= (await sha256Hex('server:' + tokenSecret)).slice(0, 32);
        return serverId;
    }

    async function hmac(text) {
        const key = await crypto.subtle.importKey('raw', encoder.encode(tokenSecret), { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
        return new Uint8Array(await crypto.subtle.sign('HMAC', key, encoder.encode(text)));
    }

    async function issueToken(user) {
        const payload = toBase64Url(encoder.encode(JSON.stringify({ u: cleanId(user.Id), n: user.Name, e: now() + TOKEN_LIFETIME_MS })));
        return payload + '.' + toBase64Url(await hmac(payload));
    }

    async function readToken(token) {
        const [payload, signature] = (token || '').split('.');
        if (!payload || !signature) return null;

        let given;
        let claims;
        try {
            given = fromBase64Url(signature);
            if (!constantTimeEqual(given, await hmac(payload))) return null;
            claims = JSON.parse(new TextDecoder().decode(fromBase64Url(payload)));
        } catch {
            return null;
        }

        return claims.e > now() ? { Id: claims.u, Name: claims.n } : null;
    }

    function tokenFrom(request, url) {
        const header = request.headers.get('Authorization') || request.headers.get('X-Emby-Authorization') || '';
        const match = header.match(/Token="?([^",\s]+)"?/i);
        return match?.[1] || request.headers.get('X-Emby-Token') || request.headers.get('X-MediaBrowser-Token')
            || url.searchParams.get('ApiKey') || url.searchParams.get('api_key');
    }

    function json(body, status = 200) {
        return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json; charset=utf-8', ...cors } });
    }

    const cors = {
        'Access-Control-Allow-Origin': '*',
        'Access-Control-Allow-Headers': 'Authorization, Content-Type, X-Emby-Authorization, X-Emby-Token, X-MediaBrowser-Token',
        'Access-Control-Allow-Methods': 'GET, POST, DELETE, OPTIONS'
    };
    const empty = (status = 204) => new Response(null, { status, headers: cors });
    const notFound = () => json({ error: 'Not available on the cloud twin.' }, 404);
    const redirect = location => new Response(null, { status: 302, headers: { Location: location, 'Cache-Control': 'no-store', ...cors } });

    async function libraryId(name) {
        return (await sha256Hex('library:' + name)).slice(0, 32);
    }

    async function userDto(user) {
        return {
            Name: user.Name,
            ServerId: await getServerId(),
            Id: cleanId(user.Id),
            HasPassword: true,
            HasConfiguredPassword: true,
            EnableAutoLogin: false,
            Configuration: {
                PlayDefaultAudioTrack: true,
                SubtitleMode: 'Default',
                EnableNextEpisodeAutoPlay: true,
                GroupedFolders: [],
                OrderedViews: [],
                LatestItemsExcludes: [],
                MyMediaExcludes: [],
                HidePlayedInLatest: false,
                RememberAudioSelections: true,
                RememberSubtitleSelections: true
            },
            Policy: {
                IsAdministrator: false,
                IsDisabled: false,
                EnableMediaPlayback: true,
                EnableAudioPlaybackTranscoding: false,
                EnableVideoPlaybackTranscoding: false,
                EnablePlaybackRemuxing: false,
                EnableContentDownloading: false,
                EnableContentDeletion: false,
                EnableAllFolders: true,
                EnableLiveTvAccess: false,
                EnableLiveTvManagement: false,
                EnableRemoteControlOfOtherUsers: false,
                EnableSharedDeviceControl: false,
                EnabledFolders: [],
                BlockedTags: [],
                AccessSchedules: []
            }
        };
    }

    async function itemDto(record, catalog, state = NO_STATE) {
        const id = cleanId(record.Id);
        const isVideo = VIDEO_TYPES.includes(record.Type);
        const series = record.SeriesId && catalog.find(r => sameId(r.Id, record.SeriesId));
        const season = record.SeasonId && catalog.find(r => sameId(r.Id, record.SeasonId));
        const parentId = cleanId(record.SeasonId || record.SeriesId) || await libraryId(record.Library);

        const dto = {
            Name: record.Name,
            ServerId: await getServerId(),
            Id: id,
            Type: record.Type,
            IsFolder: !isVideo,
            ParentId: parentId,
            Overview: record.Overview || undefined,
            ProductionYear: record.ProductionYear ?? undefined,
            PremiereDate: record.PremiereDate || undefined,
            RunTimeTicks: record.RunTimeTicks ?? undefined,
            Genres: record.Genres || [],
            IndexNumber: record.IndexNumber ?? undefined,
            ParentIndexNumber: record.ParentIndexNumber ?? undefined,
            LocationType: 'FileSystem',
            ImageTags: record.ImageKey ? { Primary: (await sha256Hex(record.ImageKey + ':' + (record.MediaSize ?? ''))).slice(0, 32) } : {},
            BackdropImageTags: [],
            ImageBlurHashes: {},
            People: [],
            Studios: [],
            GenreItems: [],
            Tags: [],
            Chapters: [],
            ExternalUrls: [],
            RemoteTrailers: [],
            ProviderIds: {},
            LockedFields: [],
            CanDelete: false,
            CanDownload: false,
            PlayAccess: 'Full',
            UserData: userDataOf(state, record, catalog)
        };

        if (series) {
            dto.SeriesId = cleanId(series.Id);
            dto.SeriesName = series.Name;
        }

        if (season) {
            dto.SeasonId = cleanId(season.Id);
            dto.SeasonName = season.Name;
        }

        if (isVideo) {
            dto.MediaType = 'Video';
            dto.VideoType = 'VideoFile';
            dto.Container = 'mp4';
            dto.MediaSources = [mediaSource(record)];
            dto.MediaStreams = [];
        } else {
            const children = catalog.filter(r => sameId(record.Type === 'Series' ? r.SeriesId : r.SeasonId, record.Id));
            dto.ChildCount = record.Type === 'Series' ? children.filter(r => r.Type === 'Season').length : children.length;
            dto.RecursiveItemCount = children.filter(r => r.Type === 'Episode').length;
        }

        return dto;
    }

    function mediaSource(record) {
        return {
            Id: cleanId(record.Id),
            Name: record.Name,
            Protocol: 'File',
            Type: 'Default',
            Container: 'mp4',
            Size: record.MediaSize ?? undefined,
            RunTimeTicks: record.RunTimeTicks ?? undefined,
            IsRemote: false,
            SupportsDirectPlay: true,
            SupportsDirectStream: true,
            SupportsTranscoding: false,
            MediaStreams: [],
            RequiredHttpHeaders: {}
        };
    }

    const SORT_KEYS = {
        sortname: r => (r.Name || '').toLowerCase(),
        name: r => (r.Name || '').toLowerCase(),
        premieredate: r => r.PremiereDate || '',
        productionyear: r => r.ProductionYear ?? 0,
        indexnumber: r => r.IndexNumber ?? 0,
        parentindexnumber: r => r.ParentIndexNumber ?? 0
    };

    async function listItems(catalog, query, state = NO_STATE) {
        const get = name => query.get(name) ?? query.get(name[0].toLowerCase() + name.slice(1));
        const types = (get('IncludeItemTypes') || '').split(',').map(t => t.trim()).filter(Boolean);
        const recursive = (get('Recursive') || '').toLowerCase() === 'true';
        const parentId = get('ParentId');
        const ids = (get('Ids') || '').split(',').filter(Boolean);
        let items = catalog.filter(r => r.Type !== 'Episode' && r.Type !== 'Movie' || r.MediaKey);

        const filters = (get('Filters') || '').toLowerCase().split(',').map(f => f.trim());
        const flag = name => (get(name) || '').toLowerCase();
        const data = r => userDataOf(state, r, catalog);
        if (filters.includes('isfavorite') || flag('IsFavorite') === 'true') items = items.filter(r => data(r).IsFavorite);
        if (filters.includes('isresumable')) items = items.filter(r => data(r).PlaybackPositionTicks > 0);
        if (filters.includes('isplayed') || flag('IsPlayed') === 'true') items = items.filter(r => data(r).Played);
        if (filters.includes('isunplayed') || flag('IsPlayed') === 'false') items = items.filter(r => !data(r).Played);
        if (filters.includes('likes') || filters.includes('dislikes')) items = [];

        // Everything published is video; a request for music or books gets nothing.
        if (!wantsVideo(get('MediaTypes'))) items = [];

        if (ids.length) {
            items = items.filter(r => ids.some(id => sameId(id, r.Id)));
        } else if (parentId) {
            const parent = catalog.find(r => sameId(r.Id, parentId));
            if (parent?.Type === 'Series') {
                items = items.filter(r => sameId(r.SeriesId, parentId) && (recursive || r.Type === 'Season'));
            } else if (parent?.Type === 'Season') {
                items = items.filter(r => sameId(r.SeasonId, parentId));
            } else {
                const names = [...new Set(catalog.map(r => r.Library))];
                const library = (await Promise.all(names.map(async n => [n, await libraryId(n)]))).find(([, id]) => sameId(id, parentId))?.[0];
                items = items.filter(r => r.Library === library && (recursive || r.Type === 'Movie' || r.Type === 'Series'));
            }
        } else if (!recursive && !types.length) {
            items = items.filter(r => r.Type === 'Movie' || r.Type === 'Series');
        }

        if (types.length) {
            items = items.filter(r => types.includes(r.Type));
        }

        const search = (get('SearchTerm') || '').toLowerCase();
        if (search) {
            items = items.filter(r => (r.Name || '').toLowerCase().includes(search));
        }

        const sortKeys = { ...SORT_KEYS, dateplayed: r => data(r).LastPlayedDate || '' };
        const keys = (get('SortBy') || 'ParentIndexNumber,IndexNumber,SortName').split(',')
            .map(k => sortKeys[k.trim().toLowerCase()]).filter(Boolean);
        const direction = (get('SortOrder') || '').toLowerCase().startsWith('desc') ? -1 : 1;
        items = [...items].sort((a, b) => {
            for (const key of keys) {
                const [x, y] = [key(a), key(b)];
                if (x !== y) return (x < y ? -1 : 1) * direction;
            }

            return 0;
        });

        const total = items.length;
        const start = parseInt(get('StartIndex') || '0', 10) || 0;
        const limit = parseInt(get('Limit') || '0', 10);
        const page = items.slice(start, limit > 0 ? start + limit : undefined);
        return { Items: await Promise.all(page.map(r => itemDto(r, catalog, state))), TotalRecordCount: total, StartIndex: start };
    }

    const wantsVideo = mediaTypes => !mediaTypes || mediaTypes.toLowerCase().split(',').includes('video');

    // A list that is already in its final order, cut to the page asked for.
    async function page(records, catalog, state, query) {
        const get = name => query.get(name) ?? query.get(name[0].toLowerCase() + name.slice(1));
        if (!wantsVideo(get('MediaTypes'))) records = [];
        const start = parseInt(get('StartIndex') || '0', 10) || 0;
        const limit = parseInt(get('Limit') || '0', 10);
        const cut = records.slice(start, limit > 0 ? start + limit : undefined);
        return { Items: await Promise.all(cut.map(r => itemDto(r, catalog, state))), TotalRecordCount: records.length, StartIndex: start };
    }

    // Play state: what was watched, where it stopped, favorites and the watchlist.
    async function stateRoutes(route, { request, catalog, url, segments, lower, method, user, state }) {
        const last = lower[lower.length - 1];
        const find = id => catalog.find(r => sameId(r.Id, id));

        if (method === 'POST' && lower[0] === 'sessions' && lower[1] === 'playing') {
            const event = { 2: 'start', 3: { progress: 'progress', stopped: 'stop' }[lower[2]] }[lower.length];
            let body = {};
            try {
                body = await request.json();
            } catch {
                // A report with no body says nothing.
            }

            const record = find(String(body.ItemId || ''));
            if (event && record?.MediaKey) {
                // A lost report is not worth failing the player over; the next one catches up.
                await playState.report(user.Id, record, Number(body.PositionTicks), event)
                    .catch(error => console.warn('[twin] a playback report was not saved:', error.message));
            }

            return empty();
        }

        const changes = method === 'POST' || method === 'DELETE';
        if (changes && (lower.includes('userplayeditems') || lower.includes('playeditems'))) {
            const record = find(last);
            if (!record) return notFound();
            const ids = record.Type === 'Series' || record.Type === 'Season' ? episodesUnder(record, catalog).map(r => r.Id) : [record.Id];
            return json(userDataOf(await playState.setPlayed(user.Id, ids, method === 'POST'), record, catalog));
        }

        if (changes && (lower.includes('userfavoriteitems') || lower.includes('favoriteitems'))) {
            const record = find(last);
            if (!record) return notFound();
            return json(userDataOf(await playState.setFavorite(user.Id, record.Id, method === 'POST'), record, catalog));
        }

        if (changes && lower[0] === 'userwatchlistitems' && lower.length === 2) {
            const record = find(last);
            if (!record) return notFound();
            await playState.setWatchlist(user.Id, record.Id, method === 'POST');
            return empty();
        }

        if (method !== 'GET') return null;

        if (route('GET', 'userwatchlistitems')) return json(await page(watchlistItems(state, catalog), catalog, state, url.searchParams));
        if (last === 'resume') return json(await page(resumeItems(state, catalog), catalog, state, url.searchParams));
        if (route('GET', 'shows', 'nextup')) {
            const seriesId = url.searchParams.get('SeriesId') || url.searchParams.get('seriesId');
            return json(await page(nextUp(state, catalog, seriesId), catalog, state, url.searchParams));
        }

        if (route('GET', 'useritems', '*', 'userdata')) {
            const record = find(segments[1]);
            return record ? json(userDataOf(state, record, catalog)) : notFound();
        }

        if (route('GET', 'userstats')) {
            const year = parseInt(url.searchParams.get('year') || url.searchParams.get('Year') || '', 10) || null;
            return json(viewingStats(state, catalog, year, now()));
        }

        return null;
    }

    async function views(catalog) {
        const names = [...new Set(catalog.map(r => r.Library))].sort();
        return {
            Items: await Promise.all(names.map(async name => ({
                Name: name,
                ServerId: await getServerId(),
                Id: await libraryId(name),
                Type: 'CollectionFolder',
                CollectionType: catalog.some(r => r.Library === name && r.Type === 'Series') ? 'tvshows' : 'movies',
                IsFolder: true,
                ImageTags: {},
                UserData: { Played: false, PlaybackPositionTicks: 0, PlayCount: 0, IsFavorite: false }
            }))),
            TotalRecordCount: names.length,
            StartIndex: 0
        };
    }

    const WEB_TYPES = {
        html: 'text/html; charset=utf-8', js: 'text/javascript; charset=utf-8', mjs: 'text/javascript; charset=utf-8',
        css: 'text/css; charset=utf-8', json: 'application/json', map: 'application/json', txt: 'text/plain; charset=utf-8',
        svg: 'image/svg+xml', png: 'image/png', jpg: 'image/jpeg', gif: 'image/gif', webp: 'image/webp', ico: 'image/x-icon',
        woff2: 'font/woff2', woff: 'font/woff', ttf: 'font/ttf', eot: 'application/vnd.ms-fontobject', mp3: 'audio/mpeg',
        wasm: 'application/wasm', webmanifest: 'application/manifest+json'
    };

    // The web client, when the home server has published it under web/. Anyone may load it;
    // it is the same open program whoever asks, and it shows nothing until someone signs in.
    async function webFile(pathname, version) {
        let relative;
        try {
            relative = decodeURIComponent(pathname.slice('/web/'.length)) || 'index.html';
        } catch {
            return notFound();
        }

        if (relative.split(/[\\/]/).some(part => part === '..' || part === '')) return notFound();

        const bytes = await store.getBytes('web/' + relative);
        if (!bytes) return json({ error: 'The web client is not published to this twin.' }, 404);

        // A name or address with a build hash in it never changes content; anything else is checked each time.
        const hashed = /\.[0-9a-f]{16,}\./.test(relative) || /^\?[0-9a-f]{16,}$/.test(version);
        return new Response(bytes, {
            status: 200,
            headers: {
                'Content-Type': WEB_TYPES[relative.split('.').pop().toLowerCase()] || 'application/octet-stream',
                'Cache-Control': hashed ? 'public, max-age=31536000, immutable' : 'no-cache',
                ...cors
            }
        });
    }

    const emptyList = () => ({ Items: [], TotalRecordCount: 0, StartIndex: 0 });

    // What an app asks for beyond browsing and playing. Most of it depends on things the
    // twin does not hold yet, so the honest answer is "none", in the shape the app expects.
    function extras(route, { catalog, url, segments, lower, method, state }) {
        const last = lower[lower.length - 1];

        if (route('GET', 'system', 'info')) {
            return getServerId().then(id => json({
                ServerName: serverName,
                Version: '12.4.0',
                ProductName: 'Jellyfin Server',
                Id: id,
                StartupWizardCompleted: true,
                OperatingSystem: '',
                HasPendingRestart: false,
                IsShuttingDown: false,
                SupportsLibraryMonitor: false,
                CanSelfRestart: false
            }));
        }

        if (route('GET', 'system', 'endpoint')) return json({ IsLocal: false, IsInNetwork: false });

        if (lower[0] === 'displaypreferences' && lower.length === 2) {
            if (method === 'POST') return empty();
            return json({
                Id: segments[1],
                SortBy: 'SortName',
                SortOrder: 'Ascending',
                RememberIndexing: false,
                RememberSorting: false,
                PrimaryImageHeight: 250,
                PrimaryImageWidth: 250,
                ScrollDirection: 'Horizontal',
                ShowBackdrop: true,
                ShowSidebar: false,
                CustomPrefs: {},
                Client: url.searchParams.get('client') || 'emby'
            });
        }

        // The newest things in a library: its top level, most recent first.
        if (method === 'GET' && last === 'latest' && lower.includes('items')) {
            const query = new URLSearchParams({
                ParentId: url.searchParams.get('ParentId') || url.searchParams.get('parentId') || '',
                SortBy: 'PremiereDate,SortName',
                SortOrder: 'Descending',
                Limit: url.searchParams.get('Limit') || url.searchParams.get('limit') || '16'
            });
            if (!query.get('ParentId')) query.delete('ParentId');
            return listItems(catalog, query, state).then(result => json(result.Items));
        }

        if (method === 'GET' && lower[0] === 'items' && lower.length === 3 && last === 'ancestors') {
            const record = catalog.find(r => sameId(r.Id, segments[1]));
            const chain = [record?.SeasonId, record?.SeriesId].map(id => id && catalog.find(r => sameId(r.Id, id))).filter(Boolean);
            return Promise.all(chain.map(r => itemDto(r, catalog, state))).then(async items => {
                const library = record && (await views(catalog)).Items.find(v => v.Name === record.Library);
                return json(library ? [...items, library] : items);
            });
        }

        if (method === 'GET' && last === 'thememedia') {
            return json({ ThemeVideosResult: emptyList(), ThemeSongsResult: emptyList(), SoundtrackSongsResult: emptyList() });
        }

        if (method === 'GET' && (last === 'filters' || last === 'filters2')) {
            return json({ Genres: [], Tags: [], OfficialRatings: [], Years: [] });
        }

        if (route('GET', 'search', 'hints')) return json({ SearchHints: [], TotalRecordCount: 0 });

        if (method === 'GET' && lower[0] === 'items' && last === 'optimizedversion') return json({ State: 'NotNeeded' });

        if (method === 'GET' && ['specialfeatures', 'localtrailers', 'groupingoptions', 'criticreviews'].includes(last)) return json([]);
        if (route('GET', 'sessions') || route('GET', 'syncplay', 'list')) return json([]);

        // An app measures its connection by timing a download of the size it asks for.
        if (route('GET', 'playback', 'bitratetest')) {
            const size = Math.min(Math.max(Number(url.searchParams.get('size') || url.searchParams.get('Size')) || 102400, 1), 1000000);
            return new Response(new Uint8Array(size), { status: 200, headers: { 'Content-Type': 'application/octet-stream', ...cors } });
        }

        const emptyLists = ['upcoming', 'similar', 'intros', 'genres', 'studios', 'persons', 'artists',
            'playlists', 'channels', 'trailers', 'musicgenres', 'collections'];
        if (method === 'GET' && (emptyLists.includes(last) || lower[0] === 'mediasegments' || lower[0] === 'livetv')) return json(emptyList());

        return null;
    }

    async function signIn(request) {
        let body;
        try {
            body = await request.json();
        } catch {
            return json({ error: 'A user name and password are required.' }, 400);
        }

        const users = await cached('auth/users.json');
        const user = users.find(u => (u.Name || '').toLowerCase() === String(body.Username || '').toLowerCase());

        // The check runs even for an unknown name, so the answer takes as long either way.
        const ok = await verifyPassword(String(body.Pw ?? ''), user?.Password || '$PBKDF2-SHA512$iterations=210000$00$00');
        if (!user || !ok) return json({ error: 'Invalid user name or password.' }, 401);

        return json({ User: await userDto(user), AccessToken: await issueToken(user), ServerId: await getServerId(), SessionInfo: {} });
    }

    async function handle(request) {
        const url = new URL(request.url);
        const method = request.method.toUpperCase();
        const path = url.pathname.replace(/\/+$/, '') || '/';
        const segments = path.split('/').filter(Boolean);
        const lower = segments.map(s => s.toLowerCase());
        const route = (verb, ...pattern) => method === verb && pattern.length === lower.length
            && pattern.every((part, i) => part === '*' || part === lower[i]);

        if (method === 'OPTIONS') return empty();

        if (route('GET', 'system', 'info', 'public')) {
            return json({
                LocalAddress: url.origin,
                ServerName: serverName,
                Version: '12.4.0',
                ProductName: 'Jellyfin Server',
                Id: await getServerId(),
                StartupWizardCompleted: true
            });
        }

        if (route('POST', 'users', 'authenticatebyname')) return signIn(request);
        if (route('GET', 'users', 'public')) return json([]);
        if (route('GET', 'system', 'ping') || route('POST', 'system', 'ping')) return json('Jellyfin Server');
        if (route('GET', 'branding', 'configuration')) return json({ LoginDisclaimer: null, CustomCss: null, SplashscreenEnabled: false });
        if (route('GET', 'branding', 'css') || route('GET', 'branding', 'css.css')) {
            return new Response('', { status: 200, headers: { 'Content-Type': 'text/css', ...cors } });
        }

        if (route('GET', 'quickconnect', 'enabled')) return json(false);
        if (route('GET', 'favicon.ico')) return notFound();

        // Live updates need a connection that stays open, which the twin does not keep.
        if (route('GET', 'socket')) return notFound();

        if (method === 'GET' && (url.pathname === '/' || url.pathname === '/web')) return redirect('/web/');
        if (method === 'GET' && lower[0] === 'web') return webFile(url.pathname, url.search);

        const catalog = await cached('catalog/all.json');

        // Pictures are addressed by unguessable ids and carry no token, as on the media server.
        if (lower[0] === 'items' && lower[2] === 'images' && method === 'GET') {
            const record = catalog.find(r => sameId(r.Id, segments[1]));
            return record?.ImageKey ? redirect(await store.signedUrl(record.ImageKey, MEDIA_LINK_SECONDS)) : notFound();
        }

        const user = await readToken(tokenFrom(request, url));
        if (!user) return json({ error: 'Sign in first.' }, 401);

        const state = await playState.load(user.Id);

        if (route('GET', 'users', 'me') || route('GET', 'users', '*')) return json(await userDto(user));
        if (route('GET', 'users')) return json([await userDto(user)]);
        if (route('GET', 'userviews') || route('GET', 'users', '*', 'views')) return json(await views(catalog));
        if (route('GET', 'items') || route('GET', 'users', '*', 'items')) return json(await listItems(catalog, url.searchParams, state));

        if (route('GET', 'items', '*') || route('GET', 'users', '*', 'items', '*')) {
            const id = segments[segments.length - 1];
            const record = catalog.find(r => sameId(r.Id, id));
            if (record) return json(await itemDto(record, catalog, state));
            const library = (await views(catalog)).Items.find(v => sameId(v.Id, id));
            if (library) return json(library);

            // Not an id: a named list such as /Items/Latest, answered further down.
        }

        if (route('GET', 'shows', '*', 'seasons')) {
            const query = new URLSearchParams({ ParentId: segments[1], IncludeItemTypes: 'Season' });
            return json(await listItems(catalog, query, state));
        }

        if (route('GET', 'shows', '*', 'episodes')) {
            const seasonId = url.searchParams.get('seasonId') || url.searchParams.get('SeasonId');
            const query = new URLSearchParams({ ParentId: seasonId || segments[1], Recursive: 'true', IncludeItemTypes: 'Episode' });
            return json(await listItems(catalog, query, state));
        }

        if (route('POST', 'items', '*', 'playbackinfo') || route('GET', 'items', '*', 'playbackinfo')) {
            const record = catalog.find(r => sameId(r.Id, segments[1]) && r.MediaKey);
            if (!record) return notFound();
            return json({ MediaSources: [mediaSource(record)], PlaySessionId: toHex(crypto.getRandomValues(new Uint8Array(16))) });
        }

        if (lower[0] === 'videos' && lower[2]?.startsWith('stream') && method === 'GET') {
            const record = catalog.find(r => sameId(r.Id, segments[1]) && r.MediaKey);
            return record ? redirect(await store.signedUrl(record.MediaKey, MEDIA_LINK_SECONDS)) : notFound();
        }

        const context = { request, catalog, url, segments, lower, method, user, state };
        const answer = await stateRoutes(route, context);
        if (answer) return answer;

        if (method === 'POST' && lower[0] === 'sessions' && (lower[1] === 'capabilities' || lower[1] === 'logout')) return empty();

        return extras(route, context) ?? notFound();
    }

    return {
        async handle(request) {
            try {
                return await handle(request);
            } catch (error) {
                console.error('[twin]', error);
                return json({ error: 'The cloud twin could not answer.' }, 500);
            }
        }
    };
}

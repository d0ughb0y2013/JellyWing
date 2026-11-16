// The cloud twin's API: the part of the media server's API that apps need to sign in,
// browse and play, answered from a library published to object storage.
//
// It is written against web-standard Request and Response so the same code runs behind
// a cloud function, in a worker, and in the local server in local.mjs.

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
            Configuration: { PlayDefaultAudioTrack: true, SubtitleMode: 'Default', EnableNextEpisodeAutoPlay: true },
            Policy: { IsAdministrator: false, IsDisabled: false, EnableMediaPlayback: true, EnableContentDownloading: false, EnableAllFolders: true }
        };
    }

    async function itemDto(record, catalog) {
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
            UserData: { Key: id, Played: false, PlaybackPositionTicks: 0, PlayCount: 0, IsFavorite: false }
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
            dto.Container = 'mp4';
            dto.MediaSources = [mediaSource(record)];
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

    async function listItems(catalog, query) {
        const get = name => query.get(name) ?? query.get(name[0].toLowerCase() + name.slice(1));
        const types = (get('IncludeItemTypes') || '').split(',').map(t => t.trim()).filter(Boolean);
        const recursive = (get('Recursive') || '').toLowerCase() === 'true';
        const parentId = get('ParentId');
        const ids = (get('Ids') || '').split(',').filter(Boolean);
        let items = catalog.filter(r => r.Type !== 'Episode' && r.Type !== 'Movie' || r.MediaKey);

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

        const keys = (get('SortBy') || 'ParentIndexNumber,IndexNumber,SortName').split(',')
            .map(k => SORT_KEYS[k.trim().toLowerCase()]).filter(Boolean);
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
        return { Items: await Promise.all(page.map(r => itemDto(r, catalog))), TotalRecordCount: total, StartIndex: start };
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

        const catalog = await cached('catalog/all.json');

        // Pictures are addressed by unguessable ids and carry no token, as on the media server.
        if (lower[0] === 'items' && lower[2] === 'images' && method === 'GET') {
            const record = catalog.find(r => sameId(r.Id, segments[1]));
            return record?.ImageKey ? redirect(await store.signedUrl(record.ImageKey, MEDIA_LINK_SECONDS)) : notFound();
        }

        const user = await readToken(tokenFrom(request, url));
        if (!user) return json({ error: 'Sign in first.' }, 401);

        if (route('GET', 'users', 'me') || route('GET', 'users', '*')) return json(await userDto(user));
        if (route('GET', 'userviews') || route('GET', 'users', '*', 'views')) return json(await views(catalog));
        if (route('GET', 'items') || route('GET', 'users', '*', 'items')) return json(await listItems(catalog, url.searchParams));

        if (route('GET', 'items', '*') || route('GET', 'users', '*', 'items', '*')) {
            const id = segments[segments.length - 1];
            const record = catalog.find(r => sameId(r.Id, id));
            if (record) return json(await itemDto(record, catalog));
            const library = (await views(catalog)).Items.find(v => sameId(v.Id, id));
            return library ? json(library) : notFound();
        }

        if (route('GET', 'shows', '*', 'seasons')) {
            const query = new URLSearchParams({ ParentId: segments[1], IncludeItemTypes: 'Season' });
            return json(await listItems(catalog, query));
        }

        if (route('GET', 'shows', '*', 'episodes')) {
            const seasonId = url.searchParams.get('seasonId') || url.searchParams.get('SeasonId');
            const query = new URLSearchParams({ ParentId: seasonId || segments[1], Recursive: 'true', IncludeItemTypes: 'Episode' });
            return json(await listItems(catalog, query));
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

        // Progress is accepted and dropped until play state sync is built.
        if (method === 'POST' && lower[0] === 'sessions' && (lower[1] === 'playing' || lower[1] === 'capabilities')) return empty();

        return notFound();
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

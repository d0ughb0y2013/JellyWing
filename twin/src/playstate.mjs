// What each person has watched, where they stopped, their favorites and their watchlist.
//
// One small file per user in the store, state/<user id>.json. The twin and the home server
// both change it, so every write names the version it started from and is retried if
// someone else got there first.

const CACHE_MS = 15_000;
const PROGRESS_EVERY_MS = 15_000;
const TICKS_PER_SECOND = 10_000_000;

// The same rules the media server uses to decide between "resume here" and "watched".
const MIN_RESUME_PERCENT = 5;
const MAX_RESUME_PERCENT = 90;
const MIN_RESUME_SECONDS = 300;

const cleanId = id => (id || '').replace(/-/g, '').toLowerCase();
const emptyState = () => ({ Items: {}, Watchlist: {} });
const emptyEntry = () => ({ Played: false, PositionTicks: 0, PlayCount: 0, IsFavorite: false, LastPlayed: null, Changed: 0 });
const isVideo = record => (record.Type === 'Movie' || record.Type === 'Episode') && !!record.MediaKey;

/** A user's entry for one item, or an entry that says nothing has happened. */
export const entryOf = (state, id) => state.Items[cleanId(id)] || emptyEntry();

/** The published episodes under a series or a season. */
export function episodesUnder(record, catalog) {
    const key = record.Type === 'Series' ? 'SeriesId' : 'SeasonId';
    return catalog.filter(r => r.Type === 'Episode' && r.MediaKey && cleanId(r[key]) === cleanId(record.Id));
}

/** Watched state in the shape apps expect. A series or season is worked out from its episodes. */
export function userDataOf(state, record, catalog) {
    const id = cleanId(record.Id);
    const entry = entryOf(state, id);
    if (record.Type !== 'Series' && record.Type !== 'Season') {
        const data = {
            Key: id,
            ItemId: id,
            Played: entry.Played,
            PlaybackPositionTicks: entry.PositionTicks,
            PlayCount: entry.PlayCount,
            IsFavorite: entry.IsFavorite
        };
        if (entry.LastPlayed) data.LastPlayedDate = entry.LastPlayed;
        if (entry.PositionTicks > 0 && record.RunTimeTicks > 0) {
            data.PlayedPercentage = Math.min(100, entry.PositionTicks / record.RunTimeTicks * 100);
        }

        return data;
    }

    const episodes = episodesUnder(record, catalog);
    const unplayed = episodes.filter(e => !entryOf(state, e.Id).Played).length;
    return {
        Key: id,
        ItemId: id,
        Played: episodes.length > 0 && unplayed === 0,
        UnplayedItemCount: unplayed,
        PlayedPercentage: episodes.length ? (episodes.length - unplayed) / episodes.length * 100 : 0,
        PlaybackPositionTicks: 0,
        PlayCount: 0,
        IsFavorite: entry.IsFavorite
    };
}

const byLastPlayed = (state, direction = -1) => (a, b) =>
    direction * (entryOf(state, a.Id).LastPlayed || '').localeCompare(entryOf(state, b.Id).LastPlayed || '');

/** Videos stopped part way, most recently watched first. */
export function resumeItems(state, catalog) {
    return catalog
        .filter(r => isVideo(r) && entryOf(state, r.Id).PositionTicks > 0 && !entryOf(state, r.Id).Played)
        .sort(byLastPlayed(state));
}

/** For each series someone has started: the first unwatched episode after the last watched one. */
export function nextUp(state, catalog, seriesId) {
    const bySeries = new Map();
    for (const record of catalog) {
        if (record.Type !== 'Episode' || !record.MediaKey || !record.SeriesId || record.ParentIndexNumber === 0) continue;
        if (seriesId && cleanId(record.SeriesId) !== cleanId(seriesId)) continue;
        const key = cleanId(record.SeriesId);
        if (!bySeries.has(key)) bySeries.set(key, []);
        bySeries.get(key).push(record);
    }

    const found = [];
    for (const episodes of bySeries.values()) {
        episodes.sort((a, b) => (a.ParentIndexNumber ?? 0) - (b.ParentIndexNumber ?? 0) || (a.IndexNumber ?? 0) - (b.IndexNumber ?? 0));
        const lastPlayed = episodes.findLastIndex(e => entryOf(state, e.Id).Played);
        const next = lastPlayed < 0 ? null : episodes.slice(lastPlayed + 1).find(e => !entryOf(state, e.Id).Played);
        if (!next) continue;

        const when = episodes.map(e => entryOf(state, e.Id).LastPlayed || '').sort().pop();
        found.push({ record: next, when });
    }

    return found.sort((a, b) => b.when.localeCompare(a.when)).map(f => f.record);
}

/** The published items on the watchlist, most recently added first. */
export function watchlistItems(state, catalog) {
    return catalog
        .filter(r => state.Watchlist[cleanId(r.Id)]?.On && (r.Type === 'Series' || isVideo(r)))
        .sort((a, b) => state.Watchlist[cleanId(b.Id)].Changed - state.Watchlist[cleanId(a.Id)].Changed);
}

const top = (counts, limit = 5) => [...counts.values()]
    .sort((a, b) => b.Count - a.Count || a.Name.localeCompare(b.Name, undefined, { sensitivity: 'base' }))
    .slice(0, limit);

/** A year of watching, counted the way the media server counts it. */
export function viewingStats(state, catalog, year, now) {
    const plays = catalog
        .filter(r => isVideo(r) && entryOf(state, r.Id).Played && entryOf(state, r.Id).LastPlayed)
        .map(r => ({ record: r, at: new Date(entryOf(state, r.Id).LastPlayed) }));
    const years = [...new Set(plays.map(p => p.at.getUTCFullYear()))].sort((a, b) => b - a);
    const chosen = year || years[0] || new Date(now).getUTCFullYear();
    const inYear = plays.filter(p => p.at.getUTCFullYear() === chosen);
    const episodes = inYear.filter(p => p.record.Type === 'Episode');
    const movies = inYear.filter(p => p.record.Type === 'Movie');

    const series = new Map();
    for (const { record } of episodes) {
        const id = cleanId(record.SeriesId);
        if (!id) continue;
        const show = catalog.find(r => cleanId(r.Id) === id);
        if (!series.has(id)) series.set(id, { Name: show?.Name || '', Id: id, Count: 0, Genres: show?.Genres || [] });
        series.get(id).Count++;
    }

    // Episodes rarely carry genres of their own, so a series is counted once, by its own.
    const genres = new Map();
    for (const list of [...movies.map(p => p.record.Genres || []), ...[...series.values()].map(s => s.Genres)]) {
        for (const genre of list) {
            const key = genre.toLowerCase();
            if (!genres.has(key)) genres.set(key, { Name: genre, Count: 0 });
            genres.get(key).Count++;
        }
    }

    const months = new Map();
    for (const { at } of inYear) months.set(at.getUTCMonth() + 1, (months.get(at.getUTCMonth() + 1) || 0) + 1);

    return {
        Year: chosen,
        Years: years,
        Movies: movies.length,
        Episodes: episodes.length,
        WatchTimeTicks: inYear.reduce((sum, p) => sum + (p.record.RunTimeTicks || 0), 0),
        BusiestMonth: inYear.length ? [...months.entries()].sort((a, b) => b[1] - a[1] || a[0] - b[0])[0][0] : null,
        TopSeries: top(series).map(({ Name, Id, Count }) => ({ Name, Id, Count })),
        TopGenres: top(genres)
    };
}

/**
 * Reads and changes play state in a store.
 * @param {{store: object, now: () => number}} options The store needs getText and putText to keep play state;
 * without them every list is empty and changes are not kept.
 */
export function createPlayState({ store, now }) {
    const supported = typeof store.getText === 'function' && typeof store.putText === 'function';
    const cache = new Map();
    const lastProgress = new Map();
    const keyOf = userId => `state/${cleanId(userId)}.json`;

    function parse(text) {
        try {
            const state = JSON.parse(text);
            return { Items: state.Items || {}, Watchlist: state.Watchlist || {} };
        } catch {
            return emptyState();
        }
    }

    async function read(userId, fresh) {
        const hit = cache.get(userId);
        if (!fresh && hit && now() - hit.at < CACHE_MS) return hit;

        const found = supported ? await store.getText(keyOf(userId)) : null;
        const entry = { at: now(), tag: found?.tag ?? null, state: found ? parse(found.text) : emptyState() };
        cache.set(userId, entry);
        return entry;
    }

    // Applies a change on top of the newest version. The change returns false when there is nothing to save.
    async function change(userId, mutate) {
        if (!supported) return emptyState();

        for (let attempt = 0; attempt < 4; attempt++) {
            const current = await read(userId, attempt > 0);
            const state = structuredClone(current.state);
            if (mutate(state) === false) return current.state;

            const result = await store.putText(keyOf(userId), JSON.stringify(state), current.tag);
            if (result.written) {
                cache.set(userId, { at: now(), tag: result.tag, state });
                return state;
            }
        }

        throw new Error('The play state kept changing and could not be saved.');
    }

    const edit = (state, id) => (state.Items[cleanId(id)] ??= emptyEntry());
    const stamp = () => new Date(now()).toISOString();

    return {
        supported,

        /** The user's state. Browsing still works if it cannot be read. */
        async load(userId) {
            try {
                return (await read(userId, false)).state;
            } catch (error) {
                console.warn('[twin] play state could not be read:', error.message);
                return emptyState();
            }
        },

        /**
         * Records a playback report.
         * @param {'start'|'progress'|'stop'} event What the player reported.
         */
        async report(userId, record, positionTicks, event) {
            const id = cleanId(record.Id);
            const throttleKey = cleanId(userId) + ':' + id;
            if (event === 'progress') {
                // Players report every few seconds; the store does not need each one.
                if (now() - (lastProgress.get(throttleKey) ?? -Infinity) < PROGRESS_EVERY_MS) return;
            }

            lastProgress.set(throttleKey, now());
            const hasPosition = Number.isFinite(positionTicks) && positionTicks >= 0;
            let position = hasPosition ? Math.floor(positionTicks) : 0;

            await change(userId, state => {
                const entry = edit(state, id);
                const before = JSON.stringify(entry);

                if (event === 'start') {
                    entry.PlayCount++;
                } else if (event === 'progress' && hasPosition) {
                    entry.PositionTicks = position;
                } else if (event === 'stop' && hasPosition) {
                    const runTime = record.RunTimeTicks || 0;
                    if (position > 0 && runTime > 0) {
                        const percent = position / runTime * 100;
                        if (percent < MIN_RESUME_PERCENT) {
                            position = 0;
                        } else if (percent > MAX_RESUME_PERCENT || position >= runTime || runTime / TICKS_PER_SECOND < MIN_RESUME_SECONDS) {
                            position = 0;
                            entry.Played = true;
                        }
                    }

                    entry.PositionTicks = position;
                }

                if (JSON.stringify(entry) === before) return false;
                entry.LastPlayed = stamp();
                entry.Changed = now();
            });
        },

        setPlayed(userId, ids, played) {
            return change(userId, state => {
                for (const id of ids) {
                    const entry = edit(state, id);
                    entry.Played = played;
                    entry.PositionTicks = 0;
                    if (played) {
                        entry.PlayCount = Math.max(entry.PlayCount, 1);
                        entry.LastPlayed = stamp();
                    }

                    entry.Changed = now();
                }
            });
        },

        setFavorite(userId, id, on) {
            return change(userId, state => {
                const entry = edit(state, id);
                if (entry.IsFavorite === on) return false;
                entry.IsFavorite = on;
                entry.Changed = now();
            });
        },

        setWatchlist(userId, id, on) {
            return change(userId, state => {
                if (!!state.Watchlist[cleanId(id)]?.On === on) return false;
                state.Watchlist[cleanId(id)] = { On: on, Changed: now() };
            });
        }
    };
}

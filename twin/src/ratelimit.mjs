// Slows down password guessing against a twin that anyone on the internet can reach.
//
// Failed sign-ins are counted per user name and per caller address. Past the limit, further
// attempts are refused without checking the password until the window has passed.
//
// The counts live in the store, so every running copy of the twin sees the same numbers.
// Each copy also keeps its own count, so if the store cannot be read or written the limit
// still stands: weaker, since each copy then counts alone, but never off.

import { sha256Hex } from './sigv4.mjs';

export const SIGN_IN_WINDOW_MS = 15 * 60 * 1000;
export const MAX_FAILURES_PER_NAME = 10;
export const MAX_FAILURES_PER_ADDRESS = 20;

// One network, not one device: a caller with a whole IPv6 block counts as one.
function network(address) {
    const text = String(address || 'unknown').trim().toLowerCase();
    return text.includes(':') && !text.includes('.') ? text.split(':').slice(0, 4).join(':') : text;
}

/**
 * @param {{store: object, now: () => number}} options The store needs getText and putText to share counts.
 */
export function createSignInLimiter({ store, now }) {
    const memory = new Map();
    const shared = typeof store.getText === 'function' && typeof store.putText === 'function';
    let warned = false;

    const recent = times => (Array.isArray(times) ? times : []).filter(t => Number.isFinite(t) && now() - t < SIGN_IN_WINDOW_MS);

    // Two lists of the same failures, joined without counting any twice or dropping two that share a moment.
    function merge(a, b) {
        const count = list => list.reduce((counts, t) => counts.set(t, (counts.get(t) || 0) + 1), new Map());
        const [inA, inB] = [count(a), count(b)];
        const merged = [];
        for (const time of new Set([...a, ...b])) {
            for (let i = Math.max(inA.get(time) || 0, inB.get(time) || 0); i > 0; i--) merged.push(time);
        }

        return merged;
    }

    function warn(error) {
        if (!warned) console.error('[twin] sign-in limits could not use the store; this copy counts on its own for now:', error.message);
        warned = true;
    }

    // What the store holds, together with what this copy has seen itself. The second part is
    // what keeps the limit standing when the store can be read but not written.
    async function read(key) {
        const own = recent(memory.get(key));
        if (!shared) return { times: own, tag: null };

        try {
            const found = await store.getText(key);
            let stored = [];
            try {
                stored = found ? JSON.parse(found.text).Failures : [];
            } catch {
                // A damaged counter starts again rather than locking everyone out.
            }

            return { times: merge(recent(stored), own), tag: found?.tag ?? null };
        } catch (error) {
            warn(error);
            return { times: own, tag: null, unreadable: true };
        }
    }

    // Returns false when someone else wrote first.
    async function write(key, times, current) {
        memory.set(key, times);
        if (!shared || current.unreadable) return true;
        try {
            return (await store.putText(key, JSON.stringify({ Failures: times }), current.tag)).written;
        } catch (error) {
            warn(error);
            return true;
        }
    }

    async function change(key, update) {
        for (let attempt = 0; attempt < 4; attempt++) {
            const current = await read(key);
            const next = update(current.times);
            if (!next || await write(key, next, current)) return;
        }
    }

    async function keysFor(name, address) {
        const keys = [{ key: `state/limits/address-${(await sha256Hex(network(address))).slice(0, 32)}.json`, max: MAX_FAILURES_PER_ADDRESS }];

        // With no name, as when an invitation code is tried, only the caller is counted.
        if (name !== null) {
            const cleanName = String(name || '').trim().toLowerCase().slice(0, 200);
            keys.unshift({ key: `state/limits/name-${(await sha256Hex(cleanName)).slice(0, 32)}.json`, max: MAX_FAILURES_PER_NAME });
        }

        return keys;
    }

    return {
        /** How long, in milliseconds, this name or this caller must wait before trying again. Zero when it may try now. */
        async waitFor(name, address) {
            let wait = 0;
            for (const { key, max } of await keysFor(name, address)) {
                const { times } = await read(key);
                if (times.length >= max) {
                    // The attempt that brought it to the limit decides when the next one is allowed.
                    const sorted = [...times].sort((a, b) => a - b);
                    wait = Math.max(wait, sorted[sorted.length - max] + SIGN_IN_WINDOW_MS - now());
                }
            }

            return Math.max(wait, 0);
        },

        async failed(name, address) {
            for (const { key } of await keysFor(name, address)) {
                await change(key, times => [...times, now()]);
            }
        },

        /** A sign-in that worked clears the count for that name, not for the caller's address. */
        async succeeded(name, address) {
            const [byName] = await keysFor(name, address);
            await change(byName.key, times => (times.length ? [] : null));
        }
    };
}

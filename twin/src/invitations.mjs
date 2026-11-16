// Invitations: a way to add someone to the twin without touching the home server.
//
// An administrator makes a single-use link. Whoever opens it picks a name and a password and
// can sign in to the twin straight away. The home server creates the matching account on its
// next sync, with the same password, and from then on that account is the real one.
//
// Everything is in one file in the store, state/invitations.json, so that accepting an
// invitation (marking it used and adding the person) is a single write.

import { sha256Hex } from './sigv4.mjs';

const KEY = 'state/invitations.json';
const CACHE_MS = 15_000;
const DAY_MS = 24 * 3600 * 1000;
const DEFAULT_DAYS = 7;
const MAX_DAYS = 30;
const KEEP_FINISHED_DAYS = 60;
const PASSWORD_ITERATIONS = 210_000;
const MIN_PASSWORD_LENGTH = 8;

// The names the media server accepts.
const VALID_NAME = /^(?!\s)[\p{L}\p{N}_ \-'.@+]+(?<!\s)$/u;

const toHex = bytes => [...new Uint8Array(bytes)].map(b => b.toString(16).padStart(2, '0')).join('');
const toBase64Url = bytes => btoa(String.fromCharCode(...new Uint8Array(bytes))).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
const sameName = (a, b) => String(a || '').toLowerCase() === String(b || '').toLowerCase();
const empty = () => ({ Invitations: {}, Users: [] });

/** Hashes a password the way the media server stores it, so the same password works at home. */
export async function hashPassword(password) {
    const salt = crypto.getRandomValues(new Uint8Array(16));
    const key = await crypto.subtle.importKey('raw', new TextEncoder().encode(password), 'PBKDF2', false, ['deriveBits']);
    const derived = await crypto.subtle.deriveBits({ name: 'PBKDF2', hash: 'SHA-512', salt, iterations: PASSWORD_ITERATIONS }, key, 512);
    return `$PBKDF2-SHA512$iterations=${PASSWORD_ITERATIONS}$${toHex(salt).toUpperCase()}$${toHex(derived).toUpperCase()}`;
}

/** Thrown for a request the person can fix; the message is shown to them. */
export class InvitationError extends Error {}

/**
 * @param {{store: object, now: () => number}} options The store needs getText and putText.
 */
export function createInvitations({ store, now }) {
    const supported = typeof store.getText === 'function' && typeof store.putText === 'function';
    let cache = null;

    function parse(text) {
        try {
            const data = JSON.parse(text);
            return { Invitations: data.Invitations || {}, Users: Array.isArray(data.Users) ? data.Users : [] };
        } catch {
            return empty();
        }
    }

    async function read(fresh) {
        if (!supported) return { data: empty(), tag: null };
        if (!fresh && cache && now() - cache.at < CACHE_MS) return cache;

        const found = await store.getText(KEY);
        cache = { at: now(), tag: found?.tag ?? null, data: found ? parse(found.text) : empty() };
        return cache;
    }

    // Old finished business is dropped so the file stays small.
    function prune(data) {
        const cutoff = now() - KEEP_FINISHED_DAYS * DAY_MS;
        for (const [id, invitation] of Object.entries(data.Invitations)) {
            if (invitation.Expires < cutoff || (invitation.UsedAt && invitation.UsedAt < cutoff)) delete data.Invitations[id];
        }

        data.Users = data.Users.filter(u => !((u.HomeId || u.Rejected) && u.Accepted < cutoff));
    }

    async function change(mutate) {
        if (!supported) throw new InvitationError('Invitations need a store the twin can write to.');

        for (let attempt = 0; attempt < 4; attempt++) {
            const current = await read(attempt > 0);
            const data = structuredClone(current.data);
            const result = mutate(data);
            prune(data);

            const written = await store.putText(KEY, JSON.stringify(data), current.tag);
            if (written.written) {
                cache = { at: now(), tag: written.tag, data };
                return result;
            }
        }

        throw new Error('The invitations kept changing and could not be saved.');
    }

    const stateOf = invitation => (invitation.UsedBy ? 'Used' : invitation.Expires <= now() ? 'Expired' : 'Open');

    return {
        supported,

        /**
         * Makes a new invitation. The code is returned once and never stored.
         * @param {string[]|null} [libraries] The libraries whoever uses it may see; null for every published one.
         */
        async create(createdBy, days, libraries = null) {
            const length = Math.min(Math.max(Math.floor(Number(days)) || DEFAULT_DAYS, 1), MAX_DAYS);
            const code = toBase64Url(crypto.getRandomValues(new Uint8Array(18)));
            const id = await sha256Hex(code);
            const invitation = { Created: now(), Expires: now() + length * DAY_MS, CreatedBy: createdBy, UsedBy: null, UsedAt: null, Libraries: libraries };
            await change(data => {
                data.Invitations[id] = invitation;
            });

            return { Code: code, Id: id.slice(0, 16), ...invitation, State: 'Open' };
        },

        async list() {
            const { data } = await read(true);
            return Object.entries(data.Invitations)
                .map(([id, invitation]) => ({ Id: id.slice(0, 16), ...invitation, State: stateOf(invitation) }))
                .sort((a, b) => b.Created - a.Created);
        },

        /** Withdraws an invitation nobody has used. */
        revoke(shortId) {
            return change(data => {
                const id = Object.keys(data.Invitations).find(key => key.slice(0, 16) === shortId);
                if (!id || data.Invitations[id].UsedBy) return false;
                delete data.Invitations[id];
                return true;
            });
        },

        /** Whether a code can still be used. */
        async check(code) {
            const { data } = await read(true);
            const invitation = data.Invitations[await sha256Hex(String(code || ''))];
            return invitation && stateOf(invitation) === 'Open' ? { Valid: true, Expires: invitation.Expires, InvitedBy: invitation.CreatedBy } : { Valid: false };
        },

        /**
         * Uses an invitation to add a person.
         * @param {string[]} takenNames Names already in use on the twin.
         * @returns {Promise<{Id: string, Name: string}>}
         */
        async accept(code, name, password, takenNames) {
            const cleanName = String(name || '').trim();
            if (!VALID_NAME.test(cleanName) || cleanName.length > 64 || cleanName === '.' || cleanName === '..') {
                throw new InvitationError('Pick a name made of letters, numbers, spaces and - \' . _ @ + only.');
            }

            if (String(password || '').length < MIN_PASSWORD_LENGTH) {
                throw new InvitationError(`Pick a password of at least ${MIN_PASSWORD_LENGTH} characters.`);
            }

            if (!(await this.check(code)).Valid) throw new InvitationError('This invitation is not valid any more.');

            const id = await sha256Hex(String(code));
            const hash = await hashPassword(String(password));
            const user = { Id: toHex(crypto.getRandomValues(new Uint8Array(16))), Name: cleanName, Password: hash, Accepted: now(), HomeId: null, Rejected: null };

            return change(data => {
                // Checked again inside the write, so two people cannot use one link or take one name.
                const invitation = data.Invitations[id];
                if (!invitation || stateOf(invitation) !== 'Open') throw new InvitationError('This invitation is not valid any more.');
                if (takenNames.some(taken => sameName(taken, cleanName)) || data.Users.some(u => sameName(u.Name, cleanName) && !u.Rejected)) {
                    throw new InvitationError('That name is taken. Pick another.');
                }

                invitation.UsedBy = cleanName;
                invitation.UsedAt = now();
                data.Users.push({ ...user, InvitedBy: invitation.CreatedBy, Libraries: invitation.Libraries ?? null });
                return { Id: user.Id, Name: cleanName };
            });
        },

        /** An invited person by name: still waiting for their account at home (has a Password) or given one (has a HomeId). */
        async userByName(name) {
            if (!supported) return null;
            return (await read(true)).data.Users.find(u => !u.Rejected && sameName(u.Name, name)) || null;
        },

        /** An invited person by the id they were given when they accepted. */
        async userByFirstId(id) {
            if (!supported) return null;
            return (await read(true)).data.Users.find(u => !u.Rejected && u.Id === id) || null;
        }
    };
}

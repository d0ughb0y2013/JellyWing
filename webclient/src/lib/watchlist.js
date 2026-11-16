/**
 * Client for the server's per-user watchlist.
 * A server without the watchlist endpoints is treated as having no watchlist support.
 */

const PATH = 'UserWatchlistItems';

// Ids on the current user's watchlist, per server. Null until loaded.
const idCache = new Map();

function cacheKey(apiClient) {
    return `${apiClient.serverId()}|${apiClient.getCurrentUserId()}`;
}

/**
 * Fetches the items on the current user's watchlist, most recently added first.
 * @param {import('jellyfin-apiclient').ApiClient} apiClient The api client.
 * @param {object} [options] Extra query options (Fields, Limit, ...).
 * @returns {Promise<{Items: object[], TotalRecordCount: number}>} The watchlist items.
 */
export function getWatchlistItems(apiClient, options = {}) {
    return apiClient.getJSON(apiClient.getUrl(PATH, {
        userId: apiClient.getCurrentUserId(),
        ...options
    })).then(result => {
        if (options.StartIndex == null && options.Limit == null) {
            idCache.set(cacheKey(apiClient), new Set(result.Items.map(item => item.Id)));
        }

        return result;
    });
}

/**
 * Checks whether an item is on the current user's watchlist.
 * @param {import('jellyfin-apiclient').ApiClient} apiClient The api client.
 * @param {string} itemId The item id.
 * @returns {Promise<boolean>} Rejects if the server has no watchlist support.
 */
export async function isOnWatchlist(apiClient, itemId) {
    const key = cacheKey(apiClient);
    if (!idCache.has(key)) {
        await getWatchlistItems(apiClient, { EnableImages: false, EnableUserData: false });
    }

    return idCache.get(key).has(itemId);
}

function change(apiClient, itemId, type) {
    return apiClient.ajax({
        type,
        url: apiClient.getUrl(`${PATH}/${itemId}`, { userId: apiClient.getCurrentUserId() })
    }).then(() => {
        const ids = idCache.get(cacheKey(apiClient));
        if (ids) {
            if (type === 'POST') {
                ids.add(itemId);
            } else {
                ids.delete(itemId);
            }
        }
    });
}

export function addToWatchlist(apiClient, itemId) {
    return change(apiClient, itemId, 'POST');
}

export function removeFromWatchlist(apiClient, itemId) {
    return change(apiClient, itemId, 'DELETE');
}

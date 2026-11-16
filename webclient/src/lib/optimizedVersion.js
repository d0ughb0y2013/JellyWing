/**
 * Client for the server's optimized versions: copies of a video that play on any device.
 * A server without these endpoints is treated as having no optimized versions.
 */

function path(itemId) {
    return `Items/${itemId}/OptimizedVersion`;
}

/**
 * Gets where an item stands.
 * @param {import('jellyfin-apiclient').ApiClient} apiClient The api client.
 * @param {string} itemId The item id.
 * @returns {Promise<{State: string, Size: number|null}>} State is one of
 * None, NotNeeded, Queued, Running, Ready or Failed.
 */
export function getOptimizedVersionStatus(apiClient, itemId) {
    return apiClient.getJSON(apiClient.getUrl(path(itemId)));
}

/**
 * Asks the server to make an optimized version of an item in the background.
 * @param {import('jellyfin-apiclient').ApiClient} apiClient The api client.
 * @param {string} itemId The item id.
 * @returns {Promise<{State: string, Size: number|null}>} The status after the request.
 */
export function requestOptimizedVersion(apiClient, itemId) {
    return apiClient.ajax({
        type: 'POST',
        url: apiClient.getUrl(path(itemId)),
        dataType: 'json'
    });
}

/**
 * Removes an item's optimized version. Needs an administrator.
 * @param {import('jellyfin-apiclient').ApiClient} apiClient The api client.
 * @param {string} itemId The item id.
 * @returns {Promise<void>}
 */
export function deleteOptimizedVersion(apiClient, itemId) {
    return apiClient.ajax({
        type: 'DELETE',
        url: apiClient.getUrl(path(itemId))
    });
}

export function getOptimizedVersionDownloadUrl(apiClient, itemId) {
    return apiClient.getUrl(`${path(itemId)}/Download`, { ApiKey: apiClient.accessToken() });
}

/**
 * Fetches a summary of what the current user watched in a year.
 * @param {import('jellyfin-apiclient').ApiClient} apiClient The api client.
 * @param {number} [year] The year; the most recent year with anything watched if omitted.
 * @returns {Promise<object>} The summary.
 */
export function getViewingStats(apiClient, year) {
    const query = { userId: apiClient.getCurrentUserId() };
    if (year) {
        query.year = year;
    }

    return apiClient.getJSON(apiClient.getUrl('UserStats', query));
}

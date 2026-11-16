import escapeHtml from 'escape-html';

import globalize from 'lib/globalize';
import { ServerConnections } from 'lib/jellyfin-apiclient';
import { getViewingStats } from 'lib/viewingStats';

const TICKS_PER_HOUR = 36e9;

function monthName(month) {
    return new Date(2000, month - 1, 1).toLocaleString(globalize.getCurrentDateTimeLocale(), { month: 'long' });
}

function formatHours(ticks) {
    const hours = ticks / TICKS_PER_HOUR;
    return hours >= 10 ? Math.round(hours) : hours.toFixed(1);
}

function card(value, label) {
    return '<div class="jellywingStat">'
        + '<div class="jellywingStatValue">' + escapeHtml(String(value)) + '</div>'
        + '<div class="jellywingStatLabel">' + escapeHtml(label) + '</div>'
        + '</div>';
}

function list(title, entries) {
    if (!entries.length) return '';

    return '<div class="jellywingStatList">'
        + '<h2 class="sectionTitle">' + escapeHtml(title) + '</h2>'
        + '<ol>'
        + entries.map(e => '<li><span>' + escapeHtml(e.Name) + '</span><span class="secondary">' + escapeHtml(String(e.Count)) + '</span></li>').join('')
        + '</ol>'
        + '</div>';
}

function render(stats) {
    const years = stats.Years.includes(stats.Year) ? stats.Years : [stats.Year, ...stats.Years];
    let html = '<div class="jellywingStats padded-left padded-right padded-top">';

    html += '<div class="jellywingStatsHeader">';
    html += '<h1>' + escapeHtml(globalize.translate('HeaderYourYear', String(stats.Year))) + '</h1>';
    if (years.length > 1) {
        html += '<select class="selectYear" aria-label="' + escapeHtml(globalize.translate('LabelYear')) + '">'
            + years.map(y => '<option value="' + y + '"' + (y === stats.Year ? ' selected' : '') + '>' + y + '</option>').join('')
            + '</select>';
    }
    html += '</div>';

    if (!stats.Movies && !stats.Episodes) {
        return html + '<p>' + escapeHtml(globalize.translate('MessageNoViewingStats')) + '</p></div>';
    }

    html += '<div class="jellywingStatCards">';
    html += card(formatHours(stats.WatchTimeTicks), globalize.translate('LabelHoursWatched'));
    html += card(stats.Movies, globalize.translate('Movies'));
    html += card(stats.Episodes, globalize.translate('Episodes'));
    if (stats.BusiestMonth) {
        html += card(monthName(stats.BusiestMonth), globalize.translate('LabelBusiestMonth'));
    }
    html += '</div>';

    html += '<div class="jellywingStatLists">';
    html += list(globalize.translate('HeaderTopShows'), stats.TopSeries);
    html += list(globalize.translate('HeaderTopGenres'), stats.TopGenres);
    html += '</div>';

    return html + '</div>';
}

class StatsTab {
    constructor(view, params) {
        this.view = view;
        this.params = params;
        this.apiClient = ServerConnections.currentApiClient();
        this.onYearChange = e => {
            if (e.target.classList.contains('selectYear')) {
                this.load(parseInt(e.target.value, 10));
            }
        };
        view.addEventListener('change', this.onYearChange);
    }

    load(year) {
        return getViewingStats(this.apiClient, year).then(stats => {
            if (this.view) {
                this.view.innerHTML = render(stats);
            }
        }).catch(err => {
            console.error('[StatsTab] failed to load viewing stats', err);
        });
    }

    onResume() {
        // What was watched changes between visits, so the numbers are reloaded each time.
        this.load();
    }

    onPause() {
        // Nothing to pause.
    }

    destroy() {
        this.view?.removeEventListener('change', this.onYearChange);
        this.view = null;
        this.params = null;
        this.apiClient = null;
    }
}

export default StatsTab;

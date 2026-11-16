import cardBuilder from 'components/cardbuilder/cardBuilder';
import focusManager from 'components/focusManager';
import globalize from 'lib/globalize';
import { ServerConnections } from 'lib/jellyfin-apiclient';
import { getWatchlistItems } from 'lib/watchlist';
import escapeHtml from 'escape-html';

import 'elements/emby-itemscontainer/emby-itemscontainer';

function getItemsHtml(items) {
    if (!items.length) {
        return '<div class="noItemsMessage centerMessage">'
            + '<h1>' + escapeHtml(globalize.translate('Watchlist')) + '</h1>'
            + '<p>' + escapeHtml(globalize.translate('MessageWatchlistEmpty')) + '</p>'
            + '</div>';
    }

    return cardBuilder.getCardsHtml({
        items,
        shape: 'auto',
        showTitle: true,
        showYear: true,
        showParentTitle: true,
        centerText: true,
        overlayPlayButton: true,
        overlayMoreButton: true,
        scalable: true,
        allowBottomPadding: true
    });
}

class WatchlistTab {
    constructor(view, params) {
        this.view = view;
        this.params = params;
        this.apiClient = ServerConnections.currentApiClient();

        view.innerHTML = '<div is="emby-itemscontainer" class="itemsContainer vertical-wrap padded-left padded-right padded-top"></div>';
        window.CustomElements.upgradeSubtree(view);

        this.itemsContainer = view.querySelector('.itemsContainer');
        this.itemsContainer.fetchData = () => getWatchlistItems(this.apiClient, {
            Fields: 'PrimaryImageAspectRatio',
            ImageTypeLimit: 1,
            EnableImageTypes: 'Primary,Backdrop,Thumb'
        });
        this.itemsContainer.getItemsHtml = getItemsHtml;
    }

    onResume(options) {
        const view = this.view;

        // The list changes from menus anywhere in the app, so it is reloaded on every visit.
        this.itemsContainer.resume({ ...options, refresh: true }).then(() => {
            if (options.autoFocus) {
                focusManager.autoFocus(view);
            }
        });
    }

    onPause() {
        this.itemsContainer?.pause();
    }

    destroy() {
        if (this.itemsContainer) {
            this.itemsContainer.fetchData = null;
            this.itemsContainer.getItemsHtml = null;
        }

        this.itemsContainer = null;
        this.view = null;
        this.params = null;
        this.apiClient = null;
    }
}

export default WatchlistTab;

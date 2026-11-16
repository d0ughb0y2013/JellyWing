import type { BaseItemDto } from '@jellyfin/sdk/lib/generated-client/models/base-item-dto';

/**
 * Views in the web app that we treat as UserViews.
 */
export const MetaView: Record<string, BaseItemDto> = {
    Favorites: {
        Id: 'favorites',
        Name: 'Favorites'
    },
    Watchlist: {
        Id: 'watchlist',
        Name: 'Watchlist'
    },
    Stats: {
        Id: 'stats',
        Name: 'HeaderViewingStats'
    }
};

/*
 * Wallpapers from jonsolberg.se – Android app.
 * Copyright (C) 2026 Jon Solberg. Licensed under the GNU GPL v3 (see LICENSE).
 */
package se.jonsolberg.wallpapers;

import android.service.quicksettings.Tile;
import android.service.quicksettings.TileService;

/** Quick settings tile: tap for the next wallpaper. */
public class NextTile extends TileService {
    @Override
    public void onStartListening() {
        Tile t = getQsTile();
        if (t == null) return;
        t.setState(Tile.STATE_INACTIVE);
        t.updateTile();
    }

    @Override
    public void onClick() {
        NextActivity.runNext(getApplicationContext());
    }
}

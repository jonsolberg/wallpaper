/*
 * Wallpapers from jonsolberg.se – Android app.
 * Copyright (C) 2026 Jon Solberg. Licensed under the GNU GPL v3 (see LICENSE).
 */
package se.jonsolberg.wallpapers;

import android.app.Activity;
import android.content.Context;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.widget.Toast;

import org.json.JSONObject;

/** The launcher shortcut "Next wallpaper": changes it and closes without showing anything. */
public class NextActivity extends Activity {
    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        Toast.makeText(this, "Changing wallpaper…", Toast.LENGTH_SHORT).show();
        runNext(getApplicationContext());
        finish();
    }

    /** Shared with the quick settings tile. */
    static void runNext(Context app) {
        new Thread(() -> {
            String r = Core.next(app);
            String msg;
            try {
                JSONObject o = new JSONObject(r);
                msg = o.optBoolean("ok") ? "Wallpaper: " + o.optString("message") : o.optString("message");
            } catch (Exception e) {
                msg = "Couldn’t change the wallpaper";
            }
            String m = msg;
            new Handler(Looper.getMainLooper()).post(() -> Toast.makeText(app, m, Toast.LENGTH_SHORT).show());
        }).start();
    }
}

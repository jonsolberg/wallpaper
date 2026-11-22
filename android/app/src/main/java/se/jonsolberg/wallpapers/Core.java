/*
 * Wallpapers from jonsolberg.se – Android app.
 * Copyright (C) 2026 Jon Solberg. Licensed under the GNU GPL v3 (see LICENSE).
 */
package se.jonsolberg.wallpapers;

import android.Manifest;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.WallpaperManager;
import android.app.job.JobInfo;
import android.app.job.JobScheduler;
import android.content.ComponentName;
import android.content.ContentValues;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.net.ConnectivityManager;
import android.net.NetworkCapabilities;
import android.net.Uri;
import android.os.Environment;
import android.provider.MediaStore;
import android.app.Notification;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Calendar;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Random;
import java.util.Set;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/** Everything the app does apart from showing the page: settings, the list from the site, changing the wallpaper. */
final class Core {
    static final String BASE = "https://jonsolberg.se:8080";
    static final String KOFI = "https://ko-fi.com/jonsolberg";
    /** Phone wallpapers on the site are named like jonsolberg-red-cabin-dusk-1080x2340.jpg. */
    private static final Pattern PHONE = Pattern.compile("^(.+?)-1080x2340\\.(jpe?g|png)$", Pattern.CASE_INSENSITIVE);
    private static final int JOB_ID = 1;
    private static final String CHANNEL = "new";
    private static final Object LOCK = new Object();

    private Core() { }

    // ------------------------------------------------------------------ settings

    private static SharedPreferences prefs(Context c) {
        return c.getSharedPreferences("wallpapers", Context.MODE_PRIVATE);
    }

    /** All settings and history as one JSON object (the page reads it with Android.state()). */
    static JSONObject state(Context c) {
        JSONObject s;
        try {
            s = new JSONObject(prefs(c).getString("state", "{}"));
        } catch (JSONException e) {
            s = new JSONObject();
        }
        try {
            def(s, "mode", "daily");
            def(s, "hours", 3);
            def(s, "hour", 7);
            def(s, "weekday", Calendar.MONDAY);
            def(s, "target", "both");
            def(s, "order", "random");
            def(s, "favOnly", false);
            def(s, "newFirst", true);
            def(s, "wifiOnly", false);
            def(s, "notifyNew", false);
            def(s, "favs", new JSONArray());
            def(s, "hidden", new JSONArray());
            def(s, "history", new JSONArray());
            def(s, "current", "");
            def(s, "seen", 0L);
            def(s, "newest", 0L);
            def(s, "lastChange", 0L);
        } catch (JSONException ignored) { }
        return s;
    }

    private static void def(JSONObject s, String k, Object v) throws JSONException {
        if (!s.has(k)) s.put(k, v);
    }

    /** The state plus a few values that are computed, not stored. */
    static String stateForPage(Context c) {
        JSONObject s = state(c);
        try {
            s.put("next", nextChange(s));
            s.put("version", versionName(c));
            s.put("notifyAllowed", notificationsAllowed(c));
        } catch (JSONException ignored) { }
        return s.toString();
    }

    static String versionName(Context c) {
        try {
            return c.getPackageManager().getPackageInfo(c.getPackageName(), 0).versionName;
        } catch (PackageManager.NameNotFoundException e) {
            return "";
        }
    }

    static void save(Context c, JSONObject s) {
        prefs(c).edit().putString("state", s.toString()).apply();
    }

    /** A setting changed from the page. Values arrive as strings. */
    static void set(Context c, String key, String value) {
        synchronized (LOCK) {
            JSONObject s = state(c);
            try {
                switch (key) {
                    case "hours": case "hour": case "weekday":
                        s.put(key, Integer.parseInt(value));
                        break;
                    case "favOnly": case "newFirst": case "wifiOnly": case "notifyNew":
                        s.put(key, Boolean.parseBoolean(value));
                        break;
                    case "seen":
                        s.put(key, Long.parseLong(value));
                        break;
                    case "mode":
                        s.put(key, value);
                        s.put("lastChange", System.currentTimeMillis());   // count the new interval from now
                        break;
                    default:
                        s.put(key, value);
                }
            } catch (JSONException | NumberFormatException ignored) { }
            save(c, s);
        }
        schedule(c);
    }

    /** Adds or removes a name in the favs or hidden list. Returns true if it is now in the list. */
    static boolean toggle(Context c, String list, String name) {
        synchronized (LOCK) {
            JSONObject s = state(c);
            JSONArray a = s.optJSONArray(list);
            if (a == null) a = new JSONArray();
            JSONArray out = new JSONArray();
            boolean found = false;
            for (int i = 0; i < a.length(); i++) {
                if (name.equals(a.optString(i))) found = true; else out.put(a.optString(i));
            }
            if (!found) out.put(name);
            try { s.put(list, out); } catch (JSONException ignored) { }
            save(c, s);
            return !found;
        }
    }

    private static Set<String> setOf(JSONArray a) {
        Set<String> r = new HashSet<>();
        if (a != null) for (int i = 0; i < a.length(); i++) r.add(a.optString(i));
        return r;
    }

    // ------------------------------------------------------------------ the list from the site

    static final class Item {
        final String name, title, url, thumb;
        final long time;   // seconds

        Item(String name, String url, long time) {
            this.name = name;
            this.url = url;
            this.time = time;
            Matcher m = PHONE.matcher(name);
            String slug = m.matches() ? m.group(1) : name;
            String t = slug.replaceFirst("^jonsolberg-", "").replaceFirst("^wallpaper-", "").replace('-', ' ').replace('_', ' ');
            this.title = t.isEmpty() ? name : t.substring(0, 1).toUpperCase(Locale.ROOT) + t.substring(1);
            this.thumb = BASE + "/Images/thumbs/" + Uri.encode(name.replaceAll("\\.[^.]+$", ".jpg"));
        }

        JSONObject json() throws JSONException {
            return new JSONObject().put("name", name).put("title", title).put("url", thumb).put("full", url).put("time", time);
        }
    }

    private static File listFile(Context c) {
        return new File(c.getFilesDir(), "list.json");
    }

    private static List<Item> parse(String raw) throws JSONException {
        JSONArray a = new JSONArray(raw);
        List<Item> out = new ArrayList<>();
        for (int i = 0; i < a.length(); i++) {
            JSONObject f = a.getJSONObject(i);
            String name = f.optString("name");
            if (!PHONE.matcher(name).matches() || f.optLong("size", 1) == 0) continue;
            out.add(new Item(name, BASE + f.optString("url"), f.optLong("time")));
        }
        out.sort((x, y) -> Long.compare(y.time, x.time));   // newest first
        return out;
    }

    /** The list from last time, without touching the network. */
    static List<Item> cached(Context c) {
        try {
            return parse(new String(readAll(new FileInputStream(listFile(c))), StandardCharsets.UTF_8));
        } catch (IOException | JSONException e) {
            return new ArrayList<>();
        }
    }

    /** Fetches the list from the site and keeps a copy for offline use. */
    static List<Item> fetchList(Context c) throws IOException, JSONException {
        byte[] raw = get(BASE + "/pictures.php");
        List<Item> items = parse(new String(raw, StandardCharsets.UTF_8));
        try (OutputStream o = new FileOutputStream(listFile(c))) { o.write(raw); }
        return items;
    }

    /** For the page: {ok, items, error}. Falls back to the cached list when offline. */
    static String load(Context c) {
        JSONObject r = new JSONObject();
        List<Item> items;
        try {
            try {
                items = fetchList(c);
                r.put("ok", true);
                remember(c, items);
            } catch (Exception e) {
                items = cached(c);
                r.put("ok", false).put("error", e.getMessage() == null ? e.getClass().getSimpleName() : e.getMessage());
            }
            JSONArray a = new JSONArray();
            for (Item it : items) a.put(it.json());
            r.put("items", a);
        } catch (JSONException ignored) { }
        return r.toString();
    }

    /** Notes the newest wallpaper we know of, so "new" mode and notifications only react to newer ones. */
    private static void remember(Context c, List<Item> items) {
        if (items.isEmpty()) return;
        synchronized (LOCK) {
            JSONObject s = state(c);
            if (s.optLong("newest") == 0) {
                try { s.put("newest", items.get(0).time); } catch (JSONException ignored) { }
                save(c, s);
            }
        }
    }

    static Item find(List<Item> items, String name) {
        for (Item it : items) if (it.name.equals(name)) return it;
        return null;
    }

    // ------------------------------------------------------------------ changing the wallpaper

    /** Which wallpaper comes next, following the settings. */
    static Item pickNext(JSONObject s, List<Item> items) {
        Set<String> hidden = setOf(s.optJSONArray("hidden")), favs = setOf(s.optJSONArray("favs"));
        String current = s.optString("current");
        List<Item> pool = new ArrayList<>();
        for (Item it : items) {
            if (hidden.contains(it.name)) continue;
            if (s.optBoolean("favOnly") && !favs.isEmpty() && !favs.contains(it.name)) continue;
            pool.add(it);
        }
        if (pool.isEmpty()) return null;
        if (pool.size() == 1) return pool.get(0);

        Set<String> used = new HashSet<>();
        JSONArray h = s.optJSONArray("history");
        if (h != null) for (int i = 0; i < h.length(); i++) used.add(h.optJSONObject(i).optString("name"));

        if (s.optBoolean("newFirst")) {
            long seen = s.optLong("seen") / 1000;
            for (Item it : pool) {   // newest first
                if (it.time > seen - 14 * 86400 && !used.contains(it.name) && !it.name.equals(current)) return it;
            }
        }
        if ("order".equals(s.optString("order"))) {
            int i = 0;
            for (int k = 0; k < pool.size(); k++) if (pool.get(k).name.equals(current)) { i = k + 1; break; }
            return pool.get(i % pool.size());
        }
        // Shuffle, but avoid the ones shown most recently.
        List<Item> fresh = new ArrayList<>();
        int recent = Math.min(pool.size() / 2, 20);
        Set<String> recentNames = new HashSet<>();
        for (int i = 0; h != null && i < Math.min(h.length(), recent); i++) recentNames.add(h.optJSONObject(i).optString("name"));
        for (Item it : pool) if (!recentNames.contains(it.name) && !it.name.equals(current)) fresh.add(it);
        List<Item> from = fresh.isEmpty() ? pool : fresh;
        return from.get(new Random().nextInt(from.size()));
    }

    /** Downloads (or reuses) the full-size picture. */
    private static File download(Context c, Item it) throws IOException {
        File dir = new File(c.getCacheDir(), "pictures");
        if (!dir.exists() && !dir.mkdirs()) throw new IOException("cannot create cache");
        File f = new File(dir, it.name);
        if (f.length() > 0) return f;
        byte[] data = get(it.url);
        File tmp = new File(dir, it.name + ".part");
        try (OutputStream o = new FileOutputStream(tmp)) { o.write(data); }
        if (!tmp.renameTo(f)) throw new IOException("cannot save picture");
        // keep the cache small
        File[] all = dir.listFiles();
        if (all != null && all.length > 12) {
            java.util.Arrays.sort(all, (a, b) -> Long.compare(a.lastModified(), b.lastModified()));
            for (int i = 0; i < all.length - 12; i++) if (!all[i].equals(f)) all[i].delete();
        }
        return f;
    }

    /** Sets a wallpaper on home, lock or both screens and records it in the history. */
    static void apply(Context c, Item it, String target) throws IOException {
        File f = download(c, it);
        WallpaperManager wm = WallpaperManager.getInstance(c);
        int which = "home".equals(target) ? WallpaperManager.FLAG_SYSTEM
                : "lock".equals(target) ? WallpaperManager.FLAG_LOCK
                : WallpaperManager.FLAG_SYSTEM | WallpaperManager.FLAG_LOCK;
        try (InputStream in = new FileInputStream(f)) {
            wm.setStream(in, null, true, which);
        }
        synchronized (LOCK) {
            JSONObject s = state(c);
            try {
                JSONArray old = s.optJSONArray("history"), h = new JSONArray();
                h.put(new JSONObject().put("name", it.name).put("title", it.title).put("target", target).put("time", System.currentTimeMillis()));
                for (int i = 0; old != null && i < old.length() && h.length() < 30; i++) h.put(old.get(i));
                s.put("history", h).put("current", it.name).put("lastChange", System.currentTimeMillis());
            } catch (JSONException ignored) { }
            save(c, s);
        }
    }

    /** For the page, the shortcut and the tile: changes to the next wallpaper. Returns {ok, message}. */
    static String next(Context c) {
        JSONObject r = new JSONObject();
        try {
            List<Item> items;
            try { items = fetchList(c); } catch (Exception e) { items = cached(c); }
            JSONObject s = state(c);
            Item it = pickNext(s, items);
            if (it == null) {
                r.put("ok", false).put("message", items.isEmpty() ? "No wallpapers loaded – check your connection" : "Nothing to change to – everything is hidden");
            } else {
                apply(c, it, s.optString("target", "both"));
                r.put("ok", true).put("message", it.title);
            }
        } catch (Exception e) {
            try { r.put("ok", false).put("message", "Couldn’t change the wallpaper (" + e.getMessage() + ")"); } catch (JSONException ignored) { }
        }
        schedule(c);
        return r.toString();
    }

    /** For the page: set a chosen wallpaper. Returns {ok, message}. */
    static String applyNamed(Context c, String name, String target) {
        JSONObject r = new JSONObject();
        try {
            Item it = find(cached(c), name);
            if (it == null) throw new IOException("not in the list");
            apply(c, it, target);
            r.put("ok", true).put("message", it.title);
        } catch (Exception e) {
            try { r.put("ok", false).put("message", "Couldn’t set the wallpaper (" + e.getMessage() + ")"); } catch (JSONException ignored) { }
        }
        schedule(c);
        return r.toString();
    }

    /** For the page: saves the full picture to Pictures/Wallpapers. Returns {ok, message}. */
    static String saveToPhone(Context c, String name) {
        JSONObject r = new JSONObject();
        try {
            Item it = find(cached(c), name);
            if (it == null) throw new IOException("not in the list");
            File f = download(c, it);
            ContentValues v = new ContentValues();
            v.put(MediaStore.Images.Media.DISPLAY_NAME, it.name);
            v.put(MediaStore.Images.Media.MIME_TYPE, it.name.toLowerCase(Locale.ROOT).endsWith(".png") ? "image/png" : "image/jpeg");
            v.put(MediaStore.Images.Media.RELATIVE_PATH, Environment.DIRECTORY_PICTURES + "/Wallpapers");
            Uri uri = c.getContentResolver().insert(MediaStore.Images.Media.EXTERNAL_CONTENT_URI, v);
            if (uri == null) throw new IOException("no storage");
            try (OutputStream o = c.getContentResolver().openOutputStream(uri); InputStream in = new FileInputStream(f)) {
                if (o == null) throw new IOException("no storage");
                byte[] b = new byte[16384];
                int n;
                while ((n = in.read(b)) > 0) o.write(b, 0, n);
            }
            r.put("ok", true).put("message", "Saved to Pictures/Wallpapers");
        } catch (Exception e) {
            try { r.put("ok", false).put("message", "Couldn’t save (" + e.getMessage() + ")"); } catch (JSONException ignored) { }
        }
        return r.toString();
    }

    // ------------------------------------------------------------------ automatic change

    /** When the next automatic change is due (ms), or 0 if none is planned. */
    static long nextChange(JSONObject s) {
        String mode = s.optString("mode");
        long last = s.optLong("lastChange");
        Calendar cal = Calendar.getInstance();
        switch (mode) {
            case "hours":
                long step = Math.max(1, s.optInt("hours", 3)) * 3600_000L;
                return Math.max(last, 1) + step;
            case "daily": {
                cal.setTimeInMillis(Math.max(last, 1));
                cal.set(Calendar.HOUR_OF_DAY, s.optInt("hour", 7));
                cal.set(Calendar.MINUTE, 0);
                cal.set(Calendar.SECOND, 0);
                cal.set(Calendar.MILLISECOND, 0);
                while (cal.getTimeInMillis() <= last) cal.add(Calendar.DAY_OF_YEAR, 1);
                return cal.getTimeInMillis();
            }
            case "weekly": {
                cal.setTimeInMillis(Math.max(last, 1));
                cal.set(Calendar.HOUR_OF_DAY, s.optInt("hour", 7));
                cal.set(Calendar.MINUTE, 0);
                cal.set(Calendar.SECOND, 0);
                cal.set(Calendar.MILLISECOND, 0);
                int day = s.optInt("weekday", Calendar.MONDAY);
                while (cal.get(Calendar.DAY_OF_WEEK) != day || cal.getTimeInMillis() <= last) cal.add(Calendar.DAY_OF_YEAR, 1);
                return cal.getTimeInMillis();
            }
            default:
                return 0;
        }
    }

    /** Runs the background check every half hour while anything automatic is turned on. */
    static void schedule(Context c) {
        JobScheduler js = c.getSystemService(JobScheduler.class);
        if (js == null) return;
        JSONObject s = state(c);
        boolean needed = !"off".equals(s.optString("mode")) || s.optBoolean("notifyNew");
        if (!needed) {
            js.cancel(JOB_ID);
            return;
        }
        JobInfo job = new JobInfo.Builder(JOB_ID, new ComponentName(c, ChangeJob.class))
                .setPeriodic(30 * 60_000L)
                .setPersisted(true)
                .setRequiredNetworkType(s.optBoolean("wifiOnly") ? JobInfo.NETWORK_TYPE_UNMETERED : JobInfo.NETWORK_TYPE_ANY)
                .build();
        JobInfo old = js.getPendingJob(JOB_ID);
        if (old == null || old.getNetworkType() != job.getNetworkType()) js.schedule(job);
    }

    /** The background check: notify about new wallpapers and change the wallpaper when it is due. */
    static void runAutomatic(Context c) {
        JSONObject s = state(c);
        String mode = s.optString("mode");
        if (s.optBoolean("wifiOnly") && !onUnmetered(c)) return;
        List<Item> items;
        try { items = fetchList(c); } catch (Exception e) { return; }
        if (items.isEmpty()) return;

        Item newest = items.get(0);
        long known = s.optLong("newest");
        boolean fresh = known > 0 && newest.time > known;
        synchronized (LOCK) {
            JSONObject st = state(c);
            try { st.put("newest", Math.max(known, newest.time)); } catch (JSONException ignored) { }
            save(c, st);
        }
        if (fresh && s.optBoolean("notifyNew")) notifyNew(c, newest);

        try {
            if ("new".equals(mode)) {
                if (fresh && !newest.name.equals(s.optString("current"))) apply(c, newest, s.optString("target", "both"));
            } else if (!"off".equals(mode)) {
                long due = nextChange(s);
                if (due > 0 && System.currentTimeMillis() >= due) {
                    Item it = pickNext(s, items);
                    if (it != null) apply(c, it, s.optString("target", "both"));
                }
            }
        } catch (IOException ignored) { }
    }

    private static boolean onUnmetered(Context c) {
        ConnectivityManager cm = c.getSystemService(ConnectivityManager.class);
        if (cm == null) return false;
        NetworkCapabilities nc = cm.getNetworkCapabilities(cm.getActiveNetwork());
        return nc != null && nc.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED);
    }

    static boolean notificationsAllowed(Context c) {
        if (android.os.Build.VERSION.SDK_INT >= 33
                && c.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) return false;
        NotificationManager nm = c.getSystemService(NotificationManager.class);
        return nm != null && nm.areNotificationsEnabled();
    }

    private static void notifyNew(Context c, Item it) {
        NotificationManager nm = c.getSystemService(NotificationManager.class);
        if (nm == null || !notificationsAllowed(c)) return;
        nm.createNotificationChannel(new NotificationChannel(CHANNEL, c.getString(R.string.channel_new), NotificationManager.IMPORTANCE_DEFAULT));
        PendingIntent open = PendingIntent.getActivity(c, 0, new Intent(c, MainActivity.class), PendingIntent.FLAG_IMMUTABLE);
        Notification n = new Notification.Builder(c, CHANNEL)
                .setSmallIcon(R.drawable.ic_stat)
                .setContentTitle("New wallpaper: " + it.title)
                .setContentText("Tap to see it in Wallpapers")
                .setContentIntent(open)
                .setAutoCancel(true)
                .build();
        nm.notify(2, n);
    }

    // ------------------------------------------------------------------ network

    static byte[] get(String url) throws IOException {
        HttpURLConnection h = (HttpURLConnection) new URL(url).openConnection();
        h.setConnectTimeout(15000);
        h.setReadTimeout(30000);
        h.setRequestProperty("User-Agent", "jonsolberg-wallpapers (Android)");
        try {
            int code = h.getResponseCode();
            if (code != 200) throw new IOException("HTTP " + code);
            return readAll(h.getInputStream());
        } finally {
            h.disconnect();
        }
    }

    private static byte[] readAll(InputStream in) throws IOException {
        try (InputStream i = in) {
            ByteArrayOutputStream o = new ByteArrayOutputStream();
            byte[] b = new byte[16384];
            int n;
            while ((n = i.read(b)) > 0) o.write(b, 0, n);
            return o.toByteArray();
        }
    }
}

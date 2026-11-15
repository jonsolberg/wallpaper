/*
 * Wallpapers from jonsolberg.se – Android app.
 * Copyright (C) 2026 Jon Solberg. Licensed under the GNU GPL v3 (see LICENSE).
 */
package se.jonsolberg.wallpapers;

import android.Manifest;
import android.annotation.SuppressLint;
import android.app.Activity;
import android.content.ActivityNotFoundException;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.net.Uri;
import android.os.Bundle;
import android.webkit.JavascriptInterface;
import android.webkit.WebResourceRequest;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;

import org.json.JSONObject;

/** Shows the gallery and settings page (assets/index.html) and answers its calls to window.Android. */
public class MainActivity extends Activity {
    private WebView web;

    @SuppressLint("SetJavaScriptEnabled")
    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        web = new WebView(this);
        web.setBackgroundColor(getColor(R.color.bg));
        setContentView(web);
        WebSettings s = web.getSettings();
        s.setJavaScriptEnabled(true);
        s.setDomStorageEnabled(true);
        s.setAllowFileAccess(false);
        s.setAllowContentAccess(false);
        s.setTextZoom(100);
        web.setOverScrollMode(WebView.OVER_SCROLL_NEVER);
        web.setWebViewClient(new WebViewClient() {
            @Override
            public boolean shouldOverrideUrlLoading(WebView view, WebResourceRequest request) {
                openExternal(request.getUrl().toString());
                return true;
            }
        });
        web.addJavascriptInterface(new Bridge(), "Android");
        web.loadUrl("file:///android_asset/index.html");
        Core.schedule(this);
    }

    @Override
    protected void onResume() {
        super.onResume();
        if (web != null) web.evaluateJavascript("window.onResumeApp&&onResumeApp()", null);
    }

    @Override
    public void onBackPressed() {
        web.evaluateJavascript("window.onBack?String(onBack()):'false'", r -> {
            if (!"\"true\"".equals(r)) finish();
        });
    }

    private void openExternal(String url) {
        try {
            startActivity(new Intent(Intent.ACTION_VIEW, Uri.parse(url)));
        } catch (ActivityNotFoundException ignored) { }
    }

    /** Runs work off the UI thread and hands the JSON result to the page's callback. */
    private void async(String callback, java.util.concurrent.Callable<String> work) {
        if (!callback.matches("__cb\\d+")) return;
        new Thread(() -> {
            String r;
            try { r = work.call(); } catch (Exception e) { r = "{\"ok\":false,\"message\":\"" + e.getClass().getSimpleName() + "\"}"; }
            String js = "window." + callback + "&&" + callback + "(" + r + ")";
            runOnUiThread(() -> web.evaluateJavascript(js, null));
        }).start();
    }

    private class Bridge {
        @JavascriptInterface public String state() { return Core.stateForPage(MainActivity.this); }

        @JavascriptInterface public void set(String key, String value) {
            Core.set(MainActivity.this, key, value);
            if ("notifyNew".equals(key) && "true".equals(value) && android.os.Build.VERSION.SDK_INT >= 33
                    && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
                runOnUiThread(() -> requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS}, 1));
            }
        }

        @JavascriptInterface public boolean toggle(String list, String name) {
            if (!"favs".equals(list) && !"hidden".equals(list)) return false;
            return Core.toggle(MainActivity.this, list, name);
        }

        @JavascriptInterface public void open(String url) {
            if (url.startsWith("https://")) runOnUiThread(() -> openExternal(url));
        }

        @JavascriptInterface public void load(String cb) { async(cb, () -> Core.load(MainActivity.this)); }

        @JavascriptInterface public void next(String cb) { async(cb, () -> Core.next(MainActivity.this)); }

        @JavascriptInterface public void apply(String name, String target, String cb) {
            async(cb, () -> Core.applyNamed(MainActivity.this, name, target));
        }

        @JavascriptInterface public void save(String name, String cb) {
            async(cb, () -> Core.saveToPhone(MainActivity.this, name));
        }
    }

    @Override
    public void onRequestPermissionsResult(int requestCode, String[] permissions, int[] grantResults) {
        web.evaluateJavascript("window.onResumeApp&&onResumeApp();window.renderAuto&&renderAuto()", null);
    }
}

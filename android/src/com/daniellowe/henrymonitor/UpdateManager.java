package com.daniellowe.henrymonitor;

import android.app.PendingIntent;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.pm.PackageInfo;
import android.content.pm.PackageInstaller;
import android.content.pm.PackageManager;
import android.os.Handler;
import android.os.Looper;
import android.util.Log;

import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.security.MessageDigest;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

/**
 * Automatic self-update for the phone app. The Cloudflare Pages feed carries
 * the latest signed APK; when its versionCode exceeds the installed one the
 * APK is downloaded, SHA-256 verified, and installed. On the device-owner
 * kiosk phone PackageInstaller commits silently; on ordinary phones Android
 * shows the standard confirmation dialog instead.
 */
public final class UpdateManager {
    private static final String TAG = "HenryUpdate";

    // GitHub "latest release" download URLs resolve anonymously to the most
    // recently published release on the public project repository.
    public static final String FEED_URL =
            "https://github.com/bassdrum4/henry-monitor/releases/latest/download/feed.json";
    private static final long MIN_APK_BYTES = 20_000L;

    public interface Listener {
        void onUpdateStatus(String message);
    }

    /** Result of a check. {@code listener} runs on the main thread. */
    public static void check(final Context context, final Listener listener) {
        new Thread(new Runnable() {
            @Override public void run() {
                final String message = checkBlocking(context);
                new Handler(Looper.getMainLooper()).post(new Runnable() {
                    @Override public void run() { listener.onUpdateStatus(message); }
                });
            }
        }, "update-check").start();
    }

    /** One blocking check/install pass; returns a short human summary. */
    public static String checkBlocking(Context context) {
        try {
            JSONObject feed = fetchJson(FEED_URL + "?t=" + System.currentTimeMillis());
            JSONObject android = feed.optJSONObject("android");
            if (android == null) return "No phone release is published in the feed.";

            int latestCode = android.optInt("versionCode", 0);
            PackageInfo installed = context.getPackageManager()
                    .getPackageInfo(context.getPackageName(), 0);
            Log.i(TAG, "Feed offers " + android.optString("version") + " (code "
                    + latestCode + "); installed code " + installed.versionCode);
            if (latestCode <= installed.versionCode) {
                return "Already up to date (app " + installed.versionName + ").";
            }

            String apkUrl = android.optString("path", "");
            String expected = android.optString("sha256", "");
            if (apkUrl.isEmpty() || expected.length() != 64) {
                Log.w(TAG, "Malformed feed entry: path='" + apkUrl + "' sha='" + expected + "'");
                return "The update feed is malformed; nothing was changed.";
            }
            // The feed may ship a bare filename (resolved against the feed
            // URL, as GitHub releases do) or a full https URL. Resolve first,
            // then enforce HTTPS on the final download address.
            String downloadUrl = apkUrl.startsWith("https://")
                    ? apkUrl : FEED_URL.replace("feed.json", apkUrl);
            if (!downloadUrl.startsWith("https://")) return "Refusing non-HTTPS update source.";

            byte[] apk = fetchBytes(downloadUrl);
            String actual = sha256Hex(apk);
            Log.i(TAG, "Downloaded " + apk.length + " bytes from " + downloadUrl
                    + "; sha ok=" + actual.equalsIgnoreCase(expected));
            if (apk.length < MIN_APK_BYTES) return "The downloaded update is implausibly small.";
            if (!actual.equalsIgnoreCase(expected)) {
                return "The downloaded update failed its checksum. Nothing was installed.";
            }

            installApk(context, apk);
            return "Installing update " + android.optString("versionName", "") + "…";
        } catch (final Exception ex) {
            Log.e(TAG, "Update check failed", ex);
            return "Update check failed: " + ex.getMessage();
        }
    }

    private static void installApk(final Context context, byte[] apk) throws Exception {
        // Watch for the system status broadcast BEFORE committing so the
        // result cannot slip past an observer registered too late.
        final CountDownLatch received = new CountDownLatch(1);
        BroadcastReceiver observer = new BroadcastReceiver() {
            @Override public void onReceive(Context ctx, Intent intent) {
                int status = intent.getIntExtra(PackageInstaller.EXTRA_STATUS, -999);
                String detail = intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE);
                Log.w(TAG, "Install status broadcast: " + status
                        + " message=" + detail);
                received.countDown();
            }
        };
        context.registerReceiver(observer, new IntentFilter(INSTALL_ACTION));

        PackageInstaller installer = context.getPackageManager().getPackageInstaller();
        PackageInstaller.SessionParams params = new PackageInstaller.SessionParams(
                PackageInstaller.SessionParams.MODE_FULL_INSTALL);
        params.setAppPackageName(context.getPackageName());

        PackageInstaller.Session session = installer.openSession(installer.createSession(params));
        try {
            try (OutputStream sessionStream = session.openWrite("henry-monitor.apk", 0, apk.length)) {
                sessionStream.write(apk);
                sessionStream.flush();
            }
            // Device-owner apps (the kiosk phone) install without any prompt.
            int intentFlags = PendingIntent.FLAG_UPDATE_CURRENT;
            if (android.os.Build.VERSION.SDK_INT >= 31) intentFlags |= PendingIntent.FLAG_MUTABLE;
            Intent confirmed = new Intent(INSTALL_ACTION).setPackage(context.getPackageName());
            PendingIntent callback = PendingIntent.getBroadcast(context, 0, confirmed, intentFlags);
            session.commit(callback.getIntentSender());
            Log.i(TAG, "Install session committed.");
        } catch (Exception failure) {
            Log.e(TAG, "Install session failed before commit", failure);
            context.unregisterReceiver(observer);
            throw failure;
        } finally {
            session.close();
        }

        // A silent install still restarts the process, so waiting here is
        // best-effort only; either way the app relaunches on the new version.
        try {
            received.await(90, TimeUnit.SECONDS);
        } catch (InterruptedException ignored) {
        } finally {
            try { context.unregisterReceiver(observer); } catch (Exception ignored) { }
        }
    }

    private static final String INSTALL_ACTION = "com.daniellowe.henrymonitor.INSTALL_RESULT";

    private static JSONObject fetchJson(String url) throws Exception {
        return new JSONObject(new String(fetchBytes(url), java.nio.charset.StandardCharsets.UTF_8));
    }

    private static byte[] fetchBytes(String url) throws Exception {
        HttpURLConnection connection = (HttpURLConnection) new URL(url).openConnection();
        connection.setConnectTimeout(10_000);
        connection.setReadTimeout(60_000);
        try {
            int code = connection.getResponseCode();
            if (code != 200) throw new Exception("HTTP " + code);
            InputStream input = connection.getInputStream();
            ByteArrayOutputStream buffer = new ByteArrayOutputStream();
            byte[] chunk = new byte[16_384];
            int read;
            while ((read = input.read(chunk)) != -1) buffer.write(chunk, 0, read);
            input.close();
            return buffer.toByteArray();
        } finally {
            connection.disconnect();
        }
    }

    private static String sha256Hex(byte[] data) throws Exception {
        MessageDigest digest = MessageDigest.getInstance("SHA-256");
        byte[] hash = digest.digest(data);
        StringBuilder hex = new StringBuilder(hash.length * 2);
        for (byte b : hash) hex.append(Character.forDigit((b >> 4) & 0xF, 16))
                .append(Character.forDigit(b & 0xF, 16));
        return hex.toString();
    }

    private UpdateManager() { }
}

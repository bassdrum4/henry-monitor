package com.daniellowe.henrymonitor;

import android.os.Environment;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Matrix;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;

import java.io.File;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Comparator;
import java.util.List;
import java.util.Locale;
import java.util.Random;

/**
 * Advances the photo slideshow shown by screensaver mode. Pictures live in
 * the Photos folder inside the phone's Download directory so Henry can add
 * or remove them directly; the list refreshes itself as slides advance.
 */
final class SlideshowEngine {
    interface Listener {
        void onPhotosChanged();
    }

    static final long SLIDE_MS = 10_000;
    private static final long RESCAN_MS = 20_000;
    private static final long SCAN_DEBOUNCE_MS = 1_500;
    private static final long CROSSFADE_MS = 1_100;
    private static final String[] EXTENSIONS = { ".jpg", ".jpeg", ".png", ".webp", ".gif" };

    private final Handler handler = new Handler(Looper.getMainLooper());
    private final Listener listener;
    private final List<File> photos = new ArrayList<>();
    private final File directory;
    private Bitmap current;
    private Bitmap previous;
    private File currentFile;
    private long currentShownAt;
    private long lastScanAt;
    private int index = -1;
    private boolean running;
    private final Random random = new Random();

    SlideshowEngine(Listener listener) {
        this.listener = listener;
        File downloads = Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS);
        this.directory = new File(downloads, "Photos");
    }

    void start() {
        running = true;
        photos.clear();
        index = -1;
        currentFile = null;
        handler.removeCallbacksAndMessages(null);
        advance();
    }

    void stop() {
        running = false;
        handler.removeCallbacksAndMessages(null);
        recycle(previous);
        previous = null;
        recycle(current);
        current = null;
        currentFile = null;
    }

    boolean hasPhotos() {
        return !photos.isEmpty();
    }

    Bitmap currentPhoto() {
        return current;
    }

    Bitmap previousPhoto() {
        return previous;
    }

    long currentShownAt() {
        return currentShownAt;
    }

    long crossfadeMillis() {
        return CROSSFADE_MS;
    }

    private void advance() {
        if (!running) return;
        scan(false);
        if (photos.isEmpty()) {
            recycle(previous);
            previous = null;
            recycle(current);
            current = null;
            currentFile = null;
            listener.onPhotosChanged();
            scheduleAdvance(RESCAN_MS);
            return;
        }
        // Shuffle: pick a random photo, avoiding an immediate repeat when
        // the folder holds more than one image.
        int next = random.nextInt(photos.size());
        if (photos.size() > 1 && next == index) next = (next + 1) % photos.size();
        index = next;
        final File file = photos.get(index);
        new Thread(new Runnable() {
            @Override public void run() {
                final Bitmap decoded = decodeSafely(file);
                handler.post(new Runnable() {
                    @Override public void run() {
                        if (!running) {
                            recycle(decoded);
                            return;
                        }
                        if (decoded == null) {
                            // Unreadable file: drop it and try the next one.
                            photos.remove(file);
                            if (!photos.isEmpty()) advance();
                            else scheduleAdvance(RESCAN_MS);
                            return;
                        }
                        recycle(previous);
                        previous = current;
                        current = decoded;
                        currentFile = file;
                        currentShownAt = SystemClock.uptimeMillis();
                        listener.onPhotosChanged();
                        scheduleAdvance(SLIDE_MS);
                    }
                });
            }
        }, "henry-photo-decode").start();
    }

    private void scheduleAdvance(long delay) {
        if (!running) return;
        handler.postDelayed(new Runnable() {
            @Override public void run() { advance(); }
        }, delay);
    }

    private void scan(boolean force) {
        long now = SystemClock.uptimeMillis();
        if (!force && now - lastScanAt < SCAN_DEBOUNCE_MS) return;
        lastScanAt = now;

        List<File> found = new ArrayList<>();
        File[] entries = directory.listFiles();
        if (entries != null) {
            Arrays.sort(entries, new Comparator<File>() {
                @Override public int compare(File a, File b) {
                    return a.getName().compareToIgnoreCase(b.getName());
                }
            });
            for (File entry : entries) {
                if (!entry.isFile() || entry.length() == 0) continue;
                String name = entry.getName().toLowerCase(Locale.US);
                boolean matches = false;
                for (String extension : EXTENSIONS) {
                    if (name.endsWith(extension)) { matches = true; break; }
                }
                if (matches) found.add(entry);
            }
        }
        if (sameList(found)) return;
        photos.clear();
        photos.addAll(found);
        // Keep pointing at the photo on screen when it still exists.
        if (currentFile != null) {
            int kept = found.indexOf(currentFile);
            index = kept >= 0 ? kept : -1;
        }
        listener.onPhotosChanged();
    }

    private boolean sameList(List<File> found) {
        if (found.size() != photos.size()) return false;
        for (int i = 0; i < found.size(); i++)
            if (!found.get(i).equals(photos.get(i))) return false;
        return true;
    }

    private static Bitmap decodeSafely(File file) {
        try {
            BitmapFactory.Options bounds = new BitmapFactory.Options();
            bounds.inJustDecodeBounds = true;
            BitmapFactory.decodeFile(file.getAbsolutePath(), bounds);
            if (bounds.outWidth <= 0 || bounds.outHeight <= 0) return null;

            // Phone cameras store portrait shots as landscape pixels plus an
            // EXIF rotation tag; BitmapFactory ignores that tag, so portrait
            // photos would render sideways unless we apply it ourselves.
            int degrees = exifRotationDegrees(file.getAbsolutePath());

            BitmapFactory.Options options = new BitmapFactory.Options();
            // When the decoded pixels will be rotated 90/270, the sampled
            // bitmap is compared against the displayed orientation, so swap
            // width and height for the sample-size math.
            int sampleWidth = bounds.outWidth;
            int sampleHeight = bounds.outHeight;
            if (degrees == 90 || degrees == 270) {
                sampleWidth = bounds.outHeight;
                sampleHeight = bounds.outWidth;
            }
            options.inSampleSize = sampleSize(sampleWidth, sampleHeight);
            options.inPreferredConfig = Bitmap.Config.RGB_565;
            Bitmap decoded = BitmapFactory.decodeFile(file.getAbsolutePath(), options);
            if (decoded == null) return null;
            if (degrees == 0) return decoded;

            Matrix matrix = new Matrix();
            matrix.postRotate(degrees);
            Bitmap rotated = Bitmap.createBitmap(decoded, 0, 0, decoded.getWidth(), decoded.getHeight(), matrix, true);
            if (rotated != decoded) decoded.recycle();
            return rotated;
        } catch (Throwable ignored) {
            return null;
        }
    }

    /** Returns the EXIF rotation for the photo in degrees (0, 90, 180, or 270). */
    private static int exifRotationDegrees(String path) {
        try {
            android.media.ExifInterface exif = new android.media.ExifInterface(path);
            switch (exif.getAttributeInt(android.media.ExifInterface.TAG_ORIENTATION,
                    android.media.ExifInterface.ORIENTATION_NORMAL)) {
                case android.media.ExifInterface.ORIENTATION_ROTATE_90:
                case android.media.ExifInterface.ORIENTATION_TRANSPOSE:
                    return 90;
                case android.media.ExifInterface.ORIENTATION_ROTATE_180:
                    return 180;
                case android.media.ExifInterface.ORIENTATION_ROTATE_270:
                case android.media.ExifInterface.ORIENTATION_TRANSVERSE:
                    return 270;
                default:
                    return 0;
            }
        } catch (Throwable ignored) {
            return 0;
        }
    }

    private static int sampleSize(int width, int height) {
        int sample = 1;
        while (width / sample > 2048 || height / sample > 2048) sample *= 2;
        return sample;
    }

    private static void recycle(Bitmap bitmap) {
        if (bitmap != null && !bitmap.isRecycled()) bitmap.recycle();
    }
}

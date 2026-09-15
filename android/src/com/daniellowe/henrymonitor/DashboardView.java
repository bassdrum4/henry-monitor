package com.daniellowe.henrymonitor;

import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.LinearGradient;
import android.graphics.Paint;
import android.graphics.Path;
import android.graphics.RectF;
import android.graphics.Shader;
import android.graphics.Typeface;
import android.os.SystemClock;
import android.view.MotionEvent;
import android.view.View;

import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Deque;
import java.util.List;
import java.util.Locale;

public final class DashboardView extends View {
    public interface Callback {
        void requestPairing();
        void sendControl(String action, boolean confirmed);
        void openSettings();
        void requestPhotosPermission();
    }

    private static final int DESIGN_W = 1280;
    private static final int DESIGN_H = 720;
    private static final int BG = Color.rgb(247, 248, 250);
    private static final int CARD = Color.WHITE;
    private static final int TEXT = Color.rgb(23, 26, 32);
    private static final int MUTED = Color.rgb(101, 108, 120);
    private static final int BORDER = Color.rgb(222, 225, 231);
    private static final int RED = Color.rgb(242, 73, 111);
    private static final int ORANGE = Color.rgb(255, 155, 72);
    private static final int BLUE = Color.rgb(78, 127, 246);
    private static final int PURPLE = Color.rgb(141, 94, 245);
    private static final int GREEN = Color.rgb(41, 169, 124);
    private static final String[] TABS = { "DASHBOARD", "SENSORS", "CONTROLS" };
    private static final long VALUE_ANIMATION_MS = 650;
    private static final long UPDATE_HOLD_MS = 2000;
    private static final long VOLUME_REPEAT_FIRST_MS = 450;
    private static final long VOLUME_REPEAT_MIN_MS = 90;

    private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint stroke = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint fill = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Typeface regular = Typeface.create("sans-serif", Typeface.NORMAL);
    private final Typeface medium = Typeface.create("sans-serif-medium", Typeface.NORMAL);
    private final Typeface bold = Typeface.create("sans-serif", Typeface.BOLD);
    private final Deque<Float> cpuHistory = new ArrayDeque<>();
    private final Deque<Float> gpuHistory = new ArrayDeque<>();
    private final long createdAt = SystemClock.uptimeMillis();
    private long connectedAt;

    private Callback callback;
    private Telemetry telemetry;
    private Telemetry previousTelemetry;
    private long transitionStarted;
    private long lastAcceptedAt;
    private long lastSequence = -1;
    private String connectionMessage = "STARTING SYSTEM";
    private boolean online;
    private int tab;
    private int previousTab;
    private long tabChangedAt;
    private String message;
    private long messageUntil;
    private long birthdayUntil;
    private long easterStarted;
    private int logoTapCount;
    private long logoTapWindowStarted;
    private String pressedAction;
    private long pressStarted;
    private boolean controlTriggered;
    private SlideshowEngine slideshow;
    private boolean screensaverActive;
    private long screensaverEnteredAt;
    private long volumeRepeatDelay;
    private final Runnable volumeRepeat = new Runnable() {
        @Override public void run() {
            if (pressedAction == null || !isVolumeAction(pressedAction)) return;
            if (callback != null) callback.sendControl(pressedAction, false);
            // Each repeat shortens by a quarter, so holding speeds up smoothly.
            volumeRepeatDelay = (long) Math.max(VOLUME_REPEAT_MIN_MS, volumeRepeatDelay * 3f / 4f);
            postDelayed(this, volumeRepeatDelay);
        }
    };

    public DashboardView(Context context) {
        super(context);
        setLayerType(View.LAYER_TYPE_SOFTWARE, null);
        setFocusable(true);
        stroke.setStyle(Paint.Style.STROKE);
    }

    public void setCallback(Callback callback) { this.callback = callback; }

    public void setTelemetry(Telemetry value) {
        long now = SystemClock.uptimeMillis();
        // Re-arm the boot cascade each time the display (re)connects.
        if (!online) connectedAt = now;
        online = true;
        connectionMessage = "LIVE";
        if (value.sequence != lastSequence) {
            lastSequence = value.sequence;
            addHistory(cpuHistory, f(value.cpu.temperatureC));
            addHistory(gpuHistory, f(value.gpu.temperatureC));
        }
        // Hold the displayed values steady for a couple of seconds so the
        // panel doesn't twitch on every one-second sample; the temperature
        // history graph above still records every raw reading.
        if (telemetry == null || now - lastAcceptedAt >= UPDATE_HOLD_MS) {
            previousTelemetry = telemetry;
            telemetry = value;
            lastAcceptedAt = now;
            transitionStarted = now;
        }
        invalidate();
    }

    public void setConnectionMessage(String value, boolean isOnline) {
        connectionMessage = value;
        online = isOnline;
        invalidate();
    }

    public void showMessage(String value) {
        message = value;
        messageUntil = SystemClock.uptimeMillis() + 4200;
        invalidate();
    }

    public void showBirthdayWelcome() {
        birthdayUntil = SystemClock.uptimeMillis() + 5200;
        invalidate();
    }

    @Override
    protected void onDraw(Canvas canvas) {
        super.onDraw(canvas);
        long now = SystemClock.uptimeMillis();
        float sx = getWidth() / (float) DESIGN_W;
        float sy = getHeight() / (float) DESIGN_H;
        if (screensaverActive) {
            canvas.save();
            canvas.scale(sx, sy);
            drawScreensaver(canvas, now);
            canvas.restore();
            postInvalidateDelayed(40);
            return;
        }
        canvas.save();
        canvas.scale(sx, sy);
        drawBackground(canvas, now);

        float reveal = ease(clamp((now - createdAt - 80) / 900f));
        int layer;
        if (reveal < .999f) {
            layer = canvas.saveLayerAlpha(0, 0, DESIGN_W, DESIGN_H,
                    Math.max(1, (int) (255 * reveal)), Canvas.ALL_SAVE_FLAG);
            canvas.translate(0, (1f - reveal) * 14f);
        } else {
            layer = canvas.save();
        }
        drawHeader(canvas, now);
        if (telemetry == null) drawWelcome(canvas);
        else if (tab == 0) drawDashboard(canvas, now);
        else if (tab == 1) drawSensors(canvas, now);
        else drawControls(canvas, now);
        drawTabs(canvas, now);
        canvas.restoreToCount(layer);

        drawBirthdayBanner(canvas, now);
        drawToast(canvas, now);
        canvas.restore();

        if (online || now - createdAt < 1300 || now - transitionStarted < VALUE_ANIMATION_MS ||
                now - tabChangedAt < 350 || now - easterStarted < 1900 ||
                pressedAction != null || now < messageUntil || now < birthdayUntil) {
            // Twenty-five frames per second keeps motion fluid without wasting
            // heat or battery on the older Galaxy J7 hardware.
            postInvalidateDelayed(40);
        }
    }

    private void drawBackground(Canvas canvas, long now) {
        canvas.drawColor(BG);
        float startup = ease(clamp((now - createdAt) / 850f));
        paint.setShader(new LinearGradient(0, 0, DESIGN_W, 0,
                new int[] { RED, ORANGE, Color.rgb(242, 205, 70), GREEN, BLUE, PURPLE, RED },
                null, Shader.TileMode.CLAMP));
        canvas.drawRect(0, 0, DESIGN_W * startup, 5, paint);
        paint.setShader(null);

        float easter = clamp((now - easterStarted) / 1700f);
        if (easterStarted > 0 && easter < 1f) {
            float x = -240 + easter * (DESIGN_W + 480);
            paint.setShader(new LinearGradient(x - 210, 0, x + 210, 0,
                    new int[] { Color.TRANSPARENT, Color.argb(210, 255, 255, 255), Color.TRANSPARENT },
                    null, Shader.TileMode.CLAMP));
            canvas.drawRect(0, 0, DESIGN_W, 7, paint);
            paint.setShader(null);
        }
    }

    private void drawHeader(Canvas canvas, long now) {
        float logoTurn = easterStarted > 0 && now - easterStarted < 1700
                ? ease(clamp((now - easterStarted) / 1500f)) * 360f : 0f;
        drawMark(canvas, 48, 50, 1f, logoTurn);
        text(canvas, "HENRY", 82, 48, 22, TEXT, bold, Paint.Align.LEFT);
        text(canvas, "/  SYSTEM MONITOR", 168, 48, 18, MUTED, regular, Paint.Align.LEFT);
        text(canvas, telemetry == null ? "PC" : shorten(telemetry.computerName, 24),
                48, 82, 12, MUTED, medium, Paint.Align.LEFT);

        float statusWidth = Math.max(106, measure(connectionMessage, 12, medium) + 38);
        roundRect(canvas, DESIGN_W - statusWidth - 86, 27, statusWidth, 40,
                online ? Color.rgb(231, 248, 241) : Color.rgb(239, 241, 244), 20);
        float breath = online ? (float) ((Math.sin(now / 320.0) + 1) / 2) : 0;
        if (online) {
            paint.setColor(Color.argb(40 + (int) (45 * breath), 41, 169, 124));
            canvas.drawCircle(DESIGN_W - statusWidth - 67, 47, 7 + breath * 2, paint);
        }
        paint.setColor(online ? GREEN : MUTED);
        canvas.drawCircle(DESIGN_W - statusWidth - 67, 47, 4 + breath * .6f, paint);
        text(canvas, connectionMessage, DESIGN_W - 101, 52, 12,
                online ? Color.rgb(29, 121, 87) : Color.rgb(86, 93, 103), medium, Paint.Align.RIGHT);

        roundRect(canvas, 1208, 27, 40, 40, Color.rgb(237, 239, 242), 20);
        text(canvas, "•••", 1228, 49, 14, TEXT, medium, Paint.Align.CENTER);
    }

    private void drawWelcome(Canvas canvas) {
        elevatedCard(canvas, 225, 165, 830, 360, 26);
        drawMark(canvas, 640, 229, 1.8f, 0);
        text(canvas, "HENRY’S DISPLAY", 640, 295, 15, MUTED, medium, Paint.Align.CENTER);
        text(canvas, "Ready to connect", 640, 350, 36, TEXT, bold, Paint.Align.CENTER);
        text(canvas, "Start Henry Monitor on the PC. This display will find it automatically.",
                640, 389, 16, MUTED, regular, Paint.Align.CENTER);
        roundRect(canvas, 505, 435, 270, 56, TEXT, 28);
        text(canvas, "SEARCH / PAIR", 640, 470, 13, Color.WHITE, medium, Paint.Align.CENTER);
    }

    private void drawDashboard(Canvas canvas, long now) {
        // Boot cascade: regions fade/slide in on connect, the dials sweep up
        // with a spring, and the temperature graph draws itself left-to-right.
        long boot = now - connectedAt;
        float weatherIn = ease(clamp(boot / 340f));
        float metricsIn = ease(clamp((boot - 150) / 540f));
        float mainIn = ease(clamp((boot - 360) / 640f));
        float sweep = overshoot(clamp((boot - 420) / 1050f));
        float graphReveal = clamp((boot - 640) / 920f);

        int weatherLayer = beginEnter(canvas, weatherIn, -10);
        drawWeatherStrip(canvas, now);
        canvas.restoreToCount(weatherLayer);

        int metricsLayer = beginEnter(canvas, metricsIn, 18);
        connectedPanel(canvas, 48, 148, 1184, 150, new float[] { 416, 800 }, null);
        drawMetricCell(canvas, 48, 148, 368, 150, "CPU TEMPERATURE",
                f(component(previousTelemetry, 0, 0)), f(telemetry.cpu.temperatureC), "°F",
                component(previousTelemetry, 0, 1), telemetry.cpu.loadPercent,
                component(previousTelemetry, 0, 1), telemetry.cpu.loadPercent, "%", "CPU LOAD",
                loadColor(telemetry.cpu.loadPercent), now, sweep);
        drawMetricCell(canvas, 416, 148, 384, 150, "GPU TEMPERATURE",
                f(component(previousTelemetry, 1, 0)), f(telemetry.gpu.temperatureC), "°F",
                component(previousTelemetry, 1, 1), telemetry.gpu.loadPercent,
                component(previousTelemetry, 1, 1), telemetry.gpu.loadPercent, "%", "GPU LOAD",
                loadColor(telemetry.gpu.loadPercent), now, sweep);
        canvas.restoreToCount(metricsLayer);

        int mainLayer = beginEnter(canvas, mainIn, 18);
        String memoryDetail = telemetry.memory.usedGb != null && telemetry.memory.totalGb != null
                ? String.format(Locale.US, "%.1f / %.1f GB", telemetry.memory.usedGb, telemetry.memory.totalGb)
                : "PHYSICAL MEMORY";
        drawMetricCell(canvas, 800, 148, 432, 150, "RAM USE",
                component(previousTelemetry, 2, 1), telemetry.memory.loadPercent, "%",
                component(previousTelemetry, 2, 1), telemetry.memory.loadPercent,
                null, null, null, memoryDetail, BLUE, now, sweep);

        connectedPanel(canvas, 48, 306, 1184, 316, new float[] { 800 }, null);
        text(canvas, "TEMPERATURE HISTORY", 76, 347, 14, MUTED, medium, Paint.Align.LEFT);
        text(canvas, "LAST 100 SECONDS", 772, 347, 12, MUTED, medium, Paint.Align.RIGHT);
        drawGraph(canvas, 76, 370, 696, 196, now, graphReveal);
        paint.setColor(temperatureColor(f(telemetry.cpu.temperatureC)));
        canvas.drawCircle(77, 593, 4, paint);
        text(canvas, "CPU", 89, 597, 12, MUTED, medium, Paint.Align.LEFT);
        paint.setColor(temperatureColor(f(telemetry.gpu.temperatureC)));
        canvas.drawCircle(155, 593, 4, paint);
        text(canvas, "GPU", 167, 597, 12, MUTED, medium, Paint.Align.LEFT);

        text(canvas, "SYSTEM STATUS", 828, 347, 13, MUTED, medium, Paint.Align.LEFT);
        statRow(canvas, 828, 402, "STORAGE USED",
                format(animated(optional(previousTelemetry), telemetry.storageUsedPercent), "%"));
        statRow(canvas, 828, 457, "NETWORK RECEIVE",
                formatRate(animated(primitive(previousTelemetry, 0), telemetry.downloadMbps),
                        telemetry.downloadMbps));
        statRow(canvas, 828, 512, "NETWORK SEND",
                formatRate(animated(primitive(previousTelemetry, 1), telemetry.uploadMbps),
                        telemetry.uploadMbps));
        statRow(canvas, 828, 567, "DISK ACTIVITY",
                formatDiskRate(animated(diskTotal(previousTelemetry), diskTotal(telemetry)),
                        diskTotal(telemetry)));
        canvas.restoreToCount(mainLayer);
    }

    private void drawWeatherStrip(Canvas canvas, long now) {
        Telemetry.Weather weather = telemetry.weather;
        if (weather == null) {
            text(canvas, "LOCAL WEATHER  UNAVAILABLE", 76, 124, 13,
                    Color.rgb(160, 166, 176), medium, Paint.Align.LEFT);
            return;
        }
        drawWeatherIcon(canvas, 62, 118, weather.isDay, weather.description, now);
        String temp = String.format(Locale.US, "%.0f°", f(weather.temperatureC));
        text(canvas, temp, 96, 128, 34, TEXT, bold, Paint.Align.LEFT);
        text(canvas, shorten(weather.description.toUpperCase(Locale.US), 24),
                178, 116, 15, TEXT, medium, Paint.Align.LEFT);
        String place = weather.city == null || weather.city.trim().isEmpty()
                ? "LOCAL AREA" : weather.city.trim().toUpperCase(Locale.US);
        text(canvas, shorten(place, 18), 178, 134, 12, MUTED, medium, Paint.Align.LEFT);
        String detail = String.format(Locale.US, "FEELS %.0f°   HUMIDITY %d%%   WIND %.0f MPH",
                f(weather.feelsLikeC), weather.humidityPercent, weather.windKmh * 0.621371);
        text(canvas, detail, DESIGN_W - 48, 124, 13, MUTED, medium, Paint.Align.RIGHT);
    }

    private void drawWeatherIcon(Canvas canvas, float cx, float cy, boolean day,
                                 String description, long now) {
        boolean cloud = description.contains("cloud") || description.contains("overcast");
        boolean rain = description.contains("rain") || description.contains("drizzle") ||
                description.contains("thunder") || description.contains("shower");
        boolean snow = description.contains("snow");
        boolean fog = description.contains("fog");
        int cloudColor = Color.rgb(150, 158, 172);
        if (day) {
            if (rain) {
                paint.setColor(BLUE);
                canvas.drawCircle(cx, cy, 12, paint);
                stroke.setColor(BLUE);
                stroke.setStrokeWidth(2.5f);
                for (int i = 0; i < 3; i++) {
                    float dx = cx - 7 + i * 7;
                    canvas.drawLine(dx, cy + 6, dx - 2, cy + 13, stroke);
                }
            } else if (snow) {
                paint.setColor(BLUE);
                canvas.drawCircle(cx, cy, 12, paint);
                paint.setColor(Color.WHITE);
                for (int i = 0; i < 3; i++) canvas.drawCircle(cx - 7 + i * 7, cy + 9, 2f, paint);
            } else if (cloud || fog) {
                paint.setColor(cloudColor);
                canvas.drawCircle(cx, cy, 12, paint);
            } else {
                paint.setColor(ORANGE);
                canvas.drawCircle(cx, cy, 12, paint);
                paint.setColor(Color.rgb(255, 205, 120));
                canvas.drawCircle(cx, cy, 17, paint);
                paint.setColor(ORANGE);
                canvas.drawCircle(cx, cy, 12, paint);
            }
        } else {
            paint.setColor(Color.rgb(96, 106, 130));
            canvas.drawCircle(cx, cy, 12, paint);
            paint.setColor(Color.rgb(220, 224, 232));
            canvas.drawCircle(cx + 5, cy - 4, 10, paint);
        }
    }

    private void drawMetricCell(Canvas canvas, float x, float y, float w, float h,
                                String label, Double oldValue, Double target, String unit,
                                Double gaugeOld, Double gaugeTarget,
                                Double secondaryOld, Double secondaryTarget, String secondaryUnit,
                                String secondaryLabel, int accent, long now, float sweep) {
        text(canvas, label, x + 28, y + 36, 15, MUTED, medium, Paint.Align.LEFT);
        Double value = animated(oldValue, target);
        Double gauge = animated(gaugeOld, gaugeTarget);
        // Boot sweep: the ring springs up from empty; the printed numbers
        // stay real the whole time.
        if (gauge != null && sweep != 1f) gauge = gauge * sweep;
        Double secondary = animated(secondaryOld, secondaryTarget);
        // Decimals follow the target value, not the animated one, so the
        // format never flips while a number is easing toward its new value.
        String primary = value == null ? "—" : String.format(Locale.US,
                target == null || Math.abs(target % 1) < .05 ? "%.0f" : "%.1f", value);
        text(canvas, primary, x + 28, y + 110, 78, TEXT, bold, Paint.Align.LEFT);
        float primaryWidth = measure(primary, 78, bold);
        text(canvas, unit, x + 38 + primaryWidth, y + 106, 24, MUTED, bold, Paint.Align.LEFT);
        if (secondary != null && secondaryTarget != null) {
            String shown = String.format(Locale.US,
                    Math.abs(secondaryTarget % 1) < .05 ? "%.0f" : "%.1f", secondary) + secondaryUnit;
            text(canvas, secondaryLabel + "  " + shown, x + 29, y + 142, 17, TEXT, medium, Paint.Align.LEFT);
        } else {
            text(canvas, secondaryLabel, x + 29, y + 142, 16, MUTED, medium, Paint.Align.LEFT);
        }
        // The dial follows its own metric (load) even when the big number
        // shows another (temperature).
        float glow = gauge == null ? 0 : (float) Math.max(0, Math.min(1, gauge / 100d));
        drawGauge(canvas, x + w - 72, y + 88, gauge == null ? 0 : gauge,
                100, accent, now, 40, glow);
    }

    private void drawGraph(Canvas canvas, float x, float y, float w, float h, long now, float reveal) {
        stroke.setStrokeWidth(1);
        stroke.setColor(Color.rgb(235, 237, 241));
        stroke.setAlpha(255);
        // Axis spans 68–212 °F to match the graph domain.
        int[] marks = { 212, 176, 140, 104, 68 };
        for (int i = 0; i <= 4; i++) {
            float gy = y + h * i / 4f;
            canvas.drawLine(x, gy, x + w, gy, stroke);
            text(canvas, marks[i] + "°", x + w - 2, gy - 5, 10,
                    Color.rgb(150, 156, 166), regular, Paint.Align.RIGHT);
        }
        // Boot: the curves draw themselves left-to-right.
        if (reveal < 1f) {
            canvas.save();
            canvas.clipRect(x - 4, y - 10, x + w * ease(reveal) + 4, y + h + 10);
        }
        drawHistory(canvas, cpuHistory, x, y, w, h, RED, now);
        drawHistory(canvas, gpuHistory, x, y, w, h, BLUE, now);
        if (reveal < 1f) canvas.restore();
    }

    private void drawHistory(Canvas canvas, Deque<Float> values, float x, float y,
                             float w, float h, int color, long now) {
        if (values.size() < 2) return;
        List<Float> points = new ArrayList<>(values);
        // Gradient wash under the curve (skipped when the history has gaps).
        boolean clean = true;
        for (Float v : points) if (v == null || v.isNaN()) { clean = false; break; }
        if (clean) {
            Path area = new Path();
            for (int i = 0; i < points.size(); i++) {
                float px = x + i * w / 99f;
                float py = graphY(points.get(i), y, h);
                if (i == 0) area.moveTo(px, py); else area.lineTo(px, py);
            }
            area.lineTo(x + w, y + h);
            area.lineTo(x, y + h);
            area.close();
            fill.setShader(new LinearGradient(0, y, 0, y + h,
                    withAlpha(color, 60), withAlpha(color, 0), Shader.TileMode.CLAMP));
            canvas.drawPath(area, fill);
            fill.setShader(null);
        }
        stroke.setStrokeCap(Paint.Cap.ROUND);
        stroke.setStrokeWidth(3);
        boolean hasLast = false;
        float lastX = 0;
        float lastY = 0;
        for (int i = 1; i < points.size(); i++) {
            Float a = points.get(i - 1);
            Float b = points.get(i);
            if (a == null || b == null || a.isNaN() || b.isNaN()) continue;
            float x1 = x + (i - 1) * w / 99f;
            float x2 = x + i * w / 99f;
            float y1 = graphY(a, y, h);
            float y2 = graphY(b, y, h);
            stroke.setColor(color);
            stroke.setAlpha(70 + (int) (185f * i / Math.max(1, points.size() - 1)));
            canvas.drawLine(x1, y1, x2, y2, stroke);
            hasLast = true;
            lastX = x2;
            lastY = y2;
        }
        stroke.setAlpha(255);
        if (hasLast) {
            float pulse = (float) ((Math.sin(now / 260.0) + 1) / 2);
            paint.setColor(withAlpha(color, 45 + (int) (35 * pulse)));
            canvas.drawCircle(lastX, lastY, 7 + pulse * 2, paint);
            paint.setColor(color);
            canvas.drawCircle(lastX, lastY, 3.5f, paint);
        }
    }

    private void drawSensors(Canvas canvas, long now) {
        connectedPanel(canvas, 48, 108, 1184, 502, new float[] { 816 }, null);
        text(canvas, "TEMPERATURES", 76, 149, 13, MUTED, medium, Paint.Align.LEFT);
        if (telemetry.temperatures.isEmpty()) {
            text(canvas, "No temperature sensors were reported by this PC.",
                    76, 216, 18, MUTED, regular, Paint.Align.LEFT);
        } else {
            int count = Math.min(10, telemetry.temperatures.size());
            for (int i = 0; i < count; i++) {
                Telemetry.Reading reading = telemetry.temperatures.get(i);
                int column = i / 5;
                int row = i % 5;
                float rx = 76 + column * 360;
                float ry = 190 + row * 78;
                text(canvas, shorten(reading.name, 24), rx, ry, 15, TEXT, bold, Paint.Align.LEFT);
                text(canvas, shorten(reading.source, 26), rx, ry + 21, 10, MUTED, regular, Paint.Align.LEFT);
                Double old = previousReading(previousTelemetry == null ? null : previousTelemetry.temperatures,
                        reading.name);
                double displayed = animated(old, reading.value);
                double shown = f(displayed);
                text(canvas, String.format(Locale.US, "%.1f°F", shown), rx + 305, ry + 5,
                        21, temperatureColor(shown), bold, Paint.Align.RIGHT);
                if (row < 4) divider(canvas, rx, ry + 45, rx + 310, ry + 45);
            }
        }

        text(canvas, "FAN SPEEDS", 844, 149, 13, MUTED, medium, Paint.Align.LEFT);
        if (telemetry.fans.isEmpty()) {
            text(canvas, "No fan-speed sensors available", 844, 211, 16, MUTED, regular, Paint.Align.LEFT);
            text(canvas, "This is normal on some PCs.", 844, 240, 12,
                    Color.rgb(142, 149, 160), regular, Paint.Align.LEFT);
        } else {
            int count = Math.min(6, telemetry.fans.size());
            for (int i = 0; i < count; i++) {
                Telemetry.Reading fan = telemetry.fans.get(i);
                float y = 199 + i * 66;
                text(canvas, shorten(fan.name, 21), 844, y, 14, TEXT, bold, Paint.Align.LEFT);
                Double old = previousReading(previousTelemetry == null ? null : previousTelemetry.fans, fan.name);
                double displayed = animated(old, fan.value);
                text(canvas, String.format(Locale.US, "%.0f RPM", displayed), 1203, y + 2,
                        18, BLUE, bold, Paint.Align.RIGHT);
                drawMiniBar(canvas, 844, y + 18, 359, Math.min(1f, (float) displayed / 3000f), BLUE);
            }
        }
    }

    private void drawControls(Canvas canvas, long now) {
        text(canvas, "PC CONTROLS", 48, 132, 13, MUTED, medium, Paint.Align.LEFT);
        text(canvas, "Hold power controls to confirm", 1232, 132, 12, MUTED, regular, Paint.Align.RIGHT);
        roundRect(canvas, 48, 150, 1184, 282, CARD, 22);
        controlCell(canvas, 48, 150, 368, 141, "LOCK PC", "Lock the Windows session", "lock", false, now);
        controlCell(canvas, 416, 150, 384, 141, "SLEEP", "Enter low-power standby", "sleep", true, now);
        controlCell(canvas, 800, 150, 432, 141, "HIBERNATE", "Save the session and power off", "hibernate", true, now);
        controlCell(canvas, 48, 291, 368, 141, "MUTE / UNMUTE", "Toggle system audio", "mute", false, now);
        controlCell(canvas, 416, 291, 384, 141, "RESTART PC", "Restart in five seconds", "restart", true, now);
        controlCell(canvas, 800, 291, 432, 141, "SHUT DOWN", "Shut down in five seconds", "shutdown", true, now);
        divider(canvas, 416, 150, 416, 432);
        divider(canvas, 800, 150, 800, 432);
        divider(canvas, 48, 291, 1232, 291);
        border(canvas, 48, 150, 1184, 282, 22);

        connectedPanel(canvas, 48, 432, 1184, 178, new float[] { 800 }, null);
        text(canvas, "VOLUME", 76, 472, 13, MUTED, medium, Paint.Align.LEFT);
        controlPad(canvas, 48, 489, 376, 121, "VOLUME DOWN", "volume_down");
        divider(canvas, 424, 489, 424, 610);
        controlPad(canvas, 424, 489, 376, 121, "VOLUME UP", "volume_up");
        text(canvas, "SCREENSAVER", 828, 472, 13, BLUE, medium, Paint.Align.LEFT);
        text(canvas, "Show photos from this phone full screen", 828, 510, 13, MUTED, regular, Paint.Align.LEFT);
        if ("screensaver".equals(pressedAction))
            roundRect(canvas, 824, 535, 380, 50, Color.rgb(237, 243, 255), 18);
        text(canvas, "START NOW", 1203, 572, 15, BLUE, bold, Paint.Align.RIGHT);
    }

    private void controlCell(Canvas canvas, float x, float y, float w, float h,
                             String title, String detail, String action, boolean hold, long now) {
        boolean pressed = action.equals(pressedAction);
        if (pressed) {
            paint.setColor(Color.rgb(241, 243, 246));
            canvas.drawRect(x + 2, y + 2, x + w - 2, y + h - 2, paint);
        }
        float inset = pressed ? 2f : 0f;
        text(canvas, title, x + 27, y + 52 + inset, 18,
                action.equals("shutdown") || action.equals("restart") ? RED : TEXT,
                bold, Paint.Align.LEFT);
        text(canvas, detail, x + 27, y + 84 + inset, 12, MUTED, regular, Paint.Align.LEFT);
        if (hold) {
            text(canvas, pressed ? "KEEP HOLDING" : "HOLD", x + w - 26, y + 52,
                    11, MUTED, medium, Paint.Align.RIGHT);
            if (pressed) {
                float progress = Math.min(1f, (now - pressStarted) / 1600f);
                paint.setColor(action.equals("shutdown") || action.equals("restart") ? RED : BLUE);
                canvas.drawRect(x, y + h - 5, x + w * progress, y + h, paint);
                if (progress >= 1 && !controlTriggered) {
                    controlTriggered = true;
                    if (callback != null) callback.sendControl(action, true);
                    showMessage(title + " command sent.");
                }
            }
        }
    }

    private void controlPad(Canvas canvas, float x, float y, float w, float h,
                            String title, String action) {
        boolean pressed = action.equals(pressedAction);
        if (pressed) {
            paint.setColor(Color.rgb(239, 242, 247));
            canvas.drawRect(x + 2, y + 2, x + w - 2, y + h - 2, paint);
        }
        text(canvas, title, x + w / 2, y + 68 + (pressed ? 2 : 0), 16, TEXT, bold, Paint.Align.CENTER);
        text(canvas, "volume_down".equals(action) ? "−" : "+", x + w / 2, y + 100,
                20, BLUE, bold, Paint.Align.CENTER);
    }

    private void drawTabs(Canvas canvas, long now) {
        paint.setColor(Color.rgb(235, 237, 241));
        canvas.drawRect(0, 635, DESIGN_W, 636, paint);
        for (int i = 0; i < TABS.length; i++) {
            float center = tabCenter(i);
            text(canvas, TABS[i], center, 681, 13, i == tab ? TEXT : MUTED,
                    i == tab ? bold : medium, Paint.Align.CENTER);
        }
        float progress = ease(clamp((now - tabChangedAt) / 300f));
        float center = lerp(tabCenter(previousTab), tabCenter(tab), progress);
        paint.setColor(TEXT);
        canvas.drawRoundRect(new RectF(center - 42, 702, center + 42, 706), 2, 2, paint);
    }

    private void drawBirthdayBanner(Canvas canvas, long now) {
        if (now >= birthdayUntil) return;
        long remaining = birthdayUntil - now;
        float appear = ease(clamp((5200 - remaining) / 450f));
        float disappear = remaining < 500 ? ease(clamp(remaining / 500f)) : 1f;
        int alpha = (int) (255 * appear * disappear);
        int layer = canvas.saveLayerAlpha(0, 0, DESIGN_W, DESIGN_H, alpha, Canvas.ALL_SAVE_FLAG);
        float y = 246 + (1f - appear) * 16;
        paint.setShadowLayer(20, 0, 8, Color.argb(38, 20, 24, 32));
        roundRect(canvas, 330, y, 620, 158, CARD, 26);
        paint.clearShadowLayer();
        border(canvas, 330, y, 620, 158, 26);
        paint.setShader(new LinearGradient(372, 0, 908, 0,
                new int[] { RED, ORANGE, GREEN, BLUE, PURPLE }, null, Shader.TileMode.CLAMP));
        canvas.drawRoundRect(new RectF(372, y + 28, 908, y + 33), 3, 3, paint);
        paint.setShader(null);
        text(canvas, "HAPPY BIRTHDAY, HENRY", 640, y + 86, 24, TEXT, bold, Paint.Align.CENTER);
        text(canvas, "Your command center is online.", 640, y + 121, 15, MUTED, regular, Paint.Align.CENTER);
        canvas.restoreToCount(layer);
    }

    private void drawToast(Canvas canvas, long now) {
        if (message == null || now >= messageUntil) return;
        float width = Math.min(790, Math.max(300, measure(message, 13, medium) + 60));
        roundRect(canvas, 640 - width / 2, 578, width, 42, Color.rgb(24, 27, 32), 21);
        text(canvas, shorten(message, 86), 640, 604, 13, Color.WHITE, medium, Paint.Align.CENTER);
    }

    @Override
    public boolean onTouchEvent(MotionEvent event) {
        if (screensaverActive) {
            if (event.getAction() == MotionEvent.ACTION_DOWN) exitScreensaver();
            return true;
        }
        float x = event.getX() * DESIGN_W / getWidth();
        float y = event.getY() * DESIGN_H / getHeight();
        if (event.getAction() == MotionEvent.ACTION_DOWN) {
            if (new RectF(20, 18, 115, 96).contains(x, y)) {
                registerLogoTap();
                return true;
            }
            if (x >= 1195 && y <= 85) {
                if (callback != null) callback.openSettings();
                return true;
            }
            if (y >= 630) {
                int nextTab = Math.min(2, Math.max(0, (int) (x / (DESIGN_W / 3f))));
                if (nextTab != tab) {
                    previousTab = tab;
                    tab = nextTab;
                    tabChangedAt = SystemClock.uptimeMillis();
                }
                pressedAction = null;
                invalidate();
                return true;
            }
            if (telemetry == null && new RectF(225, 165, 1055, 525).contains(x, y)) {
                if (callback != null) callback.requestPairing();
                return true;
            }
            if (tab == 2) {
                String action = actionAt(x, y);
                if (action != null) {
                    pressedAction = action;
                    if (isHoldAction(action)) {
                        pressStarted = SystemClock.uptimeMillis();
                        controlTriggered = false;
                    }
                    if (isVolumeAction(action)) {
                        // Fire once immediately, then keep firing faster the
                        // longer the button stays held.
                        if (callback != null) callback.sendControl(action, false);
                        volumeRepeatDelay = VOLUME_REPEAT_FIRST_MS;
                        postDelayed(volumeRepeat, volumeRepeatDelay);
                    }
                    invalidate();
                    return true;
                }
            }
        } else if (event.getAction() == MotionEvent.ACTION_UP) {
            removeCallbacks(volumeRepeat);
            if (pressedAction != null) {
                String action = pressedAction;
                boolean wasTriggered = controlTriggered;
                pressedAction = null;
                controlTriggered = false;
                if (isVolumeAction(action)) {
                    // Volume already fired on press; holding handled repeats.
                    invalidate();
                    return true;
                }
                if ("screensaver".equals(action)) {
                    if (action.equals(actionAt(x, y))) enterScreensaver();
                    invalidate();
                    return true;
                }
                if (!isHoldAction(action) && action.equals(actionAt(x, y)) && callback != null)
                    callback.sendControl(action, false);
                if (isHoldAction(action) && !wasTriggered)
                    showMessage("Hold for 1.6 seconds to confirm.");
                invalidate();
                return true;
            }
        } else if (event.getAction() == MotionEvent.ACTION_CANCEL) {
            removeCallbacks(volumeRepeat);
            pressedAction = null;
            controlTriggered = false;
            invalidate();
        }
        return true;
    }

    private void registerLogoTap() {
        long now = SystemClock.uptimeMillis();
        if (now - logoTapWindowStarted > 2400) {
            logoTapWindowStarted = now;
            logoTapCount = 0;
        }
        logoTapCount++;
        if (logoTapCount >= 5) {
            logoTapCount = 0;
            easterStarted = now;
            showMessage("May your temps stay low and your FPS stay high.");
        }
    }

    private static String actionAt(float x, float y) {
        if (new RectF(48, 150, 416, 291).contains(x, y)) return "lock";
        if (new RectF(416, 150, 800, 291).contains(x, y)) return "sleep";
        if (new RectF(800, 150, 1232, 291).contains(x, y)) return "hibernate";
        if (new RectF(48, 291, 416, 432).contains(x, y)) return "mute";
        if (new RectF(416, 291, 800, 432).contains(x, y)) return "restart";
        if (new RectF(800, 291, 1232, 432).contains(x, y)) return "shutdown";
        if (new RectF(48, 489, 424, 610).contains(x, y)) return "volume_down";
        if (new RectF(424, 489, 800, 610).contains(x, y)) return "volume_up";
        if (new RectF(800, 432, 1232, 610).contains(x, y)) return "screensaver";
        return null;
    }

    private static boolean isHoldAction(String action) {
        return "sleep".equals(action) || "hibernate".equals(action) ||
                "restart".equals(action) || "shutdown".equals(action);
    }

    private static boolean isVolumeAction(String action) {
        return "volume_up".equals(action) || "volume_down".equals(action);
    }

    public void enterScreensaver() {
        if (slideshow == null) {
            slideshow = new SlideshowEngine(new SlideshowEngine.Listener() {
                @Override public void onPhotosChanged() { invalidate(); }
            });
        }
        screensaverActive = true;
        screensaverEnteredAt = SystemClock.uptimeMillis();
        pressedAction = null;
        removeCallbacks(volumeRepeat);
        slideshow.start();
        // The screensaver draws pure bitmaps — no card shadows — so hand it
        // to the GPU. Software rendering was rescaling two large photos on
        // the CPU every frame, which is what made the mode laggy.
        setLayerType(View.LAYER_TYPE_HARDWARE, null);
        if (callback != null) callback.requestPhotosPermission();
        invalidate();
    }

    private void exitScreensaver() {
        screensaverActive = false;
        if (slideshow != null) slideshow.stop();
        // Back to software rendering: the dashboard's card shadows require it.
        setLayerType(View.LAYER_TYPE_SOFTWARE, null);
        invalidate();
    }

    private void drawScreensaver(Canvas canvas, long now) {
        canvas.drawColor(Color.BLACK);
        Bitmap current = slideshow == null ? null : slideshow.currentPhoto();
        if (current == null) {
            text(canvas, "NO PHOTOS YET", 640, 320, 30, Color.WHITE, bold, Paint.Align.CENTER);
            text(canvas, "Add pictures to the Photos folder inside Download on this phone.",
                    640, 372, 16, Color.rgb(150, 156, 166), regular, Paint.Align.CENTER);
            text(canvas, "Touch anywhere to return to the dashboard.",
                    640, 412, 14, Color.rgb(110, 116, 126), regular, Paint.Align.CENTER);
            return;
        }
        Bitmap previous = slideshow.previousPhoto();
        float fade = clamp((now - slideshow.currentShownAt()) / (float) slideshow.crossfadeMillis());
        // Ken Burns: every slide slowly zooms and drifts across its ten
        // seconds; the outgoing photo holds its final framing in the fade.
        float kb = clamp((now - slideshow.currentShownAt()) / (float) SlideshowEngine.SLIDE_MS);
        if (previous != null && fade < 1f) drawPhoto(canvas, previous, 1f, 1f);
        drawPhoto(canvas, current, fade, kb);
        float hint = 1f - clamp((now - screensaverEnteredAt) / 3500f);
        if (hint > 0) {
            text(canvas, "Touch to return", 640, 660, 13,
                    Color.argb((int) (200 * hint), 255, 255, 255), medium, Paint.Align.CENTER);
        }
    }

    private void drawPhoto(Canvas canvas, Bitmap bitmap, float alpha, float kb) {
        if (bitmap == null || bitmap.isRecycled()) return;
        paint.setShader(null);
        paint.setStyle(Paint.Style.FILL);
        paint.setFilterBitmap(true);
        // A faint cover-fit copy fills the letterbox bars so portrait photos
        // sit on a soft hint of themselves instead of plain black.
        paint.setAlpha((int) (70 * clamp(alpha)));
        drawPhotoScaled(canvas, bitmap, true, 0f);
        paint.setAlpha((int) (255 * clamp(alpha)));
        drawPhotoScaled(canvas, bitmap, false, kb);
        paint.setAlpha(255);
        paint.setFilterBitmap(false);
    }

    private void drawPhotoScaled(Canvas canvas, Bitmap bitmap, boolean cover, float kb) {
        float scale = cover
                ? Math.max(DESIGN_W / (float) bitmap.getWidth(), DESIGN_H / (float) bitmap.getHeight())
                : Math.min(DESIGN_W / (float) bitmap.getWidth(), DESIGN_H / (float) bitmap.getHeight());
        // Ken Burns: slow zoom plus a drift kept safely inside the zoomed-in
        // margins, so no photo edge can ever slide into view.
        float zoom = cover ? 1f : 1.07f + .08f * clamp(kb);
        float w = bitmap.getWidth() * scale * zoom;
        float h = bitmap.getHeight() * scale * zoom;
        int seed = bitmap.hashCode();
        float ampX = Math.max(0, (w - DESIGN_W) / 2f - 6);
        float ampY = Math.max(0, (h - DESIGN_H) / 2f - 6);
        float dx = (seed % 2 == 0 ? 1 : -1) * ampX * .45f * clamp(kb);
        float dy = (Math.abs(seed) % 3 - 1) * ampY * .35f * clamp(kb);
        canvas.drawBitmap(bitmap, null,
                new RectF(640 - w / 2f + dx, 360 - h / 2f + dy,
                        640 + w / 2f + dx, 360 + h / 2f + dy), paint);
    }

    private void drawGauge(Canvas canvas, float cx, float cy, double value, double max,
                           int color, long now, float radius, float glow) {
        RectF ring = new RectF(cx - radius, cy - radius, cx + radius, cy + radius);
        stroke.setStrokeCap(Paint.Cap.ROUND);
        stroke.setStrokeWidth(10);
        stroke.setColor(Color.rgb(232, 234, 238));
        stroke.setAlpha(255);
        canvas.drawArc(ring, -90, 360, false, stroke);
        float sweep = (float) Math.max(0, Math.min(1, value / max)) * 360;
        float heatPulse = color == RED ? (float) ((Math.sin(now / 240.0) + 1) / 2) : 0;
        // Soft halo whose intensity tracks how hard the component works.
        if (glow > 0.03f) {
            stroke.setStrokeWidth(radius * .9f);
            stroke.setColor(color);
            stroke.setAlpha((int) (10 + 34 * glow * glow));
            canvas.drawArc(ring, -90, sweep, false, stroke);
        }
        stroke.setStrokeWidth(17 + heatPulse * 2);
        stroke.setColor(color);
        stroke.setAlpha(24 + (int) (20 * heatPulse));
        canvas.drawArc(ring, -90, sweep, false, stroke);
        stroke.setStrokeWidth(10);
        stroke.setAlpha(255);
        canvas.drawArc(ring, -90, sweep, false, stroke);
    }

    private void drawMiniBar(Canvas canvas, float x, float y, float width, float progress, int color) {
        roundRect(canvas, x, y, width, 5, Color.rgb(234, 236, 240), 3);
        roundRect(canvas, x, y, width * progress, 5, color, 3);
    }

    private void statRow(Canvas canvas, float x, float y, String label, String value) {
        text(canvas, label, x, y, 13, Color.rgb(96, 102, 112), bold, Paint.Align.LEFT);
        text(canvas, value, 1203, y + 5, 27, Color.rgb(12, 15, 20), bold, Paint.Align.RIGHT);
        divider(canvas, x, y + 26, 1203, y + 26);
    }

    private void connectedPanel(Canvas canvas, float x, float y, float w, float h,
                                float[] verticals, float[] horizontals) {
        elevatedCard(canvas, x, y, w, h, 22);
        if (verticals != null) for (float vx : verticals) divider(canvas, vx, y, vx, y + h);
        if (horizontals != null) for (float hy : horizontals) divider(canvas, x, hy, x + w, hy);
    }

    private void elevatedCard(Canvas canvas, float x, float y, float w, float h, float radius) {
        paint.setShadowLayer(12, 0, 4, Color.argb(20, 24, 28, 36));
        roundRect(canvas, x, y, w, h, CARD, radius);
        paint.clearShadowLayer();
        border(canvas, x, y, w, h, radius);
    }

    private void divider(Canvas canvas, float x1, float y1, float x2, float y2) {
        stroke.setStyle(Paint.Style.STROKE);
        stroke.setStrokeWidth(1);
        stroke.setAlpha(255);
        stroke.setColor(Color.rgb(235, 237, 241));
        canvas.drawLine(x1, y1, x2, y2, stroke);
    }

    private void drawMark(Canvas canvas, float x, float y, float scale, float rotation) {
        stroke.setStyle(Paint.Style.STROKE);
        stroke.setStrokeWidth(3.2f * scale);
        stroke.setStrokeCap(Paint.Cap.ROUND);
        stroke.setAlpha(255);
        RectF ring = new RectF(x - 15 * scale, y - 15 * scale, x + 15 * scale, y + 15 * scale);
        int[] colors = { RED, ORANGE, GREEN, BLUE, PURPLE };
        for (int i = 0; i < colors.length; i++) {
            stroke.setColor(colors[i]);
            canvas.drawArc(ring, -88 + i * 65 + rotation, 48, false, stroke);
        }
        paint.setColor(TEXT);
        canvas.drawCircle(x, y, 4.2f * scale, paint);
    }

    private void text(Canvas canvas, String value, float x, float baseline, float size,
                      int color, Typeface typeface, Paint.Align align) {
        paint.setShader(null);
        paint.setStyle(Paint.Style.FILL);
        paint.setAlpha(255);
        paint.setColor(color);
        paint.setTextSize(size);
        paint.setTypeface(typeface);
        paint.setTextAlign(align);
        canvas.drawText(value == null ? "" : value, x, baseline, paint);
    }

    private float measure(String value, float size, Typeface typeface) {
        paint.setTextSize(size);
        paint.setTypeface(typeface);
        return paint.measureText(value == null ? "" : value);
    }

    private void roundRect(Canvas canvas, float x, float y, float w, float h, int color, float radius) {
        paint.setShader(null);
        paint.setStyle(Paint.Style.FILL);
        paint.setAlpha(255);
        paint.setColor(color);
        canvas.drawRoundRect(new RectF(x, y, x + w, y + h), radius, radius, paint);
    }

    private void border(Canvas canvas, float x, float y, float w, float h, float radius) {
        stroke.setStyle(Paint.Style.STROKE);
        stroke.setStrokeWidth(1);
        stroke.setAlpha(255);
        stroke.setColor(BORDER);
        canvas.drawRoundRect(new RectF(x + .5f, y + .5f, x + w - .5f, y + h - .5f),
                radius, radius, stroke);
    }

    private Double animated(Double oldValue, Double newValue) {
        if (newValue == null) return null;
        if (oldValue == null) return newValue;
        float progress = ease(clamp((SystemClock.uptimeMillis() - transitionStarted) /
                (float) VALUE_ANIMATION_MS));
        return oldValue + (newValue - oldValue) * progress;
    }

    private static Double component(Telemetry value, int part, int metric) {
        if (value == null) return null;
        Telemetry.Component component = part == 0 ? value.cpu : part == 1 ? value.gpu : value.memory;
        return metric == 0 ? component.temperatureC : component.loadPercent;
    }

    private static Double optional(Telemetry value) {
        return value == null ? null : value.storageUsedPercent;
    }

    private static Double primitive(Telemetry value, int metric) {
        if (value == null) return null;
        return metric == 0 ? value.downloadMbps : value.uploadMbps;
    }

    private static Double diskTotal(Telemetry value) {
        if (value == null) return null;
        return value.diskReadMbS + value.diskWriteMbS;
    }

    private static Double previousReading(List<Telemetry.Reading> readings, String name) {
        if (readings == null) return null;
        for (Telemetry.Reading reading : readings)
            if (name.equals(reading.name)) return reading.value;
        return null;
    }

    // Graph domain in °F — histories store Fahrenheit.
    private static final int GRAPH_MIN_F = 68;
    private static final int GRAPH_MAX_F = 212;

    private static float graphY(float value, float y, float h) {
        float safe = Math.max(GRAPH_MIN_F, Math.min(GRAPH_MAX_F, value));
        return y + h - (safe - GRAPH_MIN_F) / (float) (GRAPH_MAX_F - GRAPH_MIN_F) * h;
    }

    // Celsius → Fahrenheit for display; null-safe.
    private static Double f(Double celsius) {
        return celsius == null ? null : celsius * 9d / 5d + 32;
    }

    private static double f(double celsius) {
        return celsius * 9d / 5d + 32;
    }

    private static float tabCenter(int tab) { return DESIGN_W * (tab + .5f) / 3f; }

    private static int withAlpha(int color, int alpha) {
        return Color.argb(alpha, Color.red(color), Color.green(color), Color.blue(color));
    }

    private static float clamp(float value) { return Math.max(0, Math.min(1, value)); }

    // ease-out-back: rises just past the target then settles — dial spring.
    private static float overshoot(float x) {
        float c1 = 1.70158f;
        float c3 = c1 + 1f;
        float xm = x - 1f;
        return 1f + c3 * xm * xm * xm + c1 * xm * xm;
    }

    // Fades/slides a region in during the boot cascade; restore the returned
    // layer id with canvas.restoreToCount when the region is done.
    private int beginEnter(Canvas canvas, float progress, float slide) {
        if (progress >= .999f) return canvas.save();
        int layer = canvas.saveLayerAlpha(0, 0, DESIGN_W, DESIGN_H,
                Math.max(1, (int) (255 * progress)), Canvas.ALL_SAVE_FLAG);
        canvas.translate(0, (1f - progress) * slide);
        return layer;
    }

    private static float ease(float value) {
        float inverse = 1 - value;
        return 1 - inverse * inverse * inverse;
    }

    private static float lerp(float start, float end, float amount) {
        return start + (end - start) * amount;
    }

    private static void addHistory(Deque<Float> history, Double value) {
        history.addLast(value == null ? Float.NaN : value.floatValue());
        while (history.size() > 100) history.removeFirst();
    }

    private static int loadColor(Double value) {
        if (value == null) return BLUE;
        return loadColor(value.doubleValue());
    }

    private static int loadColor(double value) {
        if (value >= 90) return RED;
        if (value >= 65) return ORANGE;
        return GREEN;
    }

    private static int temperatureColor(Double value) {
        if (value == null) return BLUE;
        return temperatureColor(value.doubleValue());
    }

    // Thresholds in °F (was 85/72 °C).
    private static int temperatureColor(double value) {
        if (value >= 185) return RED;
        if (value >= 160) return ORANGE;
        return BLUE;
    }

    private static String format(Double value, String unit) {
        return value == null ? "—" : String.format(Locale.US, "%.1f%s", value, unit);
    }

    private static String formatRate(Double value, Double target) {
        if (value == null || target == null) return "—";
        // The unit follows the target so Mbps/Gbps never flips mid-animation.
        if (target >= 1000) return String.format(Locale.US, "%.2f Gbps", value / 1000d);
        if (target >= 1) return String.format(Locale.US, "%.1f Mbps", value);
        return String.format(Locale.US, "%.0f Kbps", value * 1000d);
    }

    private static String formatDiskRate(Double value, Double target) {
        if (value == null || target == null) return "—";
        // The unit follows the target so MB/s/GB/s never flips mid-animation.
        if (target >= 1000) return String.format(Locale.US, "%.2f GB/s", value / 1000d);
        return String.format(Locale.US, "%.1f MB/s", value);
    }

    private static String formatUptime(long seconds) {
        long days = seconds / 86400;
        long hours = (seconds % 86400) / 3600;
        long minutes = (seconds % 3600) / 60;
        return days > 0 ? String.format(Locale.US, "%dd %02dh", days, hours)
                : String.format(Locale.US, "%02dh %02dm", hours, minutes);
    }

    private static String shorten(String value, int max) {
        if (value == null) return "";
        return value.length() <= max ? value : value.substring(0, Math.max(1, max - 1)) + "…";
    }
}

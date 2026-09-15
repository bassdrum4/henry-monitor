package com.daniellowe.henrymonitor;

import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.Locale;
import java.util.UUID;

import javax.crypto.Mac;
import javax.crypto.spec.SecretKeySpec;
import android.util.Base64;

public final class ApiClient {
    public static final class PairResult {
        public String token;
        public String computerName;
        public int port;
        public long serverTimeUnix;
    }

    public static final class ApiException extends Exception {
        private static final long serialVersionUID = 1L;
        public final int statusCode;
        ApiException(int statusCode, String message) {
            super(message);
            this.statusCode = statusCode;
        }
    }

    private final String host;
    private final int port;
    private final String token;
    private final long clockOffsetSeconds;

    public ApiClient(String host, int port, String token) {
        this(host, port, token, 0);
    }

    public ApiClient(String host, int port, String token, long clockOffsetSeconds) {
        this.host = host;
        this.port = port;
        this.token = token == null ? "" : token;
        this.clockOffsetSeconds = clockOffsetSeconds;
    }

    public boolean matches(String otherHost, int otherPort, String otherToken) {
        return host.equals(otherHost) && port == otherPort && token.equals(otherToken == null ? "" : otherToken);
    }

    public Telemetry readStatus() throws Exception {
        HttpURLConnection connection = open("/api/status", "GET", true, "");
        // readResponse(false) keeps the underlying TCP socket warm so the
        // next one-second poll reuses it instead of re-handshaking.
        return Telemetry.fromJson(readResponse(connection, false));
    }

    public String control(String action, boolean confirmed) throws Exception {
        JSONObject body = new JSONObject();
        body.put("action", action);
        if (confirmed) body.put("confirmation", "HOLD_CONFIRMED");
        for (int attempt = 0; ; attempt++) {
            try {
                HttpURLConnection connection = open("/api/control", "POST", true, action);
                writeJson(connection, body.toString());
                JSONObject response = new JSONObject(readResponse(connection));
                return response.optString("message", "Control sent.");
            } catch (ApiException ex) {
                // Older companion builds applied one global 400 ms control
                // throttle. Retry it invisibly so rapid volume taps remain
                // responsive and the user never sees a rate-limit warning.
                if (ex.statusCode != 429 || attempt >= 2) throw ex;
                Thread.sleep(450);
            }
        }
    }

    public long readServerTime() throws Exception {
        HttpURLConnection connection = open("/api/time", "GET", false, "");
        return new JSONObject(readResponse(connection)).getLong("unixTime");
    }

    public PairResult pair(String code, String deviceName) throws Exception {
        JSONObject body = new JSONObject();
        body.put("code", code);
        body.put("deviceName", deviceName);
        HttpURLConnection connection = open("/api/pair", "POST", false, "");
        writeJson(connection, body.toString());
        JSONObject response = new JSONObject(readResponse(connection));
        PairResult result = new PairResult();
        result.token = response.getString("token");
        result.computerName = response.optString("computerName", "PC");
        result.port = response.optInt("port", port);
        result.serverTimeUnix = response.optLong("serverTimeUnix", 0);
        return result;
    }

    private HttpURLConnection open(String path, String method, boolean authorize, String action) throws Exception {
        URL url = new URL("http", host, port, path);
        HttpURLConnection connection = (HttpURLConnection) url.openConnection();
        connection.setRequestMethod(method);
        connection.setConnectTimeout(2500);
        connection.setReadTimeout(2500);
        connection.setUseCaches(false);
        connection.setRequestProperty("Accept", "application/json");
        if (authorize) sign(connection, method, path, action);
        if ("POST".equals(method)) {
            connection.setDoOutput(true);
            connection.setRequestProperty("Content-Type", "application/json; charset=utf-8");
        }
        return connection;
    }

    private void sign(HttpURLConnection connection, String method, String path, String action) throws Exception {
        String timestamp = Long.toString(System.currentTimeMillis() / 1000L + clockOffsetSeconds);
        String nonce = UUID.randomUUID().toString();
        String canonical = method.toUpperCase(Locale.US) + "\n" + path + "\n" + timestamp + "\n" + nonce + "\n" + action;
        Mac hmac = Mac.getInstance("HmacSHA256");
        hmac.init(new SecretKeySpec(Base64.decode(token, Base64.DEFAULT), "HmacSHA256"));
        String signature = Base64.encodeToString(hmac.doFinal(canonical.getBytes(StandardCharsets.UTF_8)), Base64.NO_WRAP);
        connection.setRequestProperty("X-HM-Timestamp", timestamp);
        connection.setRequestProperty("X-HM-Nonce", nonce);
        connection.setRequestProperty("X-HM-Signature", signature);
    }

    private static void writeJson(HttpURLConnection connection, String body) throws Exception {
        byte[] bytes = body.getBytes(StandardCharsets.UTF_8);
        connection.setFixedLengthStreamingMode(bytes.length);
        try (OutputStream output = connection.getOutputStream()) {
            output.write(bytes);
        }
    }

    private static String readResponse(HttpURLConnection connection) throws Exception {
        return readResponse(connection, true);
    }

    private static String readResponse(HttpURLConnection connection, boolean disconnect) throws Exception {
        int status = connection.getResponseCode();
        InputStream stream = status >= 200 && status < 300
                ? connection.getInputStream() : connection.getErrorStream();
        String text = readAll(stream);
        if (disconnect) connection.disconnect();
        if (status < 200 || status >= 300) {
            String message = "Connection failed (" + status + ").";
            try { message = new JSONObject(text).optString("error", message); } catch (Exception ignored) { }
            throw new ApiException(status, message);
        }
        return text;
    }

    private static String readAll(InputStream stream) throws Exception {
        if (stream == null) return "";
        StringBuilder result = new StringBuilder();
        try (BufferedReader reader = new BufferedReader(new InputStreamReader(stream, StandardCharsets.UTF_8))) {
            String line;
            while ((line = reader.readLine()) != null) result.append(line);
        }
        return result.toString();
    }
}

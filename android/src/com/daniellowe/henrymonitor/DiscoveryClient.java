package com.daniellowe.henrymonitor;

import android.content.Context;
import android.net.DhcpInfo;
import android.net.wifi.WifiManager;

import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.net.SocketTimeoutException;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.concurrent.CompletionService;
import java.util.concurrent.ExecutorCompletionService;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;

public final class DiscoveryClient {
    private static final int PORT = 47832;
    private static final byte[] MESSAGE = "HENRY_MONITOR_DISCOVER_V1".getBytes(StandardCharsets.UTF_8);

    public static final class Candidate {
        public final String host;
        public final int port;
        public final String computerName;

        Candidate(String host, int port, String computerName) {
            this.host = host;
            this.port = port;
            this.computerName = computerName;
        }
    }

    private final Context context;

    public DiscoveryClient(Context context) {
        this.context = context.getApplicationContext();
    }

    public Candidate discover() {
        Candidate broadcast = discoverBroadcast();
        return broadcast != null ? broadcast : discoverLocalSubnet();
    }

    private Candidate discoverBroadcast() {
        WifiManager wifi = (WifiManager) context.getSystemService(Context.WIFI_SERVICE);
        WifiManager.MulticastLock lock = null;
        DatagramSocket socket = null;
        try {
            if (wifi != null) {
                lock = wifi.createMulticastLock("henry-monitor-discovery");
                lock.setReferenceCounted(false);
                lock.acquire();
            }
            socket = new DatagramSocket();
            socket.setBroadcast(true);
            socket.setSoTimeout(1300);

            Map<String, InetAddress> targets = new LinkedHashMap<>();
            targets.put("global", InetAddress.getByName("255.255.255.255"));
            InetAddress localBroadcast = getBroadcastAddress(wifi);
            if (localBroadcast != null) targets.put("local", localBroadcast);

            for (InetAddress target : targets.values()) {
                try {
                    DatagramPacket request = new DatagramPacket(MESSAGE, MESSAGE.length, target, PORT);
                    socket.send(request);
                } catch (Exception ignored) {
                    // Some Android builds reject the global broadcast while the
                    // calculated Wi-Fi broadcast address still works.
                }
            }

            byte[] buffer = new byte[1024];
            DatagramPacket response = new DatagramPacket(buffer, buffer.length);
            socket.receive(response);
            String json = new String(response.getData(), response.getOffset(), response.getLength(), StandardCharsets.UTF_8);
            JSONObject value = new JSONObject(json);
            if (value.optInt("protocol") != 1) return null;
            return new Candidate(
                    response.getAddress().getHostAddress(),
                    value.optInt("port", 47831),
                    value.optString("computerName", "PC"));
        } catch (SocketTimeoutException ignored) {
            return null;
        } catch (Exception ignored) {
            return null;
        } finally {
            if (socket != null) socket.close();
            if (lock != null && lock.isHeld()) lock.release();
        }
    }

    private Candidate discoverLocalSubnet() {
        WifiManager wifi = (WifiManager) context.getSystemService(Context.WIFI_SERVICE);
        if (wifi == null) return null;
        DhcpInfo dhcp = wifi.getDhcpInfo();
        if (dhcp == null || dhcp.ipAddress == 0) return null;

        final int ip = dhcp.ipAddress;
        final String prefix = (ip & 0xFF) + "." + ((ip >> 8) & 0xFF) + "." +
                ((ip >> 16) & 0xFF) + ".";
        final int ownHost = (ip >> 24) & 0xFF;
        ExecutorService pool = Executors.newFixedThreadPool(20);
        CompletionService<Candidate> results = new ExecutorCompletionService<>(pool);
        int submitted = 0;
        try {
            for (int host = 1; host < 255; host++) {
                if (host == ownHost) continue;
                final String address = prefix + host;
                results.submit(new java.util.concurrent.Callable<Candidate>() {
                    @Override public Candidate call() { return probe(address); }
                });
                submitted++;
            }

            long deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(3200);
            for (int i = 0; i < submitted; i++) {
                long remaining = deadline - System.nanoTime();
                if (remaining <= 0) break;
                Future<Candidate> future = results.poll(remaining, TimeUnit.NANOSECONDS);
                if (future == null) break;
                Candidate candidate = future.get();
                if (candidate != null) return candidate;
            }
        } catch (Exception ignored) {
            return null;
        } finally {
            pool.shutdownNow();
        }
        return null;
    }

    private static Candidate probe(String host) {
        HttpURLConnection connection = null;
        try {
            URL url = new URL("http", host, 47831, "/api/health");
            connection = (HttpURLConnection) url.openConnection();
            connection.setRequestMethod("GET");
            connection.setConnectTimeout(280);
            connection.setReadTimeout(350);
            connection.setUseCaches(false);
            if (connection.getResponseCode() != 200) return null;
            InputStream input = connection.getInputStream();
            ByteArrayOutputStream bytes = new ByteArrayOutputStream();
            byte[] buffer = new byte[512];
            int read;
            while ((read = input.read(buffer)) != -1 && bytes.size() < 4096)
                bytes.write(buffer, 0, read);
            input.close();
            JSONObject value = new JSONObject(new String(bytes.toByteArray(), StandardCharsets.UTF_8));
            if (value.optInt("protocol") != 1 ||
                    !"Henry System Monitor".equals(value.optString("product"))) return null;
            return new Candidate(host, value.optInt("port", 47831),
                    value.optString("computerName", "PC"));
        } catch (Exception ignored) {
            return null;
        } finally {
            if (connection != null) connection.disconnect();
        }
    }

    private static InetAddress getBroadcastAddress(WifiManager wifi) {
        if (wifi == null) return null;
        try {
            DhcpInfo dhcp = wifi.getDhcpInfo();
            if (dhcp == null || dhcp.netmask == 0) return null;
            int broadcast = (dhcp.ipAddress & dhcp.netmask) | ~dhcp.netmask;
            byte[] bytes = new byte[4];
            for (int i = 0; i < 4; i++) bytes[i] = (byte) ((broadcast >> (i * 8)) & 0xFF);
            return InetAddress.getByAddress(bytes);
        } catch (Exception ignored) {
            return null;
        }
    }
}

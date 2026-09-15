package com.daniellowe.henrymonitor;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.List;

public final class Telemetry {
    public static final class Component {
        public String name = "";
        public Double temperatureC;
        public Double loadPercent;
        public Double powerWatts;
        public Double clockMhz;
        public Double usedGb;
        public Double totalGb;
    }

    public static final class Reading {
        public String name;
        public String source;
        public String unit;
        public double value;
    }

    public static final class Weather {
        public double temperatureC;
        public double feelsLikeC;
        public int humidityPercent;
        public double windKmh;
        public String description = "";
        public boolean isDay = true;
        public String city = "";
    }

    public long sequence;
    public String computerName = "PC";
    public Component cpu = new Component();
    public Component gpu = new Component();
    public Component memory = new Component();
    public Double storageUsedPercent;
    public double downloadMbps;
    public double uploadMbps;
    public double diskReadMbS;
    public double diskWriteMbS;
    public long uptimeSeconds;
    public Weather weather;
    public final List<Reading> temperatures = new ArrayList<>();
    public final List<Reading> fans = new ArrayList<>();

    public static Telemetry fromJson(String json) throws Exception {
        JSONObject root = new JSONObject(json);
        Telemetry result = new Telemetry();
        result.sequence = root.optLong("sequence");
        result.computerName = root.optString("computerName", "PC");
        result.cpu = component(root.optJSONObject("cpu"));
        result.gpu = component(root.optJSONObject("gpu"));
        result.memory = component(root.optJSONObject("memory"));
        result.storageUsedPercent = optional(root, "storageUsedPercent");
        result.downloadMbps = root.optDouble("downloadMbps", 0);
        result.uploadMbps = root.optDouble("uploadMbps", 0);
        result.diskReadMbS = root.optDouble("diskReadMbS", 0);
        result.diskWriteMbS = root.optDouble("diskWriteMbS", 0);
        result.uptimeSeconds = root.optLong("uptimeSeconds", 0);
        result.weather = weather(root.optJSONObject("weather"));
        parseReadings(root.optJSONArray("temperatures"), result.temperatures);
        parseReadings(root.optJSONArray("fans"), result.fans);
        return result;
    }

    private static Weather weather(JSONObject object) {
        if (object == null) return null;
        Weather result = new Weather();
        result.temperatureC = object.optDouble("temperatureC", 0);
        result.feelsLikeC = object.optDouble("feelsLikeC", result.temperatureC);
        result.humidityPercent = object.optInt("humidityPercent", 0);
        result.windKmh = object.optDouble("windKmh", 0);
        result.description = object.optString("description", "");
        result.isDay = object.optBoolean("isDay", true);
        result.city = object.optString("city", "");
        return result;
    }

    private static Component component(JSONObject object) {
        Component result = new Component();
        if (object == null) return result;
        result.name = object.optString("name", "");
        result.temperatureC = optional(object, "temperatureC");
        result.loadPercent = optional(object, "loadPercent");
        result.powerWatts = optional(object, "powerWatts");
        result.clockMhz = optional(object, "clockMhz");
        result.usedGb = optional(object, "usedGb");
        result.totalGb = optional(object, "totalGb");
        return result;
    }

    private static Double optional(JSONObject object, String key) {
        return object.has(key) && !object.isNull(key) ? object.optDouble(key) : null;
    }

    private static void parseReadings(JSONArray array, List<Reading> output) {
        if (array == null) return;
        for (int i = 0; i < array.length(); i++) {
            JSONObject item = array.optJSONObject(i);
            if (item == null) continue;
            Reading reading = new Reading();
            String rawName = item.optString("name");
            String rawSource = item.optString("source");
            reading.name = friendlyName(rawName, rawSource, "RPM".equalsIgnoreCase(item.optString("unit")));
            reading.source = friendlySource(rawName, rawSource);
            reading.unit = item.optString("unit");
            reading.value = item.optDouble("value");
            boolean replaced = false;
            for (int j = 0; j < output.size(); j++) {
                Reading existing = output.get(j);
                if (existing.name.equalsIgnoreCase(reading.name)) {
                    if (reading.value > existing.value) output.set(j, reading);
                    replaced = true;
                    break;
                }
            }
            if (!replaced) output.add(reading);
        }
    }

    private static String friendlyName(String value, String source, boolean fan) {
        String name = value == null ? "" : value.trim().replaceAll("\\s+", " ");
        String lower = name.toLowerCase(java.util.Locale.US);
        String sourceLower = source == null ? "" : source.toLowerCase(java.util.Locale.US);
        if (fan) {
            if (lower.startsWith("fan #")) return "System Fan " + name.substring(5);
            if (lower.contains("cpu")) return "CPU Fan";
            if (lower.contains("gpu")) return "GPU Fan";
            return name.replace("#", "").trim();
        }
        if (lower.equals("gpu core")) return "GPU";
        if (lower.contains("hot spot") || lower.contains("hotspot")) return "GPU Hotspot";
        if (lower.contains("memory junction") || lower.equals("gpu memory")) return "GPU Memory";
        if (lower.equals("core (tctl/tdie)") || lower.equals("core (tdie)") || lower.equals("tctl/tdie"))
            return "CPU Die";
        if (lower.startsWith("core #")) return "CPU Core " + name.substring(6);
        if (lower.equals("core max")) return "CPU Core Max";
        if (lower.startsWith("ccd")) return "CPU " + name.replace("(Tdie)", "").trim();
        if ((lower.equals("composite") || lower.equals("temperature")) &&
                !isCpu(sourceLower) && !isGpu(sourceLower)) return "SSD Temperature";
        if (lower.equals("system")) return "Motherboard";
        if (lower.startsWith("temperature #")) return "Board Sensor " + name.substring(13);
        return name.replace("#", "").trim();
    }

    private static String friendlySource(String sensorName, String source) {
        String sensor = sensorName == null ? "" : sensorName.toLowerCase(java.util.Locale.US);
        String lower = source == null ? "" : source.toLowerCase(java.util.Locale.US);
        if (sensor.startsWith("gpu") || isGpu(lower)) return "GPU";
        if (sensor.startsWith("cpu") || sensor.startsWith("core") || sensor.startsWith("ccd") || isCpu(lower))
            return "CPU";
        if (sensor.equals("composite") || lower.contains("nvme") || lower.contains("ssd") || lower.contains("disk"))
            return "Storage";
        return "Motherboard";
    }

    private static boolean isCpu(String value) {
        return value.contains("ryzen") || value.contains("processor") || value.contains("cpu");
    }

    private static boolean isGpu(String value) {
        return value.contains("nvidia") || value.contains("geforce") || value.contains("radeon") ||
                value.contains("gpu") || value.contains("arc a");
    }
}

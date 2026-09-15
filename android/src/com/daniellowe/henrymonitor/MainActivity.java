package com.daniellowe.henrymonitor;

import android.Manifest;
import android.app.Activity;
import android.app.ActivityManager;
import android.app.AlertDialog;
import android.app.admin.DevicePolicyManager;
import android.content.ComponentName;
import android.content.Context;
import android.content.DialogInterface;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.text.InputType;
import android.view.Gravity;
import android.view.View;
import android.view.WindowManager;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ProgressBar;
import android.widget.TextView;

import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;

public final class MainActivity extends Activity implements DashboardView.Callback {
    private static final String PREFS = "connection";
    private static final String HOST = "host";
    private static final String PORT = "port";
    private static final String TOKEN = "token";
    private static final String COMPUTER = "computer";
    private static final String CLOCK_OFFSET = "clock_offset";
    private static final String BIRTHDAY_COMPUTER = "birthday_computer";
    private static final String UPDATE_CHECK = "update_check_at";
    private static final long SIX_HOURS_MS = 6L * 60L * 60L * 1000L;
    private static final int PHOTOS_PERMISSION_REQUEST = 41;

    private final Handler main = new Handler(Looper.getMainLooper());
    private final Object connectionLock = new Object();
    private ScheduledExecutorService executor;
    private DashboardView dashboard;
    private SharedPreferences preferences;
    private String host;
    private int port;
    private String token;
    private long clockOffsetSeconds;
    private boolean pairingDialogVisible;
    private ApiClient reusableClient;
    private volatile boolean stopped;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        getWindow().addFlags(
                WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON |
                WindowManager.LayoutParams.FLAG_SHOW_WHEN_LOCKED |
                WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON |
                WindowManager.LayoutParams.FLAG_DISMISS_KEYGUARD);
        preferences = getSharedPreferences(PREFS, MODE_PRIVATE);
        loadConnection();
        dashboard = new DashboardView(this);
        dashboard.setCallback(this);
        setContentView(dashboard);
        enterImmersiveMode();
        enableManagedKioskIfAvailable();
        startConnectionLoop();
    }

    @Override
    protected void onResume() {
        super.onResume();
        enterImmersiveMode();
    }

    @Override
    protected void onDestroy() {
        stopped = true;
        if (executor != null) executor.shutdownNow();
        super.onDestroy();
    }

    @Override
    public void onBackPressed() {
        // This application acts as the phone's home surface.
    }

    private void startConnectionLoop() {
        executor = Executors.newSingleThreadScheduledExecutor();
        executor.scheduleWithFixedDelay(new Runnable() {
            @Override public void run() {
                if (stopped) return;
                String currentHost;
                int currentPort;
                String currentToken;
                long currentClockOffset;
                synchronized (connectionLock) {
                    currentHost = host;
                    currentPort = port;
                    currentToken = token;
                    currentClockOffset = clockOffsetSeconds;
                }
                if (currentHost == null || currentToken == null) {
                    discoverComputer();
                } else {
                    pollTelemetry(currentHost, currentPort, currentToken, currentClockOffset);
                }
            }
        // Half-second cadence: the dashboard holds values for two seconds
        // anyway, so faster polls only make the smoothing silkier.
        }, 0, 500, TimeUnit.MILLISECONDS);

        // Self-update sweep: quietly install newer releases a few minutes
        // after boot and then at most every six hours. The dashboard reappears
        // on its own once the updated package restarts.
        executor.scheduleWithFixedDelay(new Runnable() {
            @Override public void run() {
                if (stopped) return;
                long now = System.currentTimeMillis();
                long last = preferences.getLong(UPDATE_CHECK, 0);
                if (now - last < SIX_HOURS_MS) return;
                preferences.edit().putLong(UPDATE_CHECK, now).apply();
                UpdateManager.checkBlocking(MainActivity.this);
            }
        }, 4, 30, TimeUnit.MINUTES);
    }

    private void discoverComputer() {
        main.post(new Runnable() {
            @Override public void run() { dashboard.setConnectionMessage("SEARCHING FOR HENRY’S PC", false); }
        });
        final DiscoveryClient.Candidate candidate = new DiscoveryClient(this).discover();
        if (candidate == null || stopped) return;
        main.post(new Runnable() {
            @Override public void run() {
                dashboard.setConnectionMessage(candidate.computerName + " FOUND", false);
                if (!pairingDialogVisible) showPairingDialog(candidate);
            }
        });
    }

    private void pollTelemetry(String currentHost, int currentPort, String currentToken, long currentClockOffset) {
        try {
            // One client instance = one warm TCP connection, reused across polls.
            ApiClient client = reusableClient;
            if (client == null || !client.matches(currentHost, currentPort, currentToken)) {
                client = new ApiClient(currentHost, currentPort, currentToken, currentClockOffset);
                reusableClient = client;
            }
            final Telemetry telemetry = client.readStatus();
            main.post(new Runnable() {
                @Override public void run() { dashboard.setTelemetry(telemetry); }
            });
        } catch (final ApiClient.ApiException ex) {
            if (ex.statusCode == 401) {
                retryAfterClockSync(currentHost, currentPort, currentToken);
            } else {
                showOffline();
            }
        } catch (Exception ignored) {
            showOffline();
        }
    }

    private void retryAfterClockSync(String currentHost, int currentPort, String currentToken) {
        try {
            long serverTime = new ApiClient(currentHost, currentPort, "").readServerTime();
            final long adjustedOffset = serverTime - System.currentTimeMillis() / 1000L;
            final Telemetry telemetry = new ApiClient(
                    currentHost, currentPort, currentToken, adjustedOffset).readStatus();
            synchronized (connectionLock) { clockOffsetSeconds = adjustedOffset; }
            preferences.edit().putLong(CLOCK_OFFSET, adjustedOffset).apply();
            main.post(new Runnable() {
                @Override public void run() { dashboard.setTelemetry(telemetry); }
            });
        } catch (Exception retryFailure) {
            clearConnection();
            main.post(new Runnable() {
                @Override public void run() {
                    dashboard.setConnectionMessage("PAIRING REQUIRED", false);
                    dashboard.showMessage("The saved pairing is no longer valid. Pair this display again.");
                }
            });
        }
    }

    private void showOffline() {
        main.post(new Runnable() {
            @Override public void run() { dashboard.setConnectionMessage("PC OFFLINE — RECONNECTING", false); }
        });
    }

    private void showPairingDialog(final DiscoveryClient.Candidate candidate) {
        pairingDialogVisible = true;
        final int padding = dp(22);
        LinearLayout content = new LinearLayout(this);
        content.setOrientation(LinearLayout.VERTICAL);
        content.setPadding(padding, dp(8), padding, 0);

        TextView help = new TextView(this);
        help.setText("Enter the six-digit code shown by Henry Monitor on " + candidate.computerName + ".");
        help.setTextColor(Color.rgb(72, 77, 86));
        help.setTextSize(16);
        help.setPadding(0, 0, 0, dp(16));

        final EditText code = new EditText(this);
        code.setHint("000 000");
        code.setGravity(Gravity.CENTER);
        code.setTextSize(28);
        code.setSingleLine(true);
        code.setInputType(InputType.TYPE_CLASS_NUMBER);

        final TextView error = new TextView(this);
        error.setTextColor(Color.rgb(198, 44, 68));
        error.setTextSize(13);
        error.setPadding(0, dp(10), 0, 0);
        content.addView(help);
        content.addView(code);
        content.addView(error);

        final AlertDialog dialog = new AlertDialog.Builder(this)
                .setTitle("Connect to " + candidate.computerName)
                .setView(content)
                .setPositiveButton("PAIR", null)
                .setNeutralButton("MANUAL IP", null)
                .setNegativeButton("CANCEL", null)
                .create();
        dialog.setOnDismissListener(new DialogInterface.OnDismissListener() {
            @Override public void onDismiss(DialogInterface dialogInterface) { pairingDialogVisible = false; }
        });
        dialog.setOnShowListener(new DialogInterface.OnShowListener() {
            @Override public void onShow(DialogInterface ignored) {
                dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(new View.OnClickListener() {
                    @Override public void onClick(View view) {
                        String entered = code.getText().toString().replace(" ", "").trim();
                        if (entered.length() != 6) {
                            error.setText("Enter all six digits.");
                            return;
                        }
                        error.setText("Connecting…");
                        pairWith(candidate.host, candidate.port, candidate.computerName, entered, dialog, error);
                    }
                });
                dialog.getButton(AlertDialog.BUTTON_NEUTRAL).setOnClickListener(new View.OnClickListener() {
                    @Override public void onClick(View view) {
                        dialog.dismiss();
                        showManualAddressDialog();
                    }
                });
            }
        });
        dialog.show();
        code.requestFocus();
        dialog.getWindow().setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_STATE_ALWAYS_VISIBLE);
    }

    private void pairWith(final String targetHost, final int targetPort, final String name,
                          final String code, final AlertDialog dialog, final TextView error) {
        executor.execute(new Runnable() {
            @Override public void run() {
                try {
                    ApiClient.PairResult result = new ApiClient(targetHost, targetPort, "").pair(code, Build.MODEL);
                    long offset = result.serverTimeUnix > 0
                            ? result.serverTimeUnix - System.currentTimeMillis() / 1000L
                            : 0;
                    synchronized (connectionLock) {
                        host = targetHost;
                        port = result.port;
                        token = result.token;
                        clockOffsetSeconds = offset;
                    }
                    final boolean showBirthday = !result.computerName.equals(
                            preferences.getString(BIRTHDAY_COMPUTER, ""));
                    SharedPreferences.Editor editor = preferences.edit()
                            .putString(HOST, targetHost)
                            .putInt(PORT, result.port)
                            .putString(TOKEN, result.token)
                            .putString(COMPUTER, result.computerName)
                            .putLong(CLOCK_OFFSET, offset);
                    if (showBirthday) editor.putString(BIRTHDAY_COMPUTER, result.computerName);
                    editor.apply();
                    main.post(new Runnable() {
                        @Override public void run() {
                            dialog.dismiss();
                            if (showBirthday) dashboard.showBirthdayWelcome();
                            else dashboard.showMessage("Securely paired with " + name + ".");
                        }
                    });
                } catch (final Exception ex) {
                    main.post(new Runnable() {
                        @Override public void run() { error.setText(ex.getMessage()); }
                    });
                }
            }
        });
    }

    private void showManualAddressDialog() {
        pairingDialogVisible = true;
        final boolean[] continuing = { false };
        final EditText address = new EditText(this);
        address.setHint("192.168.1.25");
        address.setSingleLine(true);
        address.setInputType(InputType.TYPE_CLASS_PHONE);
        int padding = dp(22);
        address.setPadding(padding, dp(10), padding, 0);
        new AlertDialog.Builder(this)
                .setTitle("PC network address")
                .setMessage("Enter the IPv4 address shown in the Windows companion.")
                .setView(address)
                .setPositiveButton("CONTINUE", new DialogInterface.OnClickListener() {
                    @Override public void onClick(DialogInterface dialog, int which) {
                        String value = address.getText().toString().trim();
                        if (!value.isEmpty()) {
                            continuing[0] = true;
                            showPairingDialog(new DiscoveryClient.Candidate(value, 47831, "Henry’s PC"));
                        }
                    }
                })
                .setNegativeButton("CANCEL", null)
                .setOnDismissListener(new DialogInterface.OnDismissListener() {
                    @Override public void onDismiss(DialogInterface dialog) {
                        if (!continuing[0]) pairingDialogVisible = false;
                    }
                })
                .show();
    }

    private void showSearchDialog() {
        if (pairingDialogVisible) return;
        pairingDialogVisible = true;

        final int padding = dp(22);
        LinearLayout content = new LinearLayout(this);
        content.setOrientation(LinearLayout.VERTICAL);
        content.setGravity(Gravity.CENTER_HORIZONTAL);
        content.setPadding(padding, dp(8), padding, 0);

        final ProgressBar progress = new ProgressBar(this);
        LinearLayout.LayoutParams progressParams = new LinearLayout.LayoutParams(dp(36), dp(36));
        progressParams.bottomMargin = dp(14);
        content.addView(progress, progressParams);

        final TextView status = new TextView(this);
        status.setText("Looking for the Windows companion on this Wi-Fi network…");
        status.setTextColor(Color.rgb(72, 77, 86));
        status.setTextSize(15);
        status.setGravity(Gravity.CENTER);
        status.setPadding(0, 0, 0, dp(8));
        content.addView(status);

        final AlertDialog dialog = new AlertDialog.Builder(this)
                .setTitle("Find Henry’s PC")
                .setView(content)
                .setPositiveButton("SEARCH AGAIN", null)
                .setNeutralButton("MANUAL IP", null)
                .setNegativeButton("CANCEL", null)
                .create();
        dialog.setOnDismissListener(new DialogInterface.OnDismissListener() {
            @Override public void onDismiss(DialogInterface ignored) { pairingDialogVisible = false; }
        });
        dialog.setOnShowListener(new DialogInterface.OnShowListener() {
            @Override public void onShow(DialogInterface ignored) {
                dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(new View.OnClickListener() {
                    @Override public void onClick(View view) {
                        searchFromDialog(dialog, status, progress);
                    }
                });
                dialog.getButton(AlertDialog.BUTTON_NEUTRAL).setOnClickListener(new View.OnClickListener() {
                    @Override public void onClick(View view) {
                        dialog.dismiss();
                        main.post(new Runnable() {
                            @Override public void run() { showManualAddressDialog(); }
                        });
                    }
                });
                searchFromDialog(dialog, status, progress);
            }
        });
        dialog.show();
    }

    private void searchFromDialog(final AlertDialog dialog, final TextView status,
                                  final ProgressBar progress) {
        status.setText("Looking for the Windows companion on this Wi-Fi network…");
        progress.setVisibility(View.VISIBLE);
        dialog.getButton(AlertDialog.BUTTON_POSITIVE).setEnabled(false);
        executor.execute(new Runnable() {
            @Override public void run() {
                final DiscoveryClient.Candidate candidate = new DiscoveryClient(MainActivity.this).discover();
                main.post(new Runnable() {
                    @Override public void run() {
                        if (stopped || !dialog.isShowing()) return;
                        progress.setVisibility(View.GONE);
                        dialog.getButton(AlertDialog.BUTTON_POSITIVE).setEnabled(true);
                        if (candidate == null) {
                            status.setText("No PC answered. Open Henry Monitor on Windows, confirm both devices are on the same Wi-Fi, then search again—or use Manual IP.");
                            return;
                        }
                        dialog.dismiss();
                        showPairingDialog(candidate);
                    }
                });
            }
        });
    }

    @Override
    public void requestPairing() {
        clearConnection();
        dashboard.setConnectionMessage("SEARCHING FOR HENRY’S PC", false);
        showSearchDialog();
    }

    @Override
    public void sendControl(final String action, final boolean confirmed) {
        final String currentHost;
        final int currentPort;
        final String currentToken;
        final long currentClockOffset;
        synchronized (connectionLock) {
            currentHost = host;
            currentPort = port;
            currentToken = token;
            currentClockOffset = clockOffsetSeconds;
        }
        if (currentHost == null || currentToken == null) {
            dashboard.showMessage("Connect to the PC before using controls.");
            return;
        }
        executor.execute(new Runnable() {
            @Override public void run() {
                try {
                    final String message = new ApiClient(
                            currentHost, currentPort, currentToken, currentClockOffset).control(action, confirmed);
                    main.post(new Runnable() {
                        @Override public void run() { dashboard.showMessage(message); }
                    });
                } catch (final Exception ex) {
                    main.post(new Runnable() {
                        @Override public void run() { dashboard.showMessage(ex.getMessage()); }
                    });
                }
            }
        });
    }

    @Override
    public void requestPhotosPermission() {
        // Setup grants this silently on device-owner phones; this covers the
        // Home-mode fallback where a one-time system dialog is acceptable.
        if (checkSelfPermission(Manifest.permission.READ_EXTERNAL_STORAGE)
                == PackageManager.PERMISSION_GRANTED) return;
        requestPermissions(new String[] { Manifest.permission.READ_EXTERNAL_STORAGE },
                PHOTOS_PERMISSION_REQUEST);
    }

    @Override
    public void openSettings() {
        final String computer = preferences.getString(COMPUTER, "No PC paired");
        new AlertDialog.Builder(this)
                .setTitle("Henry Monitor settings")
                .setItems(new String[] {
                        "Reconnect or pair a different PC",
                        "Check for app updates",
                        "Exit fullscreen for 60 seconds",
                        "Connection: " + computer
                }, new DialogInterface.OnClickListener() {
                    @Override public void onClick(DialogInterface dialog, int which) {
                        if (which == 0) requestPairing();
                        if (which == 1) checkForUpdate();
                        if (which == 2) temporarilyExitKiosk();
                    }
                })
                .setNegativeButton("CLOSE", null)
                .show();
    }

    private void checkForUpdate() {
        dashboard.showMessage("Checking for updates…");
        UpdateManager.check(this, new UpdateManager.Listener() {
            @Override public void onUpdateStatus(String message) {
                dashboard.showMessage(message);
            }
        });
    }

    private void temporarilyExitKiosk() {
        try { stopLockTask(); } catch (Exception ignored) { }
        getWindow().getDecorView().setSystemUiVisibility(View.SYSTEM_UI_FLAG_VISIBLE);
        dashboard.showMessage("Android controls unlocked for 60 seconds.");
        main.postDelayed(new Runnable() {
            @Override public void run() {
                enterImmersiveMode();
                enableManagedKioskIfAvailable();
            }
        }, 60_000);
    }

    private void enableManagedKioskIfAvailable() {
        DevicePolicyManager manager = (DevicePolicyManager) getSystemService(Context.DEVICE_POLICY_SERVICE);
        if (manager == null || !manager.isDeviceOwnerApp(getPackageName())) return;
        try {
            manager.setLockTaskPackages(new ComponentName(this, MonitorDeviceAdminReceiver.class),
                    new String[] { getPackageName() });
            ActivityManager activityManager = (ActivityManager) getSystemService(Context.ACTIVITY_SERVICE);
            if (activityManager != null && activityManager.getLockTaskModeState() == ActivityManager.LOCK_TASK_MODE_NONE)
                startLockTask();
        } catch (Exception ignored) { }
    }

    private void enterImmersiveMode() {
        getWindow().getDecorView().setSystemUiVisibility(
                View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY |
                View.SYSTEM_UI_FLAG_FULLSCREEN |
                View.SYSTEM_UI_FLAG_HIDE_NAVIGATION |
                View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN |
                View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION |
                View.SYSTEM_UI_FLAG_LAYOUT_STABLE);
    }

    private void loadConnection() {
        synchronized (connectionLock) {
            host = preferences.getString(HOST, null);
            port = preferences.getInt(PORT, 47831);
            token = preferences.getString(TOKEN, null);
            clockOffsetSeconds = preferences.getLong(CLOCK_OFFSET, 0);
        }
    }

    private void clearConnection() {
        synchronized (connectionLock) {
            host = null;
            port = 47831;
            token = null;
            clockOffsetSeconds = 0;
        }
        preferences.edit()
                .remove(HOST)
                .remove(PORT)
                .remove(TOKEN)
                .remove(COMPUTER)
                .remove(CLOCK_OFFSET)
                .apply();
    }

    private int dp(int value) {
        return (int) (value * getResources().getDisplayMetrics().density + 0.5f);
    }
}

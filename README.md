# Henry System Monitor

Henry System Monitor turns a Samsung Galaxy J7 into a dedicated instrument panel for a Windows gaming PC. It is a private, two-part product: a Windows sensor companion and a native Android 8.1 dashboard.

## Install the finished product

1. Put the Galaxy J7 and the Windows PC on the same private Wi-Fi network.
2. On the J7, enable Developer options and USB debugging. Connect it to the PC with a data-capable USB cable and approve the computer.
3. Double-click `HenryMonitorSetup.exe` on the Windows PC.
4. Accept the Windows administrator prompt that adds the private-network firewall rule.
5. When the phone opens Henry Monitor, select the detected PC and enter the six-digit code displayed by the Windows companion.
6. The USB cable can be disconnected after pairing. Leave the phone on a suitable charger for its permanent display use.

The installer adds startup registration, Desktop and Start Menu shortcuts, the Android app, local-network access, and the uninstaller. It does not require .NET, Java, Android Studio, or internet access.

## Automatic updates

Both apps keep themselves current, so an installation on someone else's PC improves on its own:

- The Windows companion checks a small published feed every six hours (first check a couple of minutes after launch) and from its notification-icon menu (`Check for updates`). A newer release is downloaded in the background, verified against its SHA-256 checksum, and swapped in; the monitor restarts through its existing startup task and reappears on its own.
- The phone app checks the same feed a few minutes after boot and at most every six hours, silently installing newer signed builds through Android's package installer (the kiosk phone allows this without prompts). `Henry Monitor settings → Check for app updates` triggers it manually.
- The feed lives at `https://henry-monitor-updates.pages.dev/feed.json` (Cloudflare Pages) and also serves the current `HenryMonitorSetup.exe`-grade artifacts for manual installs.
- A failed or interrupted agent update cannot brick the install: the previous executable is kept as `HenryMonitor.exe.old` and restored automatically if the new image is missing.

Publishing a new version is one command from the development machine: `./scripts/publish-update.sh` builds everything, runs the tests, and deploys the feed. Henry's devices pick it up within six hours.

After installation, open the Windows monitor from the `Henry System Monitor`
Desktop shortcut, the Start menu, or by double-clicking its notification-area
icon. Opening the shortcut restores the existing background window instead of
starting a duplicate monitoring service. Version 1.3.0 uses a privilege-safe
activation signal, so a normal Desktop shortcut can reliably restore the
hardware-enabled background process, and adds the automatic-update system
described above.

Setup creates a highest-privilege Windows logon task after one administrator
approval. This is required because LibreHardwareMonitor documents that some
temperature and fan sensors require administrator access. It prevents the
"memory only" state while avoiding a new UAC prompt at every startup.

One additional administrator prompt installs the official signed PawnIO hardware-access driver used by current LibreHardwareMonitor releases. Setup leaves an existing PawnIO installation untouched, and uninstalling Henry Monitor does not remove it because other sensor applications may share it. If a particular driver or sensor cannot initialize, the companion remains open with Windows memory, storage, network, and uptime data and records the sensor error in its local diagnostic log.

Windows may warn that the installer is from an unknown publisher because this personal gift does not have a commercial code-signing certificate. Verify the SHA-256 checksum in `SHA256SUMS.txt` before running it.

If automatic discovery does not find the PC, tap `SEARCH / PAIR`, then use
`MANUAL IP` and enter the IPv4 address shown by the Windows companion. Version
1.1.0 first tries both broadcast discovery and a bounded local-network scan,
then reports clearly if no PC answers.

## What it displays

- CPU and GPU temperature and utilization
- Memory usage
- System-drive usage
- Live upload and download throughput
- System uptime
- A rolling 100-second thermal graph
- Up to twelve temperature sensors and ten fan sensors

Version 1.1.0 uses a connected, space-efficient instrument-panel layout with
large high-contrast readings, friendly sensor labels, smooth gauge and number
motion, a live thermal trail, temperature-aware accents, and responsive control
feedback. Storage Used is the percentage consumed on the Windows system drive,
normally `C:`.

Unavailable hardware sensors are hidden or shown as unavailable instead of displaying fabricated values. Sensor availability depends on the PC motherboard, GPU, and firmware.

## PC controls

- Lock
- Sleep
- Hibernate
- Restart with a five-second cancellation window
- Shut down with a five-second cancellation window
- Mute and volume adjustment
- Photo screensaver on the phone, fed by the Photos folder in the phone's Download directory

Volume buttons fire once on press and repeat faster the longer they are held.
Power controls still require a deliberate hold, and signed nonces still prevent a
captured request from being replayed.

The phone's Controls screen has a SCREENSAVER button in place of the old cancel
action; starting it shows a full-screen slideshow of the pictures in
`Download/Photos` on the phone, and touching the screen returns to the dashboard.

Sleep, hibernate, restart, and shutdown require a 1.6-second hold on the phone. Every request is signed with a paired 256-bit key, timestamp, and one-use nonce. Replayed or modified control requests are rejected. The service binds only to the local machine and local network; it has no cloud component.

## Kiosk modes

The installer always configures fullscreen Home mode: Henry Monitor becomes the phone's Home surface, hides Android controls, stays awake while powered, and starts at boot.

Android's stronger locked kiosk mode requires a clean device with no Samsung or Google accounts. Once the old Samsung account is recovered:

1. Remove both the Samsung and every Google account through Android Settings.
2. Factory-reset from inside Android Settings, not with recovery buttons.
3. During initial setup, do not add an account.
4. Enable Developer options and USB debugging again.
5. Re-run `HenryMonitorSetup.exe`.

Setup will then enroll Henry Monitor as the device owner. The menu in the upper-right corner has a 60-second maintenance escape.

## Safety and maintenance

- Do not leave an old battery continuously powered if the phone swells, separates at the back, smells unusual, or becomes hot.
- Keep the device ventilated and use a sound charger and cable.
- The light interface uses subtle movement and graphs, but any static OLED display can eventually develop burn-in. Reduce brightness to the lowest comfortable level.
- To uninstall, use Windows Settings → Apps → Henry System Monitor. If the phone is connected and authorized, the uninstaller removes the Android app too.

## Source layout

- `android/` — native Java dashboard for Android 8.1/API 27
- `windows/HenryMonitor.Agent/` — sensor service, authenticated API, discovery service, tray UI, and self-update service
- `windows/HenryMonitor.Setup/` — guided self-contained installer
- `android/.../UpdateManager.java` — phone-side self-update via PackageInstaller
- `tests/` — pairing and request-authentication tests
- `scripts/` — reproducible build helpers

See `BUILDING.md` for toolchain details and `SECURITY.md` for the trust model.

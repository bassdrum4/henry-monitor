# Building from source

The checked release targets Windows 10/11 x64 and Android 8.1/API 27.

## Required tools

- .NET SDK 8 or newer with Windows desktop targeting support
- JDK 17
- Android SDK Platform 28 and Android Build Tools 34.0.0, or the Debian
  Platform 23/build-tools packages used by the fallback build path
- `zip`
- Windows Android Platform-Tools payload: `adb.exe`, `AdbWinApi.dll`, and `AdbWinUsbApi.dll`

Set `ANDROID_SDK_ROOT`, `JAVA_HOME`, and optionally `DOTNET`. Place the three Windows ADB files in `windows/HenryMonitor.Setup/Payload` before building Setup.

## Android

Run:

```bash
./scripts/build-android.sh
```

The script deliberately uses the Android command-line tools rather than Gradle, keeping the application dependency-free. It compiles resources, Java bytecode, DEX, alignment, and APK signatures. Updates must use the same signing key in `signing/henry-monitor.keystore`.

## Windows and installer

Run:

```bash
./scripts/build-release.sh
```

This builds and tests the Windows companion, publishes a compressed self-contained Windows executable, refreshes the installer's embedded payload, and publishes the final guided installer.

## Publishing an automatic update

Run:

```bash
./scripts/publish-update.sh
```

This rebuilds the Android app and Windows agent, runs the protocol tests, chunks the self-contained agent executable into 20 MiB parts (static-host per-file limit), writes `feed.json` with versions, sizes, and SHA-256 digests, and deploys everything to the `henry-monitor-updates` Cloudflare Pages project with `wrangler`. Installed agents and phone apps poll `https://henry-monitor-updates.pages.dev/feed.json` and update themselves.

Bump versions first: `<Version>` in both Windows `.csproj` files, `DisplayVersion` in `windows/HenryMonitor.Setup/Installer.cs`, and `android:versionCode` / `android:versionName` in `android/AndroidManifest.xml` (`versionCode` must also be updated in `scripts/publish-update.sh`). Updates only install when the feed version is strictly newer.

## Verification

The build must complete all of these checks:

- Android Java compilation against API 28 (API 23 is accepted by the fallback
  builder because the application itself still declares minimum API 27)
- D8 conversion with minimum API 27, or compatible DX conversion
- APK v2/v3 signature verification
- Windows companion compilation with no warnings or errors
- Pair-code rotation and request authentication tests
- Windows Setup compilation with no warnings or errors
- SHA-256 digest generation

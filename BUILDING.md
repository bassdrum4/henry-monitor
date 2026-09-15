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

This rebuilds the Android app and Windows agent, runs the protocol tests, writes `feed.json` with versions, sizes, and SHA-256 digests, and publishes everything (`HenryMonitor.exe`, `HenryMonitor.apk`, `HenryMonitorSetup.exe`, `feed.json`, `SHA256SUMS.txt`) as assets on a draft release `v<version>` in `bassdrum4/henry-monitor`, then flips it published. Installed agents and phone apps poll `https://github.com/bassdrum4/henry-monitor/releases/latest/download/feed.json` and update themselves. Re-running the script for an existing version replaces that release's assets idempotently; use `RELEASE_NOTES="..." ./scripts/publish-update.sh` to set the release notes.

Machine-provided Setup payload binaries (the adb platform tools and `HenryPhotos.exe`) are fetched automatically from the repo's pinned `payload` release by `scripts/fetch-payload.sh`; `PawnIO_setup.exe` comes from the official `namazso/PawnIO.Setup` releases. Pushing a `v*` tag — or running the `publish` workflow manually from GitHub's Actions tab — runs the same pipeline on CI, signing the Android app with the repository's `HM_KEYSTORE_B64` / `HM_KEYSTORE_PASSWORD` secrets.

Bump versions first: `<Version>` in both Windows `.csproj` files, `DisplayVersion` in `windows/HenryMonitor.Setup/Installer.cs`, and `android:versionCode` / `android:versionName` in `android/AndroidManifest.xml`. Updates only install when the feed version is strictly newer.

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

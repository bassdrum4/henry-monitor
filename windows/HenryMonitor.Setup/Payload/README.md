# Installer payload

The release build script places the compiled `HenryMonitor.exe` and `HenryMonitor.apk` here.

For a clean source rebuild, also place these unmodified official files in this directory:

- `adb.exe`
- `AdbWinApi.dll`
- `AdbWinUsbApi.dll`
- `PawnIO_setup.exe` version 2.2.0

The first three come from Google's Windows SDK Platform-Tools archive. PawnIO Setup comes from the official `namazso/PawnIO.Setup` GitHub release; its expected SHA-256 is `1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032`.

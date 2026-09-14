#!/usr/bin/env bash
set -euo pipefail

project_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet_bin="${DOTNET:-dotnet}"
build_id="$(date +%Y%m%d-%H%M%S)-$$"
agent_output="$project_root/build/windows-agent-$build_id"
setup_output="$project_root/build/windows-setup-$build_id"
payload="$project_root/windows/HenryMonitor.Setup/Payload"
dist="$project_root/dist"

"$project_root/scripts/build-android.sh"
mkdir -p "$agent_output" "$setup_output" "$payload" "$dist"

"$dotnet_bin" build "$project_root/windows/HenryMonitor.Agent/HenryMonitor.Agent.csproj" \
  -c Release -r win-x64
DOTNET_ROLL_FORWARD=Major "$dotnet_bin" run \
  --project "$project_root/tests/HenryMonitor.ProtocolTests/HenryMonitor.ProtocolTests.csproj" -c Release
"$dotnet_bin" publish "$project_root/windows/HenryMonitor.Agent/HenryMonitor.Agent.csproj" \
  -c Release -r win-x64 --self-contained true -o "$agent_output"

cp "$agent_output/HenryMonitor.exe" "$payload/HenryMonitor.exe"
cp "$dist/HenryMonitor.apk" "$payload/HenryMonitor.apk"

for adb_file in adb.exe AdbWinApi.dll AdbWinUsbApi.dll PawnIO_setup.exe; do
  if [[ ! -f "$payload/$adb_file" ]]; then
    echo "Place the Windows Platform-Tools file $adb_file in $payload" >&2
    exit 1
  fi
done

"$dotnet_bin" build "$project_root/windows/HenryMonitor.Setup/HenryMonitor.Setup.csproj" \
  -c Release -r win-x64
"$dotnet_bin" publish "$project_root/windows/HenryMonitor.Setup/HenryMonitor.Setup.csproj" \
  -c Release -r win-x64 --self-contained true -o "$setup_output"

cp "$setup_output/HenryMonitorSetup.exe" "$dist/HenryMonitorSetup.exe"
(cd "$dist" && sha256sum HenryMonitorSetup.exe HenryMonitor.apk > SHA256SUMS.txt)
echo "Release ready in $dist"

#!/usr/bin/env bash
set -euo pipefail

# Install the Android SDK pieces scripts/build-android-windows.sh requires.
# Used by CI (.github/workflows/publish.yml); on a local machine Android
# Studio normally provides all of this already.

sdk="${ANDROID_SDK_ROOT:-${ANDROID_HOME:-}}"
if [[ -z "$sdk" ]]; then
  echo "Set ANDROID_SDK_ROOT or ANDROID_HOME to the Android SDK location." >&2
  exit 1
fi
# Runner env vars arrive as Windows paths; normalize for bash file tests.
if command -v cygpath >/dev/null 2>&1 && [[ "$sdk" =~ ^[A-Za-z]: ]]; then
  sdk="$(cygpath -u "$sdk")"
fi
export ANDROID_HOME="$(cygpath -w "$sdk" 2>/dev/null || echo "$sdk")"

# Find sdkmanager: not on PATH on fresh Windows runners.
sdkmanager=""
for candidate in \
  "$(command -v sdkmanager 2>/dev/null || true)" \
  "$sdk/cmdline-tools/latest/bin/sdkmanager.bat" \
  "$sdk"/cmdline-tools/*/bin/sdkmanager.bat; do
  [[ -n "$candidate" && -f "$candidate" ]] && { sdkmanager="$candidate"; break; }
done
[[ -n "$sdkmanager" ]] || {
  echo "sdkmanager.bat not found under $sdk (install Android commandline-tools)." >&2
  exit 1
}

yes | "$sdkmanager" --licenses > /dev/null
"$sdkmanager" --install "platform-tools" "platforms;android-28" "build-tools;34.0.0" "build-tools;36.0.0" > /dev/null

for required in "build-tools/34.0.0/aapt2.exe" "build-tools/34.0.0/zipalign.exe" \
                "build-tools/34.0.0/lib/apksigner.jar" "build-tools/36.0.0/lib/d8.jar" \
                "platforms/android-28/android.jar"; do
  [[ -f "$sdk/$required" ]] || { echo "Missing after install: $sdk/$required" >&2; exit 1; }
done
echo "Android SDK prerequisites ready in $sdk"

#!/usr/bin/env bash
set -euo pipefail

# Install the Android SDK pieces scripts/build-android-windows.sh requires.
# Used by CI (.github/workflows/publish.yml); on a local machine Android
# Studio normally provides all of this already.

sdk="${ANDROID_SDK_ROOT:-${ANDROID_HOME:-/usr/local/lib/android/sdk}}"
export ANDROID_HOME="$sdk"

command -v sdkmanager >/dev/null 2>&1 \
  || { echo "sdkmanager not found on PATH; install Android commandline-tools first." >&2; exit 1; }

yes | sdkmanager --licenses > /dev/null
sdkmanager --install "platform-tools" "platforms;android-28" "build-tools;34.0.0" "build-tools;36.0.0" > /dev/null

for required in "build-tools/34.0.0/aapt2.exe" "build-tools/34.0.0/zipalign.exe" \
                "build-tools/34.0.0/lib/apksigner.jar" "build-tools/36.0.0/lib/d8.jar" \
                "platforms/android-28/android.jar"; do
  [[ -f "$sdk/$required" ]] || { echo "Missing after install: $sdk/$required" >&2; exit 1; }
done
echo "Android SDK prerequisites ready in $sdk"

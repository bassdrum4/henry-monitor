#!/usr/bin/env bash
set -euo pipefail

project_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
android_sdk="${ANDROID_SDK_ROOT:-${ANDROID_HOME:-}}"
java_root="${JAVA_HOME:-}"

if [[ -z "$android_sdk" || -z "$java_root" ]]; then
  echo "Set ANDROID_SDK_ROOT and JAVA_HOME before building." >&2
  exit 1
fi

build_tools="$android_sdk/build-tools/34.0.0"
[[ -x "$build_tools/aapt2" ]] || build_tools="$android_sdk/build-tools/debian"
android_jar="$android_sdk/platforms/android-28/android.jar"
[[ -f "$android_jar" ]] || android_jar="$android_sdk/platforms/android-23/android.jar"
for required in "$build_tools/aapt2" "$build_tools/zipalign" "$build_tools/apksigner" "$android_jar" "$java_root/bin/java"; do
  if [[ ! -e "$required" ]]; then
    echo "Missing required build tool: $required" >&2
    exit 1
  fi
done

if [[ -x "$java_root/bin/javac" ]]; then
  javac_command=("$java_root/bin/javac")
else
  # Some compact JDK images include the compiler module without the javac launcher.
  javac_command=("$java_root/bin/java" -m jdk.compiler/com.sun.tools.javac.Main)
fi

build_id="$(date +%Y%m%d-%H%M%S)-$$"
working="$project_root/build/android-$build_id"
output="$project_root/dist"
classes="$working/classes"
dex="$working/dex"
keystore="$project_root/signing/henry-monitor.keystore"
password="$(tr -d '\r\n' < "$project_root/signing/password.txt")"
mkdir -p "$classes" "$dex" "$output"

"$build_tools/aapt2" compile --dir "$project_root/android/res" -o "$working/resources.zip"
"$build_tools/aapt2" link -o "$working/app-unsigned.apk" \
  --manifest "$project_root/android/AndroidManifest.xml" \
  -I "$android_jar" "$working/resources.zip"

find "$project_root/android/src" -name '*.java' -print0 | \
  xargs -0 "${javac_command[@]}" -source 8 -target 8 -bootclasspath "$android_jar" -d "$classes"

if [[ -x "$build_tools/d8" ]]; then
  JAVA_HOME="$java_root" "$build_tools/d8" --min-api 27 --lib "$android_jar" \
    --output "$dex" $(find "$classes" -name '*.class')
elif [[ -x "$build_tools/dx" ]]; then
  JAVA_HOME="$java_root" "$build_tools/dx" --dex --output="$dex/classes.dex" "$classes"
else
  echo "Missing required DEX compiler: d8 or dx" >&2
  exit 1
fi

cp "$working/app-unsigned.apk" "$working/app-with-dex.apk"
(cd "$dex" && zip -q -j "$working/app-with-dex.apk" classes.dex)
"$build_tools/zipalign" -f 4 "$working/app-with-dex.apk" "$working/app-aligned.apk"
JAVA_HOME="$java_root" "$build_tools/apksigner" sign \
  --ks "$keystore" --ks-pass "pass:$password" --key-pass "pass:$password" \
  --out "$output/HenryMonitor.apk" "$working/app-aligned.apk"
JAVA_HOME="$java_root" "$build_tools/apksigner" verify --verbose "$output/HenryMonitor.apk"

echo "Built $output/HenryMonitor.apk"

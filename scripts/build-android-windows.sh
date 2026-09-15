#!/usr/bin/env bash
set -euo pipefail

# Windows-native variant of build-android.sh for machines without WSL.
project_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
android_sdk="${ANDROID_SDK_ROOT:-${ANDROID_HOME:-/c/Users/Lowe/AppData/Local/Android/Sdk}}"
java_root="${JAVA_HOME:-/c/Program Files/Android/Android Studio/jbr}"

build_tools="$android_sdk/build-tools/34.0.0"
# d8 in 34.0.0 is a broken dev build; use a newer one for dexing.
d8_tools="$android_sdk/build-tools/36.0.0"
android_jar="$android_sdk/platforms/android-34/android.jar"
# FLAG_MUTABLE (API 31) and friends need a modern compile platform; fall back
# to android-28 only on machines that never installed a newer one.
[[ -f "$android_jar" ]] || android_jar="$android_sdk/platforms/android-28/android.jar"
for required in "$build_tools/aapt2.exe" "$build_tools/zipalign.exe" "$build_tools/lib/apksigner.jar" \
                "$d8_tools/lib/d8.jar" "$android_jar" "$java_root/bin/java.exe" "$java_root/bin/javac.exe"; do
  if [[ ! -e "$required" ]]; then
    echo "Missing required build tool: $required" >&2
    exit 1
  fi
done

build_id="$(date +%Y%m%d-%H%M%S)-$$"
working="$project_root/build/android-$build_id"
output="$project_root/dist"
classes="$working/classes"
dex="$working/dex"
keystore="$project_root/signing/henry-monitor.keystore"
password="$(tr -d '\r\n' < "$project_root/signing/password.txt")"
mkdir -p "$classes" "$dex" "$output"

"$build_tools/aapt2.exe" compile --dir "$project_root/android/res" -o "$working/resources.zip"
"$build_tools/aapt2.exe" link -o "$working/app-unsigned.apk" \
  --manifest "$project_root/android/AndroidManifest.xml" \
  -I "$android_jar" "$working/resources.zip"

find "$project_root/android/src" -name '*.java' -print0 | \
  xargs -0 "$java_root/bin/javac.exe" -source 8 -target 8 -encoding UTF-8 \
  -bootclasspath "$android_jar" -d "$classes"

# d8 needs Windows-style paths when invoked directly through java.
classes_win="$(cygpath -w "$classes")"
android_jar_win="$(cygpath -w "$android_jar")"
dex_win="$(cygpath -w "$dex")"
class_files="$(find "$classes" -name '*.class' | while read -r f; do cygpath -w "$f"; done)"
JAVA_HOME="$(cygpath -w "$java_root")" "$java_root/bin/java.exe" \
  -cp "$(cygpath -w "$d8_tools/lib/d8.jar")" com.android.tools.r8.D8 \
  --min-api 27 --lib "$android_jar_win" --output "$dex_win" $class_files

cp "$working/app-unsigned.apk" "$working/app-with-dex.apk"
python - "$dex/classes.dex" "$working/app-with-dex.apk" <<'PY'
import sys, zipfile
dex, apk = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(apk, 'a', zipfile.ZIP_DEFLATED) as archive:
    archive.write(dex, 'classes.dex')
PY

"$build_tools/zipalign.exe" -f 4 "$working/app-with-dex.apk" "$working/app-aligned.apk"
"$java_root/bin/java.exe" -jar "$build_tools/lib/apksigner.jar" sign \
  --ks "$keystore" --ks-pass "pass:$password" --key-pass "pass:$password" \
  --out "$output/HenryMonitor.apk" "$working/app-aligned.apk"
"$java_root/bin/java.exe" -jar "$build_tools/lib/apksigner.jar" verify "$output/HenryMonitor.apk"

echo "Built $output/HenryMonitor.apk"

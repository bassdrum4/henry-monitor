#!/usr/bin/env bash
set -euo pipefail

# Build a release and publish it to the project's public GitHub releases
# (bassdrum4/henry-monitor). Installed agents and phone apps poll
# https://github.com/bassdrum4/henry-monitor/releases/latest/download/feed.json
# and update themselves from the newest published release.

project_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dist="$project_root/dist"
staging="$project_root/build/update-release"
repo="bassdrum4/henry-monitor"

agent_version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' \
  "$project_root/windows/HenryMonitor.Agent/HenryMonitor.Agent.csproj" | head -1)"
version_code="$(sed -n 's:.*android:versionCode="\([0-9]*\)".*:\1:p' \
  "$project_root/android/AndroidManifest.xml")"
[[ -n "$version_code" ]] || { echo "Could not read android:versionCode" >&2; exit 1; }
tag="v$agent_version"

token="$(printf 'protocol=https\nhost=github.com\n' | \
  GIT_TERMINAL_PROMPT=0 GCM_INTERACTIVE=never git credential fill 2>/dev/null | \
  sed -n 's/^password=//p')"
[[ -n "$token" ]] || { echo "No stored GitHub credentials found (git credential fill)." >&2; exit 1; }

api() { curl -s -H "Authorization: token $token" -H "Accept: application/vnd.github+json" "$@"; }

echo "== Building Android app =="
"$project_root/scripts/build-android-windows.sh"

echo "== Building Windows agent $agent_version =="
agent_out="$project_root/build/update-agent"
dotnet publish "$project_root/windows/HenryMonitor.Agent/HenryMonitor.Agent.csproj" \
  -c Release -r win-x64 --self-contained true -o "$agent_out"

echo "== Building Setup installer =="
mkdir -p "$project_root/windows/HenryMonitor.Setup/Payload"
cp "$agent_out/HenryMonitor.exe" "$project_root/windows/HenryMonitor.Setup/Payload/HenryMonitor.exe"
cp "$dist/HenryMonitor.apk" "$project_root/windows/HenryMonitor.Setup/Payload/HenryMonitor.apk"
setup_out="$project_root/build/update-setup"
dotnet publish "$project_root/windows/HenryMonitor.Setup/HenryMonitor.Setup.csproj" \
  -c Release -r win-x64 --self-contained true -o "$setup_out"
cp "$setup_out/HenryMonitorSetup.exe" "$dist/HenryMonitorSetup.exe"

echo "== Running protocol tests =="
DOTNET_ROLL_FORWARD=Major dotnet run \
  --project "$project_root/tests/HenryMonitor.ProtocolTests/HenryMonitor.ProtocolTests.csproj" -c Release

echo "== Staging release $tag =="
rm -rf "$staging"; mkdir -p "$staging"
cp "$agent_out/HenryMonitor.exe" "$staging/HenryMonitor.exe"
cp "$dist/HenryMonitor.apk" "$staging/HenryMonitor.apk"
cp "$dist/HenryMonitorSetup.exe" "$staging/HenryMonitorSetup.exe"

agent_size="$(stat -c %s "$staging/HenryMonitor.exe" 2>/dev/null || stat -f %z "$staging/HenryMonitor.exe")"
apk_size="$(stat -c %s "$staging/HenryMonitor.apk" 2>/dev/null || stat -f %z "$staging/HenryMonitor.apk")"
setup_size="$(stat -c %s "$staging/HenryMonitorSetup.exe" 2>/dev/null || stat -f %z "$staging/HenryMonitorSetup.exe")"
agent_sha="$(sha256sum "$staging/HenryMonitor.exe" | awk '{print $1}')"
apk_sha="$(sha256sum "$staging/HenryMonitor.apk" | awk '{print $1}')"
setup_sha="$(sha256sum "$staging/HenryMonitorSetup.exe" | awk '{print $1}')"

notes="${RELEASE_NOTES:-Agent and phone automatic self-updates via GitHub releases.}"
published="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
notes_json="$(python - "$notes" <<'PY'
import json, sys
print(json.dumps(sys.argv[1]))
PY
)"

cat > "$staging/feed.json" <<EOF
{
  "channel": "stable",
  "publishedUtc": "$published",
  "agent": {
    "version": "$agent_version",
    "versionCode": 0,
    "path": "HenryMonitor.exe",
    "size": $agent_size,
    "sha256": "$agent_sha",
    "releaseNotes": $notes_json
  },
  "android": {
    "version": "$agent_version",
    "versionCode": $version_code,
    "path": "HenryMonitor.apk",
    "size": $apk_size,
    "sha256": "$apk_sha",
    "releaseNotes": $notes_json
  }
}
EOF

printf '%s  %s\n' "$agent_sha" HenryMonitor.exe "$apk_sha" HenryMonitor.apk \
  "$setup_sha" HenryMonitorSetup.exe > "$staging/SHA256SUMS.txt"
cp "$staging/SHA256SUMS.txt" "$dist/SHA256SUMS.txt"

echo "== Preparing GitHub release $tag =="
release_json="$(api "https://api.github.com/repos/$repo/releases/tags/$tag")"
release_id="$(printf '%s' "$release_json" | python -c "
import json, sys
try:
    data = json.load(sys.stdin)
    print(data.get('id', ''))
except Exception:
    print('')
")"

if [[ -z "$release_id" ]]; then
  echo "   creating $tag"
  release_id="$(api -X POST "https://api.github.com/repos/$repo/releases" \
    -d "{\"tag_name\":\"$tag\",\"target_commitish\":\"main\",\"name\":\"Henry System Monitor $agent_version\",\"body\":$notes_json,\"draft\":true,\"prerelease\":false}" \
    | python -c "import json,sys; print(json.load(sys.stdin).get('id',''))")"
  [[ -n "$release_id" ]] || { echo "Could not create the release (is $repo pushed?)" >&2; exit 1; }
else
  echo "   updating existing $tag (id $release_id)"
  api -X PATCH "https://api.github.com/repos/$repo/releases/$release_id" \
    -d "{\"name\":\"Henry System Monitor $agent_version\",\"body\":$notes_json,\"draft\":true}" > /dev/null
fi

# Replace same-name assets so republishing a version stays idempotent.
while IFS= read -r asset_id; do
  [[ -n "$asset_id" ]] && api -X DELETE "https://api.github.com/repos/$repo/releases/assets/$asset_id" > /dev/null
done < <(printf '%s' "$release_json" | python -c "
import json, sys
try:
    for asset in json.load(sys.stdin).get('assets', []):
        print(asset['id'])
except Exception:
    pass
")

echo "== Uploading assets =="
for file in HenryMonitor.exe HenryMonitor.apk HenryMonitorSetup.exe SHA256SUMS.txt feed.json; do
  printf '   %s (%s bytes) ' "$file" "$(stat -c %s "$staging/$file" 2>/dev/null || stat -f %z "$staging/$file")"
  code="$(curl -s -o /tmp/asset-upload.json -w '%{http_code}' \
    -X POST -H "Authorization: token $token" \
    -H 'Content-Type: application/octet-stream' \
    --data-binary "@$staging/$file" \
    "https://uploads.github.com/repos/$repo/releases/$release_id/assets?name=$file")"
  [[ "$code" == 201 ]] && echo "ok" || { echo "FAILED (HTTP $code)"; cat /tmp/asset-upload.json; exit 1; }
done

# Publishing last makes releases/latest flip over atomically with assets in place.
api -X PATCH "https://api.github.com/repos/$repo/releases/$release_id" -d '{"draft":false}' > /dev/null

echo ""
echo "Published $agent_version as $tag:"
echo "  feed:  https://github.com/$repo/releases/latest/download/feed.json"
echo "  agent: $agent_size bytes (sha256 $agent_sha)"
echo "  apk:   $apk_size bytes, versionCode $version_code (sha256 $apk_sha)"
echo "  setup: $dist/HenryMonitorSetup.exe"

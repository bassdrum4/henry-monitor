#!/usr/bin/env bash
set -euo pipefail

# Fill windows/HenryMonitor.Setup/Payload with the machine-provided binaries
# (adb platform tools, the PawnIO driver installer, HenryPhotos) by fetching
# whatever is missing from the repo's pinned "payload" release. A fresh clone
# or a CI runner needs no hand-placed files once that release exists; version
# publishes never have to touch this.

project_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
payload="$project_root/windows/HenryMonitor.Setup/Payload"
repo="bassdrum4/henry-monitor"

mkdir -p "$payload"

missing=()
for file in adb.exe AdbWinApi.dll AdbWinUsbApi.dll PawnIO_setup.exe HenryPhotos.exe; do
  [[ -f "$payload/$file" ]] || missing+=("$file")
done
if [[ ${#missing[@]} -eq 0 ]]; then
  echo "Payload already complete."
  exit 0
fi

token="${GITHUB_TOKEN:-$(printf 'protocol=https\nhost=github.com\n' | \
  GIT_TERMINAL_PROMPT=0 GCM_INTERACTIVE=never git credential fill 2>/dev/null | \
  sed -n 's/^password=//p')}"

echo "Fetching ${missing[*]} from the $repo payload release"
for file in "${missing[@]}"; do
  case "$file" in
    adb.exe|AdbWinApi.dll|AdbWinUsbApi.dll)
      url="https://github.com/$repo/releases/download/payload/$file" ;;
    PawnIO_setup.exe)
      url="https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe" ;;
    HenryPhotos.exe)
      url="https://github.com/$repo/releases/download/payload/HenryPhotos.exe" ;;
    *)
      echo "No known source for $file" >&2; exit 1 ;;
  esac
  # The repo is public, so try anonymous first; retry with a token if needed.
  if curl -sfL -o "$payload/$file" "$url" \
     || { [[ -n "$token" ]] && curl -sfL -H "Authorization: token $token" -o "$payload/$file" "$url"; }; then
    echo "   $file"
  else
    rm -f "$payload/$file"
    echo "Could not download $file." >&2
    echo "Create it once on GitHub with: RELEASE_NAME=payload scripts/publish-payload.sh" >&2
    echo "or place the file manually in $payload" >&2
    exit 1
  fi
done
echo "Payload ready."

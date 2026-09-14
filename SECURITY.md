# Security model

Henry System Monitor is designed for one trusted phone and one Windows PC on a private home network.

## Protections

- Pairing requires a random six-digit code displayed physically on the PC and expires after fifteen minutes.
- Pairing is limited to eight failed attempts per client per minute.
- Successful pairing provisions a random 256-bit secret.
- The secret is never sent again as an authorization header. Each request uses HMAC-SHA-256 over the HTTP method, route, current timestamp, unique nonce, and requested action.
- Requests outside a sixty-second clock window, altered actions, invalid signatures, and reused nonces are rejected.
- Power actions require a distinct confirmation value produced only after the Android hold interaction.
- The control service uses a fixed action allowlist. It cannot execute caller-provided programs or command arguments.
- There is no cloud server, analytics, advertising, or account on the local network.

## Automatic updates

The Windows agent and Android app poll one static update feed on Cloudflare Pages (`https://henry-monitor-updates.pages.dev/feed.json`).

- Updates are downloaded over HTTPS only and verified against the SHA-256 digest recorded in the feed before anything is installed. A checksum mismatch aborts and changes nothing.
- The feed is static content; it carries no code and no secrets. Compromising it can only serve a doctored build, which still must match the feed's own digest — and both apps compare versions so a feed cannot downgrade an install.
- The agent keeps its previous executable as `HenryMonitor.exe.old` and restores it automatically if a swap leaves the install unusable.
- The feed URL is compiled into both apps. To retire or relocate the feed, publish a final release that changes nothing but the URL handling, or reinstall from a newer Setup.

## Boundaries

Telemetry is transported as local HTTP. It is not confidential from an attacker already capable of passively observing the home Wi-Fi, but control messages cannot be modified or replayed. Do not expose TCP port 47831 or UDP port 47832 through a router, port-forwarding rule, public Wi-Fi, or an untrusted network.

The access key is stored per Windows user under `%LOCALAPPDATA%\HenryMonitor` and in the Android application's private storage. Reset Pairing in the Windows companion immediately invalidates the phone's key.

This personal build is not Authenticode-signed. Its release checksum is supplied separately.

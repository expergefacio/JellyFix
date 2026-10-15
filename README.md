# JellyFix 0.3.0 — Jellyfin 12.2

Plugin with some extra settings for JellyFin:

- subtitle selection priority 
- transcoding limits
- fixed browser title

## Install or upgrade

Extract this ZIP on your Linuxserver Jellyfin Docker host. From the extracted `jellyfix` folder run:

```bash
bash install.sh jellyfin
```

The installer copies the compiled plugin into `/config/data/plugins/JellyFix_0.3.0.0`, archives previous Subtitle Priority/JellyFix versions outside the plugins directory, and restarts Jellyfin. Active playback is interrupted. No .NET SDK is required on the server. It preserves the shared plugin-configuration directory.

Open **Dashboard → Plugins → My Plugins → JellyFix** to access the settings page. If the old dashboard is already open, reload it first.

## Settings

| Control | Default | Behaviour |
| --- | --- | --- |
| Priority 1 — language | English | First available subtitle language. |
| Priority 2 — language | Norwegian | Used when the first language is unavailable. |
| Priority 3 — fallback | Any subtitle | Choose either Any subtitle or No subtitles when both languages are absent. |
| Maximum resolution | 720p | 240p, 360p, 480p, 720p, 1080p, 1440p, 2160p, or No limit. |
| Maximum bitrate (Mbps) | 1.2 | Combined video/audio budget. Decimal input, including a decimal comma, is accepted. Enter 0 for no bitrate limit; otherwise 0.2–1000 Mbps. |
| Override title tag | Empty | Optional fixed browser title. Empty keeps Jellyfin's normal titles. |

Click Save. Subtitle/transcoding changes apply to new requests without a server restart. Reload the web client after changing the title override.

The language dropdowns load the server's culture/language list. They retain saved selections if that list is temporarily unavailable. Standard and hearing-impaired subtitles are equally eligible. Full subtitles precede forced tracks within each language priority. Generic Norwegian includes Bokmål and Nynorsk; selecting either variant specifically keeps it distinct. Common ISO two-letter, three-letter and bibliographic aliases are matched.

## Subtitle selection

Applies to users whose Jellyfin subtitle mode is **Always play**, regardless of audio language. Embedded and external subtitles are both eligible. With no subtitles available, subtitles remain off. With fallback set to No subtitles, unrelated languages remain off. Explicit choices sent by a client, including subtitles-off, still take priority so manual changes keep working. Disable remembered subtitle selections while testing.

Selection occurs before device-specific playback setup, without timed post-start switching. Advert-only subtitles are also eligible under Any subtitle; remove those files if unwanted.

## Transcoding limits

Only ordinary library-video requests which Jellyfin identifies as re-encoding video acquire these limits. Static/direct playback, video-copy/remux, audio-only conversion and audio-only playback remain unrestricted. Limits apply on LAN and remotely. Lower requested limits and smaller sources stay lower; aspect ratio is preserved and there is no upscaling.

The combined bitrate allocates up to 128 kbps to audio and the remainder to video. The default is normally 1.072 Mbps video plus 128 kbps audio. Without audio, video can use the full budget. Audio is encoded within the budget when needed, using a compatible requested codec or the normal codec for the container. Lower audio requests are retained. With bitrate set to 0, JellyFix does not impose an audio bitrate or codec change.

Jellyfin's own user permissions and encoder rate-control behaviour still apply. Forbidding audio transcoding can prevent a combined budget being met when the original audio must be copied. This is an encoded bitrate budget, not a traffic shaper: muxing overhead, buffers and segment-download bursts can exceed it briefly.

Live/open/infinite sources and legacy packed Params requests are outside the first transcoding adapter. If a request cannot be evaluated, JellyFix logs a warning and leaves Jellyfin's handling in place. Normal capped requests log `JellyFix transcode ceiling`, without playback URLs or tokens.

For verification, play compatible high-resolution content normally and confirm Direct Play. Then start a fresh video transcode (e.g. choose a client quality below the source) and inspect the playback dashboard or FFmpeg log for the configured bounds. Jellyfin may select a resolution below the maximum at low bitrates.

## Browser title override

Enter a short fixed title such as `Media`, save, and reload Jellyfin. The server-hosted web index uses this title immediately and loads an early script that keeps it fixed while navigating, including when the app changes document.title or replaces the title element. New history entries use that title in the browser checks.

This changes titles, not URLs, and does not erase old history. It applies to the web client served by this Jellyfin server; native apps and separately hosted web clients are unaffected. Blank disables it after reloading. The title is visible to visitors just like any site title; do not put a secret in it.

The server injects a same-origin external script into its small HTML index response. It does not edit the installed Jellyfin web files, change CSP, or buffer video/subtitle responses. Title text is escaped for both HTML and JavaScript. Root and base-path hosting are covered by tests.

## Configuration and rollback

Settings are persisted using Jellyfin's native plugin configuration at `/config/data/plugins/configurations/Jellyfin.Plugin.JellyFix.xml` in the Linuxserver image. Upgrades keep this file. There is no separate database.

To remove the current plugin from loading:

```bash
bash uninstall.sh jellyfin
```

This archives only the current plugin folder and restarts Jellyfin. Configuration is retained. To restore a prior build, stop Jellyfin, move the desired archived plugin folder back under `data/plugins` in the host `/config` volume, remove its timestamp suffix, and restart. Never restore the broken 0.1.0 prototype.

## Validation and source

- 97 C# checks pass against official Jellyfin.Controller 12.2.0 and the actual playback/encoding helper code. Covers existing subtitle/encoding behaviour, plugin metadata, XML persistence/reload, configured priorities, validation, new limits without restart, HTTP title endpoint and index middleware, escaping and stream bypass.
- 16 Chromium checks pass for dropdown loading/saving, third-priority choices, decimal-comma input, validation, unlimited/blank values, title setters, DOM mutation/replacement, and browser navigation history titles. Dashboard API calls are mocked in the browser form checks; server-side persistence and title delivery are tested separately.
- Upgrade/rollback scripts are tested with a mock Docker CLI. This 0.3.0 build has not yet been installed in the user's running server.

See TEST-RESULTS.txt and BROWSER-TEST-RESULTS.txt for recorded output. Build with .NET 10 using `bash build.sh`. Browser checks require Node.js, Playwright and Chromium; `bash build.sh` generates `research/title-test.js`; then run `node tests/browser-checks.cjs` from the `jellyfix` folder. The optional PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH environment variable selects a Chromium binary.

The source wraps IMediaSourceManager for initial subtitle choice, uses a scoped MVC filter plus Jellyfin's public EncodingHelper decision for encoding limits, and registers an embedded IHasWebPages page with BasePlugin<PluginConfiguration> for native persistence. No private server methods or FFmpeg command strings are patched.

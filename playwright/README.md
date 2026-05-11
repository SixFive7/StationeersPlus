# Playwright MCP Configuration

Single source of truth for every `@playwright/mcp` setting: where it lives, what it does, the value we chose, and why.

## Files

| File | Role |
|------|------|
| [../.mcp.json](../.mcp.json) | Bootstraps the MCP server. Contains only `--config` and the single CLI-only flag (`--output-mode`). |
| [config.json](config.json) | Every config-schema setting we deviate from default on. Canonical source for Playwright-level configuration. |
| **README.md** (this file) | Documentation plus motivation for every setting: active, defaulted, and code-only. |

## Rules

**Changing a setting.** Edit the value in `playwright/config.json` (or `.mcp.json` for `--output-mode`) and update the matching row in the table below in the **same commit**. Motivation only lives here, so it must not drift.

**HAR archive convention.** `recordHar.path` is **static**: Playwright does not support placeholder substitution. Each new browser context overwrites `playwright/output/network.har` at close. If a session's network trace is worth keeping, the agent MUST rename the file before the next context starts:

```
mv playwright/output/network.har playwright/output/har/network-{YYYY-MM-DDTHH-MM}-{task-slug}.har
```

Unrenamed `network.har` files are considered disposable.

**Screenshot filenames.** When calling `browser_take_screenshot` with an explicit `filename`, **prepend `playwright/output/`** (e.g. `playwright/output/foo.png`). Upstream resolves explicit filenames via `workspaceFile()` against CWD (the repo root), not `outputDir`, so a bare `foo.png` lands in the repo root and pollutes the working tree. Auto-naming (omitting `filename` entirely) routes through `outputFile()` and does honour `outputDir`. The tool description ("Prefer relative file names to stay within the output directory") is misleading vs. the actual code path; see `packages/playwright-core/src/tools/backend/screenshot.ts` plus `response.ts` upstream. No config flag bridges the two; this is the agent's responsibility.

**Single Playwright instance per repo.** Chrome's SingletonLock on `playwright/profile/` prevents a second Claude Code window in this repo from launching a browser. This is intentional: it serialises Playwright usage across windows and prevents file-collision issues (HAR overwrites, profile-lock races). Subagents within one Claude Code session share the parent's MCP process and browser context, so they do not collide.

**Steam Workshop and steamcommunity.com.** Any lookup against `https://steamcommunity.com` (Workshop item pages, changelogs, comment threads, file details) MUST go through this Playwright server: navigate to the page, then read values from the rendered DOM or accessibility snapshot. Do not use `WebFetch` or the model's built-in browsing for `steamcommunity.com` content; Anthropic's fetch infrastructure returns bogus or stale data for that host. See the "Tool: Playwright MCP for web browsing" section in the repo root `CLAUDE.md`.

**Downloading files.** Click the site's download button: Playwright intercepts the `download` event and writes the authentic server-served file to `playwright/output/`. Do **not** use `browser_pdf_save` for this: that tool renders the current HTML to PDF (a page screenshot), not the real file. The `pdf` capability is intentionally disabled for this reason. Move files you want to keep out of `playwright/output/` (gitignored) into `.work/` per the repo `CLAUDE.md`.

**Resetting the browser profile.** Delete `playwright/profile/` and reconnect the MCP server via `/mcp`. The next context starts with a fresh, logged-out profile.

**Debugging a failed session.** The most recent `playwright/output/session-{timestamp}/session.md` contains the full tool-call trace: each call written as a `### Tool call: <name>` heading with `Args` and `Result` JSON-fenced blocks. `playwright/output/network.har` has the corresponding network activity including WebSocket frames. (Older docs may say `session-*.jsonl`; that was always wrong: upstream has used Markdown since the feature was introduced in PR #740, 2025-07.)

**Upgrading `@playwright/mcp`.** `npx` pulls `@latest` at every server start, so upgrades are automatic. After a major upgrade, run `npx @playwright/mcp@latest --help` and diff against the table below; new flags or config keys need a new row.

## Location legend

| Location | Meaning |
|----------|---------|
| `json` | Set in [config.json](config.json). |
| `flag` | Set as a CLI argument in [../.mcp.json](../.mcp.json). Used only when the setting has no config-schema equivalent. |
| `code` | Only settable programmatically (function/callable type, cannot be represented in JSON). Listed for completeness; we never use these. |
| `default` | Not set by us; upstream default is active. |

## Merge order

Upstream: `defaults < configFile < env vars < CLI flags`. CLI wins over everything. We do not set environment variables; all overrides go through one of the two config files above.

---

## Settings

| # | Setting | Location | Function | Value | Motivation |
|--:|---------|:--------:|----------|-------|------------|
| | **▸ Browser engine & profile** | | | | |
| 1 | `browser.browserName` | json | Browser engine family (`chromium`/`firefox`/`webkit`) | `chromium` | Chromium required for full CDP access (WebSocket frames, network bodies, performance traces). Default: `chromium`. |
| 2 | `browser.launchOptions.channel` | json | Chrome channel within the Chromium engine | `chrome` | System Chrome instead of bundled Chromium: matches the daily browser, stays current via Windows Update. Default: none (bundled chromium). |
| 3 | `browser.userDataDir` | json | Persistent browser profile on disk | `playwright/profile` | Logins (Steam, GitHub, vendor portals) survive between sessions. Chrome's SingletonLock blocks concurrent instances, which is intentional. Default: temp dir per session. |
| 4 | `browser.isolated` | default | In-memory profile (mutually exclusive with #3) | (default) | Mechanically incompatible with persistent `userDataDir`; we choose persistence. Default: `false`. |
| 5 | `browser.launchOptions.executablePath` | default | Explicit path to browser binary | (default) | Auto-detection finds system Chrome reliably. Default: auto. |
| | **▸ Launch options (browser process)** | | | | |
| 6 | `browser.launchOptions.headless` | json | Run without a visible window | `true` | User works in parallel on the same PC; visible windows would disrupt. Default: `false` (headed). |
| 7 | `browser.launchOptions.args` | default | Extra Chromium CLI flags | (default) | No specific portal misbehaves; no anti-detection need. Default: `[]`. |
| 8 | `browser.launchOptions.env` | default | Environment variables for browser process | (default) | Shell env suffices; no browser tuning required. Default: inherits shell env. |
| 9 | `browser.launchOptions.slowMo` | default | Delay each action by N ms (debug aid) | (default) | Productive headless run; slowing masks bugs. Default: `0`. |
| 10 | `browser.launchOptions.devtools` | default | Open DevTools automatically on page load | (default) | No-op under headless; we use `capabilities: ["devtools"]` for programmatic CDP. Default: `false`. |
| 11 | `browser.launchOptions.downloadsPath` | default | Separate downloads directory from `outputDir` | (default) | Keeping everything under `playwright/output/` avoids split storage. Default: inherits `outputDir`. |
| 12 | `browser.launchOptions.chromiumSandbox` | default | Sandbox on the parent browser process | (default) | Renderer-sandbox is the real protection; this extra layer has Windows start-up quirks for little gain. Default: `false`. |
| 13 | `browser.launchOptions.timeout` | default | Browser launch timeout (ms) | (default) | 30s is comfortable on any machine we use. Default: `30000`. |
| 14 | `browser.launchOptions.proxy` | default | HTTP/SOCKS proxy plus bypass | (default) | Home workflow, no corp proxy. Default: none. |
| 15 | `browser.launchOptions.ignoreDefaultArgs` | default | Skip Playwright's default Chrome args | (default) | Advanced escape hatch, never needed. Default: `[]`. |
| 16 | `browser.launchOptions.firefoxUserPrefs` | default | Firefox-specific prefs | (default) | N/A, we use Chromium. Default: `{}`. |
| 17 | `browser.launchOptions.handleSIGINT/TERM/HUP` | default | Signal handling on MCP shutdown | (default) | Default `true` correct for stdio spawn. Default: `true` (all three). |
| 18 | `browser.launchOptions.artifactsDir` | default | Directory for Playwright's internal artifacts (screenshots/videos/traces) | (default) | `outputDir` already centralizes these; splitting complicates review. Default: inherits `outputDir`. |
| 19 | `browser.launchOptions.tracesDir` | default | Directory for Playwright's native `tracing.start()` output | (default) | We use HAR for network tracing; Playwright's own trace format is test-runner-oriented and redundant. Default: inherits `outputDir`. |
| 20 | `browser.launchOptions.logger` | code | Custom `Logger` interface for Playwright internals | (default) | Function/object type, not JSON-representable. Only settable programmatically. No use case. Default: none. |
| | **▸ Context options: display & emulation** | | | | |
| 21 | `browser.contextOptions.viewport.width` | json | Viewport width in pixels | `1920` | 1080p desktop: most common business resolution; triggers desktop layout. Default: `1280`. |
| 22 | `browser.contextOptions.viewport.height` | json | Viewport height in pixels | `1080` | See #21. Default: `720`. |
| 23 | `browser.contextOptions.locale` | json | `navigator.language` plus Accept-Language header | `nl-NL` | Set explicitly for reproducible rendering across machines; `nl-NL` matches the developer's environment. Default: inherits OS locale. |
| 24 | `browser.contextOptions.timezoneId` | json | Browser timezone (`Date`, `Intl.DateTimeFormat`) | `Europe/Amsterdam` | Set explicitly for reproducible timestamps across machines; matches the developer's environment. Default: inherits OS timezone. |
| 25 | `browser.contextOptions.colorScheme` | default | `prefers-color-scheme` override | (default) | Default `light` keeps screenshots readable; no dark-mode variants to handle. Default: `light`. |
| 26 | `browser.contextOptions.contrast` | default | `prefers-contrast` CSS media query override | (default) | Accessibility testing feature for one's own app; we work with external portals only. Default: `no-preference`. |
| 27 | `browser.contextOptions.forcedColors` | default | Windows High Contrast emulation | (default) | Accessibility testing knob; not applicable to external portals. Default: `none`. |
| 28 | `browser.contextOptions.reducedMotion` | default | `prefers-reduced-motion` override | (default) | Headless renders no animations; zero observable effect. Default: `no-preference`. |
| 29 | `browser.contextOptions.hasTouch` | default | Simulate touch input | (default) | Desktop-only workflow. Default: `false`. |
| 30 | `browser.contextOptions.isMobile` | default | Mobile device emulation | (default) | Desktop-only workflow. Default: `false`. |
| 31 | `browser.contextOptions.screen` | default | Outer screen dimensions (distinct from viewport) | (default) | Viewport covers rendering needs; `window.screen.*` is rarely inspected. Default: matches viewport. |
| 32 | `browser.contextOptions.deviceScaleFactor` | default | DPR override (hi-DPI emulation) | (default) | Headless renders at 1x, fine for automation. Default: `1`. |
| 33 | `browser.contextOptions.userAgent` | default | Override the UA string | (default) | Default Chrome UA works everywhere; custom UA only invites site breakage. Default: real Chrome UA. |
| | **▸ Context options: network & security** | | | | |
| 34 | `browser.contextOptions.ignoreHTTPSErrors` | json | Accept invalid/self-signed certs | `true` | Local dev servers with self-signed certs must remain reachable. Default: `false`. |
| 35 | `browser.contextOptions.permissions` | json | Permissions auto-granted (no dialog) | `["clipboard-read", "clipboard-write"]` | Agent must copy/paste between pages without modal interruption. Other permissions (geolocation, camera, microphone, notifications) intentionally omitted: no use case, larger surface. Default: `[]`. |
| 36 | `browser.contextOptions.geolocation` | default | Simulated GPS coordinates | (default) | Sites we use do not request this; simulation = fingerprint noise. Default: none. |
| 37 | `browser.contextOptions.httpCredentials` | default | Basic Auth credentials (user/pass/origin) | (default) | Tokens should not be persisted to disk; handle interactively per session if needed. Default: none. |
| 38 | `browser.contextOptions.extraHTTPHeaders` | default | Static custom headers on every request | (default) | No concrete use case; agent can add per-request headers at runtime. Default: `{}`. |
| 39 | `browser.contextOptions.baseURL` | default | URL prefix for relative `page.goto()` calls | (default) | We cross many sites; a single baseURL would break everything else. Default: none. |
| 40 | `browser.contextOptions.bypassCSP` | default | Disable Content-Security-Policy enforcement | (default) | Weakens security; standard agent tool set does not hit CSP walls. Default: `false`. |
| 41 | `browser.contextOptions.serviceWorkers` | default | `allow`/`block` service workers | (default) | Many sites (Steam, Notion, GitHub) use service workers for offline sync; blocking would break them. Default: `allow`. |
| 42 | `browser.contextOptions.javaScriptEnabled` | default | Disable JavaScript execution in the context | (default) | Modern sites require JS; disabling would break everything. Default: `true`. |
| 43 | `browser.contextOptions.offline` | default | Simulate offline network | (default) | No use case; defeats the purpose of live browsing. Default: `false`. |
| 44 | `browser.contextOptions.acceptDownloads` | default | Permit file downloads | (default) | Our download-to-`playwright/output/` flow **requires** this to stay `true`; never change. Default: `true`. |
| 45 | `browser.contextOptions.storageState` | default | Import serialised cookies/localStorage at context start | (default) | Redundant with persistent `userDataDir`. Default: none. |
| 46 | `browser.contextOptions.clientCertificates` | default | mTLS client certificates per origin | (default) | No mTLS scenario in this workflow. Default: `[]`. |
| 47 | `browser.contextOptions.proxy` | default | Per-context proxy (distinct from `launchOptions.proxy`) | (default) | No proxy needed at either level. Default: inherits `launchOptions.proxy`. |
| 48 | `network.allowedOrigins` | default | Whitelist of origins the browser may reach | (default) | User explicitly wants no URL restrictions. Default: all allowed. |
| 49 | `network.blockedOrigins` | default | Blacklist of origins | (default) | See #48. Default: none. |
| | **▸ Context options: recording** | | | | |
| 50 | `browser.contextOptions.recordVideo` | default | Record session as `.webm` video | (default) | Session log plus HAR cover debug needs; video is large disk impact. Consider enabling ad-hoc for visual-specific issues. Default: none. |
| 51 | `browser.contextOptions.videoSize` | default | Video recording resolution (paired with `recordVideo`) | (default) | Depends on `recordVideo` (#50) which is default off. Default: matches viewport. |
| 52 | `browser.contextOptions.recordHar.path` | json | HAR file location | `playwright/output/network.har` | Passive full HTTP plus WebSocket trace for data-flow analysis. See HAR archive convention in "Rules". Default: no recording. |
| 53 | `browser.contextOptions.recordHar.mode` | json | `full` or `minimal` entry detail | `full` | Full entries required for debugging. Default: `full`. |
| 54 | `browser.contextOptions.recordHar.content` | json | `embed`/`attach`/`omit`: response body storage | `embed` | `embed` inlines response bodies AND WebSocket frame payloads (via the `_webSocketMessages` HAR extension). Default: varies per mode. |
| 55 | `browser.contextOptions.recordHar.urlFilter` | default | Regex/string filter for which URLs to record | (default) | We want complete data-flow capture; filtering would defeat the purpose. Default: record all. |
| | **▸ Capabilities & tools** | | | | |
| 56 | `capabilities` | json | List of MCP tool capabilities | `["vision", "devtools"]` | `vision` = screenshot tools plus coord-based clicking. `devtools` = CDP introspection (WebSocket frames, network bodies, performance traces). `pdf` intentionally omitted: `browser_pdf_save` renders HTML to PDF (screenshot), we want server-served PDFs via Playwright's download handling. Default: core only. |
| 57 | `codegen` | json | Language for code-generation tools | `none` | Agent works live interactively; no Playwright scripts needed. Saves context plus tool surface. Default: `typescript`. |
| 58 | `snapshot.mode` | default | Accessibility snapshot mode (`full`/`none`) | (default) | Non-vision workflow leans on snapshots; `none` breaks most tools. Default: `full`. |
| 59 | `imageResponses` | default | Screenshots in tool response | (default) | Model chooses when a screenshot is useful. Default: `auto`. |
| 60 | `testIdAttribute` | default | HTML attribute used as test id | (default) | External sites, not own-app testing. Default: `data-testid`. |
| 61 | `browser.contextOptions.strictSelectors` | default | Locators fail on multi-match instead of picking first | (default) | Breaks Playwright MCP's standard tool flow (assumes first-match). Test-runner feature. Default: `false`. |
| | **▸ Output & logging** | | | | |
| 62 | `outputDir` | json | Directory for downloads, auto-named screenshots, session folders, HAR, snapshot dumps | `playwright/output` | Centralised, gitignored, relative to CWD = repo root. **Caveat:** an explicit `filename` on `browser_take_screenshot` resolves against CWD, NOT `outputDir`; see "Screenshot filenames" rule. Default: OS temp dir. |
| 63 | `--output-mode` | **flag** | Route snapshot/network/console to `file` or `stdout` | `file` | Only CLI-only setting: not in the Config schema (MCP-protocol-level, not Playwright-level). File instead of tool-response prevents context bloat on large pages. Default: `stdout`. |
| 64 | `saveSession` | json | Per-session folder `{outputDir}/session-{timestamp}/session.md`: Markdown trace of every tool call (one `### Tool call:` block per call, with `Args` plus `Result` JSON-fenced sections); referenced screenshots/snapshots saved as siblings in the same folder | `true` | Critical for post-hoc debugging of complex flows. Format has been Markdown since upstream PR #740 (commit `b1a0f77`, 2025-07-22); never JSONL despite older notes. Default: `false`. |
| 65 | `console.level` | json | Browser console message level | `warning` | `info`/`debug` spam context; `error`-only misses CSP/deprecation warnings. Default: `info`. |
| 66 | `allowUnrestrictedFileAccess` | json | Agent can access paths outside workspace plus `file://` URLs | `true` | Needed for local PDF analysis plus `file://` debugging. Default: `false`. |
| 67 | `secrets` | default | Inline secrets object for injection into scripts | (default) | Tokens should not be persisted to disk; agent prompts per session. Default: none. |
| 68 | `browser.contextOptions.logger` | code | Per-context custom `Logger` interface | (default) | Function/object type, not JSON-representable. Only settable programmatically. No use case. Default: none. |
| | **▸ Timeouts** | | | | |
| 69 | `timeouts.action` | default | Per-action timeout (ms) | (default) | 5s is generous; longer masks bugs. Default: `5000`. |
| 70 | `timeouts.navigation` | default | Per-navigation timeout (ms) | (default) | 60s is ample for any portal we use. Default: `60000`. |
| 71 | `timeouts.expect` | default | `expect()` assertion timeout | (default) | Test-runner feature; we use `browser_wait_for` instead. Default: `5000`. |
| | **▸ Transport & remote connections** (N/A under stdio) | | | | |
| 72 | `server.port` | default | HTTP/SSE transport bind port | (default) | stdio transport; HTTP not used. Default: none. |
| 73 | `server.host` | default | HTTP/SSE transport bind host | (default) | stdio transport. Default: `localhost`. |
| 74 | `server.allowedHosts` | default | DNS rebinding protection for HTTP transport | (default) | stdio transport. Default: bind host. |
| 75 | `sharedBrowserContext` | default | Share browser context between HTTP clients | (default) | stdio, no clients to share between. Default: `false`. |
| 76 | `extension` | default | Attach to running Edge/Chrome via bridge extension | (default) | Requires Edge/Chrome plus running browser; conflicts with headless plus our launch. Default: `false`. |
| 77 | `browser.cdpEndpoint` | default | Connect to existing browser via CDP | (default) | We launch our own browser. Default: none. |
| 78 | `browser.cdpHeaders` | default | Headers for CDP connect | (default) | Depends on #77. Default: none. |
| 79 | `browser.cdpTimeout` | default | CDP connect timeout (ms) | (default) | Depends on #77. Default: `30000`. |
| 80 | `browser.remoteEndpoint` | default | Connect to running Playwright server | (default) | No use case. Default: none. |
| | **▸ Init scripts** | | | | |
| 81 | `browser.initPage[]` | default | TypeScript files evaluated on every Playwright page object | (default) | No custom runtime hooks needed. Default: `[]`. |
| 82 | `browser.initScript[]` | default | JS files injected into every page before site code | (default) | No use case (fingerprint masking, global helpers, etc.). Default: `[]`. |

---

## Environment variables

Playwright MCP reads 29 `PLAYWRIGHT_MCP_*` environment variables that override this config at runtime (merge order: `defaults < configFile < env < cli`). Examples: `PLAYWRIGHT_MCP_HEADLESS`, `_BROWSER`, `_USER_DATA_DIR`, `_OUTPUT_DIR`, `_VIEWPORT_SIZE`. We do not use them; the shell that spawns `.mcp.json` should not set them. Additional Playwright-level variables (`PWDEBUG`, `DEBUG`, `PLAYWRIGHT_BROWSERS_PATH`) are honoured by the underlying runtime, also unused.

## Hidden / undocumented flags

- `--vision`: deprecated alias for `capabilities: ["vision"]`. Hidden via `.hideHelp()`. Do not use.
- `install-browser <name>`: subcommand absent from `--help`. Delegates to Playwright's installer. Prefer `npx playwright install <browser>`.
- `browser.launchOptions.assistantMode`: forced `true` internally; not configurable.

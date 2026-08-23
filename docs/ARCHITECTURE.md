# Soul Remote — Architecture

## Overview

Soul Remote is a WPF (.NET 8) desktop app plus a small Cloudflare Worker. The
worker is a transparent reverse proxy to `api.telegram.org`; the desktop app
long-polls Telegram *through* that worker and executes commands locally.

```
+-----------+       +---------------------------+       +--------------------------+
|  Telegram | <---> |  Cloudflare Worker (edge) | <---> |  Soul Remote (Windows)   |
|  Bot API  |       |  cloudflare/worker.js     |       |  long-poll + dispatch    |
+-----------+       +---------------------------+       +--------------------------+
        ^  api.telegram.org blocked in Iran  ^  workers.dev reachable in Iran  ^
```

## Two assemblies, one seam

The code is split by whether it needs Windows, not by feature:

| | `SoulRemote.Core` (`net8.0`) | `SoulRemote` (`net8.0-windows`) |
|---|---|---|
| Holds | the relay, the bot, settings, logging, the string catalogue | WPF, the tray, and everything that touches Win32 |
| Knows about Windows | nothing | all of it |
| Testable | yes, anywhere — `dotnet test` on any OS | compile-checked only |

The core declares interfaces for the machine-specific half —
`ISystemControlService`, `ISystemInfoService`, `IScreenshotService`,
`IStartupManager`, plus `IUiDispatcher`, `ISecretProtector` and `IClock` — and the
desktop app implements them. `AppServices` is where the two meet.

That seam is why `CommandRouter`, `TelegramClient`, `SettingsService` and the rest
can be tested at all: `tests/SoulRemote.Core.Tests` drives them with fakes and a
hand-turned clock, with no desktop session anywhere in sight.

```
src/SoulRemote.Core/            everything that does not need Windows
  Abstractions/                 IUiDispatcher, ISecretProtector, IClock,
                                and the interfaces the Windows half implements
  Localization/                 AppLanguage, Strings (en + fa in one table), Loc
  Models/                       AppSettings, LinkState, Telegram & Cloudflare DTOs
  Services/
    ConnectionOrchestrator.cs   one-press bring-up pipeline with per-stage reporting
    SettingsService.cs          JSON persistence + secret protection
    CloudflareService.cs        granular Cloudflare API client (one op per method)
    TelegramClient.cs           Bot API calls via the worker; flood control, retries
    BotEngine.cs                long-poll loop, lifecycle, reconnect/backoff
    UpdateDispatcher.cs         per-chat serialisation, cross-chat concurrency
    CommandRouter.cs            maps updates -> actions, auth whitelist + pairing
    BotMenu.cs / ChatPrompts.cs the bot's screens and its one-shot prompts
    FileBrowser.cs              folder listing, fetching, and receiving files
    RateLimiter.cs              per-chat sliding window
    LogService.cs               in-memory + rolling file log with retention

src/SoulRemote/                 the Windows half
  App.xaml(.cs)                 bootstrap, single-instance, tray, global error handling
  GlobalUsings.cs               pins names that clash between WPF and WinForms
  MainWindow.xaml(.cs)          shell: custom chrome + navigation rail + page host
  Platform/                     WpfDispatcher, DpapiSecretProtector, the {p:T} markup
                                extension, and the PasswordBox binding helper
  Resources/                    Palette / Typography / Controls
  Controls/RelayLine.xaml(.cs)  the signature control: live view of the three hops
  Services/                     SystemControl, SystemInfo, Screenshot, Startup, Tray
    Native/                     Win32 P/Invoke + Core Audio COM interop
  ViewModels/ Views/            MVVM, one UserControl per page

cloudflare/worker.js            the ONLY copy of the worker script; SoulRemote.Core
                                embeds it directly as a linked EmbeddedResource, so
                                what ships and what you review cannot diverge

tests/SoulRemote.Core.Tests/    xunit; runs on any OS and in CI
```

## Key flows

### One-press connect

`ConnectionOrchestrator.RunAsync` owns the whole bring-up so no single service
has to know the workflow. Each stage is a `ConnectionStep` the UI binds to:

1. Verify token → 2. Resolve account → 3. Resolve workers.dev subdomain →
4. Upload worker → 5. Publish route → 6. Probe the edge → 7. `getMe` through
that edge → 8. Start the polling engine.

Settings are persisted as each phase succeeds, so a failure halfway leaves the
app with everything it had already established. The probe is advisory: edge
propagation can lag, and step 7 is the real proof the chain works. The probe also
reads the worker's version back and warns when the edge is still serving an older
script than this build deploys.

Re-running Connect while the relay is already up stops the poll loop first. The
loop caches the URL, token and secret it started with, so a re-run that changed
any of them has to replace it rather than report a relay it never started.

### Cloudflare API calls
1. `GET /user/tokens/verify` → token is active.
2. `GET /accounts` → pick the account.
3. `GET /accounts/{id}/workers/subdomain` → the `*.workers.dev` subdomain.
4. `PUT /accounts/{id}/workers/scripts/{name}` (multipart ESM module + metadata,
   with a `secret_text` binding `PROXY_SECRET`) → upload the worker.
5. `POST /accounts/{id}/workers/scripts/{name}/subdomain {enabled:true}` → route.
6. `GET {workerUrl}/healthz` (authenticated) until it answers.

### Telegram polling
- `BotEngine` configures `TelegramClient` with the worker URL, bot token, and the
  proxy secret (sent as `X-Proxy-Secret`).
- `getMe` verifies the bot; `deleteWebhook` frees long-polling; `setMyCommands`
  publishes the "/" menu in the current language.
- The backlog is drained on startup (`getUpdates offset=-1`) so stale commands
  are never executed.
- The loop calls `getUpdates(offset, timeout)` — re-reading the timeout each pass,
  so changing it in Settings applies without a restart — and hands each update to
  `UpdateDispatcher` rather than awaiting it. Updates from one chat still run in
  order; a 60-second shell command from one chat no longer stalls every other.
- Network errors back off exponentially (1→30 s). A 409 is called out by name:
  it means a second poller is sharing the bot token, and retrying faster only
  makes it worse.
- The client honours `parameters.retry_after` on a 429 and retries transient
  faults, so a reply is delayed rather than dropped under load.

### Authorization
- Commands are accepted only from chat IDs in the whitelist.
- Bootstrapping uses a **pairing code** shown in the app; sending `/pair <code>`
  adds the sender's chat ID and persists it. The code is single-use, expires after
  ten minutes, is compared in constant time, and is only accepted in a **private
  chat** — pairing a group would authorize every member of it, now and later.
- Failed attempts are counted per chat, so one stranger guessing cannot lock the
  owner out of pairing.
- A pairing that cannot be written to disk is reported as a failure and does not
  consume the code.
- Destructive actions (shutdown/restart/logoff/hibernate) require an inline
  confirmation tap.

## Security model
- Secrets encrypted at rest (DPAPI, current user). A value that cannot be
  decrypted is left on disk untouched rather than overwritten with an empty
  string, which would destroy a token that was merely unreadable this once.
- The worker refuses every request without the correct `X-Proxy-Secret`,
  **including when no secret is bound at all** — failing open there would leave an
  open Telegram proxy on the user's own Cloudflare account. The comparison hashes
  both sides first, so it is constant-time and leaks no length.
- The worker relays only Bot API paths. Everything else is refused.
- Three capabilities are off until switched on: `/cmd` (arbitrary shell), file
  browsing and fetching, and typing into the focused window. The last is gated
  because synthetic keystrokes into a focused terminal reach the same place a
  shell command does.
- Shell commands are time-limited (60 s) with truncated output.
- Paired chats are rate-limited, and unauthenticated senders much harder, so a
  leaked worker URL cannot be turned into an outbound message pump.

## Localization

`Strings` holds English and Persian on the same row of one table, so a key cannot
exist in one language and not the other. `StringsTests` enforces the rest
mechanically: nothing blank, nothing left untranslated, matching `{0}`
placeholders, and balanced HTML in both halves — an unbalanced `<b>` makes
Telegram reject the whole message.

The desktop reads the same catalogue through `Loc`, an indexer that WPF refreshes
wholesale when the language changes, so switching re-renders the window in place.
Persian sets `FlowDirection="RightToLeft"` on the root window, which mirrors the
entire layout, and every font stack ends in a face that has Arabic-script glyphs.

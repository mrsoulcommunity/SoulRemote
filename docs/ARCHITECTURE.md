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

## Projects & layers

```
src/SoulRemote/
  App.xaml(.cs)              app bootstrap, single-instance, tray, global error handling
  MainWindow.xaml(.cs)       shell window with Dashboard / Settings / Logs tabs
  Models/                    AppSettings, Telegram & Cloudflare DTOs
  Services/
    AppServices.cs           composition root (manual DI)
    SettingsService.cs       JSON persistence + DPAPI encryption of secrets
    CloudflareService.cs     token verify, account/subdomain lookup, worker deploy
    TelegramClient.cs        Telegram Bot API calls, always via the worker URL
    BotEngine.cs             long-poll loop, lifecycle, reconnect/backoff
    CommandRouter.cs         maps updates -> actions, auth whitelist + pairing
    SystemControlService.cs  power/media/process actions (shutdown.exe + P/Invoke)
    SystemInfoService.cs     sysinfo/disks/battery/processes/network
    ScreenshotService.cs     multi-monitor capture -> PNG
    StartupManager.cs        run-at-login registry entry
    TrayIconManager.cs       tray icon + minimize-to-tray
    Native/                  Win32 P/Invoke + Core Audio COM interop
    Security/                DPAPI wrapper + CSPRNG helpers
  ViewModels/                MVVM (Main/Dashboard/Settings/Log)
  Views/                     UserControls for each tab
  Assets/worker.js           embedded worker script (deployed via the CF API)
cloudflare/worker.js         reference copy for manual/Wrangler deployment
```

## Key flows

### Cloudflare connect & deploy
1. `GET /user/tokens/verify` → token is active.
2. `GET /accounts` → pick the account.
3. `GET /accounts/{id}/workers/subdomain` → the `*.workers.dev` subdomain.
4. `PUT /accounts/{id}/workers/scripts/{name}` (multipart ESM module + metadata,
   with a `secret_text` binding `PROXY_SECRET`) → upload the worker.
5. `POST /accounts/{id}/workers/scripts/{name}/subdomain {enabled:true}` → route.
6. Health-check `GET {workerUrl}/healthz` until it answers.

### Telegram polling
- `BotEngine` configures `TelegramClient` with the worker URL, bot token, and the
  proxy secret (sent as `X-Proxy-Secret`).
- `getMe` verifies the bot; `deleteWebhook` frees long-polling.
- The backlog is drained on startup (`getUpdates offset=-1`) so stale commands
  are never executed.
- The loop calls `getUpdates(offset, timeout)`; each update advances the offset
  and is dispatched to `CommandRouter`. Network errors trigger exponential
  backoff (1→30 s) without killing the loop.

### Authorization
- Commands are accepted only from chat IDs in the whitelist.
- Bootstrapping uses a **pairing code** shown in the app; sending
  `/pair <code>` adds the sender's chat ID and persists it.
- Destructive actions (shutdown/restart/logoff/hibernate) require an inline
  confirmation tap.

## Security model
- Secrets encrypted at rest (DPAPI, current user).
- Worker rejects requests without the correct `X-Proxy-Secret`.
- `/cmd` shell access is opt-in and time-limited (60 s) with truncated output.

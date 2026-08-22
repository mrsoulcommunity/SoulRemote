# Soul Remote — Setup Guide

This guide walks through connecting Cloudflare, creating a Telegram bot, and
linking your first chat.

## 1. Create a Telegram bot

1. Open Telegram and message [@BotFather](https://t.me/BotFather).
2. Send `/newbot`, choose a name and a username ending in `bot`.
3. Copy the **HTTP API token** it gives you (looks like `123456:ABC-...`).

## 2. Create a Cloudflare API token

1. Sign in to the [Cloudflare dashboard](https://dash.cloudflare.com/).
2. If you have never used Workers, open **Workers & Pages** once. On first visit
   Cloudflare asks you to pick a free `*.workers.dev` **subdomain** — set it.
   Soul Remote needs this subdomain to build the public worker URL.
3. Go to **My Profile → API Tokens → Create Token**.
4. Use the **“Edit Cloudflare Workers”** template (or create a custom token with
   *Account → Workers Scripts → Edit*).
5. Create the token and copy it.

## 3. Configure Soul Remote

Open the app → **Connect**:

1. Paste your **Cloudflare API token**.
2. Leave the worker name as `soul-remote-proxy` (or choose your own).
3. Paste your **Telegram bot token**.
4. Press **Connect**.

The bring-up sequence runs on the right and shows each stage as it completes:

```
✓ Verify Cloudflare token      Token is active
✓ Resolve account              My Account
✓ Find workers.dev subdomain   myname.workers.dev
✓ Deploy relay worker          soul-remote-proxy
✓ Publish public route         https://soul-remote-proxy.myname.workers.dev
✓ Reach the edge               Edge is answering
✓ Authenticate Telegram bot    @yourbot
✓ Start listening              Listening for commands
```

If a stage fails, the pipeline stops there and the reason is shown on that
stage — so you always know which part of the chain to fix.

Preferences live on the **Settings** page and save as you change them.

## 4. Link your Telegram chat

On the **Dashboard**:

1. The relay is already listening after Connect (the relay line shows all three
   hops lit).
2. In Telegram, open your bot and send:
   ```
   /pair 123456
   ```
   using the 6-digit code shown on the Dashboard. The code is single-use and a
   fresh one is issued after each successful pair.
3. You should get “✅ Paired successfully”. Send `/menu` to get the button panel.

## 5. Everyday use

Type a command or tap a button:

- `/screenshot` — capture the desktop (`/screenshot 1` for monitor #1)
- `/sysinfo`, `/disks`, `/battery`, `/processes`, `/network`
- `/lock`, `/sleep`, `/hibernate`
- `/shutdown`, `/restart`, `/logoff` (each asks for confirmation), `/cancel`
- `/volume 40`, `/volup`, `/voldown`, `/mute`
- `/play`, `/next`, `/prev`
- `/kill chrome` or `/kill 1234`
- `/cmd dir C:\` (only if you enabled shell commands)
- `/clipboard` — read the clipboard; `/clip <text>` — set it
- `/open <url|path>`, `/type <text>`, `/say <text>`, `/screens`
- `/whoami`, `/ping`

## Troubleshooting

| Symptom | Fix |
|---|---|
| “This account has no workers.dev subdomain” | Open Cloudflare **Workers & Pages** once to register a subdomain, then retry. |
| “Cloudflare token is not active” | Recreate the token with the *Edit Cloudflare Workers* template. |
| Telegram test returns non-JSON | The worker URL/secret is wrong — re-deploy from Settings. |
| Bot online but no replies | Make sure your chat is paired (`/pair`), and the bot was started. |
| Screenshot is black | The session is locked or on a secure desktop; unlock to capture. |

Logs are written to `%APPDATA%\SoulRemote\logs\` and shown live on the **Logs** tab.

<div align="center">

# 🎛 Soul Remote

**Control your Windows PC from Telegram — even where Telegram is blocked.**

Soul Remote is a lightweight, native Windows desktop app that turns a Telegram
bot into a full remote‑control panel for your machine (screenshot, shutdown,
restart, volume, processes, and more). Because `api.telegram.org` is blocked in
some regions (e.g. Iran), Soul Remote routes **all** Telegram traffic through a
**Cloudflare Worker** it deploys for you automatically — Cloudflare's edge stays
reachable, so the bot keeps working.

</div>

---

## ✨ Features

| Category | Commands |
|---|---|
| **Power** | Shutdown, Restart, Log off, Sleep, Hibernate, Cancel pending, Turn display off |
| **Capture** | Full‑desktop or per‑monitor screenshot |
| **System info** | OS/CPU/RAM/uptime, disks, battery/power, top processes, network (local + public IP) |
| **Media & audio** | Play/Pause, Next, Previous, Volume up/down, set volume 0–100, mute toggle |
| **Processes** | List top processes, kill by name or PID |
| **Advanced** | Run shell commands via `/cmd` (opt‑in), inline‑button menus |

- 🔐 **Secure by default** — tokens encrypted at rest with Windows DPAPI, a
  chat‑ID **whitelist**, a **pairing code** to link a chat, and a shared secret
  so the deployed worker is not an open relay.
- 🧊 **Runs in the tray** — keeps working in the background; optional start with
  Windows and auto‑start of the bot.
- 🧱 **Native & dependency‑light** — WPF on .NET 8, no third‑party NuGet packages.
- 🪶 **Stable polling engine** — long‑polling with automatic reconnect/backoff,
  startup‑backlog draining, and confirmation prompts for destructive actions.

---

## 🧭 How it works

```
Telegram  ⇄  Cloudflare Worker (proxy)  ⇄  Soul Remote (your PC, long‑polling)
             (reachable in Iran)            (executes commands locally)
```

1. You paste a **Cloudflare API token**; Soul Remote verifies it and **deploys a
   Worker** (`cloudflare/worker.js`) that transparently forwards requests to
   `api.telegram.org`.
2. You paste a **Telegram bot token**; the app talks to Telegram **only** through
   the worker URL (`https://<worker>.<subdomain>.workers.dev/bot<token>/…`).
3. The app **long‑polls** `getUpdates` through the worker, runs each command
   locally, and replies (text/photo) back through the worker.

See [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) for details.

---

## 🚀 Getting started

### Prerequisites
- Windows 10/11 (x64)
- A free **Cloudflare** account
- A **Telegram bot** token from [@BotFather](https://t.me/BotFather)

### 1) Create a Cloudflare API token
Cloudflare dashboard → **My Profile → API Tokens → Create Token** →
use the **“Edit Cloudflare Workers”** template → Create → copy the token.

> The token needs *Account → Workers Scripts → Edit*. The app also reads your
> account list and workers.dev subdomain. If your account has never used
> Workers, open **Workers & Pages** once so a free `*.workers.dev` subdomain is
> registered.

### 2) Run Soul Remote → **Settings**
1. Paste the **Cloudflare API token**, keep the default worker name, click
   **Connect & deploy worker**. The proxy URL appears when it succeeds.
2. Paste the **Telegram bot token**, click **Test connection**.
3. Adjust options (start with Windows, auto‑start bot, etc.), click **Save**.

### 3) Link your Telegram chat (Dashboard)
1. Click **Start bot**.
2. Open your bot in Telegram, send `/pair <code>` using the code shown on the
   Dashboard.
3. You're in — send `/menu`.

Full walkthrough: [`docs/SETUP.md`](docs/SETUP.md).

---

## 🛠 Build from source

```powershell
git clone https://github.com/mrsoulcommunity/SoulRemote.git
cd SoulRemote
dotnet build SoulRemote.sln -c Release
# or a single self-contained exe:
dotnet publish src/SoulRemote/SoulRemote.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -o publish
```

Requires the **.NET 8 SDK** (Windows). CI builds the same on every push
(`.github/workflows/build.yml`) and uploads a ready `SoulRemote.exe` artifact.

---

## 🔒 Security notes

- Secrets (Cloudflare token, bot token, proxy secret) are stored **encrypted**
  under `%APPDATA%\SoulRemote\settings.json` via DPAPI (current user only).
- Only **paired chat IDs** can run commands; everyone else is rejected.
- The worker enforces an `X-Proxy-Secret` header so a leaked URL can't be abused.
- `/cmd` (arbitrary shell) is **disabled** until you explicitly enable it.
- This is a powerful remote‑control tool. Only pair chats you control, and keep
  your bot token private.

---

<div dir="rtl" align="right">

## 🇮🇷 راهنمای فارسی

**سول ریموت** یک برنامهٔ ویندوزی سبک است که با یک بات تلگرام، کنترل کامل سیستم شما را
فراهم می‌کند (اسکرین‌شات، خاموش/ری‌استارت، صدا، پروسه‌ها و…). چون در ایران
`api.telegram.org` فیلتر است، سول ریموت به‌صورت خودکار یک **Cloudflare Worker**
می‌سازد و تمام ترافیک تلگرام را از طریق کلادفلر (که در دسترس است) عبور می‌دهد.

### راه‌اندازی
۱. در **Cloudflare** یک **API Token** با قالب «Edit Cloudflare Workers» بسازید.
۲. در برنامه به **Settings** بروید، توکن کلادفلر را وارد و روی **Connect & deploy
   worker** بزنید تا ورکر پراکسی ساخته شود.
۳. توکن **بات تلگرام** (از @BotFather) را وارد و **Test connection** بزنید و
   تنظیمات را ذخیره کنید.
۴. در **Dashboard** روی **Start bot** بزنید، سپس در تلگرام دستور
   `/pair <کد>` را با کدی که نمایش داده می‌شود بفرستید.
۵. حالا `/menu` را بفرستید و سیستم را کنترل کنید.

### امنیت
- توکن‌ها با DPAPI ویندوز رمزنگاری و فقط برای کاربر فعلی ذخیره می‌شوند.
- فقط چت‌هایی که با «کد جفت‌سازی» تأیید شده‌اند اجازهٔ کنترل دارند.
- ورکر با هدر مخفی `X-Proxy-Secret` محافظت می‌شود تا پراکسی عمومی نشود.
- قابلیت اجرای دستور دلخواه (`/cmd`) به‌صورت پیش‌فرض غیرفعال است.

</div>

---

## 📄 License

MIT © MrSoul — see [LICENSE](LICENSE).

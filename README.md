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
| **Input & clipboard** | Read/set the clipboard, type into the focused window, open a URL or file, speak text aloud |
| **Advanced** | Run shell commands via `/cmd` (opt‑in), inline‑button menus |

- ⚡ **One‑press bring‑up** — paste both tokens and press **Connect**: Soul Remote
  verifies the token, deploys the worker, publishes its route, probes the edge,
  signs the bot in *through* that edge and starts listening — reporting every
  stage live, and stopping with the reason on whichever stage broke.
- 🎛 **Purpose‑built console UI** — a live **relay line** shows the three hops
  (this PC → Cloudflare edge → Telegram); traffic only animates along a conduit
  while that hop is actually carrying it.
- 🔐 **Secure by default** — tokens encrypted at rest with Windows DPAPI, a
  chat‑ID **whitelist**, a single‑use **pairing code** (rate‑limited, compared in
  constant time), and a shared secret so the deployed worker is not an open relay.
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

### 2) Run Soul Remote → **Connect**
1. Paste the **Cloudflare API token** and the **Telegram bot token**.
2. Press **Connect**. The bring‑up sequence runs on the right; when it finishes
   the relay endpoint appears and the bot is already listening.

### 3) Link your Telegram chat (Dashboard)
1. Open your bot in Telegram and send `/pair <code>` with the code on the
   Dashboard. The code works once, then a fresh one is issued.
2. You're in — send `/menu`.

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
۲. در برنامه به صفحهٔ **Connect** بروید و **هر دو توکن** (کلادفلر و بات تلگرام) را وارد کنید.
۳. روی **Connect** بزنید. برنامه خودش ورکر را دیپلوی می‌کند، مسیر عمومی را منتشر می‌کند،
   لبه را تست می‌کند، بات را از همان مسیر وارد می‌کند و شروع به گوش‌دادن می‌کند —
   هر مرحله زنده گزارش می‌شود.
۴. در **Dashboard** کد جفت‌سازی را ببینید و در تلگرام `/pair <کد>` را بفرستید
   (کد یک‌بارمصرف است).
۵. حالا `/menu` را بفرستید و سیستم را کنترل کنید.

### امنیت
- توکن‌ها با DPAPI ویندوز رمزنگاری و فقط برای کاربر فعلی ذخیره می‌شوند.
- فقط چت‌هایی که با «کد جفت‌سازی» تأیید شده‌اند اجازهٔ کنترل دارند.
- ورکر با هدر مخفی `X-Proxy-Secret` محافظت می‌شود تا پراکسی عمومی نشود.
- قابلیت اجرای دستور دلخواه (`/cmd`) و دسترسی به فایل‌ها به‌صورت پیش‌فرض غیرفعال است.
- کد جفت‌سازی یک‌بارمصرف، با محدودیت تعداد تلاش و مقایسهٔ ثابت‌زمان است.

</div>

---

## 📄 License

MIT © MrSoul — see [LICENSE](LICENSE).

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
| **Files** | Browse folders, fetch a file to Telegram, send a file to the PC (opt‑in) |
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
- 🌍 **Speaks Persian** — the whole bot *and* the whole desktop, switchable from
  either side, with the window mirrored right‑to‑left.
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

### 0) Install
Run **`SoulRemote-<version>-x64.msi`** and follow the wizard. It installs **for the
current user only**, so there is no UAC prompt and no admin account needed:

| | |
|---|---|
| Program | `%LOCALAPPDATA%\Programs\Soul Remote\SoulRemote.exe` |
| Shortcuts | Start menu, and the desktop unless you say otherwise |
| Settings | `%APPDATA%\SoulRemote\settings.json` — kept on upgrade, kept on uninstall |
| Uninstall | *Settings → Apps → Soul Remote*, like any other app |

Installing a newer MSI upgrades in place and keeps your tokens and paired chats.
For an unattended install:

```powershell
msiexec /i SoulRemote-1.0.0-x64.msi /qn INSTALLDESKTOPSHORTCUT=0
```

The plain `SoulRemote.exe` still works on its own if you would rather not install
anything — it needs no runtime, and everything below applies unchanged.

### 1) Create a Cloudflare API token
Cloudflare dashboard → **My Profile → API Tokens → Create Token** →
use the **“Edit Cloudflare Workers”** template → Create → copy the token.

> The token needs *Account → Workers Scripts → Edit*. The app also reads your
> account list and workers.dev subdomain. If your account has never used
> Workers, open **Workers & Pages** once so a free `*.workers.dev` subdomain is
> registered.

### 2) Start Soul Remote → **Connect**
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
dotnet test SoulRemote.sln -c Release

# a single self-contained exe:
dotnet publish src/SoulRemote/SoulRemote.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -o publish
```

### The installer

One command runs the tests, publishes the exe, and wraps it in the MSI, writing
both plus their SHA-256 files to `dist/`:

```powershell
powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1 -Version 1.0.0
```

It needs the [WiX 5](https://wixtoolset.org) CLI once:

```powershell
dotnet tool install --global wix --version 5.0.2
wix extension add -g WixToolset.UI.wixext/5.0.2
wix extension add -g WixToolset.Util.wixext/5.0.2
```

The installer is authored in [`installer/SoulRemote.wxs`](installer/SoulRemote.wxs).
Its artwork — and the app's own icon — is drawn from the WPF palette by
[`tools/make-brand.ps1`](tools/make-brand.ps1) rather than committed as binaries
nobody can edit; `build-installer.ps1` re-runs it on every build, so the icon can
never drift from `Resources/Palette.xaml`.

Requires the **.NET 8 SDK**. The desktop app targets `net8.0-windows`, but
`SoulRemote.Core` and the test suite are plain `net8.0` — so `dotnet test` runs
anywhere, and the app itself can be built off Windows with
`-p:EnableWindowsTargeting=true`.

CI builds and tests on every push (`.github/workflows/build.yml`); pushing a
`v*` tag publishes a release with the exe and its SHA‑256.

---

## 🔒 Security notes

- Secrets (Cloudflare token, bot token, proxy secret) are stored **encrypted**
  under `%APPDATA%\SoulRemote\settings.json` via DPAPI (current user only). A value
  that cannot be decrypted is left alone rather than overwritten, so a token is
  never destroyed just because Windows could not read it once.
- Only **paired chat IDs** can run commands; everyone else is rejected. The
  pairing code is single‑use, expires after ten minutes, is compared in constant
  time, and is **only accepted in a private chat** — pairing a group would hand
  control of the PC to every member of it.
- The worker enforces an `X-Proxy-Secret` header, **refuses everything when no
  secret is bound**, compares it in constant time, and relays only Bot API paths.
- Three capabilities are **off by default** and each has its own switch in
  Settings: `/cmd` (arbitrary shell), file browsing and fetching, and typing into
  the focused window. The last one is gated because synthetic keystrokes into a
  focused terminal reach the same place a shell command does.
- What is *not* encrypted in `settings.json`: the paired chat IDs and their
  names, the bot handle, and the worker URL. Anyone who can read that file learns
  who can drive this machine, without needing the tokens.
- This is a powerful remote‑control tool. Only pair chats you control, and keep
  your bot token private.

---

<div dir="rtl" align="right">

## 🇮🇷 راهنمای فارسی

**سول ریموت** یک برنامهٔ ویندوزی سبک است که با یک بات تلگرام، کنترل کامل سیستم شما را
فراهم می‌کند (اسکرین‌شات، خاموش/ری‌استارت، صدا، پروسه‌ها و…). چون در ایران
`api.telegram.org` فیلتر است، سول ریموت به‌صورت خودکار یک **Cloudflare Worker**
می‌سازد و تمام ترافیک تلگرام را از طریق کلادفلر (که در دسترس است) عبور می‌دهد.

### نصب
فایل **`SoulRemote-<version>-x64.msi`** را اجرا کنید و مراحل نصب را ادامه دهید.
نصب فقط برای **کاربر فعلی** انجام می‌شود، بنابراین نه دسترسی ادمین لازم است و نه
پنجرهٔ UAC بالا می‌آید:

- برنامه در `%LOCALAPPDATA%\Programs\Soul Remote` نصب می‌شود.
- میان‌بر در منوی استارت و (در صورت تمایل) روی دسکتاپ ساخته می‌شود.
- تنظیمات در `%APPDATA%\SoulRemote\settings.json` می‌ماند و با نصب نسخهٔ جدید یا
  حذف برنامه پاک نمی‌شود؛ پس توکن‌ها و چت‌های جفت‌شده باقی می‌مانند.
- حذف برنامه از مسیر همیشگی ویندوز: **تنظیمات ← Apps ← Soul Remote**.

اگر ترجیح می‌دهید چیزی نصب نشود، همان فایل `SoulRemote.exe` به‌تنهایی هم کار می‌کند
و به هیچ رانتایمی نیاز ندارد.

### راه‌اندازی
۱. در **Cloudflare** یک **API Token** با قالب «Edit Cloudflare Workers» بسازید.
۲. در برنامه به صفحهٔ **Connect** بروید و **هر دو توکن** (کلادفلر و بات تلگرام) را وارد کنید.
۳. روی **Connect** بزنید. برنامه خودش ورکر را دیپلوی می‌کند، مسیر عمومی را منتشر می‌کند،
   لبه را تست می‌کند، بات را از همان مسیر وارد می‌کند و شروع به گوش‌دادن می‌کند —
   هر مرحله زنده گزارش می‌شود.
۴. در **Dashboard** کد جفت‌سازی را ببینید و در تلگرام `/pair <کد>` را بفرستید
   (کد یک‌بارمصرف است و پس از ۱۰ دقیقه منقضی می‌شود؛ فقط در چت خصوصی پذیرفته می‌شود).
۵. حالا `/menu` را بفرستید و سیستم را کنترل کنید.

### زبان فارسی
تمام برنامه — هم پنجرهٔ ویندوز و هم خودِ بات — فارسی صحبت می‌کند. برای تغییر زبان
یا در **Settings** گزینهٔ زبان را انتخاب کنید، یا در تلگرام دکمهٔ 🌐 پایین منو را
بزنید (یا `/lang` را بفرستید). با انتخاب فارسی، چیدمان پنجره هم راست‌به‌چپ می‌شود.

### امنیت
- توکن‌ها با DPAPI ویندوز رمزنگاری و فقط برای کاربر فعلی ذخیره می‌شوند.
- فقط چت‌هایی که با «کد جفت‌سازی» تأیید شده‌اند اجازهٔ کنترل دارند.
- ورکر با هدر مخفی `X-Proxy-Secret` محافظت می‌شود؛ اگر این کلید تنظیم نشده باشد
  ورکر همه‌چیز را رد می‌کند تا هرگز به پراکسی باز تبدیل نشود.
- سه قابلیت پیش‌فرض خاموش‌اند و هرکدام کلید جداگانهٔ خود را در **Settings** دارند:
  اجرای دستور (`/cmd`)، دسترسی به فایل، و تایپ در پنجرهٔ فعال.
- کد جفت‌سازی یک‌بارمصرف است، منقضی می‌شود، تعداد تلاش برای هر چت جداگانه شمرده
  می‌شود، و فقط در چت خصوصی پذیرفته می‌شود.
- شناسهٔ چت‌های متصل، نام بات و نشانی ورکر در فایل تنظیمات رمزنگاری **نمی‌شوند**.

</div>

---

## 📄 License

MIT © MrSoul — see [LICENSE](LICENSE).

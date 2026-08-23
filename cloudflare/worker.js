/**
 * Soul Remote — Cloudflare Worker (Telegram reverse proxy)
 * -------------------------------------------------------------------------
 * Telegram's api.telegram.org is blocked in some regions (e.g. Iran), but
 * Cloudflare's edge is reachable. This Worker transparently forwards every
 * request it receives to https://api.telegram.org, so the desktop app can
 * reach the Telegram Bot API through the worker URL instead of directly.
 *
 * It is deployed automatically by the Soul Remote desktop app through the
 * Cloudflare API, but it is also kept in the repo (cloudflare/worker.js) so
 * it can be reviewed or deployed manually with Wrangler.
 *
 * Security model
 * --------------
 * The worker sits on a public, guessable URL, so it authenticates every
 * request with the shared PROXY_SECRET the app generates at deploy time:
 *
 *  - No PROXY_SECRET bound  -> the worker refuses everything. It used to skip
 *    the check in that case, which turned a mis-deployed worker into an open
 *    Telegram proxy on someone else's Cloudflare account.
 *  - The comparison hashes both sides first, so it is constant-time and does
 *    not leak the secret's length.
 *  - Only Telegram Bot API paths are relayed. Anything else is refused rather
 *    than forwarded, so the worker cannot be used to reach the rest of the
 *    origin's surface.
 */

const TELEGRAM_ORIGIN = "https://api.telegram.org";

// /bot<token>/<method> and /file/bot<token>/<path> are the only two shapes the
// Bot API uses, and the only two this worker will relay.
const TELEGRAM_PATH = /^\/(?:file\/)?bot[0-9]+:[A-Za-z0-9_-]+\/.+/;

function json(body, status) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": "application/json", "cache-control": "no-store" },
  });
}

async function digest(value) {
  const bytes = new TextEncoder().encode(value);
  return crypto.subtle.digest("SHA-256", bytes);
}

/**
 * Constant-time secret check. Both sides are hashed to a fixed 32 bytes first,
 * so timingSafeEqual always compares equal-length buffers and the length of the
 * provided header tells an attacker nothing.
 */
async function isAuthorized(request, env) {
  const expected = env && env.PROXY_SECRET;
  if (!expected) {
    // Fail closed: an unbound secret means a misconfigured deploy, not a public relay.
    return false;
  }
  const provided = request.headers.get("X-Proxy-Secret");
  if (!provided) {
    return false;
  }
  const [a, b] = await Promise.all([digest(provided), digest(expected)]);
  return crypto.subtle.timingSafeEqual(a, b);
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);

    // A bare hit from a browser gets a neutral 200 with nothing identifying on it.
    // Anything that reveals what this worker is would help someone who found the
    // URL by scanning workers.dev decide it is worth attacking.
    if (url.pathname === "/") {
      return new Response("ok\n", {
        status: 200,
        headers: { "content-type": "text/plain", "cache-control": "no-store" },
      });
    }

    if (!(await isAuthorized(request, env))) {
      return json({ ok: false, error_code: 401, description: "Unauthorized proxy request" }, 401);
    }

    // Authenticated health probe: the desktop app uses this to confirm the route
    // is published and the secret matches before it tries to sign the bot in.
    if (url.pathname === "/healthz") {
      return json({ ok: true, service: "soul-remote-proxy" }, 200);
    }

    // The public address of the machine that made this request, straight from
    // Cloudflare. This exists so the app can report its own public IP without
    // calling a third-party lookup service over the very network it is trying
    // to avoid being observed on.
    if (url.pathname === "/whoami") {
      return json({
        ok: true,
        ip: request.headers.get("cf-connecting-ip") || null,
        country: (request.cf && request.cf.country) || null,
      }, 200);
    }

    if (!TELEGRAM_PATH.test(url.pathname)) {
      return json({ ok: false, error_code: 404, description: "Not a Telegram Bot API path" }, 404);
    }

    const target = TELEGRAM_ORIGIN + url.pathname + url.search;

    // Rebuild headers, dropping hop-by-hop / host headers that must not be forwarded.
    const headers = new Headers(request.headers);
    headers.delete("host");
    headers.delete("x-proxy-secret");
    headers.delete("cf-connecting-ip");
    headers.delete("cf-ipcountry");
    headers.delete("cf-ray");
    headers.delete("cf-visitor");
    headers.delete("x-forwarded-for");
    headers.delete("x-forwarded-proto");
    headers.delete("x-real-ip");

    const method = request.method.toUpperCase();
    const hasBody = method !== "GET" && method !== "HEAD";

    const init = {
      method,
      headers,
      body: hasBody ? request.body : undefined,
      redirect: "follow",
    };
    // Streaming request bodies require the "half" duplex hint on the modern runtime.
    if (hasBody) {
      init.duplex = "half";
    }

    let upstream;
    try {
      upstream = await fetch(target, init);
    } catch (err) {
      return json({
        ok: false,
        error_code: 502,
        description: "Proxy upstream error: " + (err && err.message ? err.message : String(err)),
      }, 502);
    }

    // Pass the Telegram response straight back to the caller.
    const respHeaders = new Headers(upstream.headers);
    respHeaders.set("access-control-allow-origin", "*");
    return new Response(upstream.body, {
      status: upstream.status,
      statusText: upstream.statusText,
      headers: respHeaders,
    });
  },
};

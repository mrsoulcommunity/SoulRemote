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
 * Optional hardening: if the PROXY_SECRET binding is set, requests must carry
 * a matching "X-Proxy-Secret" header, otherwise the worker refuses to relay.
 * This prevents the deployment from being abused as an open Telegram proxy.
 */

const TELEGRAM_ORIGIN = "https://api.telegram.org";

export default {
  async fetch(request, env) {
    const url = new URL(request.url);

    // Lightweight health/landing endpoint so a browser hit doesn't look broken.
    if (url.pathname === "/" || url.pathname === "/healthz") {
      return new Response(
        JSON.stringify({ ok: true, service: "soul-remote-proxy" }),
        { status: 200, headers: { "content-type": "application/json" } }
      );
    }

    // Optional shared-secret gate.
    if (env && env.PROXY_SECRET) {
      const provided = request.headers.get("X-Proxy-Secret");
      if (provided !== env.PROXY_SECRET) {
        return new Response(
          JSON.stringify({ ok: false, error_code: 401, description: "Unauthorized proxy request" }),
          { status: 401, headers: { "content-type": "application/json" } }
        );
      }
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
      return new Response(
        JSON.stringify({ ok: false, error_code: 502, description: "Proxy upstream error: " + (err && err.message ? err.message : String(err)) }),
        { status: 502, headers: { "content-type": "application/json" } }
      );
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

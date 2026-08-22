using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SoulRemote.Models;

namespace SoulRemote.Services;

/// <summary>
/// Thin, single-purpose client for the slice of the Cloudflare API v4 that Soul
/// Remote needs. It performs one API operation per method and holds no workflow
/// state; sequencing those operations is <see cref="ConnectionOrchestrator"/>'s job.
/// </summary>
public interface ICloudflareService
{
    Task<CfTokenVerify> VerifyTokenAsync(string apiToken, CancellationToken ct = default);
    Task<List<CfAccount>> GetAccountsAsync(string apiToken, CancellationToken ct = default);
    Task<string?> GetWorkersDevSubdomainAsync(string apiToken, string accountId, CancellationToken ct = default);
    Task UploadWorkerAsync(string apiToken, string accountId, string workerName, string proxySecret, CancellationToken ct = default);
    Task<bool> EnableSubdomainRouteAsync(string apiToken, string accountId, string workerName, CancellationToken ct = default);

    /// <summary>Polls the deployed worker's health endpoint until it answers. Never throws on failure.</summary>
    Task<bool> ProbeWorkerAsync(string workerUrl, string proxySecret, CancellationToken ct = default);

    /// <summary>Normalises a user-supplied worker name to what Cloudflare accepts.</summary>
    string NormalizeWorkerName(string name);
}

public sealed class CloudflareService : ICloudflareService
{
    private const string ApiBase = "https://api.cloudflare.com/client/v4";
    private const string CompatibilityDate = "2024-11-01";

    private readonly HttpClient _http;
    private readonly ILogService _log;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public CloudflareService(ILogService log)
    {
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SoulRemote/1.0");
    }

    public async Task<CfTokenVerify> VerifyTokenAsync(string apiToken, CancellationToken ct = default)
    {
        using var req = Build(HttpMethod.Get, $"{ApiBase}/user/tokens/verify", apiToken);
        var result = await SendAsync<CfTokenVerify>(req, ct).ConfigureAwait(false);
        if (result is null || !string.Equals(result.Status, "active", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "This Cloudflare token is not active. Create one from the \"Edit Cloudflare Workers\" template and paste it again.");
        return result;
    }

    public async Task<List<CfAccount>> GetAccountsAsync(string apiToken, CancellationToken ct = default)
    {
        using var req = Build(HttpMethod.Get, $"{ApiBase}/accounts?per_page=50", apiToken);
        var accounts = await SendAsync<List<CfAccount>>(req, ct).ConfigureAwait(false);
        if (accounts is null || accounts.Count == 0)
            throw new InvalidOperationException(
                "The token works but reaches no account. Give it Account -> Workers Scripts -> Edit permission.");
        return accounts;
    }

    public async Task<string?> GetWorkersDevSubdomainAsync(string apiToken, string accountId, CancellationToken ct = default)
    {
        // A transport or permission error must propagate: only a successful response with
        // no subdomain means the account has not claimed one.
        using var req = Build(HttpMethod.Get, $"{ApiBase}/accounts/{accountId}/workers/subdomain", apiToken);
        var sub = await SendAsync<CfSubdomain>(req, ct).ConfigureAwait(false);
        return sub?.Subdomain;
    }

    public async Task UploadWorkerAsync(string apiToken, string accountId, string workerName, string proxySecret, CancellationToken ct = default)
    {
        var script = LoadWorkerScript();

        var bindings = new List<object>();
        if (!string.IsNullOrEmpty(proxySecret))
            bindings.Add(new { type = "secret_text", name = "PROXY_SECRET", text = proxySecret });

        var metadata = new
        {
            main_module = "worker.js",
            compatibility_date = CompatibilityDate,
            bindings,
        };

        using var form = new MultipartFormDataContent();

        var metadataContent = new StringContent(JsonSerializer.Serialize(metadata), Encoding.UTF8);
        metadataContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        form.Add(metadataContent, "metadata");

        var moduleContent = new StringContent(script, Encoding.UTF8);
        moduleContent.Headers.ContentType = new MediaTypeHeaderValue("application/javascript+module");
        form.Add(moduleContent, "worker.js", "worker.js");

        using var req = Build(HttpMethod.Put, $"{ApiBase}/accounts/{accountId}/workers/scripts/{workerName}", apiToken);
        req.Content = form;
        await SendRawAsync(req, ct).ConfigureAwait(false);
    }

    public async Task<bool> EnableSubdomainRouteAsync(string apiToken, string accountId, string workerName, CancellationToken ct = default)
    {
        using var req = Build(HttpMethod.Post, $"{ApiBase}/accounts/{accountId}/workers/scripts/{workerName}/subdomain", apiToken);
        req.Content = new StringContent("{\"enabled\":true}", Encoding.UTF8, "application/json");
        try
        {
            await SendRawAsync(req, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Not fatal on a re-deploy where the route already exists, but the caller must
            // know it was not confirmed rather than being told the route is published.
            _log.Warning($"Could not publish the public route: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> ProbeWorkerAsync(string workerUrl, string proxySecret, CancellationToken ct = default)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{workerUrl.TrimEnd('/')}/healthz");
                if (!string.IsNullOrEmpty(proxySecret))
                    req.Headers.TryAddWithoutValidation("X-Proxy-Secret", proxySecret);
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                    return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The probe only reports reachability; a failure here never fails a deploy.
                _log.Debug($"Edge not answering yet (attempt {attempt}): {ex.Message}");
            }

            if (attempt < 5)
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct).ConfigureAwait(false);
        }
        return false;
    }

    public string NormalizeWorkerName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "soul-remote-proxy";
        var cleaned = new string(name.Trim().ToLowerInvariant()
                .Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-').ToArray())
            .Trim('-');
        return string.IsNullOrEmpty(cleaned) ? "soul-remote-proxy" : cleaned;
    }

    // ---- transport helpers ----

    private static HttpRequestMessage Build(HttpMethod method, string url, string apiToken)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return req;
    }

    private async Task<T?> SendAsync<T>(HttpRequestMessage req, CancellationToken ct)
    {
        var body = await SendRawAsync(req, ct).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<CfResponse<T>>(body, JsonOptions)
                     ?? throw new InvalidOperationException("Cloudflare returned an empty response.");
        if (!parsed.Success)
            throw new InvalidOperationException("Cloudflare rejected the request: " + parsed.ErrorSummary());
        return parsed.Result;
    }

    private async Task<string> SendRawAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (resp.IsSuccessStatusCode)
            return body;

        var message = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}";
        try
        {
            var err = JsonSerializer.Deserialize<CfResponse<object>>(body, JsonOptions);
            if (err is { Success: false })
                message += " — " + err.ErrorSummary();
        }
        catch (JsonException)
        {
            // Body was not JSON; the status line is the best message available.
        }
        throw new InvalidOperationException(message);
    }

    private static string LoadWorkerScript()
    {
        var asm = Assembly.GetExecutingAssembly();
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("worker.js", StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
            throw new InvalidOperationException("The embedded worker.js resource is missing from this build.");
        using var stream = asm.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

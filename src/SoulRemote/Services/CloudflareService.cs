using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SoulRemote.Models;

namespace SoulRemote.Services;

public interface ICloudflareService
{
    Task<CfTokenVerify> VerifyTokenAsync(string apiToken, CancellationToken ct = default);
    Task<List<CfAccount>> GetAccountsAsync(string apiToken, CancellationToken ct = default);

    /// <summary>Verifies the token, deploys the proxy worker and returns its public URL.</summary>
    Task<CloudflareDeployResult> ConnectAndDeployAsync(
        string apiToken, string workerName, string proxySecret,
        string? preferredAccountId, CancellationToken ct = default);
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
            throw new InvalidOperationException("Cloudflare token is not active. Check the token and its permissions.");
        return result;
    }

    public async Task<List<CfAccount>> GetAccountsAsync(string apiToken, CancellationToken ct = default)
    {
        using var req = Build(HttpMethod.Get, $"{ApiBase}/accounts?per_page=50", apiToken);
        var accounts = await SendAsync<List<CfAccount>>(req, ct).ConfigureAwait(false);
        if (accounts is null || accounts.Count == 0)
            throw new InvalidOperationException("No Cloudflare accounts are accessible with this token.");
        return accounts;
    }

    public async Task<CloudflareDeployResult> ConnectAndDeployAsync(
        string apiToken, string workerName, string proxySecret,
        string? preferredAccountId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiToken))
            throw new ArgumentException("Cloudflare API token is required.", nameof(apiToken));

        workerName = NormalizeWorkerName(workerName);

        _log.Info("Verifying Cloudflare token...");
        await VerifyTokenAsync(apiToken, ct).ConfigureAwait(false);

        var accounts = await GetAccountsAsync(apiToken, ct).ConfigureAwait(false);
        var account = accounts.FirstOrDefault(a => a.Id == preferredAccountId) ?? accounts[0];
        _log.Info($"Using Cloudflare account '{account.Name}' ({account.Id}).");

        var subdomain = await GetWorkersDevSubdomainAsync(apiToken, account.Id, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(subdomain))
            throw new InvalidOperationException(
                "This account has no workers.dev subdomain yet. Open the Cloudflare dashboard > Workers & Pages once to register a free subdomain, then retry.");

        _log.Info($"Deploying worker '{workerName}'...");
        await UploadWorkerAsync(apiToken, account.Id, workerName, proxySecret, ct).ConfigureAwait(false);

        _log.Info("Enabling workers.dev route...");
        await EnableSubdomainAsync(apiToken, account.Id, workerName, ct).ConfigureAwait(false);

        var url = $"https://{workerName}.{subdomain}.workers.dev";
        _log.Info($"Worker deployed at {url}");

        // Confirm the worker actually answers (edge propagation can lag a few seconds).
        await VerifyWorkerReachableAsync(url, proxySecret, ct).ConfigureAwait(false);

        return new CloudflareDeployResult
        {
            AccountId = account.Id,
            AccountName = account.Name,
            Subdomain = subdomain,
            WorkerUrl = url,
        };
    }

    private async Task<string?> GetWorkersDevSubdomainAsync(string apiToken, string accountId, CancellationToken ct)
    {
        using var req = Build(HttpMethod.Get, $"{ApiBase}/accounts/{accountId}/workers/subdomain", apiToken);
        try
        {
            var sub = await SendAsync<CfSubdomain>(req, ct).ConfigureAwait(false);
            return sub?.Subdomain;
        }
        catch (Exception ex)
        {
            _log.Warning($"Could not read workers.dev subdomain: {ex.Message}");
            return null;
        }
    }

    private async Task UploadWorkerAsync(string apiToken, string accountId, string workerName, string proxySecret, CancellationToken ct)
    {
        var script = LoadWorkerScript();

        var bindings = new List<object>();
        if (!string.IsNullOrEmpty(proxySecret))
            bindings.Add(new { type = "secret_text", name = "PROXY_SECRET", text = proxySecret });

        var metadata = new
        {
            main_module = "worker.js",
            compatibility_date = CompatibilityDate,
            bindings = bindings,
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
        // Read to ensure success/error surfaces; result body is the script metadata.
        await SendRawAsync(req, ct).ConfigureAwait(false);
    }

    private async Task EnableSubdomainAsync(string apiToken, string accountId, string workerName, CancellationToken ct)
    {
        using var req = Build(HttpMethod.Post, $"{ApiBase}/accounts/{accountId}/workers/scripts/{workerName}/subdomain", apiToken);
        req.Content = new StringContent("{\"enabled\":true}", Encoding.UTF8, "application/json");
        try
        {
            await SendRawAsync(req, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Non-fatal: the route may already be enabled from a prior deploy.
            _log.Warning($"Enable subdomain returned: {ex.Message}");
        }
    }

    private async Task VerifyWorkerReachableAsync(string url, string proxySecret, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{url}/healthz");
                if (!string.IsNullOrEmpty(proxySecret))
                    req.Headers.TryAddWithoutValidation("X-Proxy-Secret", proxySecret);
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                    return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The health check must only WARN, never fail the deploy — the worker was
                // already uploaded. Swallow every attempt's transient error (incl. the last).
                _log.Debug($"Worker not reachable yet (attempt {attempt}): {ex.Message}");
            }
            if (attempt < 5)
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct).ConfigureAwait(false);
        }
        _log.Warning("Worker deployed but health check did not confirm reachability yet; it may still be propagating.");
    }

    // ---- helpers ----

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
        var parsed = JsonSerializer.Deserialize<CfResponse<T>>(body, JsonOptions);
        if (parsed is null)
            throw new InvalidOperationException("Empty response from Cloudflare.");
        if (!parsed.Success)
            throw new InvalidOperationException("Cloudflare API error: " + parsed.ErrorSummary());
        return parsed.Result;
    }

    private async Task<string> SendRawAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            // Try to surface the Cloudflare error array for a useful message.
            var message = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}";
            try
            {
                var err = JsonSerializer.Deserialize<CfResponse<object>>(body, JsonOptions);
                if (err is { Success: false })
                    message += " — " + err.ErrorSummary();
            }
            catch { /* body was not JSON */ }
            throw new InvalidOperationException(message);
        }
        return body;
    }

    private static string NormalizeWorkerName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "soul-remote-proxy";
        var cleaned = new string(name.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-').ToArray())
            .Trim('-');
        return string.IsNullOrEmpty(cleaned) ? "soul-remote-proxy" : cleaned;
    }

    private static string LoadWorkerScript()
    {
        var asm = Assembly.GetExecutingAssembly();
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("worker.js", StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
            throw new InvalidOperationException("Embedded worker.js resource not found.");
        using var stream = asm.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

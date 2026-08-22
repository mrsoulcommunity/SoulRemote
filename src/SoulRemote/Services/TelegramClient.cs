using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SoulRemote.Models;

namespace SoulRemote.Services;

public interface ITelegramClient
{
    void Configure(string workerUrl, string botToken, string proxySecret);
    bool IsConfigured { get; }

    Task<TgUser> GetMeAsync(CancellationToken ct = default);
    Task DeleteWebhookAsync(CancellationToken ct = default);
    Task<List<TgUpdate>> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken ct = default);
    Task<long?> SendMessageAsync(long chatId, string text, TgInlineKeyboardMarkup? keyboard = null, CancellationToken ct = default);

    /// <summary>Sends with any reply markup (inline keyboard, reply keyboard or force-reply).</summary>
    Task<long?> SendWithMarkupAsync(long chatId, string text, object? replyMarkup, CancellationToken ct = default);

    /// <summary>
    /// Rewrites an existing message in place so menu navigation does not flood the chat.
    /// Returns false when Telegram reports the content is unchanged.
    /// </summary>
    Task<bool> EditMessageAsync(long chatId, long messageId, string text, TgInlineKeyboardMarkup? keyboard, CancellationToken ct = default);
    Task SendPhotoAsync(long chatId, byte[] photo, string fileName, string? caption = null, CancellationToken ct = default);
    Task SendDocumentAsync(long chatId, byte[] file, string fileName, string? caption = null, CancellationToken ct = default);
    Task AnswerCallbackAsync(string callbackId, string? text = null, bool showAlert = false, CancellationToken ct = default);
}

/// <summary>
/// Talks to the Telegram Bot API, but always through the Cloudflare worker URL
/// so requests originate from Cloudflare's edge rather than api.telegram.org.
/// </summary>
public sealed class TelegramClient : ITelegramClient
{
    private readonly ILogService _log;
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private string _workerUrl = string.Empty;
    private string _botToken = string.Empty;
    private string _proxySecret = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_workerUrl) && !string.IsNullOrWhiteSpace(_botToken);

    public TelegramClient(ILogService log)
    {
        _log = log;
        // Timeout must exceed the longest long-poll; cancellation tokens do the fine control.
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SoulRemote/1.0");
    }

    public void Configure(string workerUrl, string botToken, string proxySecret)
    {
        _workerUrl = (workerUrl ?? string.Empty).TrimEnd('/');
        _botToken = botToken ?? string.Empty;
        _proxySecret = proxySecret ?? string.Empty;
    }

    private string MethodUrl(string method) => $"{_workerUrl}/bot{_botToken}/{method}";

    private void AddSecret(HttpRequestMessage req)
    {
        if (!string.IsNullOrEmpty(_proxySecret))
            req.Headers.TryAddWithoutValidation("X-Proxy-Secret", _proxySecret);
    }

    public async Task<TgUser> GetMeAsync(CancellationToken ct = default)
    {
        var result = await PostJsonAsync<TgUser>("getMe", new { }, ct).ConfigureAwait(false);
        return result ?? throw new InvalidOperationException("getMe returned no result.");
    }

    public async Task DeleteWebhookAsync(CancellationToken ct = default)
    {
        // Ensures getUpdates long-polling is allowed (a set webhook would block it).
        try
        {
            await PostJsonAsync<object>("deleteWebhook", new { drop_pending_updates = false }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Debug($"deleteWebhook: {ex.Message}");
        }
    }

    public async Task<List<TgUpdate>> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken ct = default)
    {
        var payload = new
        {
            offset,
            timeout = timeoutSeconds,
            allowed_updates = new[] { "message", "callback_query" },
        };
        var updates = await PostJsonAsync<List<TgUpdate>>("getUpdates", payload, ct).ConfigureAwait(false);
        return updates ?? new List<TgUpdate>();
    }

    public async Task<long?> SendMessageAsync(long chatId, string text, TgInlineKeyboardMarkup? keyboard = null, CancellationToken ct = default)
    {
        // Telegram caps message text at 4096 chars; split defensively. Our HTML messages are
        // line-structured (tags/entities never span a newline), so break on the last newline
        // before the limit to avoid splitting inside a tag or entity.
        if (string.IsNullOrEmpty(text))
            return null;

        const int max = 4000;
        long? lastId = null;
        var pos = 0;
        while (pos < text.Length)
        {
            var remaining = text.Length - pos;
            var take = Math.Min(max, remaining);
            if (take == max)
            {
                var nl = text.LastIndexOf('\n', pos + take - 1, take);
                if (nl > pos)
                    take = nl - pos + 1;
            }
            var chunk = text.Substring(pos, take);
            pos += take;
            var isLast = pos >= text.Length;
            var payload = new
            {
                chat_id = chatId,
                text = chunk,
                parse_mode = "HTML",
                disable_web_page_preview = true,
                reply_markup = isLast ? keyboard : null,
            };
            var msg = await PostJsonAsync<TgMessage>("sendMessage", payload, ct).ConfigureAwait(false);
            lastId = msg?.MessageId;
        }
        return lastId;
    }

    public async Task<long?> SendWithMarkupAsync(long chatId, string text, object? replyMarkup, CancellationToken ct = default)
    {
        var payload = new
        {
            chat_id = chatId,
            text,
            parse_mode = "HTML",
            disable_web_page_preview = true,
            reply_markup = replyMarkup,
        };
        var msg = await PostJsonAsync<TgMessage>("sendMessage", payload, ct).ConfigureAwait(false);
        return msg?.MessageId;
    }

    public async Task<bool> EditMessageAsync(long chatId, long messageId, string text, TgInlineKeyboardMarkup? keyboard, CancellationToken ct = default)
    {
        var payload = new
        {
            chat_id = chatId,
            message_id = messageId,
            text,
            parse_mode = "HTML",
            disable_web_page_preview = true,
            reply_markup = keyboard,
        };
        try
        {
            await PostJsonAsync<TgMessage>("editMessageText", payload, ct).ConfigureAwait(false);
            return true;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not modified", StringComparison.OrdinalIgnoreCase))
        {
            // Tapping the button for the screen you are already on is not an error.
            return false;
        }
    }

    public Task SendPhotoAsync(long chatId, byte[] photo, string fileName, string? caption = null, CancellationToken ct = default)
        => SendFileAsync("sendPhoto", "photo", chatId, photo, fileName, caption, "image/png", ct);

    public Task SendDocumentAsync(long chatId, byte[] file, string fileName, string? caption = null, CancellationToken ct = default)
        => SendFileAsync("sendDocument", "document", chatId, file, fileName, caption, "application/octet-stream", ct);

    public async Task AnswerCallbackAsync(string callbackId, string? text = null, bool showAlert = false, CancellationToken ct = default)
    {
        try
        {
            // Telegram caps callback toasts at 200 characters.
            var toast = text ?? string.Empty;
            if (toast.Length > 195) toast = toast[..195];
            await PostJsonAsync<object>("answerCallbackQuery",
                new { callback_query_id = callbackId, text = toast, show_alert = showAlert }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Debug($"answerCallbackQuery: {ex.Message}");
        }
    }

    private async Task SendFileAsync(string method, string field, long chatId, byte[] data, string fileName, string? caption, string contentType, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(chatId.ToString()), "chat_id");
        if (!string.IsNullOrEmpty(caption))
        {
            form.Add(new StringContent(caption), "caption");
            form.Add(new StringContent("HTML"), "parse_mode");
        }
        var fileContent = new ByteArrayContent(data);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(fileContent, field, fileName);

        using var req = new HttpRequestMessage(HttpMethod.Post, MethodUrl(method)) { Content = form };
        AddSecret(req);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        EnsureOk(body, method, resp);
    }

    private async Task<T?> PostJsonAsync<T>(string method, object payload, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Telegram client is not configured.");

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        using var req = new HttpRequestMessage(HttpMethod.Post, MethodUrl(method))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        AddSecret(req);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var parsed = EnsureOk<T>(body, method, resp);
        return parsed.Result;
    }

    private static TgResponse<T> EnsureOk<T>(string body, string method, HttpResponseMessage resp)
    {
        TgResponse<T>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<TgResponse<T>>(body, JsonOptions);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(
                $"Telegram '{method}' returned non-JSON (HTTP {(int)resp.StatusCode}). The proxy may be misconfigured. Body: {Trim(body)}");
        }
        if (parsed is null)
            throw new InvalidOperationException($"Telegram '{method}' returned an empty response.");
        if (!parsed.Ok)
            throw new InvalidOperationException($"Telegram '{method}' failed: {parsed.ErrorCode} {parsed.Description}");
        return parsed;
    }

    private static void EnsureOk(string body, string method, HttpResponseMessage resp)
        => EnsureOk<object>(body, method, resp);

    private static string Trim(string s) => s.Length > 200 ? s.Substring(0, 200) + "..." : s;
}

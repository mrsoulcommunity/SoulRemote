using System.Net;
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

    /// <summary>Resolves a file_id to a downloadable path on the Bot API host.</summary>
    Task<TgFile> GetFileAsync(string fileId, CancellationToken ct = default);

    /// <summary>Downloads a file the bot has been sent, through the same proxy.</summary>
    Task<byte[]> DownloadFileAsync(string filePath, long maxBytes, CancellationToken ct = default);

    /// <summary>
    /// Shows "typing" or "uploading photo" in the chat. A screenshot on a slow link can
    /// take several seconds, and without this the bot looks as though it ignored the tap.
    /// </summary>
    Task SendChatActionAsync(long chatId, string action, CancellationToken ct = default);

    /// <summary>Publishes the typed-command list Telegram shows behind the "/" button.</summary>
    Task SetMyCommandsAsync(IReadOnlyList<(string Command, string Description)> commands, CancellationToken ct = default);
}

/// <summary>
/// Talks to the Telegram Bot API, but always through the Cloudflare worker URL
/// so requests originate from Cloudflare's edge rather than api.telegram.org.
/// </summary>
public sealed class TelegramClient : ITelegramClient, IDisposable
{
    /// <summary>Telegram's own cap on message text.</summary>
    public const int MaxMessageLength = 4096;

    /// <summary>What we actually pack into one message, leaving room for markup.</summary>
    public const int ChunkLength = 4000;

    /// <summary>Telegram's cap on a photo or document caption.</summary>
    public const int MaxCaptionLength = 1024;

    /// <summary>Telegram's cap on the text of a callback answer.</summary>
    public const int MaxCallbackAnswerLength = 200;

    /// <summary>sendPhoto rejects anything larger; bigger images go as documents.</summary>
    public const long MaxPhotoBytes = 10L * 1024 * 1024;

    /// <summary>A bot may not upload more than this in one file.</summary>
    public const long MaxUploadBytes = 50L * 1024 * 1024;

    /// <summary>A bot may not download more than this through getFile.</summary>
    public const long MaxDownloadBytes = 20L * 1024 * 1024;

    private const int SendAttempts = 3;
    private static readonly TimeSpan MaxFloodWait = TimeSpan.FromSeconds(30);

    private readonly ILogService _log;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private string _workerUrl = string.Empty;
    private string _botToken = string.Empty;
    private string _proxySecret = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_workerUrl) && !string.IsNullOrWhiteSpace(_botToken);

    /// <param name="handler">
    /// Transport override. Production leaves this null and gets the default handler;
    /// the tests hand in a stub so the whole request/response contract — including
    /// flood control — can be exercised without a network.
    /// </param>
    public TelegramClient(ILogService log, HttpMessageHandler? handler = null)
    {
        _log = log;
        // Timeout must exceed the longest long-poll; cancellation tokens do the fine control.
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(120);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SoulRemote/1.0");
        _ownsHttp = true;
    }

    public void Configure(string workerUrl, string botToken, string proxySecret)
    {
        _workerUrl = (workerUrl ?? string.Empty).TrimEnd('/');
        _botToken = botToken ?? string.Empty;
        _proxySecret = proxySecret ?? string.Empty;
    }

    private string MethodUrl(string method) => $"{_workerUrl}/bot{_botToken}/{method}";

    private string FileUrl(string filePath) => $"{_workerUrl}/file/bot{_botToken}/{filePath}";

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
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
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
        // No internal retry: the poll loop owns backoff and must stay responsive to cancellation.
        var updates = await PostJsonAsync<List<TgUpdate>>("getUpdates", payload, ct, attempts: 1).ConfigureAwait(false);
        return updates ?? new List<TgUpdate>();
    }

    public async Task<long?> SendMessageAsync(long chatId, string text, TgInlineKeyboardMarkup? keyboard = null, CancellationToken ct = default)
    {
        var chunks = TextUtil.SplitForTelegram(text, ChunkLength);
        long? lastId = null;
        for (var i = 0; i < chunks.Count; i++)
        {
            var isLast = i == chunks.Count - 1;
            var payload = new
            {
                chat_id = chatId,
                text = chunks[i],
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
            text = TextUtil.Clip(text, MaxMessageLength),
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
            text = TextUtil.Clip(text, MaxMessageLength),
            parse_mode = "HTML",
            disable_web_page_preview = true,
            reply_markup = keyboard,
        };
        try
        {
            await PostJsonAsync<TgMessage>("editMessageText", payload, ct).ConfigureAwait(false);
            return true;
        }
        catch (TelegramApiException ex) when (ex.Description?.Contains("not modified", StringComparison.OrdinalIgnoreCase) == true)
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
            await PostJsonAsync<object>("answerCallbackQuery", new
            {
                callback_query_id = callbackId,
                text = TextUtil.Clip(text, MaxCallbackAnswerLength),
                show_alert = showAlert,
            }, ct, attempts: 1).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // A toast that does not land is cosmetic; never let it fail the action behind it.
            _log.Debug($"answerCallbackQuery: {ex.Message}");
        }
    }

    public async Task SendChatActionAsync(long chatId, string action, CancellationToken ct = default)
    {
        try
        {
            await PostJsonAsync<object>("sendChatAction",
                new { chat_id = chatId, action }, ct, attempts: 1).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Purely cosmetic; never let it fail the work it was announcing.
            _log.Debug($"sendChatAction: {ex.Message}");
        }
    }

    public async Task SetMyCommandsAsync(IReadOnlyList<(string Command, string Description)> commands, CancellationToken ct = default)
    {
        try
        {
            var payload = new
            {
                commands = commands.Select(c => new { command = c.Command, description = c.Description }).ToArray(),
            };
            await PostJsonAsync<object>("setMyCommands", payload, ct, attempts: 1).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.Debug($"setMyCommands: {ex.Message}");
        }
    }

    public async Task<TgFile> GetFileAsync(string fileId, CancellationToken ct = default)
    {
        var file = await PostJsonAsync<TgFile>("getFile", new { file_id = fileId }, ct).ConfigureAwait(false);
        if (file is null || string.IsNullOrEmpty(file.FilePath))
            throw new InvalidOperationException("Telegram did not return a path for that file.");
        return file;
    }

    public async Task<byte[]> DownloadFileAsync(string filePath, long maxBytes, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, FileUrl(filePath));
        AddSecret(req);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Downloading the file failed (HTTP {(int)resp.StatusCode}).");

        // Trust the declared length when there is one, but still stop reading at the cap:
        // a chunked response can lie about its size, and this buffer is held in memory.
        if (resp.Content.Headers.ContentLength is { } declared && declared > maxBytes)
            throw new InvalidOperationException($"That file is {TextUtil.HumanBytes(declared)}, over the {TextUtil.HumanBytes(maxBytes)} limit.");

        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new InvalidOperationException($"That file is over the {TextUtil.HumanBytes(maxBytes)} limit.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private async Task SendFileAsync(string method, string field, long chatId, byte[] data, string fileName, string? caption, string contentType, CancellationToken ct)
    {
        if (data.LongLength > MaxUploadBytes)
            throw new InvalidOperationException(
                $"That is {TextUtil.HumanBytes(data.LongLength)} — Telegram only accepts up to {TextUtil.HumanBytes(MaxUploadBytes)} from a bot.");

        await WithRetriesAsync(method, async _ =>
        {
            // A MultipartFormDataContent cannot be replayed, so each attempt builds its own.
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(chatId.ToString(System.Globalization.CultureInfo.InvariantCulture)), "chat_id");
            if (!string.IsNullOrEmpty(caption))
            {
                form.Add(new StringContent(TextUtil.Clip(caption, MaxCaptionLength), Encoding.UTF8), "caption");
                form.Add(new StringContent("HTML"), "parse_mode");
            }
            var fileContent = new ByteArrayContent(data);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            form.Add(fileContent, field, fileName);

            using var req = new HttpRequestMessage(HttpMethod.Post, MethodUrl(method)) { Content = form };
            AddSecret(req);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            EnsureOk<object>(body, method, resp);
            return (object?)null;
        }, SendAttempts, ct).ConfigureAwait(false);
    }

    private async Task<T?> PostJsonAsync<T>(string method, object payload, CancellationToken ct, int attempts = SendAttempts)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Telegram client is not configured.");

        var json = JsonSerializer.Serialize(payload, JsonOptions);

        return await WithRetriesAsync(method, async _ =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, MethodUrl(method))
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            AddSecret(req);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return EnsureOk<T>(body, method, resp).Result;
        }, attempts, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one API call, honouring Telegram's flood control.
    ///
    /// A 429 carries <c>parameters.retry_after</c>: the number of seconds before the
    /// call will be accepted. Treating that as a plain failure is what makes a bot
    /// look broken under load — the reply is simply dropped. So the wait is obeyed
    /// (up to a ceiling, past which the caller deserves an error rather than a stall),
    /// and transient 5xx / transport faults get a short backoff too. Everything else —
    /// a 400 for bad markup, a 403 from a blocked chat — fails immediately, because
    /// retrying it would only repeat the same mistake.
    /// </summary>
    private async Task<T?> WithRetriesAsync<T>(string method, Func<int, Task<T?>> attempt, int attempts, CancellationToken ct)
    {
        for (var i = 1; ; i++)
        {
            try
            {
                return await attempt(i).ConfigureAwait(false);
            }
            catch (TelegramApiException ex) when (i < attempts && ex.RetryAfter is { } wait && wait <= MaxFloodWait)
            {
                _log.Warning($"Telegram is rate-limiting '{method}'; waiting {wait.TotalSeconds:0}s.");
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
            catch (TelegramApiException ex) when (i < attempts && ex.IsTransient)
            {
                _log.Debug($"Telegram '{method}' failed transiently ({ex.Message}); retrying.");
                await Task.Delay(TimeSpan.FromSeconds(i), ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (i < attempts)
            {
                _log.Debug($"Telegram '{method}' transport error ({ex.Message}); retrying.");
                await Task.Delay(TimeSpan.FromSeconds(i), ct).ConfigureAwait(false);
            }
        }
    }

    internal static TgResponse<T> EnsureOk<T>(string body, string method, HttpResponseMessage resp)
    {
        TgResponse<T>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<TgResponse<T>>(body, JsonOptions);
        }
        catch (JsonException)
        {
            throw new TelegramApiException(
                $"Telegram '{method}' returned non-JSON (HTTP {(int)resp.StatusCode}). The proxy may be misconfigured. Body: {Trim(body)}",
                (int)resp.StatusCode, null, null, IsTransientStatus(resp.StatusCode));
        }
        if (parsed is null)
            throw new TelegramApiException($"Telegram '{method}' returned an empty response.",
                (int)resp.StatusCode, null, null, IsTransientStatus(resp.StatusCode));
        if (!parsed.Ok)
        {
            var retryAfter = parsed.Parameters?.RetryAfter is { } seconds && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : (TimeSpan?)null;
            var code = parsed.ErrorCode ?? (int)resp.StatusCode;
            throw new TelegramApiException(
                $"Telegram '{method}' failed: {code} {parsed.Description}",
                code, parsed.Description, retryAfter,
                IsTransientStatus((HttpStatusCode)code));
        }
        return parsed;
    }

    private static bool IsTransientStatus(HttpStatusCode status) =>
        (int)status >= 500 || status == HttpStatusCode.RequestTimeout;

    private static string Trim(string s) => s.Length > 200 ? s[..200] + "..." : s;

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}

/// <summary>
/// A call Telegram itself refused, carrying enough detail to decide what to do next:
/// the numeric error code, the human description, and the flood-control wait when
/// there is one.
/// </summary>
public sealed class TelegramApiException : Exception
{
    public TelegramApiException(string message, int errorCode, string? description, TimeSpan? retryAfter, bool isTransient)
        : base(message)
    {
        ErrorCode = errorCode;
        Description = description;
        RetryAfter = retryAfter;
        IsTransient = isTransient;
    }

    public int ErrorCode { get; }
    public string? Description { get; }
    public TimeSpan? RetryAfter { get; }
    public bool IsTransient { get; }
}

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

    /// <summary>
    /// Looks custom ("premium") emoji up by identifier. An incoming message names the
    /// emoji a user sent only by its id; this is what turns that into the ordinary
    /// emoji it stands for and the pack it came from. Telegram takes at most 200 ids
    /// at a time and asks for no privilege to read them.
    /// </summary>
    Task<IReadOnlyList<TgSticker>> GetCustomEmojiStickersAsync(IReadOnlyList<string> ids, CancellationToken ct = default);

    /// <summary>Fetches a whole sticker set — for an emoji pack, every custom emoji in it.</summary>
    Task<TgStickerSet> GetStickerSetAsync(string name, CancellationToken ct = default);
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

    /// <summary>getCustomEmojiStickers takes at most this many identifiers per call.</summary>
    public const int MaxCustomEmojiLookup = 200;

    private const int SendAttempts = 3;
    private static readonly TimeSpan MaxFloodWait = TimeSpan.FromSeconds(30);

    private readonly ILogService _log;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    /// <summary>
    /// The custom-emoji look, applied here rather than where messages are composed.
    /// Null on a client built without one — the tests, and any path that wants the
    /// bot's plain output.
    /// </summary>
    private readonly IEmojiStyler? _emoji;
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
    /// <param name="emoji">
    /// Dresses outgoing text and buttons in the user's premium emoji. Optional so a
    /// client can be built before the setting it reads exists, and so the tests can
    /// exercise the wire format without one.
    /// </param>
    public TelegramClient(ILogService log, HttpMessageHandler? handler = null, IEmojiStyler? emoji = null)
    {
        _log = log;
        _emoji = emoji;
        // Timeout must exceed the longest long-poll; cancellation tokens do the fine control.
        _http = handler is null
            ? new HttpClient(CreateHandler(), disposeHandler: true)
            : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(120);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SoulRemote/1.0");
        _ownsHttp = true;
    }


    /// <summary>
    /// The transport every outbound call shares.
    ///
    /// The default handler pools connections for the life of the process. On the
    /// networks this app is built for that is the wrong default by a wide margin: a
    /// censored link changes routes and DNS answers under you, and a pooled connection
    /// to an address that has stopped working keeps being reused until something
    /// forces it closed. Capping the lifetime means the relay re-resolves and
    /// re-connects on its own within a couple of minutes instead of staying wedged.
    /// </summary>
    internal static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(90),
        ConnectTimeout = TimeSpan.FromSeconds(15),
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        EnableMultipleHttp2Connections = true,
    };

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
        // Split first, decorate second. The splitter reasons about markup it can see,
        // and a custom emoji tag is fifty characters wrapped round one — expanding
        // before the split would move every boundary and could put a tag across two
        // messages. Telegram counts a message after its entities are parsed anyway,
        // so the tags cost nothing against the limit.
        var chunks = TextUtil.SplitForTelegram(text, ChunkLength);
        long? lastId = null;
        for (var i = 0; i < chunks.Count; i++)
        {
            var isLast = i == chunks.Count - 1;
            var markup = isLast ? keyboard : null;
            var msg = await SendDecoratedAsync("sendMessage", chunks[i], markup,
                (body, reply) => new
                {
                    chat_id = chatId,
                    text = body,
                    parse_mode = "HTML",
                    disable_web_page_preview = true,
                    reply_markup = reply,
                }, ct).ConfigureAwait(false);
            lastId = msg?.MessageId;
        }
        return lastId;
    }

    public async Task<long?> SendWithMarkupAsync(long chatId, string text, object? replyMarkup, CancellationToken ct = default)
    {
        var msg = await SendDecoratedAsync("sendMessage", TextUtil.Clip(text, MaxMessageLength), replyMarkup,
            (body, reply) => new
            {
                chat_id = chatId,
                text = body,
                parse_mode = "HTML",
                disable_web_page_preview = true,
                reply_markup = reply,
            }, ct).ConfigureAwait(false);
        return msg?.MessageId;
    }

    public async Task<bool> EditMessageAsync(long chatId, long messageId, string text, TgInlineKeyboardMarkup? keyboard, CancellationToken ct = default)
    {
        try
        {
            await SendDecoratedAsync("editMessageText", TextUtil.Clip(text, MaxMessageLength), keyboard,
                (body, reply) => new
                {
                    chat_id = chatId,
                    message_id = messageId,
                    text = body,
                    parse_mode = "HTML",
                    disable_web_page_preview = true,
                    reply_markup = reply,
                }, ct).ConfigureAwait(false);
            return true;
        }
        catch (TelegramApiException ex) when (ex.Description?.Contains("not modified", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Tapping the button for the screen you are already on is not an error.
            return false;
        }
    }

    public Task SendPhotoAsync(long chatId, byte[] photo, string fileName, string? caption = null, CancellationToken ct = default)
        => SendFileWithFallbackAsync("sendPhoto", "photo", chatId, photo, fileName, caption, "image/png", ct);

    public Task SendDocumentAsync(long chatId, byte[] file, string fileName, string? caption = null, CancellationToken ct = default)
        => SendFileWithFallbackAsync("sendDocument", "document", chatId, file, fileName, caption, "application/octet-stream", ct);

    /// <summary>
    /// Uploads once with the caption decorated, and again plain if Telegram refuses it
    /// over a custom emoji.
    ///
    /// A 400 is not transient, so the retry loop will not touch it: without this, one
    /// bad identifier in the map turns every screenshot into an error message. The
    /// upload is repeated in full because a caption cannot be edited onto a message
    /// that was never accepted, and a screenshot the user asked for matters rather
    /// more than the bytes.
    /// </summary>
    private async Task SendFileWithFallbackAsync(string method, string field, long chatId, byte[] data,
        string fileName, string? caption, string contentType, CancellationToken ct)
    {
        // Clipped before it is decorated, and decorated exactly once: clipping a
        // caption that already carried tags could cut one in half, and the tags do not
        // count towards Telegram's limit anyway — it measures the text after entities
        // are parsed.
        var plain = TextUtil.Clip(caption, MaxCaptionLength);
        var decorated = _emoji?.Decorate(plain) ?? plain;

        try
        {
            await SendFileAsync(method, field, chatId, data, fileName, decorated, contentType, ct)
                .ConfigureAwait(false);
        }
        // Only when something was actually added. A document really can be invalid on
        // its own account, and a refusal blamed on emoji that were never there would
        // switch the whole feature off over an unrelated upload.
        catch (TelegramApiException ex) when (!ReferenceEquals(decorated, plain) && IsCustomEmojiRefusal(ex))
        {
            _emoji?.ReportRejected(ex.Description);
            await SendFileAsync(method, field, chatId, data, fileName, plain, contentType, ct)
                .ConfigureAwait(false);
        }
    }

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

    public async Task<IReadOnlyList<TgSticker>> GetCustomEmojiStickersAsync(
        IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        if (ids is null || ids.Count == 0)
            return Array.Empty<TgSticker>();

        // Telegram takes 200 at a time and answers a longer list with an error rather
        // than a short result, so the cap is honoured here instead of being discovered.
        var wanted = ids.Take(MaxCustomEmojiLookup).ToArray();
        var stickers = await PostJsonAsync<List<TgSticker>>(
            "getCustomEmojiStickers", new { custom_emoji_ids = wanted }, ct).ConfigureAwait(false);
        return stickers ?? (IReadOnlyList<TgSticker>)Array.Empty<TgSticker>();
    }

    public async Task<TgStickerSet> GetStickerSetAsync(string name, CancellationToken ct = default)
    {
        var set = await PostJsonAsync<TgStickerSet>("getStickerSet", new { name }, ct).ConfigureAwait(false);
        return set ?? throw new InvalidOperationException("Telegram returned no sticker set by that name.");
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

    /// <param name="caption">Already clipped and already decorated by the caller.</param>
    private async Task SendFileAsync(string method, string field, long chatId, byte[] data, string fileName,
        string? caption, string contentType, CancellationToken ct)
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
                form.Add(new StringContent(caption, Encoding.UTF8), "caption");
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

    /// <summary>
    /// Sends one message-shaped call with the user's premium emoji applied, and falls
    /// back to the plain version if Telegram will not take it.
    ///
    /// Two different failures are covered, because Telegram has two ways of saying no.
    /// A malformed identifier is a hard 400, which is caught here and answered by
    /// resending the undecorated text — a reply the user was waiting for must not be
    /// lost to a cosmetic setting. A bot that simply is not entitled to custom emoji
    /// gets no error at all: the message goes through with its entities quietly
    /// removed, which is why the echoed message is handed to the styler to read.
    /// </summary>
    /// <param name="build">Builds the request body from the text and markup to send.</param>
    private async Task<TgMessage?> SendDecoratedAsync(
        string method, string text, object? markup,
        Func<string, object?, object> build, CancellationToken ct)
    {
        var decoratedText = _emoji?.Decorate(text) ?? text;
        var decoratedMarkup = _emoji?.DecorateMarkup(markup) ?? markup;
        var changed = !ReferenceEquals(decoratedText, text) || !ReferenceEquals(decoratedMarkup, markup);

        try
        {
            var msg = await PostJsonAsync<TgMessage>(method, build(decoratedText, decoratedMarkup), ct)
                .ConfigureAwait(false);
            if (changed)
                _emoji?.Observe(decoratedText, msg);
            return msg;
        }
        catch (TelegramApiException ex) when (changed && IsCustomEmojiRefusal(ex))
        {
            _emoji?.ReportRejected(ex.Description);
            return await PostJsonAsync<TgMessage>(method, build(text, markup), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// True when Telegram refused the call over a custom emoji rather than over its
    /// content.
    ///
    /// The obvious strings are not the whole list. An identifier that parses as a
    /// number but names no sticker comes back as a bare "DOCUMENT_INVALID" — the
    /// custom emoji IS a document, and Telegram is complaining about the document
    /// rather than about the emoji. That was found the hard way: a startup
    /// notification carrying one was lost, because a description that never said
    /// "emoji" did not look like an emoji problem and the plain retry never ran.
    /// </summary>
    private static bool IsCustomEmojiRefusal(TelegramApiException ex)
    {
        if (ex.Description is not { Length: > 0 } d)
            return false;
        foreach (var marker in CustomEmojiRefusals)
        {
            if (d.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// What Telegram says when it will not take a custom emoji. Matched as substrings
    /// because the description is prefixed ("Bad Request: ...") and sometimes suffixed.
    /// </summary>
    private static readonly string[] CustomEmojiRefusals =
    {
        "custom emoji",         // "Invalid custom emoji identifier specified"
        "tg-emoji",             // "Unsupported start tag \"tg-emoji\"" on an old API server
        "CUSTOM_EMOJI_INVALID",
        "DOCUMENT_INVALID",     // a well-formed identifier that names no sticker
        "EMOJI_INVALID",
    };

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
            // A retry_after of 0 is still flood control — it means "try again now" —
            // so it must not fall through to the generic failure path.
            var retryAfter = parsed.Parameters?.RetryAfter is { } seconds && seconds >= 0
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

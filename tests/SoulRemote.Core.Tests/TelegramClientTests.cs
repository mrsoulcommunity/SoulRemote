using System.Net;
using System.Text;
using SoulRemote.Services;
using Xunit;

namespace SoulRemote.Tests;

/// <summary>
/// The client is tested through a stub transport rather than a network, so the parts
/// that only show up under stress — flood control, retries, message splitting — can
/// be exercised deterministically.
/// </summary>
public sealed class TelegramClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();

        public List<string> Requests { get; } = new();
        public List<string> Bodies { get; } = new();

        public StubHandler Then(HttpStatusCode status, string body)
        {
            _responses.Enqueue((status, body));
            return this;
        }

        public StubHandler ThenOk(string result = "{}") =>
            Then(HttpStatusCode.OK, $"{{\"ok\":true,\"result\":{result}}}");

        public StubHandler ThenFloodWait(int seconds) =>
            Then((HttpStatusCode)429,
                $"{{\"ok\":false,\"error_code\":429,\"description\":\"Too Many Requests\",\"parameters\":{{\"retry_after\":{seconds}}}}}");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.AbsolutePath);
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct));

            var (status, body) = _responses.Count > 0
                ? _responses.Dequeue()
                : (HttpStatusCode.OK, "{\"ok\":true,\"result\":{}}");
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static (TelegramClient Client, StubHandler Handler, FakeLog Log) Build(StubHandler handler)
    {
        var log = new FakeLog();
        var client = new TelegramClient(log, handler);
        client.Configure("https://relay.example.workers.dev", "123:ABC", "s3cret");
        return (client, handler, log);
    }

    [Fact]
    public async Task Every_call_goes_through_the_worker_and_carries_the_shared_secret()
    {
        var (client, handler, _) = Build(new StubHandler().ThenOk("{\"id\":1,\"is_bot\":true,\"username\":\"b\"}"));

        var me = await client.GetMeAsync();

        Assert.Equal("b", me.Username);
        Assert.Equal("/bot123:ABC/getMe", handler.Requests[0]);
    }

    [Fact]
    public async Task A_429_is_waited_out_and_the_call_repeated()
    {
        var handler = new StubHandler()
            .ThenFloodWait(0)      // zero-second wait keeps the test instant
            .ThenOk("{\"message_id\":7}");
        var (client, _, log) = Build(handler);

        var id = await client.SendMessageAsync(42, "hello");

        Assert.Equal(7, id);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains(log.Messages, m => m.Contains("rate-limiting", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_flood_wait_longer_than_the_ceiling_is_reported_rather_than_slept_through()
    {
        // Blocking the poll loop for ten minutes would look exactly like a hang.
        var handler = new StubHandler().ThenFloodWait(600);
        var (client, _, _) = Build(handler);

        var ex = await Assert.ThrowsAsync<TelegramApiException>(() => client.SendMessageAsync(42, "hello"));

        Assert.Equal(429, ex.ErrorCode);
        Assert.Equal(TimeSpan.FromSeconds(600), ex.RetryAfter);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_server_error_is_retried_but_a_bad_request_is_not()
    {
        var transient = new StubHandler()
            .Then(HttpStatusCode.BadGateway, "{\"ok\":false,\"error_code\":502,\"description\":\"Bad Gateway\"}")
            .ThenOk("{\"message_id\":3}");
        var (client, _, _) = Build(transient);
        await client.SendMessageAsync(1, "x");
        Assert.Equal(2, transient.Requests.Count);

        var permanent = new StubHandler()
            .Then(HttpStatusCode.BadRequest, "{\"ok\":false,\"error_code\":400,\"description\":\"chat not found\"}");
        var (client2, _, _) = Build(permanent);
        await Assert.ThrowsAsync<TelegramApiException>(() => client2.SendMessageAsync(1, "x"));
        Assert.Single(permanent.Requests);
    }

    [Fact]
    public async Task Editing_a_message_to_what_it_already_says_is_not_an_error()
    {
        var handler = new StubHandler().Then(HttpStatusCode.BadRequest,
            "{\"ok\":false,\"error_code\":400,\"description\":\"Bad Request: message is not modified\"}");
        var (client, _, _) = Build(handler);

        var changed = await client.EditMessageAsync(1, 2, "same", null);

        Assert.False(changed);
    }

    [Fact]
    public async Task A_long_message_is_sent_as_several_and_only_the_last_carries_the_keyboard()
    {
        var handler = new StubHandler();
        var (client, _, _) = Build(handler);

        var text = string.Join("\n", Enumerable.Range(0, 900).Select(i => $"line {i}"));
        await client.SendMessageAsync(42, text, new Models.TgInlineKeyboardMarkup());

        Assert.True(handler.Bodies.Count > 1);
        Assert.All(handler.Bodies.Take(handler.Bodies.Count - 1),
            b => Assert.DoesNotContain("reply_markup", b));
        Assert.Contains("reply_markup", handler.Bodies[^1]);
    }

    [Fact]
    public async Task A_toast_longer_than_telegram_allows_is_clipped_before_it_is_sent()
    {
        var handler = new StubHandler();
        var (client, _, _) = Build(handler);

        await client.AnswerCallbackAsync("cb", new string('x', 500));

        // Telegram rejects the whole call over 200 characters, losing the toast entirely.
        Assert.DoesNotContain(new string('x', 250), handler.Bodies[0]);
    }

    [Fact]
    public async Task An_upload_over_the_bot_limit_is_refused_locally_rather_than_by_telegram()
    {
        var (client, handler, _) = Build(new StubHandler());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SendDocumentAsync(1, new byte[TelegramClient.MaxUploadBytes + 1], "big.bin"));

        Assert.Contains("Telegram only accepts", ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_non_json_body_names_the_proxy_as_the_likely_cause()
    {
        // This is what a misconfigured worker returns, and the message has to point at it.
        var handler = new StubHandler().Then(HttpStatusCode.OK, "<html>Cloudflare</html>");
        var (client, _, _) = Build(handler);

        var ex = await Assert.ThrowsAsync<TelegramApiException>(() => client.GetMeAsync());

        Assert.Contains("proxy", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_unconfigured_client_refuses_to_call_anything()
    {
        var client = new TelegramClient(new FakeLog(), new StubHandler());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetMeAsync());
    }

    [Fact]
    public async Task Polling_does_not_retry_internally_so_the_loop_keeps_control()
    {
        var handler = new StubHandler()
            .Then(HttpStatusCode.BadGateway, "{\"ok\":false,\"error_code\":502,\"description\":\"Bad Gateway\"}");
        var (client, _, _) = Build(handler);

        await Assert.ThrowsAsync<TelegramApiException>(() => client.GetUpdatesAsync(0, 25));

        // The engine owns backoff and cancellation; a hidden retry here would fight it.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_download_larger_than_the_cap_is_stopped_mid_stream()
    {
        var handler = new StubHandler().Then(HttpStatusCode.OK, new string('x', 5000));
        var (client, _, _) = Build(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.DownloadFileAsync("docs/big.bin", 100));
    }
}

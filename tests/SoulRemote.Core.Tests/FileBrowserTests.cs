using SoulRemote.Models;
using SoulRemote.Services;
using Xunit;

namespace SoulRemote.Tests;

public sealed class FileBrowserTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "soulremote-files-" + Guid.NewGuid().ToString("N"));

    public FileBrowserTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "subfolder"));
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "hello");
        File.WriteAllBytes(Path.Combine(_root, "data.bin"), new byte[2048]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    [Fact]
    public void Listing_shows_folders_before_files()
    {
        var listing = new FileBrowser().List(1, _root);

        Assert.Equal(3, listing.Entries.Count);
        Assert.True(listing.Entries[0].IsDirectory);
        Assert.Equal("subfolder", listing.Entries[0].Name);
        Assert.All(listing.Entries.Skip(1), e => Assert.False(e.IsDirectory));
    }

    [Fact]
    public void Listing_remembers_where_each_chat_is()
    {
        var browser = new FileBrowser();
        browser.List(1, _root);
        browser.List(2, Path.Combine(_root, "subfolder"));

        Assert.Equal(_root, browser.CurrentFor(1)!.Path);
        Assert.Equal(Path.Combine(_root, "subfolder"), browser.CurrentFor(2)!.Path);
        Assert.Null(browser.CurrentFor(3));
    }

    [Fact]
    public void Forgetting_a_chat_drops_its_position()
    {
        var browser = new FileBrowser();
        browser.List(1, _root);
        browser.Forget(1);

        // A chat that has just been un-paired must not leave a folder listing behind
        // that a stale button could still resolve against.
        Assert.Null(browser.CurrentFor(1));
    }

    [Fact]
    public void A_missing_folder_is_reported_in_words_rather_than_as_a_dotnet_exception()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new FileBrowser().List(1, Path.Combine(_root, "nope")));

        Assert.DoesNotContain("Exception", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reading_a_file_returns_its_bytes()
    {
        var bytes = new FileBrowser().Read(Path.Combine(_root, "notes.txt"), 1024);
        Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void A_file_over_the_limit_is_refused_before_it_is_read_into_memory()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new FileBrowser().Read(Path.Combine(_root, "data.bin"), 100));

        Assert.Contains("2 KB", ex.Message);
    }

    [Fact]
    public void A_saved_file_cannot_choose_its_own_directory()
    {
        var browser = new FileBrowser();
        var target = Path.Combine(_root, "incoming");

        // The sender controls this name; a path in it must not escape the folder.
        var saved = browser.Save(@"..\..\evil.txt", new byte[] { 1 }, target);

        Assert.Equal(target, Path.GetDirectoryName(saved));
        Assert.Equal("evil.txt", Path.GetFileName(saved));
    }

    [Fact]
    public void A_saved_file_never_silently_replaces_one_already_there()
    {
        var browser = new FileBrowser();
        var target = Path.Combine(_root, "incoming");

        var first = browser.Save("report.txt", new byte[] { 1 }, target);
        var second = browser.Save("report.txt", new byte[] { 2 }, target);

        Assert.NotEqual(first, second);
        Assert.Equal("report (2).txt", Path.GetFileName(second));
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(first));
    }

    [Fact]
    public void A_nameless_file_still_gets_saved()
    {
        var saved = new FileBrowser().Save(null, new byte[] { 1 }, Path.Combine(_root, "incoming"));
        Assert.True(File.Exists(saved));
    }

    [Fact]
    public void The_download_folder_defaults_under_the_user_profile_and_settings_win()
    {
        var custom = new AppSettings { DownloadFolder = @"D:\elsewhere" };
        Assert.Equal(@"D:\elsewhere", FileBrowser.DefaultDownloadFolder(custom));

        var fallback = FileBrowser.DefaultDownloadFolder(new AppSettings());
        Assert.Contains("Soul Remote", fallback);
    }

    [Fact]
    public void A_long_listing_is_truncated_and_says_so()
    {
        var big = Path.Combine(_root, "many");
        Directory.CreateDirectory(big);
        for (var i = 0; i < FileBrowser.PageSize + 10; i++)
            File.WriteAllText(Path.Combine(big, $"file{i:00}.txt"), "x");

        var listing = new FileBrowser().List(1, big);

        Assert.Equal(FileBrowser.PageSize, listing.Entries.Count);
        Assert.True(listing.Truncated);
        Assert.Contains(FileBrowser.PageSize.ToString(), FileBrowser.Describe(listing));
    }

    [Fact]
    public void A_listing_at_a_root_offers_no_way_further_up()
    {
        var listing = new FileBrowser().List(1, _root);
        Assert.NotNull(listing.Parent);

        var top = new FileBrowser().List(1, Path.GetPathRoot(_root)!);
        Assert.Null(top.Parent);
    }
}

public sealed class UpdateDispatcherTests
{
    [Fact]
    public async Task Work_for_one_chat_runs_in_order()
    {
        var log = new FakeLog();
        await using var dispatcher = new UpdateDispatcher(log);
        var order = new List<int>();
        var gate = new SemaphoreSlim(0);

        // The first item blocks; if the second overtook it the order would flip.
        dispatcher.Enqueue(7, async () => { await gate.WaitAsync(); lock (order) order.Add(1); });
        dispatcher.Enqueue(7, () => { lock (order) order.Add(2); return Task.CompletedTask; });

        gate.Release();
        await dispatcher.DrainAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { 1, 2 }, order);
    }

    [Fact]
    public async Task A_slow_chat_does_not_hold_up_another()
    {
        var log = new FakeLog();
        await using var dispatcher = new UpdateDispatcher(log);
        var slowStarted = new SemaphoreSlim(0);
        var release = new SemaphoreSlim(0);
        var fastFinished = new SemaphoreSlim(0);

        dispatcher.Enqueue(1, async () => { slowStarted.Release(); await release.WaitAsync(); });
        await slowStarted.WaitAsync(TimeSpan.FromSeconds(5));

        dispatcher.Enqueue(2, () => { fastFinished.Release(); return Task.CompletedTask; });

        // This is the whole point: chat 2 must not wait behind chat 1's 60-second command.
        Assert.True(await fastFinished.WaitAsync(TimeSpan.FromSeconds(5)));

        release.Release();
        await dispatcher.DrainAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_command_that_throws_is_logged_and_the_chat_keeps_working()
    {
        var log = new FakeLog();
        await using var dispatcher = new UpdateDispatcher(log);
        var ran = false;

        dispatcher.Enqueue(1, () => throw new InvalidOperationException("boom"));
        dispatcher.Enqueue(1, () => { ran = true; return Task.CompletedTask; });

        await dispatcher.DrainAsync(TimeSpan.FromSeconds(5));

        Assert.True(ran);
        Assert.Contains(log.Messages, m => m.Contains("boom", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_cancelled_command_is_not_reported_as_a_failure()
    {
        var log = new FakeLog();
        await using var dispatcher = new UpdateDispatcher(log);

        dispatcher.Enqueue(1, () => throw new OperationCanceledException());
        await dispatcher.DrainAsync(TimeSpan.FromSeconds(5));

        Assert.DoesNotContain(log.Messages, m => m.StartsWith("Error", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Finished_chats_are_not_tracked_forever()
    {
        var log = new FakeLog();
        await using var dispatcher = new UpdateDispatcher(log);

        for (var chat = 0; chat < 200; chat++)
            dispatcher.Enqueue(chat, () => Task.CompletedTask);
        await dispatcher.DrainAsync(TimeSpan.FromSeconds(10));

        // The cleanup continuation is itself scheduled, so give it a moment to land.
        for (var i = 0; i < 50 && dispatcher.ActiveChats > 0; i++)
            await Task.Delay(20);

        Assert.Equal(0, dispatcher.ActiveChats);
    }
}

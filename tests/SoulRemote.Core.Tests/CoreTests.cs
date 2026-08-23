using System.Reflection;
using SoulRemote.Abstractions;
using SoulRemote.Localization;
using SoulRemote.Models;
using SoulRemote.Services;
using Xunit;

namespace SoulRemote.Tests;

public sealed class AppSettingsTests
{
    /// <summary>
    /// Clone() is written by hand, so a property added later is silently dropped —
    /// and every settings change goes through a clone, so a dropped field means a
    /// setting that resets itself the first time anything else is saved.
    /// </summary>
    [Fact]
    public void Clone_copies_every_property()
    {
        var original = new AppSettings();
        var properties = typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.CanRead)
            .ToArray();

        Assert.NotEmpty(properties);
        foreach (var property in properties)
            property.SetValue(original, DistinctValueFor(property));

        var copy = original.Clone();

        foreach (var property in properties)
        {
            var a = property.GetValue(original);
            var b = property.GetValue(copy);
            if (a is List<long> listA && b is List<long> listB)
                Assert.Equal(listA, listB);
            else if (a is Dictionary<string, string> mapA && b is Dictionary<string, string> mapB)
                Assert.Equal(mapA, mapB);
            else
                Assert.True(Equals(a, b), $"Clone() dropped {property.Name}");
        }
    }

    [Fact]
    public void Clone_is_a_copy_not_a_share()
    {
        var original = new AppSettings { AuthorizedChatIds = { 1 }, ChatNames = { ["1"] = "phone" } };
        var copy = original.Clone();

        copy.AuthorizedChatIds.Add(2);
        copy.ChatNames["2"] = "laptop";

        // The poll thread reads the live settings while the UI mutates a clone.
        Assert.Single(original.AuthorizedChatIds);
        Assert.Single(original.ChatNames);
    }

    [Theory]
    [InlineData(0, 25)]
    [InlineData(-5, 25)]
    [InlineData(3, 5)]
    [InlineData(25, 25)]
    [InlineData(999, 50)]
    public void Normalize_clamps_a_hand_edited_poll_timeout(int stored, int expected)
    {
        var settings = new AppSettings { PollTimeoutSeconds = stored };
        settings.Normalize();
        Assert.Equal(expected, settings.PollTimeoutSeconds);
    }

    [Fact]
    public void Normalize_drops_duplicate_chats_and_names_for_chats_that_are_gone()
    {
        var settings = new AppSettings
        {
            AuthorizedChatIds = { 7, 7, 9 },
            ChatNames = { ["7"] = "phone", ["404"] = "an old laptop" },
        };

        settings.Normalize();

        Assert.Equal(new[] { 7L, 9L }, settings.AuthorizedChatIds);
        Assert.True(settings.ChatNames.ContainsKey("7"));
        Assert.False(settings.ChatNames.ContainsKey("404"));
    }

    [Fact]
    public void An_unknown_language_tag_falls_back_rather_than_throwing()
    {
        var settings = new AppSettings { Language = "klingon" };
        settings.Normalize();
        Assert.Equal("en", settings.Language);
    }

    [Fact]
    public void NameFor_falls_back_to_the_id_for_a_chat_that_paired_before_names_were_kept()
    {
        var settings = new AppSettings { AuthorizedChatIds = { 55 } };
        Assert.Equal("55", settings.NameFor(55));

        settings.ChatNames["55"] = "Sara";
        Assert.Equal("Sara", settings.NameFor(55));
    }

    private static object? DistinctValueFor(PropertyInfo property)
    {
        var seed = Math.Abs(property.Name.GetHashCode() % 1000) + 1;
        if (property.PropertyType == typeof(string)) return "value-" + property.Name;
        if (property.PropertyType == typeof(bool)) return true;
        if (property.PropertyType == typeof(int)) return seed;
        if (property.PropertyType == typeof(List<long>)) return new List<long> { seed };
        if (property.PropertyType == typeof(Dictionary<string, string>))
            return new Dictionary<string, string> { [seed.ToString()] = property.Name };
        throw new NotSupportedException(
            $"AppSettings.{property.Name} is a {property.PropertyType.Name}; teach this test how to fill it.");
    }
}

public sealed class RateLimiterTests
{
    [Fact]
    public void Requests_inside_the_budget_are_allowed_and_the_rest_are_not()
    {
        var clock = new FakeClock();
        var limiter = new RateLimiter(3, TimeSpan.FromSeconds(10), clock);

        Assert.True(limiter.Allow(1));
        Assert.True(limiter.Allow(1));
        Assert.True(limiter.Allow(1));
        Assert.False(limiter.Allow(1));
    }

    [Fact]
    public void The_window_slides_rather_than_resetting_on_a_fixed_schedule()
    {
        var clock = new FakeClock();
        var limiter = new RateLimiter(2, TimeSpan.FromSeconds(10), clock);

        limiter.Allow(1);
        clock.Advance(TimeSpan.FromSeconds(6));
        limiter.Allow(1);
        Assert.False(limiter.Allow(1));

        // The first hit ages out; the second has not.
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(limiter.Allow(1));
        Assert.False(limiter.Allow(1));
    }

    [Fact]
    public void One_key_running_hot_does_not_affect_another()
    {
        var limiter = new RateLimiter(1, TimeSpan.FromSeconds(10), new FakeClock());

        Assert.True(limiter.Allow(1));
        Assert.False(limiter.Allow(1));
        Assert.True(limiter.Allow(2));
    }

    [Fact]
    public void Forgetting_a_key_clears_its_history()
    {
        var limiter = new RateLimiter(1, TimeSpan.FromSeconds(10), new FakeClock());

        limiter.Allow(1);
        limiter.Forget(1);

        Assert.True(limiter.Allow(1));
    }
}

public sealed class ChatPromptsTests
{
    [Fact]
    public void A_prompt_is_taken_once()
    {
        var prompts = new ChatPrompts(new FakeClock());
        prompts.Ask(1, PromptKind.Volume);

        Assert.Equal(PromptKind.Volume, prompts.Take(1));
        Assert.Equal(PromptKind.None, prompts.Take(1));
    }

    [Fact]
    public void An_expired_prompt_does_not_claim_a_later_message()
    {
        var clock = new FakeClock();
        var prompts = new ChatPrompts(clock);
        prompts.Ask(1, PromptKind.TypeText);

        clock.Advance(ChatPrompts.Lifetime + TimeSpan.FromSeconds(1));

        Assert.False(prompts.HasPending(1));
        Assert.Equal(PromptKind.None, prompts.Take(1));
    }

    [Fact]
    public void Prompts_are_per_chat()
    {
        var prompts = new ChatPrompts(new FakeClock());
        prompts.Ask(1, PromptKind.Volume);

        Assert.Equal(PromptKind.None, prompts.Take(2));
        Assert.Equal(PromptKind.Volume, prompts.Take(1));
    }

    [Fact]
    public void Every_prompt_kind_has_wording_and_a_placeholder()
    {
        foreach (var kind in Enum.GetValues<PromptKind>())
        {
            Assert.False(string.IsNullOrWhiteSpace(ChatPrompts.PromptFor(kind)));
            Assert.False(string.IsNullOrWhiteSpace(ChatPrompts.PlaceholderFor(kind)));
        }
    }
}

public sealed class BotMenuTests
{
    /// <summary>
    /// Telegram rejects any button whose callback_data is over 64 bytes, and it does
    /// so when the message is sent — so an over-long payload takes down the whole
    /// screen, not just its button.
    /// </summary>
    [Fact]
    public void Every_callback_payload_fits_telegrams_64_byte_limit()
    {
        foreach (var language in Enum.GetValues<AppLanguage>())
        {
            Strings.Use(language);
            foreach (var screen in AllScreens())
            {
                foreach (var row in screen.Keyboard.InlineKeyboard)
                foreach (var button in row)
                {
                    var bytes = System.Text.Encoding.UTF8.GetByteCount(button.CallbackData ?? string.Empty);
                    Assert.True(bytes <= 64, $"'{button.CallbackData}' is {bytes} bytes");
                    Assert.False(string.IsNullOrEmpty(button.Text), "a button with no caption is invisible");
                }
            }
        }
        Strings.Use(AppLanguage.English);
    }

    [Fact]
    public void Callback_payloads_do_not_change_with_the_language()
    {
        // A tap made before a language switch still arrives afterwards, and has to
        // still mean the same thing. The language button itself is the one deliberate
        // exception — it offers whichever language you are not using — so it is
        // checked separately rather than excused.
        Strings.Use(AppLanguage.English);
        var english = Payloads();
        Strings.Use(AppLanguage.Persian);
        var persian = Payloads();
        Strings.Use(AppLanguage.English);

        Assert.Equal(english.Where(NotLanguage), persian.Where(NotLanguage));
        Assert.Contains("l:fa", english);
        Assert.Contains("l:en", persian);

        static bool NotLanguage(string? data) => !(data ?? string.Empty).StartsWith("l:", StringComparison.Ordinal);
    }

    private static string?[] Payloads() =>
        AllScreens().SelectMany(s => s.Keyboard.InlineKeyboard).SelectMany(r => r)
            .Select(b => b.CallbackData).ToArray();

    [Fact]
    public void The_files_screen_offers_nothing_while_file_access_is_off()
    {
        var off = BotMenu.Files(false);
        var on = BotMenu.Files(true);

        Assert.Single(off.Keyboard.InlineKeyboard);   // just "Menu"
        Assert.True(on.Keyboard.InlineKeyboard.Count > 1);
    }

    [Fact]
    public void Shortcut_captions_cover_both_languages()
    {
        var captions = BotMenu.ShortcutCaptions().ToArray();

        Assert.Equal(8, captions.Length);
        Assert.Contains(captions, c => c.Caption == Strings.Get(AppLanguage.English, "bot.bar.lock"));
        Assert.Contains(captions, c => c.Caption == Strings.Get(AppLanguage.Persian, "bot.bar.lock"));
        Assert.All(captions, c => Assert.Contains(c.Action, new[] { "menu", "shot", "lock", "power" }));
    }

    private static IEnumerable<BotMenu.Screen> AllScreens()
    {
        yield return BotMenu.Home("PC", "status", true);
        yield return BotMenu.Home("PC", "status", false);
        yield return BotMenu.Capture(1);
        yield return BotMenu.Capture(4);
        yield return BotMenu.Power();
        yield return BotMenu.Audio();
        yield return BotMenu.Input(true);
        yield return BotMenu.Input(false);
        yield return BotMenu.System();
        yield return BotMenu.Processes(true);
        yield return BotMenu.Processes(false);
        yield return BotMenu.Files(true);
        yield return BotMenu.Files(false);
        yield return BotMenu.Confirm("sd", "Shut down?");
    }
}

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "soulremote-tests-" + Guid.NewGuid().ToString("N"));

    private string Path_ => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    [Fact]
    public void Settings_round_trip_through_disk()
    {
        var service = new SettingsService(new FakeLog(), NullSecretProtector.Instance, Path_);
        var settings = new AppSettings
        {
            TelegramBotToken = "token",
            WorkerUrl = "https://x.workers.dev",
            AuthorizedChatIds = { 42 },
            Language = "fa",
        };

        Assert.True(service.Save(settings));

        var reloaded = new SettingsService(new FakeLog(), NullSecretProtector.Instance, Path_).Load();
        Assert.Equal("token", reloaded.TelegramBotToken);
        Assert.Equal(new[] { 42L }, reloaded.AuthorizedChatIds);
        Assert.Equal(AppLanguage.Persian, reloaded.LanguageOrDefault);
    }

    [Fact]
    public void A_corrupt_settings_file_falls_back_to_defaults_instead_of_crashing()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, "{ this is not json");

        var loaded = new SettingsService(new FakeLog(), NullSecretProtector.Instance, Path_).Load();

        Assert.Equal(25, loaded.PollTimeoutSeconds);
        Assert.Empty(loaded.AuthorizedChatIds);
    }

    /// <summary>
    /// A protector that cannot read what is already stored — a settings file carried
    /// from another machine, or a profile DPAPI cannot reach yet.
    /// </summary>
    private sealed class BrokenProtector : ISecretProtector
    {
        public bool TryProtect(string? plainText, out string cipherText)
        {
            cipherText = plainText ?? string.Empty;
            return true;
        }

        public bool TryUnprotect(string? cipherText, out string plainText)
        {
            plainText = string.Empty;
            return string.IsNullOrEmpty(cipherText);
        }
    }

    [Fact]
    public void A_secret_that_cannot_be_decrypted_is_not_overwritten_with_nothing()
    {
        // Write a file the way a healthy install would.
        var healthy = new SettingsService(new FakeLog(), NullSecretProtector.Instance, Path_);
        healthy.Save(new AppSettings { TelegramBotToken = "the-real-token" });

        // Reopen it somewhere the secret cannot be read, and change an unrelated setting.
        var log = new FakeLog();
        var broken = new SettingsService(log, new BrokenProtector(), Path_);
        var loaded = broken.Load();
        Assert.Equal(string.Empty, loaded.TelegramBotToken);
        Assert.Contains(log.Messages, m => m.Contains("could not be decrypted", StringComparison.Ordinal));

        var changed = loaded.Clone();
        changed.ReduceMotion = true;
        Assert.True(broken.Save(changed));

        // Back on the machine that can read it, the token is still there.
        var recovered = new SettingsService(new FakeLog(), NullSecretProtector.Instance, Path_).Load();
        Assert.Equal("the-real-token", recovered.TelegramBotToken);
        Assert.True(recovered.ReduceMotion);
    }
}

public sealed class LogServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "soulremote-logs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    [Fact]
    public void Entries_reach_both_the_list_and_the_file()
    {
        var log = new LogService(ImmediateDispatcher.Instance, _dir);
        log.Info("hello");

        Assert.Single(log.Entries);
        var written = Directory.EnumerateFiles(_dir).Single();
        Assert.Contains("hello", File.ReadAllText(written));
    }

    [Fact]
    public void Pruning_removes_old_files_and_keeps_recent_and_current_ones()
    {
        var log = new LogService(ImmediateDispatcher.Instance, _dir);
        log.Info("today");

        var old = Path.Combine(_dir, $"soulremote-{DateTime.Today.AddDays(-40):yyyyMMdd}.log");
        var recent = Path.Combine(_dir, $"soulremote-{DateTime.Today.AddDays(-2):yyyyMMdd}.log");
        var unrelated = Path.Combine(_dir, "notes.txt");
        File.WriteAllText(old, "old");
        File.WriteAllText(recent, "recent");
        File.WriteAllText(unrelated, "not ours");

        var removed = log.PruneOlderThan(14);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public void Pruning_is_off_when_retention_is_zero()
    {
        var log = new LogService(ImmediateDispatcher.Instance, _dir);
        File.WriteAllText(Path.Combine(_dir, $"soulremote-{DateTime.Today.AddDays(-400):yyyyMMdd}.log"), "ancient");

        Assert.Equal(0, log.PruneOlderThan(0));
        Assert.Single(Directory.EnumerateFiles(_dir));
    }

    [Fact]
    public void Clearing_removes_the_files_as_well_as_the_list()
    {
        var log = new LogService(ImmediateDispatcher.Instance, _dir);
        log.Info("a paired chat id lives in here");

        log.Clear();
        log.DeleteAllFiles();

        Assert.Empty(log.Entries);
        Assert.Empty(Directory.EnumerateFiles(_dir));
    }

    [Fact]
    public void Logging_never_throws_even_when_the_folder_is_gone()
    {
        var log = new LogService(ImmediateDispatcher.Instance, _dir);
        Directory.Delete(_dir, true);

        log.Error("this must not take the poll loop down");

        Assert.Single(log.Entries);
    }
}

/// <summary>
/// The worker script is embedded from cloudflare/worker.js and deployed as-is, so the
/// version the app expects and the version the file announces have to agree. Nothing
/// else checks this — a mismatch would show up only as a spurious "run Connect again"
/// warning on every single bring-up.
/// </summary>
public sealed class WorkerScriptTests
{
    [Fact]
    public void The_embedded_worker_announces_the_version_the_app_expects()
    {
        var script = ReadEmbeddedWorker();
        var match = System.Text.RegularExpressions.Regex.Match(script, @"WORKER_VERSION\s*=\s*(\d+)");

        Assert.True(match.Success, "worker.js no longer declares WORKER_VERSION");
        Assert.Equal(CloudflareService.ExpectedWorkerVersion, int.Parse(match.Groups[1].Value));
    }

    [Fact]
    public void The_worker_refuses_when_no_secret_is_bound()
    {
        // Failing open here turned a mis-deployed worker into an open Telegram proxy
        // on the user's own Cloudflare account.
        var script = ReadEmbeddedWorker();
        Assert.Contains("if (!expected)", script);
        Assert.Contains("return false", script);
    }

    [Fact]
    public void The_worker_compares_the_secret_in_constant_time()
    {
        var script = ReadEmbeddedWorker();
        Assert.Contains("timingSafeEqual", script);
        Assert.Contains("SHA-256", script);
    }

    [Fact]
    public void The_worker_only_relays_bot_api_paths()
    {
        var script = ReadEmbeddedWorker();
        Assert.Contains("TELEGRAM_PATH", script);
        Assert.Contains("Not a Telegram Bot API path", script);
    }

    private static string ReadEmbeddedWorker()
    {
        var assembly = typeof(CloudflareService).Assembly;
        var name = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith("worker.js", StringComparison.OrdinalIgnoreCase));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

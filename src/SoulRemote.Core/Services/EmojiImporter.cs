using SoulRemote.Localization;
using SoulRemote.Models;

namespace SoulRemote.Services;

/// <summary>
/// Puts premium emoji on the bot, and takes them off again.
///
/// Both settings surfaces come through here. That is not tidiness for its own sake:
/// the rules about which premium emoji may stand in for which ordinary one are
/// Telegram's, not this app's, and a second copy of them written for the desktop
/// window would be a second chance to get them subtly wrong — and wrong in the way
/// that is hardest to notice, because Telegram answers a mismatched custom emoji by
/// silently showing the plain one.
///
/// The one rule everything here follows: a custom emoji may only replace the
/// ordinary emoji it is a version of. Telegram requires the entity to wrap exactly
/// that character and ignores it otherwise, so there is nothing to choose. Sending
/// a premium camera converts the camera.
/// </summary>
public sealed class EmojiImporter
{
    private readonly ISettingsService _settings;
    private readonly ITelegramClient _telegram;

    public EmojiImporter(ISettingsService settings, ITelegramClient telegram)
    {
        _settings = settings;
        _telegram = telegram;
    }

    /// <summary>
    /// Converts the whole bot from one emoji pack, named however the user had it to
    /// hand: a t.me/addemoji link, the bare pack name, or one emoji out of the pack.
    /// The last needs Telegram's help — an emoji arrives as an identifier and nothing
    /// else, so it takes a lookup to learn which pack it came from.
    /// </summary>
    public async Task<string> ImportPackAsync(
        string? input, IReadOnlyList<TgMessageEntity>? entities, CancellationToken ct = default)
    {
        var name = EmojiPack.NameFrom(input) ?? await PackNameFromEmojiAsync(entities, ct).ConfigureAwait(false);
        if (name is null)
            throw new InvalidOperationException(Strings.Get("act.emoji.needpack"));

        var set = await _telegram.GetStickerSetAsync(name, ct).ConfigureAwait(false);
        if (!set.IsCustomEmojiSet)
            throw new InvalidOperationException(Strings.Format("act.emoji.notemojipack", Title(set)));

        var matched = EmojiPack.Match(set.Stickers);
        if (matched.Count == 0)
            throw new InvalidOperationException(Strings.Format("act.emoji.nomatch", Title(set)));

        // The pack replaces the map rather than merging into it. "Use this pack" is
        // what was asked for, and a map half one pack and half another would be a look
        // nobody chose and nobody could describe.
        return Apply(s =>
        {
            s.PremiumEmoji = matched;
            s.PremiumEmojiPack = set.Name;
            s.UsePremiumEmoji = true;
        }, "act.emoji.imported", matched.Count, EmojiCatalog.Count, Title(set));
    }

    /// <summary>
    /// Files each premium emoji the user supplied under the ordinary emoji it is a
    /// version of. Ids can come from the entities of a message sent to the bot, or be
    /// pasted straight in from the desktop, which has no emoji keyboard to send from.
    /// </summary>
    /// <param name="target">
    /// The one emoji this answer was asked for, when the user tapped it specifically.
    /// Null when they were simply invited to send some.
    /// </param>
    public async Task<string> AdoptAsync(
        IReadOnlyList<TgMessageEntity>? entities, IEnumerable<string>? pastedIds,
        string? target, CancellationToken ct = default)
    {
        var ids = CustomEmojiIds(entities)
            .Concat(pastedIds ?? Enumerable.Empty<string>())
            .Where(EmojiText.IsValidCustomEmojiId)
            .Distinct(StringComparer.Ordinal)
            .Take(TelegramClient.MaxCustomEmojiLookup)
            .ToArray();
        if (ids.Length == 0)
            throw new InvalidOperationException(Strings.Get("act.emoji.needpremium"));

        var stickers = await _telegram.GetCustomEmojiStickersAsync(ids, ct).ConfigureAwait(false);

        var adopted = new Dictionary<string, string>(StringComparer.Ordinal);
        string? stray = null;
        foreach (var sticker in stickers)
        {
            if (sticker?.CustomEmojiId is not { Length: > 0 } id || !EmojiText.IsValidCustomEmojiId(id))
                continue;
            if (sticker.Emoji is not { Length: > 0 } stands)
                continue;

            var match = CatalogueMatch(stands);
            if (match is null || (target is not null && !string.Equals(match, target, StringComparison.Ordinal)))
            {
                stray ??= stands;
                continue;
            }
            adopted.TryAdd(match, id);
        }

        if (adopted.Count == 0)
        {
            // Naming the emoji it actually stands for is the whole of the explanation.
            // Without it, "that one will not work here" is a dead end.
            throw new InvalidOperationException(target is not null
                ? Strings.Format("act.emoji.wrongone", target, stray ?? "?")
                : Strings.Format("act.emoji.unused", stray ?? "?"));
        }

        return Apply(s =>
        {
            foreach (var (emoji, id) in adopted)
                s.PremiumEmoji[emoji] = id;
            // Once the map is a mixture, the pack label no longer describes it.
            s.PremiumEmojiPack = string.Empty;
            s.UsePremiumEmoji = true;
        }, adopted.Count == 1 ? "act.emoji.added.one" : "act.emoji.added", adopted.Count);
    }

    /// <summary>Takes one emoji's premium stand-in away again.</summary>
    public string ClearOne(string? emoji)
    {
        if (emoji is not { Length: > 0 } || !_settings.Current.PremiumEmoji.ContainsKey(emoji))
            return Strings.Get("bot.nothing");

        return Apply(s =>
        {
            s.PremiumEmoji.Remove(emoji);
            s.PremiumEmojiPack = string.Empty;
        }, "act.emoji.removed", emoji);
    }

    /// <summary>Puts every emoji back to the plain one.</summary>
    public string ClearAll() => Apply(s =>
    {
        s.PremiumEmoji.Clear();
        s.PremiumEmojiPack = string.Empty;
    }, "act.emoji.cleared");

    /// <summary>The pack behind the first premium emoji in a message, or null if there is none.</summary>
    private async Task<string?> PackNameFromEmojiAsync(IReadOnlyList<TgMessageEntity>? entities, CancellationToken ct)
    {
        var id = CustomEmojiIds(entities).FirstOrDefault();
        if (id is null)
            return null;

        var stickers = await _telegram.GetCustomEmojiStickersAsync(new[] { id }, ct).ConfigureAwait(false);
        var setName = stickers.FirstOrDefault()?.SetName;
        return string.IsNullOrWhiteSpace(setName) ? null : setName;
    }

    /// <summary>The bot's own form of an emoji, ignoring the invisible variation selector.</summary>
    public static string? CatalogueMatch(string? emoji)
    {
        if (emoji is not { Length: > 0 })
            return null;
        var folded = EmojiText.Fold(emoji);
        foreach (var use in EmojiCatalog.All)
        {
            if (string.Equals(EmojiText.Fold(use.Emoji), folded, StringComparison.Ordinal))
                return use.Emoji;
        }
        return null;
    }

    private static IEnumerable<string> CustomEmojiIds(IReadOnlyList<TgMessageEntity>? entities)
    {
        if (entities is null)
            yield break;
        foreach (var entity in entities)
        {
            if (entity.IsCustomEmoji && entity.CustomEmojiId is { Length: > 0 } id)
                yield return id;
        }
    }

    /// <summary>A pack's own title, falling back to its name when it has none.</summary>
    private static string Title(TgStickerSet set) =>
        set.Title is { Length: > 0 } title ? title : set.Name;

    /// <summary>
    /// Saves a change and reports it. Mirrors what the router does for every other
    /// setting: clone, mutate, save, and refuse to claim success on a write that did
    /// not reach disk — an import that says it worked and is gone on the next launch
    /// is worse than one that says it failed.
    /// </summary>
    private string Apply(Action<AppSettings> mutate, string resultKey, params object?[] args)
    {
        var settings = _settings.Current.Clone();
        mutate(settings);
        if (!_settings.Save(settings))
            throw new InvalidOperationException(Strings.Get("bot.set.savefailed"));
        return args.Length == 0 ? Strings.Get(resultKey) : Strings.Format(resultKey, args);
    }
}

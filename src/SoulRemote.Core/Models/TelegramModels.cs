using System.Text.Json.Serialization;

namespace SoulRemote.Models;

// Minimal DTOs for the subset of the Telegram Bot API that Soul Remote uses.
// See https://core.telegram.org/bots/api

public sealed class TgResponse<T>
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("result")] public T? Result { get; set; }
    [JsonPropertyName("error_code")] public int? ErrorCode { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("parameters")] public TgResponseParameters? Parameters { get; set; }
}

/// <summary>Extra instructions Telegram attaches to a failure — chiefly flood control.</summary>
public sealed class TgResponseParameters
{
    /// <summary>Seconds to wait before repeating the request, sent with a 429.</summary>
    [JsonPropertyName("retry_after")] public int? RetryAfter { get; set; }

    [JsonPropertyName("migrate_to_chat_id")] public long? MigrateToChatId { get; set; }
}

public sealed class TgUser
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("is_bot")] public bool IsBot { get; set; }
    [JsonPropertyName("first_name")] public string? FirstName { get; set; }
    [JsonPropertyName("username")] public string? Username { get; set; }
}

public sealed class TgChat
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("first_name")] public string? FirstName { get; set; }
}

public sealed class TgMessage
{
    [JsonPropertyName("message_id")] public long MessageId { get; set; }
    [JsonPropertyName("from")] public TgUser? From { get; set; }
    [JsonPropertyName("chat")] public TgChat? Chat { get; set; }
    [JsonPropertyName("date")] public long Date { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("caption")] public string? Caption { get; set; }
    [JsonPropertyName("document")] public TgDocument? Document { get; set; }
    [JsonPropertyName("photo")] public List<TgPhotoSize>? Photo { get; set; }

    /// <summary>
    /// Formatting Telegram found in the text. The bot reads these for one reason: a
    /// premium emoji a user typed arrives as ordinary text plus a "custom_emoji"
    /// entity holding its identifier, and that identifier is the only way to learn
    /// which custom emoji was sent.
    /// </summary>
    [JsonPropertyName("entities")] public List<TgMessageEntity>? Entities { get; set; }

    [JsonPropertyName("caption_entities")] public List<TgMessageEntity>? CaptionEntities { get; set; }
}

/// <summary>
/// One piece of formatting inside a message. Offsets are in UTF-16 code units,
/// which is what a .NET string is indexed in, so they can be used directly.
/// </summary>
public sealed class TgMessageEntity
{
    /// <summary>"bold", "code", "custom_emoji" and the rest.</summary>
    [JsonPropertyName("type")] public string? Type { get; set; }

    [JsonPropertyName("offset")] public int Offset { get; set; }
    [JsonPropertyName("length")] public int Length { get; set; }

    /// <summary>Set only on a "custom_emoji" entity: the identifier of the emoji.</summary>
    [JsonPropertyName("custom_emoji_id")] public string? CustomEmojiId { get; set; }

    public bool IsCustomEmoji => string.Equals(Type, "custom_emoji", StringComparison.Ordinal);
}

/// <summary>
/// A sticker, of which a custom emoji is one kind. Only the fields that say which
/// emoji it stands for and which set it came from are modelled.
/// </summary>
public sealed class TgSticker
{
    [JsonPropertyName("file_id")] public string FileId { get; set; } = string.Empty;

    /// <summary>"regular", "mask" or "custom_emoji".</summary>
    [JsonPropertyName("type")] public string? Type { get; set; }

    /// <summary>
    /// The ordinary emoji this sticker stands for. Telegram ignores a custom emoji
    /// entity that does not wrap exactly this character, so it is what decides which
    /// of the bot's emoji a given premium one may replace.
    /// </summary>
    [JsonPropertyName("emoji")] public string? Emoji { get; set; }

    [JsonPropertyName("set_name")] public string? SetName { get; set; }

    /// <summary>Present on a custom emoji sticker: the id to send it by.</summary>
    [JsonPropertyName("custom_emoji_id")] public string? CustomEmojiId { get; set; }

    [JsonPropertyName("is_animated")] public bool IsAnimated { get; set; }
    [JsonPropertyName("is_video")] public bool IsVideo { get; set; }
}

/// <summary>A whole sticker set, as returned by getStickerSet.</summary>
public sealed class TgStickerSet
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;

    /// <summary>"regular", "mask" or "custom_emoji".</summary>
    [JsonPropertyName("sticker_type")] public string? StickerType { get; set; }

    [JsonPropertyName("stickers")] public List<TgSticker> Stickers { get; set; } = new();

    public bool IsCustomEmojiSet => string.Equals(StickerType, "custom_emoji", StringComparison.Ordinal);
}

/// <summary>A file attached to an incoming message.</summary>
public sealed class TgDocument
{
    [JsonPropertyName("file_id")] public string FileId { get; set; } = string.Empty;
    [JsonPropertyName("file_name")] public string? FileName { get; set; }
    [JsonPropertyName("mime_type")] public string? MimeType { get; set; }
    [JsonPropertyName("file_size")] public long? FileSize { get; set; }
}

public sealed class TgPhotoSize
{
    [JsonPropertyName("file_id")] public string FileId { get; set; } = string.Empty;
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    [JsonPropertyName("file_size")] public long? FileSize { get; set; }
}

/// <summary>What getFile returns: where the file can be fetched from.</summary>
public sealed class TgFile
{
    [JsonPropertyName("file_id")] public string FileId { get; set; } = string.Empty;
    [JsonPropertyName("file_size")] public long? FileSize { get; set; }
    [JsonPropertyName("file_path")] public string? FilePath { get; set; }
}

public sealed class TgCallbackQuery
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("from")] public TgUser? From { get; set; }
    [JsonPropertyName("message")] public TgMessage? Message { get; set; }
    [JsonPropertyName("data")] public string? Data { get; set; }
}

public sealed class TgUpdate
{
    [JsonPropertyName("update_id")] public long UpdateId { get; set; }
    [JsonPropertyName("message")] public TgMessage? Message { get; set; }
    [JsonPropertyName("edited_message")] public TgMessage? EditedMessage { get; set; }
    [JsonPropertyName("callback_query")] public TgCallbackQuery? CallbackQuery { get; set; }
}

// ---- Inline keyboard (outbound) ----

public sealed class TgInlineKeyboardButton
{
    [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
    [JsonPropertyName("callback_data")] public string? CallbackData { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }

    /// <summary>
    /// A custom emoji drawn before the label. Button labels take no markup at all,
    /// so this field is the only way a premium emoji reaches a button — which is why
    /// the emoji is moved out of the caption and into here rather than being wrapped
    /// where it stands.
    /// </summary>
    [JsonPropertyName("icon_custom_emoji_id")] public string? IconCustomEmojiId { get; set; }

    public TgInlineKeyboardButton() { }
    public TgInlineKeyboardButton(string text, string callbackData)
    {
        Text = text;
        CallbackData = callbackData;
    }
}

public sealed class TgInlineKeyboardMarkup
{
    [JsonPropertyName("inline_keyboard")]
    public List<List<TgInlineKeyboardButton>> InlineKeyboard { get; set; } = new();
}

/// <summary>A button on the persistent reply keyboard below the message box.</summary>
public sealed class TgKeyboardButton
{
    [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;

    /// <summary>A custom emoji drawn before the label. See the inline button's copy.</summary>
    [JsonPropertyName("icon_custom_emoji_id")] public string? IconCustomEmojiId { get; set; }

    public TgKeyboardButton() { }
    public TgKeyboardButton(string text) => Text = text;
}

/// <summary>The always-visible shortcut bar under the composer.</summary>
public sealed class TgReplyKeyboardMarkup
{
    [JsonPropertyName("keyboard")]
    public List<List<TgKeyboardButton>> Keyboard { get; set; } = new();

    [JsonPropertyName("resize_keyboard")] public bool ResizeKeyboard { get; set; } = true;
    [JsonPropertyName("is_persistent")] public bool IsPersistent { get; set; } = true;
    [JsonPropertyName("input_field_placeholder")] public string? Placeholder { get; set; }
}

/// <summary>Prompts the user for a value, showing a reply box pre-aimed at the bot.</summary>
public sealed class TgForceReply
{
    [JsonPropertyName("force_reply")] public bool ForceReply { get; set; } = true;
    [JsonPropertyName("input_field_placeholder")] public string? Placeholder { get; set; }
    [JsonPropertyName("selective")] public bool Selective { get; set; }
}

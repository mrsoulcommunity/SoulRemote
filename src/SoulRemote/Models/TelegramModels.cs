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

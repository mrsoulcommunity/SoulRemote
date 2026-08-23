using SoulRemote.Localization;

namespace SoulRemote.ViewModels;

/// <summary>
/// One paired Telegram chat, as a row the owner can actually act on.
///
/// The dashboard used to render the whole whitelist as a single joined string of raw
/// ids, with "Revoke all" as the only control — so the answer to "my phone was
/// stolen, cut it off" was to un-pair every device and re-pair the rest by hand.
/// </summary>
public sealed class PairedChatViewModel
{
    public PairedChatViewModel(long id, string label, Action<long> remove)
    {
        Id = id;
        Label = label;
        RemoveCommand = new RelayCommand(() => remove(id));
    }

    public long Id { get; }

    /// <summary>The remembered name, or the bare id when the chat paired before names were kept.</summary>
    public string Label { get; }

    /// <summary>Always shown alongside the label, because the id is what the bot reports.</summary>
    public string IdText => Id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>True when there is a real name to show above the id.</summary>
    public bool HasName => !string.Equals(Label, IdText, StringComparison.Ordinal);

    public string RemoveText => Strings.Get("ui.dash.remove");

    public RelayCommand RemoveCommand { get; }
}

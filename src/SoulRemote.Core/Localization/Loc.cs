using System.ComponentModel;

namespace SoulRemote.Localization;

/// <summary>
/// The binding source XAML uses to read localized text:
/// <c>Text="{Binding [ui.nav.dashboard], Source={x:Static loc:Loc.Instance}}"</c>
///
/// The indexer is the whole trick. WPF refreshes every indexer binding on an object
/// when it raises <c>PropertyChanged</c> for <c>"Item[]"</c>, so switching language
/// re-renders the entire window in place — no reload, no re-created views, and no
/// per-string plumbing in the view models.
///
/// It deliberately does <em>not</em> subscribe to <see cref="Strings.LanguageChanged"/>
/// itself. That event can be raised from the polling thread — a chat can switch the
/// language from Telegram — and this object is bound to live UI. The shell view model
/// calls <see cref="Refresh"/> after marshalling to the UI thread, which keeps the hop
/// explicit rather than relying on WPF to notice.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    private Loc()
    {
    }

    public string this[string key] => Strings.Get(key);

    /// <summary>The current language's flow direction, as WPF spells it.</summary>
    public string Direction => Strings.IsRightToLeft ? "RightToLeft" : "LeftToRight";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Refresh()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Direction)));
    }
}

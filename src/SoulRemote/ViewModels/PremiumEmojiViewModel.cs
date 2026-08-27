using System.Collections.ObjectModel;
using SoulRemote.Localization;
using SoulRemote.Models;
using SoulRemote.Services;

namespace SoulRemote.ViewModels;

/// <summary>One of the bot's emoji on the desktop page, and whether it has been converted.</summary>
public sealed class EmojiRowViewModel : ViewModelBase
{
    public EmojiRowViewModel(string emoji) => Emoji = emoji;

    public string Emoji { get; }

    /// <summary>What this emoji is for, read out of the first string it appears in.</summary>
    public string Label => EmojiCatalog.LabelFor(Emoji);

    private bool _isPremium;
    public bool IsPremium { get => _isPremium; set => SetProperty(ref _isPremium, value); }

    /// <summary>The identifier behind it, shown so a value can be copied out or checked.</summary>
    private string _customEmojiId = string.Empty;
    public string CustomEmojiId { get => _customEmojiId; set => SetProperty(ref _customEmojiId, value); }

    /// <summary>Re-reads the label after the language changed.</summary>
    public void NotifyLanguageChanged() => OnPropertyChanged(nameof(Label));
}

/// <summary>
/// The desktop half of the premium-emoji setting.
///
/// The window cannot offer an emoji picker — premium emoji live on Telegram's own
/// keyboard, and there is no way to reach it from here. So this page does the two
/// things a desktop is actually better at: taking a pack by name or link and
/// converting everything in one action, and laying all sixty-odd emoji out at once
/// so you can see what a pack did and did not cover. Picking a single premium emoji
/// by hand belongs in the chat, where the keyboard is, and the hint says so.
/// </summary>
public sealed class PremiumEmojiViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly EmojiImporter _importer;

    public PremiumEmojiViewModel(AppServices services)
    {
        _services = services;
        _importer = new EmojiImporter(services.Settings, services.Telegram);

        foreach (var use in EmojiCatalog.All)
            Emojis.Add(new EmojiRowViewModel(use.Emoji));

        ImportCommand = new AsyncRelayCommand(ImportAsync, () => !string.IsNullOrWhiteSpace(PackInput));
        ClearAllCommand = new RelayCommand(_ => Run(() => _importer.ClearAll()), _ => MappedCount > 0);
        ClearOneCommand = new RelayCommand(p => Run(() => _importer.ClearOne(p as string)));

        Reload(services.Settings.Current);
        services.Settings.Changed += OnSettingsChanged;
        services.Emoji.StateChanged += OnStateChanged;
    }

    public ObservableCollection<EmojiRowViewModel> Emojis { get; } = new();

    public AsyncRelayCommand ImportCommand { get; }
    public RelayCommand ClearAllCommand { get; }
    public RelayCommand ClearOneCommand { get; }

    private void OnSettingsChanged(AppSettings settings) => UiThread.Post(() => Reload(settings));

    private void OnStateChanged(PremiumEmojiState state) => UiThread.Post(() =>
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(StateIsFault));
    });

    /// <summary>Re-reads every row from the saved map, whichever side changed it.</summary>
    private void Reload(AppSettings settings)
    {
        _loading = true;
        _usePremiumEmoji = settings.UsePremiumEmoji;
        foreach (var row in Emojis)
        {
            var mapped = settings.PremiumEmoji.TryGetValue(row.Emoji, out var id);
            row.IsPremium = mapped;
            row.CustomEmojiId = mapped ? id! : string.Empty;
        }
        _loading = false;

        OnPropertyChanged(nameof(UsePremiumEmoji));
        OnPropertyChanged(nameof(MappedCount));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasAny));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(StateIsFault));
    }

    /// <summary>Re-reads every label after the language changed.</summary>
    public void NotifyLanguageChanged()
    {
        foreach (var row in Emojis)
            row.NotifyLanguageChanged();
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(StateText));
    }

    private bool _loading;

    private bool _usePremiumEmoji;
    public bool UsePremiumEmoji
    {
        get => _usePremiumEmoji;
        set
        {
            if (!SetProperty(ref _usePremiumEmoji, value) || _loading)
                return;
            var settings = _services.Settings.Current.Clone();
            settings.UsePremiumEmoji = value;
            if (!_services.Settings.Save(settings))
                Status = Strings.Get("ui.settings.savefailed");
        }
    }

    /// <summary>How many of the bot's emoji are actually converted right now.</summary>
    public int MappedCount => EmojiCatalog.ConvertedCount(_services.Settings.Current.PremiumEmoji);

    /// <summary>
    /// Whether there is anything for "Undo them all" to undo. The whole mapping, not
    /// just the part this build uses: clearing it clears the leftovers too.
    /// </summary>
    public bool HasAny => _services.Settings.Current.PremiumEmoji.Count > 0;

    public string Summary => MappedCount == 0
        ? Strings.Get("ui.settings.emoji.none")
        : Strings.Format("ui.settings.emoji.count", MappedCount, EmojiCatalog.Count);

    /// <summary>
    /// Whether Telegram is actually honouring the emoji. Left blank until something
    /// has been mapped: a warning about premium emoji not being allowed, shown to
    /// someone who has not set any, is a problem they do not have.
    /// </summary>
    public string StateText => MappedCount == 0 ? string.Empty : _services.Emoji.State switch
    {
        PremiumEmojiState.Working => Strings.Get("ui.settings.emoji.working"),
        PremiumEmojiState.Refused => Strings.Get("ui.settings.emoji.refused"),
        _ => Strings.Get("ui.settings.emoji.untested"),
    };

    public bool StateIsFault => MappedCount > 0 && _services.Emoji.State == PremiumEmojiState.Refused;

    private string _packInput = string.Empty;
    public string PackInput
    {
        get => _packInput;
        set => SetProperty(ref _packInput, value ?? string.Empty);
    }

    private string _status = string.Empty;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    /// <summary>
    /// Takes whatever is in the box. A bare identifier converts that one emoji —
    /// a pack link or name converts everything the pack covers. One field rather
    /// than two because the three things a user might paste are told apart reliably
    /// by looking at them, and a chooser would be asking a question with a knowable
    /// answer.
    /// </summary>
    private async Task ImportAsync()
    {
        var input = PackInput.Trim();
        if (input.Length == 0)
            return;

        // Every route to a pack goes through the bot's own connection to Telegram,
        // which is only up while the relay is. Saying so beats a timeout.
        if (!_services.Telegram.IsConfigured)
        {
            Status = Strings.Get("ui.settings.emoji.offline");
            return;
        }

        Status = Strings.Get("ui.settings.emoji.busy");
        try
        {
            Status = EmojiText.IsValidCustomEmojiId(input)
                ? await _importer.AdoptAsync(null, new[] { input }, null).ConfigureAwait(true)
                : await _importer.ImportPackAsync(input, null).ConfigureAwait(true);
            PackInput = string.Empty;
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            _services.Log.Warning($"Premium emoji import failed: {ex.Message}");
        }
    }

    /// <summary>Runs one of the synchronous actions, reporting whatever it says.</summary>
    private void Run(Func<string> action)
    {
        try
        {
            Status = action();
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }
}

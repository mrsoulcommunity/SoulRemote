using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using SoulRemote.Models;

namespace SoulRemote.Controls;

/// <summary>
/// Live view of the relay chain: this PC -> Cloudflare edge -> Telegram.
/// Each conduit only animates while that hop is actually carrying traffic, so the
/// motion is a status report rather than decoration.
/// </summary>
public partial class RelayLine : UserControl
{
    private const double ConduitTravel = 98;

    public RelayLine()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshConduits();
        Unloaded += (_, _) => StopAll();
    }

    // ---- hop states ----

    public static readonly DependencyProperty PcStateProperty = DependencyProperty.Register(
        nameof(PcState), typeof(LinkState), typeof(RelayLine),
        new PropertyMetadata(LinkState.Online));

    public LinkState PcState
    {
        get => (LinkState)GetValue(PcStateProperty);
        set => SetValue(PcStateProperty, value);
    }

    public static readonly DependencyProperty EdgeStateProperty = DependencyProperty.Register(
        nameof(EdgeState), typeof(LinkState), typeof(RelayLine),
        new PropertyMetadata(LinkState.Idle));

    public LinkState EdgeState
    {
        get => (LinkState)GetValue(EdgeStateProperty);
        set => SetValue(EdgeStateProperty, value);
    }

    public static readonly DependencyProperty TelegramStateProperty = DependencyProperty.Register(
        nameof(TelegramState), typeof(LinkState), typeof(RelayLine),
        new PropertyMetadata(LinkState.Idle));

    public LinkState TelegramState
    {
        get => (LinkState)GetValue(TelegramStateProperty);
        set => SetValue(TelegramStateProperty, value);
    }

    // ---- captions under each hop ----

    public static readonly DependencyProperty PcDetailProperty = DependencyProperty.Register(
        nameof(PcDetail), typeof(string), typeof(RelayLine), new PropertyMetadata(string.Empty));

    public string PcDetail
    {
        get => (string)GetValue(PcDetailProperty);
        set => SetValue(PcDetailProperty, value);
    }

    public static readonly DependencyProperty EdgeDetailProperty = DependencyProperty.Register(
        nameof(EdgeDetail), typeof(string), typeof(RelayLine), new PropertyMetadata(string.Empty));

    public string EdgeDetail
    {
        get => (string)GetValue(EdgeDetailProperty);
        set => SetValue(EdgeDetailProperty, value);
    }

    public static readonly DependencyProperty TelegramDetailProperty = DependencyProperty.Register(
        nameof(TelegramDetail), typeof(string), typeof(RelayLine), new PropertyMetadata(string.Empty));

    public string TelegramDetail
    {
        get => (string)GetValue(TelegramDetailProperty);
        set => SetValue(TelegramDetailProperty, value);
    }

    // ---- conduit energy ----

    public static readonly DependencyProperty EdgeLiveProperty = DependencyProperty.Register(
        nameof(EdgeLive), typeof(bool), typeof(RelayLine),
        new PropertyMetadata(false, OnConduitChanged));

    /// <summary>True when the PC -> edge conduit is carrying traffic.</summary>
    public bool EdgeLive
    {
        get => (bool)GetValue(EdgeLiveProperty);
        set => SetValue(EdgeLiveProperty, value);
    }

    public static readonly DependencyProperty TelegramLiveProperty = DependencyProperty.Register(
        nameof(TelegramLive), typeof(bool), typeof(RelayLine),
        new PropertyMetadata(false, OnConduitChanged));

    /// <summary>True when the edge -> Telegram conduit is carrying traffic.</summary>
    public bool TelegramLive
    {
        get => (bool)GetValue(TelegramLiveProperty);
        set => SetValue(TelegramLiveProperty, value);
    }

    public static readonly DependencyProperty AnimationsEnabledProperty = DependencyProperty.Register(
        nameof(AnimationsEnabled), typeof(bool), typeof(RelayLine),
        new PropertyMetadata(true, OnConduitChanged));

    /// <summary>Honours the app's "reduce motion" preference.</summary>
    public bool AnimationsEnabled
    {
        get => (bool)GetValue(AnimationsEnabledProperty);
        set => SetValue(AnimationsEnabledProperty, value);
    }

    private static void OnConduitChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((RelayLine)d).RefreshConduits();

    private void RefreshConduits()
    {
        if (!IsLoaded)
            return;
        Energize(Conduit1, Packet1, Packet1Move, EdgeLive);
        Energize(Conduit2, Packet2, Packet2Move, TelegramLive);
    }

    private void Energize(UIElement conduit, Shape packet, TranslateTransform move, bool live)
    {
        // Fade the conduit itself.
        conduit.BeginAnimation(OpacityProperty,
            new DoubleAnimation(live ? 1 : 0, TimeSpan.FromMilliseconds(260))
            {
                FillBehavior = FillBehavior.HoldEnd,
            });

        if (!live || !AnimationsEnabled)
        {
            packet.BeginAnimation(OpacityProperty, null);
            move.BeginAnimation(TranslateTransform.XProperty, null);
            packet.Opacity = 0;
            move.X = 0;
            return;
        }

        packet.Opacity = 1;
        move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
        {
            From = 0,
            To = ConduitTravel,
            Duration = TimeSpan.FromSeconds(1.7),
            RepeatBehavior = RepeatBehavior.Forever,
        });
    }

    private void StopAll()
    {
        Packet1Move.BeginAnimation(TranslateTransform.XProperty, null);
        Packet2Move.BeginAnimation(TranslateTransform.XProperty, null);
        Conduit1.BeginAnimation(OpacityProperty, null);
        Conduit2.BeginAnimation(OpacityProperty, null);
    }
}

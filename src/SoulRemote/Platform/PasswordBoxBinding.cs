using System.Windows;
using System.Windows.Controls;

namespace SoulRemote.Platform;

/// <summary>
/// Two-way binding for <see cref="PasswordBox.Password"/>, which WPF deliberately
/// does not expose as a dependency property.
///
/// Soul Remote needs it because its Connect page is the entry point for two secrets
/// that between them are the whole system: a Telegram bot token, which is full
/// control of the relay bot, and a Cloudflare API token, which is account-level API
/// access. The view model already refuses to prefill either from storage — its own
/// comment says a secret should not sit on screen during a screen-share — but they
/// were then typed into plain TextBoxes and left there in 13px monospace for the
/// whole bring-up, and on a failed attempt for as long as the retry took.
///
/// The usual objection to binding Password is that it puts the secret in a managed
/// string. It already is one: the view model holds it, the settings layer encrypts
/// it, and the client sends it. Masking is about who can read the screen.
/// </summary>
public static class PasswordBoxBinding
{
    private static readonly DependencyProperty UpdatingProperty = DependencyProperty.RegisterAttached(
        "Updating", typeof(bool), typeof(PasswordBoxBinding), new PropertyMetadata(false));

    public static readonly DependencyProperty PasswordProperty = DependencyProperty.RegisterAttached(
        "Password", typeof(string), typeof(PasswordBoxBinding),
        new FrameworkPropertyMetadata(string.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPasswordChanged));

    public static string GetPassword(DependencyObject element) => (string)element.GetValue(PasswordProperty);

    public static void SetPassword(DependencyObject element, string value) => element.SetValue(PasswordProperty, value);

    /// <summary>Attach once per box; harmless to set twice.</summary>
    public static readonly DependencyProperty AttachProperty = DependencyProperty.RegisterAttached(
        "Attach", typeof(bool), typeof(PasswordBoxBinding), new PropertyMetadata(false, OnAttachChanged));

    public static bool GetAttach(DependencyObject element) => (bool)element.GetValue(AttachProperty);

    public static void SetAttach(DependencyObject element, bool value) => element.SetValue(AttachProperty, value);

    private static void OnAttachChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box)
            return;
        box.PasswordChanged -= OnPasswordBoxChanged;
        if (e.NewValue is true)
            box.PasswordChanged += OnPasswordBoxChanged;
    }

    private static void OnPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box)
            return;
        // Without this guard, writing the box back from the binding it just raised
        // re-enters PasswordChanged and moves the caret on every keystroke.
        if ((bool)box.GetValue(UpdatingProperty))
            return;
        var incoming = e.NewValue as string ?? string.Empty;
        if (!string.Equals(box.Password, incoming, StringComparison.Ordinal))
            box.Password = incoming;
    }

    private static void OnPasswordBoxChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox box)
            return;
        box.SetValue(UpdatingProperty, true);
        SetPassword(box, box.Password);
        box.SetValue(UpdatingProperty, false);
    }
}

using System.Windows;

namespace SoulRemote.Views;

public partial class UpdateCard
{
    public UpdateCard() => InitializeComponent();

    /// <summary>
    /// The card is a modal, and Escape is bound to "Later" — which does nothing unless
    /// something inside it holds focus. It is never clicked into first, it simply
    /// appears, so focus has to be taken the moment it becomes visible. Loaded is no
    /// use here: WPF loads the overlay while it is still collapsed.
    /// </summary>
    private void Card_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
            Focus();
    }
}

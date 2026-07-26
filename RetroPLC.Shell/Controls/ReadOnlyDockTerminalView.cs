using Avalonia.Input;

namespace RetroPLC.Shell.Controls;

/// <summary>
/// Output-only terminal. Text can still be selected and copied, but input is
/// never forwarded to the process running in the PTY.
/// </summary>
public sealed class ReadOnlyDockTerminalView : DockTerminalView
{
    protected override void OnKeyDown(KeyEventArgs e)
    {
        var copyShortcut =
            (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control | KeyModifiers.Shift)) ||
            (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Meta));

        if (copyShortcut)
        {
            base.OnKeyDown(e);
            return;
        }

        // Do not call TerminalView.OnKeyDown, so nothing reaches the PTY.
        // Leave the event unhandled so IDE-level shortcuts can still bubble.
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        // Do not forward key-up sequences to the PTY.
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            // TerminalView uses right-click as paste when no text is selected.
            e.Handled = true;
            return;
        }

        base.OnPointerPressed(e);
    }
}

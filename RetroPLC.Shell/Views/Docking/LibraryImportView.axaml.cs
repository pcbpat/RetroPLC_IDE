using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using RetroPLC.Shell.ViewModels.Docking;

namespace RetroPLC.Shell.Views.Docking;

public partial class LibraryImportView : UserControl
{
    private LibraryImportViewModel? _viewModel;

    public LibraryImportView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.Session.PropertyChanged -= OnSessionPropertyChanged;

        _viewModel = DataContext as LibraryImportViewModel;
        if (_viewModel is null)
            return;

        _viewModel.Session.PropertyChanged += OnSessionPropertyChanged;
        if (_viewModel.Session.ConsumeFocusRequest())
            FocusTerminal();
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BuildTerminalSession.Terminal) &&
            _viewModel?.Session.ConsumeFocusRequest() == true)
            FocusTerminal();
    }

    private void FocusTerminal()
    {
        var terminal = _viewModel?.Session.Terminal;
        if (terminal is null)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (terminal.IsLoaded)
            {
                terminal.Focus();
                return;
            }

            void FocusOnLoaded(object? sender, RoutedEventArgs e)
            {
                terminal.Loaded -= FocusOnLoaded;
                Dispatcher.UIThread.Post(() => terminal.Focus(), DispatcherPriority.Input);
            }

            terminal.Loaded += FocusOnLoaded;
        }, DispatcherPriority.Input);
    }

    private void TerminalScrollBar_OnScroll(object? sender, ScrollEventArgs e)
    {
        if (DataContext is LibraryImportViewModel import)
            import.Session.Terminal.ViewportY = (int)e.NewValue;
    }
}

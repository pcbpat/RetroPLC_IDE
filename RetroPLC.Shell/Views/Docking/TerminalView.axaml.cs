// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using RetroPLC.Shell.ViewModels.Docking;

namespace RetroPLC.Shell.Views.Docking;

public partial class TerminalView : UserControl
{
    private TerminalViewModel? _viewModel;

    public TerminalView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as TerminalViewModel;
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        if (_viewModel.IsActive || _viewModel.IsSelected || _viewModel.ConsumeFocusRequest())
        {
            FocusTerminal();
        }
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is { IsActive: true } or { IsSelected: true } ||
            _viewModel?.ConsumeFocusRequest() == true)
        {
            FocusTerminal();
        }
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TerminalViewModel.IsActive) or nameof(TerminalViewModel.IsSelected) &&
            _viewModel is { IsActive: true } or { IsSelected: true })
        {
            FocusTerminal();
        }
    }

    private void FocusTerminal()
    {
        var terminal = _viewModel?.Terminal;
        if (terminal is null)
        {
            return;
        }

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
        if (DataContext is TerminalViewModel terminal)
        {
            terminal.Terminal.ViewportY = (int)e.NewValue;
        }
    }
}

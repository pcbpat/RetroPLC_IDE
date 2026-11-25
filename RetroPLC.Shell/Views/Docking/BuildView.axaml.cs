// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RetroPLC.Shell.ViewModels.Docking;

namespace RetroPLC.Shell.Views.Docking;

public partial class BuildView : UserControl
{
    private BuildViewModel? _viewModel;

    public BuildView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindOutput();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        BindOutput();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        UnbindOutput();
    }

    private void BindOutput()
    {
        UnbindOutput();
        if (IsLoaded && DataContext is BuildViewModel model)
        {
            _viewModel = model;
            model.Output.CollectionChanged += OnOutputChanged;
            ScrollToEnd();
        }
    }

    private void UnbindOutput()
    {
        if (_viewModel is { } model)
            model.Output.CollectionChanged -= OnOutputChanged;
        _viewModel = null;
    }

    private void OnOutputChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScrollToEnd();

    private void ScrollToEnd()
    {
        if (_viewModel?.Output.Count is > 0)
            OutputList.ScrollIntoView(_viewModel.Output.Count - 1);
    }
}

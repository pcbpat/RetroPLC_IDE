// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Input;
using RetroPLC.Shell.ViewModels.Docking;

namespace RetroPLC.Shell.Views.Docking;

public partial class ReferencesView : UserControl
{
    public ReferencesView() => InitializeComponent();

    private void TreeView_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is TreeView { SelectedItem: ReferenceTreeNode node } &&
            DataContext is ReferencesViewModel viewModel &&
            viewModel.TryNavigate(node))
            e.Handled = true;
    }
}

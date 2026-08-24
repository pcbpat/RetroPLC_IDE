// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Input;
using RetroPLC.Shell.ViewModels.Docking;

namespace RetroPLC.Shell.Views.Docking;

public partial class MessagesView : UserControl
{
    public MessagesView() => InitializeComponent();

    private void MessageList_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: LanguageMessageItem item } &&
            DataContext is MessagesViewModel viewModel &&
            viewModel.TryNavigate(item))
        {
            e.Handled = true;
        }
    }
}

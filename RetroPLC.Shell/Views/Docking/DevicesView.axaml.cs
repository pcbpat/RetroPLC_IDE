using System;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using RetroPLC.Shell.Views;
using Classic.CommonControls.Dialogs;
using RetroPLC.Shell.ViewModels.Docking;

namespace RetroPLC.Shell.Views.Docking;

public partial class DevicesView : UserControl
{
    public DevicesView()
    {
        InitializeComponent();
        ProjectTree.AddHandler(
            PointerPressedEvent,
            Tree_OnPointerPressed,
            RoutingStrategies.Tunnel);
    }

    private void Tree_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount != 2 || e.Source is not Control source)
            return;

        if (source is ToggleButton || source.FindAncestorOfType<ToggleButton>() is not null)
            return;

        var item = source as TreeViewItem ?? source.FindAncestorOfType<TreeViewItem>();
        if (item?.DataContext is DeviceTreeNode node &&
            DataContext is DevicesViewModel viewModel &&
            viewModel.TryOpenNode(node))
        {
            e.Handled = true;
        }
    }

    private async void AddPou_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DevicesViewModel viewModel ||
            this.FindAncestorOfType<Window>() is not { } owner)
        {
            return;
        }

        var dialog = new AddPouWindow();
        if (!await dialog.ShowDialog<bool>(owner) || dialog.Result is not { } definition)
            return;

        try
        {
            viewModel.AddPou(definition);
        }
        catch (Exception exception)
        {
            await MessageBox.ShowDialog(
                owner,
                exception.Message,
                "Unable to add POU",
                MessageBoxButtons.Ok,
                MessageBoxIcon.Error);
        }
    }

    private async void ImportCodesysLibrary_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DevicesViewModel viewModel ||
            this.FindAncestorOfType<Window>() is not { } owner)
            return;

        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select a CODESYS library",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("CODESYS libraries") { Patterns = ["*.lib", "*.library"] },
                FilePickerFileTypes.All
            ]
        });
        if (files.Count != 1 || files[0].TryGetLocalPath() is not { } sourcePath)
            return;

        var dialog = new ImportCodesysLibraryWindow(sourcePath);
        if (!await dialog.ShowDialog<bool>(owner) || dialog.Result is not { } import)
            return;

        try
        {
            viewModel.ImportCodesysLibrary(import);
        }
        catch (Exception exception)
        {
            await MessageBox.ShowDialog(
                owner,
                exception.Message,
                "Unable to import CODESYS library",
                MessageBoxButtons.Ok,
                MessageBoxIcon.Error);
        }
    }
}

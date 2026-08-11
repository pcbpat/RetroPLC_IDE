using System;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using RetroPLC.Shell.Models;
using RetroPLC.Shell.Views;
using Classic.CommonControls.Dialogs;
using RetroPLC.Shell.ViewModels.Docking;

namespace RetroPLC.Shell.Views.Docking;

public partial class DevicesView : UserControl
{
    private DeviceTreeNode? _contextNode;

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
        if (e.Source is not Control source)
            return;

        // Remember which node the context menu will act on. The TreeView
        // itself hosts the ContextMenu, so the menu has no per-node owner.
        // A right-click outside a node clears the previous selection so the
        // menu never acts on a stale node.
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            var contextItem = source as TreeViewItem ?? source.FindAncestorOfType<TreeViewItem>();
            _contextNode = contextItem?.DataContext as DeviceTreeNode;
            return;
        }

        if (e.ClickCount != 2)
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

    private void ContextMenu_OnOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var contextNode = _contextNode;
        AddResourceMenuItem.IsEnabled = contextNode?.Kind == ProjectNodeKinds.Configuration;
        AddTaskMenuItem.IsEnabled = contextNode?.Kind == ProjectNodeKinds.Resource;
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

    private async void AddDataType_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DevicesViewModel viewModel ||
            this.FindAncestorOfType<Window>() is not { } owner)
        {
            return;
        }

        var dialog = new AddDataTypeWindow();
        if (!await dialog.ShowDialog<bool>(owner) || dialog.Result is not { } definition)
            return;

        try
        {
            viewModel.AddDataType(definition);
        }
        catch (Exception exception)
        {
            await MessageBox.ShowDialog(
                owner,
                exception.Message,
                "Unable to add data type",
                MessageBoxButtons.Ok,
                MessageBoxIcon.Error);
        }
    }

    private async void AddConfiguration_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DevicesViewModel viewModel ||
            this.FindAncestorOfType<Window>() is not { } owner)
        {
            return;
        }

        var dialog = new AddConfigurationWindow();
        if (!await dialog.ShowDialog<bool>(owner) || dialog.ResultName is not { } name)
            return;

        try
        {
            viewModel.AddConfiguration(name);
        }
        catch (Exception exception)
        {
            await MessageBox.ShowDialog(
                owner,
                exception.Message,
                "Unable to add Configuration",
                MessageBoxButtons.Ok,
                MessageBoxIcon.Error);
        }
    }

    private async void AddResource_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_contextNode is not { Kind: ProjectNodeKinds.Configuration } configurationNode ||
            DataContext is not DevicesViewModel viewModel ||
            this.FindAncestorOfType<Window>() is not { } owner)
        {
            return;
        }

        var dialog = new AddResourceWindow();
        if (!await dialog.ShowDialog<bool>(owner) || dialog.Result is not { } definition)
            return;

        try
        {
            viewModel.AddResource(definition, configurationNode);
        }
        catch (Exception exception)
        {
            await MessageBox.ShowDialog(
                owner,
                exception.Message,
                "Unable to add Resource",
                MessageBoxButtons.Ok,
                MessageBoxIcon.Error);
        }
    }

    private async void AddTask_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_contextNode is not { Kind: ProjectNodeKinds.Resource } resourceNode ||
            DataContext is not DevicesViewModel viewModel ||
            this.FindAncestorOfType<Window>() is not { } owner)
        {
            return;
        }

        var dialog = new AddTaskWindow();
        if (!await dialog.ShowDialog<bool>(owner) || dialog.Result is not { } definition)
            return;

        try
        {
            viewModel.AddTask(definition, resourceNode);
        }
        catch (Exception exception)
        {
            await MessageBox.ShowDialog(
                owner,
                exception.Message,
                "Unable to add Task",
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

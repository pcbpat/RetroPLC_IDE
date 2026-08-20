using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Classic.CommonControls.Dialogs;
using RetroPLC.Icons;
using RetroPLC.Shell.Models;
using RetroPLC.Shell.ViewModels;

namespace RetroPLC.Shell.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void NewProject_OnClick(object? sender, RoutedEventArgs e)
    {
        await ShowNewProjectAsync();
    }

    private async void AddPou_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel || !viewModel.IsProjectOpen)
            return;

        var dialog = new AddPouWindow();
        if (!await dialog.ShowDialog<bool>(this) || dialog.Result is not { } definition)
            return;

        try
        {
            viewModel.AddPou(definition);
        }
        catch (Exception exception)
        {
            await ShowProjectError("Unable to add POU", exception);
        }
    }

    private async void AddDataType_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel || !viewModel.IsProjectOpen)
            return;

        var dialog = new AddDataTypeWindow();
        if (!await dialog.ShowDialog<bool>(this) || dialog.Result is not { } definition)
            return;

        try
        {
            viewModel.AddDataType(definition);
        }
        catch (Exception exception)
        {
            await ShowProjectError("Unable to add data type", exception);
        }
    }

    private async System.Threading.Tasks.Task ShowNewProjectAsync()
    {
        var dialog = new NewProjectWindow();
        if (!await dialog.ShowDialog<bool>(this) || dialog.Result is not { } result)
            return;

        try
        {
            if (DataContext is not MainWindowViewModel viewModel)
                throw new InvalidOperationException("The project workspace is not available.");

            var project = await viewModel.CreateProjectAsync(
                result.Location,
                result.Name,
                result.Template);
            Title = $"{project.Document.Name} - RetroPLC IDE";
        }
        catch (Exception exception)
        {
            await ShowProjectError("Unable to create project", exception);
        }
    }

    private async void MainWindow_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2)
        {
            e.Handled = true;
            if (DataContext is MainWindowViewModel renameViewModel &&
                renameViewModel.RenameSymbolCommand.CanExecute(null))
            {
                renameViewModel.RenameSymbolCommand.Execute(null);
            }
            return;
        }

        if (e.Key == Key.F12)
        {
            e.Handled = true;
            if (DataContext is not MainWindowViewModel navigationViewModel)
                return;

            var command = e.KeyModifiers.HasFlag(KeyModifiers.Shift)
                ? navigationViewModel.FindReferencesCommand
                : navigationViewModel.GoToDefinitionCommand;
            if (command.CanExecute(null))
                command.Execute(null);
            return;
        }

        if (e.KeyModifiers != KeyModifiers.Control)
        {
            if (e.Key == Key.F &&
                e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Alt) &&
                DataContext is MainWindowViewModel formatViewModel &&
                formatViewModel.FormatDocumentCommand.CanExecute(null))
            {
                e.Handled = true;
                formatViewModel.FormatDocumentCommand.Execute(null);
            }
            return;
        }

        switch (e.Key)
        {
            case Key.N:
                e.Handled = true;
                await ShowNewProjectAsync();
                break;

            case Key.S:
                e.Handled = true;
                if (DataContext is MainWindowViewModel viewModel &&
                    viewModel.SaveActiveDocumentCommand.CanExecute(null))
                {
                    viewModel.SaveActiveDocumentCommand.Execute(null);
                }
                break;
        }
    }

    private async void OpenProject_OnClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = $"Select the folder containing {ProjectStore.ManifestFileName}",
            AllowMultiple = false
        });

        if (folders.Count != 1 || folders[0].TryGetLocalPath() is not { } projectDirectory)
            return;

        try
        {
            await LoadProjectAsync(ProjectStore.Open(projectDirectory));
        }
        catch (Exception exception)
        {
            await ShowProjectError("Unable to open project", exception);
        }
    }

    private async void ImportCodesysLibrary_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel || !viewModel.IsProjectOpen)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
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
        if (!await dialog.ShowDialog<bool>(this) || dialog.Result is not { } import)
            return;

        try
        {
            viewModel.ImportCodesysLibrary(import);
        }
        catch (Exception exception)
        {
            await ShowProjectError("Unable to import CODESYS library", exception);
        }
    }

    private async System.Threading.Tasks.Task LoadProjectAsync(OpenedProject project)
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;

        await viewModel.OpenProjectAsync(project);
        Title = $"{project.Document.Name} - RetroPLC IDE";
    }

    private async System.Threading.Tasks.Task ShowProjectError(string title, Exception exception)
    {
        await MessageBox.ShowDialog(
            this,
            exception.Message,
            title,
            MessageBoxButtons.Ok,
            MessageBoxIcon.Error);
    }

    private async void About_OnClick(object? sender, RoutedEventArgs e)
    {
        await AboutDialog.ShowDialog(this, new AboutDialogOptions
        {
            Title = "RetroPLC IDE",
            SubTitle = "IEC 61131-3 Structured Text development environment",
            Copyright = "Copyright © 2026 RetroPLC contributors",
            Icon = Se98Icons.Actions.Size32.HelpAbout
        });
    }

    private void Exit_OnClick(object? sender, RoutedEventArgs e) => Close();

    private void FullScreen_OnClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.FullScreen
            ? WindowState.Normal
            : WindowState.FullScreen;

    private void AppearanceMode_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: { } mode } &&
            DataContext is MainWindowViewModel viewModel)
        {
            viewModel.IsDarkMode = string.Equals(
                mode.ToString(),
                "Dark",
                StringComparison.OrdinalIgnoreCase);
        }
    }

    private void FontSize_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: { } value } &&
            DataContext is MainWindowViewModel viewModel &&
            double.TryParse(value.ToString(), out var size))
        {
            viewModel.TextSize = size;
        }
    }

    private void ResizeGrip_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginResizeDrag(WindowEdge.SouthEast, e);
            e.Handled = true;
        }
    }
}

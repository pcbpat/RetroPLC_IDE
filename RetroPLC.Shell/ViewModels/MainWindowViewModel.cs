// SPDX-License-Identifier: GPL-3.0-or-later
﻿﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Controls;
using Dock.Model.Core;
using RetroPLC.BuildHost;
using RetroPLC.Shell.Models;
using RetroPLC.Shell.ViewModels.Docking;
using RetroPLC.Theme;

namespace RetroPLC.Shell.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly DockFactory _factory;
    private IRootDock? _layout;
    private bool _isDarkMode = true;
    private double _textSize = 12;
    private bool _isOnline;
    private bool _isLoggedIn;
    private bool _isRunning;
    private bool _isProjectOpen;
    private bool _isLoadingProject;
    private bool _isPreparingBuild;
    private string _plcStatus = string.Empty;

    public IRootDock? Layout
    {
        get => _layout;
        private set => SetProperty(ref _layout, value);
    }

    public ICommand ResetLayoutCommand { get; }
    public IRelayCommand SaveActiveDocumentCommand { get; }
    public IRelayCommand SaveAllDocumentsCommand { get; }
    public IRelayCommand RenameSymbolCommand { get; }
    public IRelayCommand GoToDefinitionCommand { get; }
    public IRelayCommand FindReferencesCommand { get; }
    public IRelayCommand FormatDocumentCommand { get; }
    public IRelayCommand CloseAllDocumentsCommand { get; }
    public IRelayCommand OpenTerminalCommand { get; }
    public IRelayCommand GoOnlineCommand { get; }
    public IRelayCommand GoOfflineCommand { get; }
    public IRelayCommand LoginCommand { get; }
    public IRelayCommand LogoutCommand { get; }
    public IRelayCommand RunCommand { get; }
    public IRelayCommand StopCommand { get; }
    public IRelayCommand ResetPlcCommand { get; }
    public IRelayCommand VerifyCommand { get; }
    public IRelayCommand BuildCommand { get; }
    public IRelayCommand RebuildCommand { get; }
    public IRelayCommand CleanCommand { get; }
    public IRelayCommand DownloadCommand { get; }

    public string PlcStatus
    {
        get => _plcStatus;
        private set => SetProperty(ref _plcStatus, value);
    }

    public bool IsProjectOpen
    {
        get => _isProjectOpen;
        private set => SetProperty(ref _isProjectOpen, value);
    }

    public bool IsLoadingProject
    {
        get => _isLoadingProject;
        private set => SetProperty(ref _isLoadingProject, value);
    }

    public bool IsDarkMode
    {
        get => _isDarkMode;
        set
        {
            if (SetProperty(ref _isDarkMode, value))
            {
                ApplyTheme();
            }
        }
    }

    public double TextSize
    {
        get => _textSize;
        set
        {
            var size = System.Math.Clamp(value, 8, 20);
            if (SetProperty(ref _textSize, size))
            {
                ApplyTextSize();
            }
        }
    }

    public async Task OpenProjectAsync(OpenedProject project)
    {
        IsLoadingProject = true;
        try
        {
            // Let the Welcome panel paint the progress bar before the blocking
            // workspace load (tree migration, dock layout, language server).
            await Dispatcher.Yield(DispatcherPriority.Background);
            OpenProjectCore(project);
        }
        finally
        {
            IsLoadingProject = false;
        }
    }

    public async Task<OpenedProject> CreateProjectAsync(
        string location,
        string name,
        string template)
    {
        IsLoadingProject = true;
        try
        {
            // Let the Welcome panel paint the progress bar before the blocking
            // workspace creation (template copy, tree, dock layout).
            await Dispatcher.Yield(DispatcherPriority.Background);
            var tree = CreateDefaultProjectTree(name);
            var project = ProjectStore.Create(location, name, template, tree);
            OpenProjectCore(project);
            return project;
        }
        finally
        {
            IsLoadingProject = false;
        }
    }

    public void AddPou(NewPouDefinition definition) =>
        _factory.AddPou(definition);

    public void AddDataType(NewDataTypeDefinition definition) =>
        _factory.AddDataType(definition);

    public void ImportCodesysLibrary(CodesysLibraryImport import) =>
        _factory.ImportCodesysLibrary(import);

    private static IReadOnlyList<ProjectNodeDefinition> CreateDefaultProjectTree(string projectName) =>
        DevicesViewModel.CreateDefaultNodes(projectName)
            .Select(node => node.ToDefinition())
            .ToList();

    public Task ShutdownAsync() => _factory.ShutdownAsync();

    public MainWindowViewModel()
    {
        _factory = new DockFactory();
        ResetLayoutCommand = new RelayCommand(CreateLayout);
        SaveActiveDocumentCommand = new RelayCommand(_factory.SaveActiveDocument);
        SaveAllDocumentsCommand = new RelayCommand(_factory.SaveAllDocuments);
        RenameSymbolCommand = new RelayCommand(
            _factory.RequestRenameActiveDocument,
            () => _isProjectOpen);
        GoToDefinitionCommand = new RelayCommand(
            _factory.RequestGoToDefinitionActiveDocument,
            () => _isProjectOpen);
        FindReferencesCommand = new RelayCommand(
            _factory.RequestFindReferencesActiveDocument,
            () => _isProjectOpen);
        FormatDocumentCommand = new RelayCommand(
            _factory.RequestFormatActiveDocument,
            () => _isProjectOpen);
        CloseAllDocumentsCommand = new RelayCommand(_factory.CloseAllDocuments);
        OpenTerminalCommand = new AsyncRelayCommand(_factory.OpenTerminalAsync);
        GoOnlineCommand = new RelayCommand(GoOnline, () => !_isOnline);
        GoOfflineCommand = new RelayCommand(GoOffline, () => _isOnline);
        LoginCommand = new RelayCommand(Login, () => _isOnline && !_isLoggedIn);
        LogoutCommand = new RelayCommand(Logout, () => _isLoggedIn);
        RunCommand = new RelayCommand(Run, () => _isLoggedIn && !_isRunning);
        StopCommand = new RelayCommand(Stop, () => _isLoggedIn && _isRunning);
        ResetPlcCommand = new RelayCommand(ResetPlc, () => _isLoggedIn);
        VerifyCommand = new AsyncRelayCommand(VerifyAsync, () => _isProjectOpen);
        BuildCommand = new AsyncRelayCommand(BuildAsync, () => _isProjectOpen);
        RebuildCommand = new AsyncRelayCommand(RebuildAsync, () => _isProjectOpen);
        CleanCommand = new AsyncRelayCommand(CleanAsync, () => _isProjectOpen);
        DownloadCommand = new AsyncRelayCommand(DownloadAsync, () => _isProjectOpen && !_isRunning);
        _factory.BuildOperationExited += OnBuildOperationExited;
        ApplyTheme();
        ApplyTextSize();
    }

    private void OpenProjectCore(OpenedProject project)
    {
        _factory.OpenProject(project.Document, project.DirectoryPath);
        CreateLayout();
        IsProjectOpen = true;
        VerifyCommand.NotifyCanExecuteChanged();
        BuildCommand.NotifyCanExecuteChanged();
        RebuildCommand.NotifyCanExecuteChanged();
        CleanCommand.NotifyCanExecuteChanged();
        DownloadCommand.NotifyCanExecuteChanged();
        RenameSymbolCommand.NotifyCanExecuteChanged();
        GoToDefinitionCommand.NotifyCanExecuteChanged();
        FindReferencesCommand.NotifyCanExecuteChanged();
        FormatDocumentCommand.NotifyCanExecuteChanged();
    }

    private void ApplyTextSize()
    {
        if (Application.Current is not { } application)
        {
            return;
        }

        application.Resources["FontSizeSmall"] = System.Math.Max(8, TextSize - 2);
        application.Resources["FontSizeNormal"] = TextSize;
        application.Resources["FontSizeLarge"] = TextSize + 2;
        application.Resources[AppResourceKeys.CodeTextSize] = TextSize + 2;
        application.Resources[AppResourceKeys.StatusBarHeight] = TextSize + 10;
        application.Resources[AppResourceKeys.DockTabMinHeight] = TextSize + 10;
        application.Resources[AppResourceKeys.DockButtonSize] = System.Math.Max(13, TextSize + 2);
        application.Resources[AppResourceKeys.DockCloseGlyphWidth] = System.Math.Max(8, TextSize * 0.65);
        application.Resources[AppResourceKeys.DockCloseGlyphHeight] = System.Math.Max(7, TextSize * 0.55);
        application.Resources[AppResourceKeys.DockPinGlyphWidth] = System.Math.Max(6, TextSize * 0.5);
        application.Resources[AppResourceKeys.DockPinGlyphHeight] = System.Math.Max(9, TextSize * 0.7);
        application.Resources[AppResourceKeys.DockMenuGlyphWidth] = System.Math.Max(6, TextSize * 0.5);
        application.Resources[AppResourceKeys.DockMenuGlyphHeight] = System.Math.Max(3, TextSize * 0.3);
        application.Resources[Classic.CommonControls.SystemParameters.MenuBarHeightKey] = TextSize + 10;
        application.Resources[AppResourceKeys.MenuItemHeight] = TextSize + 10;
        application.Resources[AppResourceKeys.MenuItemPadding] =
            new Thickness(System.Math.Max(4, TextSize * 0.45), 0);
        application.Resources[AppResourceKeys.MenuSeparatorHeight] = System.Math.Max(8, TextSize * 0.5);
        application.Resources[AppResourceKeys.MenuArrowWidth] = System.Math.Max(4, TextSize * 0.3);
        application.Resources[AppResourceKeys.MenuArrowHeight] = System.Math.Max(7, TextSize * 0.55);
        application.Resources[AppResourceKeys.MenuCheckSize] = System.Math.Max(7, TextSize * 0.55);
    }

    private void ApplyTheme()
    {
        if (Application.Current is not { } application)
        {
            return;
        }

        application.RequestedThemeVariant = IsDarkMode
            ? ThemeVariants.Dark
            : ThemeVariants.Light;
    }

    private void GoOnline()
    {
        _isOnline = true;
        RefreshPlcCommands();
    }

    private void GoOffline()
    {
        _isRunning = false;
        _isLoggedIn = false;
        _isOnline = false;
        RefreshPlcCommands();
    }

    private void Login()
    {
        _isLoggedIn = true;
        RefreshPlcCommands();
    }

    private void Logout()
    {
        _isRunning = false;
        _isLoggedIn = false;
        RefreshPlcCommands();
    }

    private void Run()
    {
        _isRunning = true;
        RefreshPlcCommands();
    }

    private void Stop()
    {
        _isRunning = false;
        RefreshPlcCommands();
    }

    private void ResetPlc()
    {
        _isRunning = false;
        RefreshPlcCommands();
    }

    private Task VerifyAsync() => StartBuildOperationAsync(BuildOperation.Verify, "verifying…");
    private Task BuildAsync() => StartBuildOperationAsync(BuildOperation.Build, "building…");
    private Task RebuildAsync() => StartBuildOperationAsync(BuildOperation.Rebuild, "rebuilding…");
    private Task CleanAsync() => StartBuildOperationAsync(BuildOperation.Clean, "cleaning…");
    private Task DownloadAsync() => StartBuildOperationAsync(BuildOperation.Download, "downloading…");

    private async Task StartBuildOperationAsync(BuildOperation operation, string status)
    {
        if (_isPreparingBuild)
            return;

        _isPreparingBuild = true;
        PlcStatus = status;
        try
        {
            _factory.SaveAllDocuments();
            await _factory.RunBuildToolAsync(operation);
        }
        catch (Exception exception)
        {
            PlcStatus = $"{GetBuildOperationName(operation)} failed: {exception.Message}";
        }
        finally
        {
            _isPreparingBuild = false;
        }
    }

    private void OnBuildOperationExited(BuildOperation operation, int exitCode)
    {
        var action = GetBuildOperationName(operation);
        var result = exitCode == 0 ? $"{action} succeeded" :
            _factory.BuildOperationError is { } error ? $"{action} failed: {error}" :
            $"{action} failed ({exitCode}). See the Build output.";
        PlcStatus = result;
    }

    private static string GetBuildOperationName(BuildOperation operation) =>
        operation switch
        {
            BuildOperation.Verify => "verification",
            BuildOperation.Build => "build",
            BuildOperation.Rebuild => "rebuild",
            BuildOperation.Clean => "clean",
            BuildOperation.Download => "download",
            _ => "operation"
        };

    private void RefreshPlcCommands()
    {
        GoOnlineCommand.NotifyCanExecuteChanged();
        GoOfflineCommand.NotifyCanExecuteChanged();
        LoginCommand.NotifyCanExecuteChanged();
        LogoutCommand.NotifyCanExecuteChanged();
        RunCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        ResetPlcCommand.NotifyCanExecuteChanged();
        DownloadCommand.NotifyCanExecuteChanged();
    }

    private void CreateLayout()
    {
        if (Layout is IDock oldLayout && oldLayout.Close.CanExecute(null))
        {
            oldLayout.Close.Execute(null);
        }

        var layout = _factory.CreateLayout();
        _factory.InitLayout(layout);
        Layout = layout;
    }
}

public static class AppResourceKeys
{
    public const string CodeTextSize = "RetroPLC.Shell.CodeTextSize";
    public const string StatusBarHeight = "RetroPLC.Shell.StatusBarHeight";
    public const string DockTabMinHeight = "RetroPLC.Shell.DockTabMinHeight";
    public const string DockButtonSize = "RetroPLC.Shell.DockButtonSize";
    public const string DockCloseGlyphWidth = "RetroPLC.Shell.DockCloseGlyphWidth";
    public const string DockCloseGlyphHeight = "RetroPLC.Shell.DockCloseGlyphHeight";
    public const string DockPinGlyphWidth = "RetroPLC.Shell.DockPinGlyphWidth";
    public const string DockPinGlyphHeight = "RetroPLC.Shell.DockPinGlyphHeight";
    public const string DockMenuGlyphWidth = "RetroPLC.Shell.DockMenuGlyphWidth";
    public const string DockMenuGlyphHeight = "RetroPLC.Shell.DockMenuGlyphHeight";
    public const string MenuItemHeight = "RetroPLC.Shell.MenuItemHeight";
    public const string MenuItemPadding = "RetroPLC.Shell.MenuItemPadding";
    public const string MenuSeparatorHeight = "RetroPLC.Shell.MenuSeparatorHeight";
    public const string MenuArrowWidth = "RetroPLC.Shell.MenuArrowWidth";
    public const string MenuArrowHeight = "RetroPLC.Shell.MenuArrowHeight";
    public const string MenuCheckSize = "RetroPLC.Shell.MenuCheckSize";
}

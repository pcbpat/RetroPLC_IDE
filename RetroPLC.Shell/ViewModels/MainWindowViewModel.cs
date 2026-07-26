﻿﻿using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Classic.Avalonia.Theme;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Controls;
using Dock.Model.Core;
using RetroPLC.Shell.Models;
using RetroPLC.Shell.ViewModels.Docking;
using ClassicSystemColors = Classic.CommonControls.SystemColors;

namespace RetroPLC.Shell.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly DockFactory _factory;
    private IRootDock? _layout;
    private ThemeOption _selectedTheme;
    private bool _isDarkMode = true;
    private double _textSize = 12;
    private bool _isOnline;
    private bool _isLoggedIn;
    private bool _isRunning;
    private bool _isProjectOpen;
    private string _plcStatus = "Offline";

    public IReadOnlyList<ThemeOption> Themes { get; } =
    [
        new("Standard Windows", ClassicTheme.Standard, "#0A246A", "#A6CAF0", "#0A246A"),
        new("Classic Windows", ClassicTheme.Classic, "#000080", "#1084D0", "#000080"),
        new("Brick", ClassicTheme.Brick, "#800000", "#B07440", "#8D8961"),
        new("Wheat", ClassicTheme.Wheat, "#808000", "#C8B048", "#808000"),
        new("Marine", ClassicTheme.Marine, "#000080", "#18B4C0", "#000080"),
        new("Spruce", ClassicTheme.Sprouce, "#599764", "#98C8E8", "#599764"),
        new("Plum", ClassicTheme.Plum, "#484060", "#A084B8", "#008080"),
        new("Rose", ClassicTheme.Rose, "#9F6070", "#D8CCD0", "#9F6070"),
        new("Storm", ClassicTheme.Storm, "#800080", "#388CB0", "#800080"),
        new("Desert", ClassicTheme.Desert, "#008080", "#84BDAA", "#008080"),
        new("Eggplant", ClassicTheme.Eggplant, "#588078", "#834B83", "#588078"),
        new("Stars and Stripes", ClassicTheme.StarsAndStripes, "#800000", "#0010A8", "#800000"),
        new("Pumpkin", ClassicTheme.Pumpkin, "#D7A52F", "#E0CC88", "#800080")
    ];

    public IReadOnlyList<double> TextSizes { get; } = [9, 10, 11, 12, 13, 14, 16, 18, 20];

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
    public IRelayCommand CloseAllDocumentsCommand { get; }
    public IRelayCommand OpenTerminalCommand { get; }
    public IRelayCommand OpenAppearanceCommand { get; }
    public IRelayCommand GoOnlineCommand { get; }
    public IRelayCommand GoOfflineCommand { get; }
    public IRelayCommand LoginCommand { get; }
    public IRelayCommand LogoutCommand { get; }
    public IRelayCommand RunCommand { get; }
    public IRelayCommand StopCommand { get; }
    public IRelayCommand ResetPlcCommand { get; }
    public IRelayCommand BuildCommand { get; }
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

    public ThemeOption SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (SetProperty(ref _selectedTheme, value))
            {
                ApplyTheme();
            }
        }
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
            var size = System.Math.Clamp(value, 9, 20);
            if (SetProperty(ref _textSize, size))
            {
                ApplyTextSize();
            }
        }
    }

    public void OpenProject(OpenedProject project) =>
        OpenProjectCore(project);

    public void AddPou(NewPouDefinition definition) =>
        _factory.AddPou(definition);

    public void ImportCodesysLibrary(CodesysLibraryImport import) =>
        _factory.ImportCodesysLibrary(import);

    public IReadOnlyList<ProjectNodeDefinition> CreateDefaultProjectTree(string projectName) =>
        DevicesViewModel.CreateDefaultNodes(projectName)
            .Select(node => node.ToDefinition())
            .ToList();

    public Task ShutdownAsync() => _factory.ShutdownAsync();

    public MainWindowViewModel()
    {
        _selectedTheme = Themes[1];
        _factory = new DockFactory(this);
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
        CloseAllDocumentsCommand = new RelayCommand(_factory.CloseAllDocuments);
        OpenTerminalCommand = new RelayCommand(_factory.OpenTerminal);
        OpenAppearanceCommand = new RelayCommand(_factory.OpenAppearance, () => _isProjectOpen);
        GoOnlineCommand = new RelayCommand(GoOnline, () => !_isOnline);
        GoOfflineCommand = new RelayCommand(GoOffline, () => _isOnline);
        LoginCommand = new RelayCommand(Login, () => _isOnline && !_isLoggedIn);
        LogoutCommand = new RelayCommand(Logout, () => _isLoggedIn);
        RunCommand = new RelayCommand(Run, () => _isLoggedIn && !_isRunning);
        StopCommand = new RelayCommand(Stop, () => _isLoggedIn && _isRunning);
        ResetPlcCommand = new RelayCommand(ResetPlc, () => _isLoggedIn);
        BuildCommand = new RelayCommand(Build, () => _isProjectOpen);
        _factory.BuildExited += OnBuildExited;
        DownloadCommand = new RelayCommand(Download, () => _isLoggedIn && !_isRunning);
        ApplyTheme();
        ApplyTextSize();
    }

    private void OpenProjectCore(OpenedProject project)
    {
        _factory.OpenProject(project.Document, project.DirectoryPath);
        CreateLayout();
        IsProjectOpen = true;
        BuildCommand.NotifyCanExecuteChanged();
        RenameSymbolCommand.NotifyCanExecuteChanged();
        GoToDefinitionCommand.NotifyCanExecuteChanged();
        FindReferencesCommand.NotifyCanExecuteChanged();
        OpenAppearanceCommand.NotifyCanExecuteChanged();
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

        application.Resources[AppResourceKeys.DarkMode] = IsDarkMode;
        ClearDarkPalette(application);
        application.RequestedThemeVariant = SelectedTheme.Variant;

        if (IsDarkMode)
        {
            ApplyDarkPalette(application, SelectedTheme);
        }
    }

    private static void ApplyDarkPalette(Application application, ThemeOption theme)
    {
        var activeCaption = Shade(theme.ActiveCaption, 0.9);
        var activeCaptionGradient = Shade(theme.GradientCaption, 0.58);
        var highlight = Shade(theme.Highlight, 0.9);

        Set(application, ClassicSystemColors.ActiveBorderColorKey, "#484848");
        Set(application, ClassicSystemColors.ActiveCaptionColorKey, activeCaption);
        Set(application, ClassicSystemColors.ActiveCaptionTextColorKey, "#FFFFFF");
        Set(application, ClassicSystemColors.AppWorkspaceColorKey, "#181818");
        Set(application, ClassicSystemColors.ControlColorKey, "#303030");
        Set(application, ClassicSystemColors.ControlDarkColorKey, "#1B1B1B");
        Set(application, ClassicSystemColors.ControlDarkDarkColorKey, "#080808");
        Set(application, ClassicSystemColors.ControlLightColorKey, "#505050");
        Set(application, ClassicSystemColors.ControlLightLightColorKey, "#707070");
        Set(application, ClassicSystemColors.ControlTextColorKey, "#F0F0F0");
        Set(application, ClassicSystemColors.DesktopColorKey, Mix("#101010", activeCaption, 0.18));
        Set(application, ClassicSystemColors.GradientActiveCaptionColorKey, activeCaptionGradient);
        Set(application, ClassicSystemColors.GradientInactiveCaptionColorKey, Shade(activeCaptionGradient, 0.62));
        Set(application, ClassicSystemColors.GrayTextColorKey, "#8A8A8A");
        Set(application, ClassicSystemColors.HighlightColorKey, highlight);
        Set(application, ClassicSystemColors.HighlightTextColorKey, "#FFFFFF");
        Set(application, ClassicSystemColors.HotTrackColorKey, theme.GradientCaption);
        Set(application, ClassicSystemColors.InactiveBorderColorKey, "#3C3C3C");
        Set(application, ClassicSystemColors.InactiveCaptionColorKey, Shade(activeCaption, 0.48));
        Set(application, ClassicSystemColors.InactiveCaptionTextColorKey, "#D0D0D0");
        Set(application, ClassicSystemColors.InfoColorKey, "#383426");
        Set(application, ClassicSystemColors.InfoTextColorKey, "#F0F0F0");
        Set(application, ClassicSystemColors.MenuColorKey, "#303030");
        Set(application, ClassicSystemColors.MenuBarColorKey, "#303030");
        Set(application, ClassicSystemColors.MenuHighlightColorKey, highlight);
        Set(application, ClassicSystemColors.MenuTextColorKey, "#F0F0F0");
        Set(application, ClassicSystemColors.ScrollBarColorKey, "#383838");
        Set(application, ClassicSystemColors.WindowColorKey, "#1E1E1E");
        Set(application, ClassicSystemColors.WindowFrameColorKey, "#080808");
        Set(application, ClassicSystemColors.WindowTextColorKey, "#F0F0F0");
    }

    private static void ClearDarkPalette(Application application)
    {
        foreach (var key in DarkColorKeys)
        {
            application.Resources.Remove(key);
        }
    }

    private static void Set(Application application, object key, string color) =>
        application.Resources[key] = Color.Parse(color);

    private static void Set(Application application, object key, Color color) =>
        application.Resources[key] = color;

    private static Color Shade(Color color, double factor) =>
        Color.FromRgb(
            (byte)(color.R * factor),
            (byte)(color.G * factor),
            (byte)(color.B * factor));

    private static Color Mix(string baseColor, Color accent, double accentAmount)
    {
        var background = Color.Parse(baseColor);
        var baseAmount = 1.0 - accentAmount;
        return Color.FromRgb(
            (byte)(background.R * baseAmount + accent.R * accentAmount),
            (byte)(background.G * baseAmount + accent.G * accentAmount),
            (byte)(background.B * baseAmount + accent.B * accentAmount));
    }

    private static readonly object[] DarkColorKeys =
    [
        ClassicSystemColors.ActiveBorderColorKey,
        ClassicSystemColors.ActiveCaptionColorKey,
        ClassicSystemColors.ActiveCaptionTextColorKey,
        ClassicSystemColors.AppWorkspaceColorKey,
        ClassicSystemColors.ControlColorKey,
        ClassicSystemColors.ControlDarkColorKey,
        ClassicSystemColors.ControlDarkDarkColorKey,
        ClassicSystemColors.ControlLightColorKey,
        ClassicSystemColors.ControlLightLightColorKey,
        ClassicSystemColors.ControlTextColorKey,
        ClassicSystemColors.DesktopColorKey,
        ClassicSystemColors.GradientActiveCaptionColorKey,
        ClassicSystemColors.GradientInactiveCaptionColorKey,
        ClassicSystemColors.GrayTextColorKey,
        ClassicSystemColors.HighlightColorKey,
        ClassicSystemColors.HighlightTextColorKey,
        ClassicSystemColors.HotTrackColorKey,
        ClassicSystemColors.InactiveBorderColorKey,
        ClassicSystemColors.InactiveCaptionColorKey,
        ClassicSystemColors.InactiveCaptionTextColorKey,
        ClassicSystemColors.InfoColorKey,
        ClassicSystemColors.InfoTextColorKey,
        ClassicSystemColors.MenuColorKey,
        ClassicSystemColors.MenuBarColorKey,
        ClassicSystemColors.MenuHighlightColorKey,
        ClassicSystemColors.MenuTextColorKey,
        ClassicSystemColors.ScrollBarColorKey,
        ClassicSystemColors.WindowColorKey,
        ClassicSystemColors.WindowFrameColorKey,
        ClassicSystemColors.WindowTextColorKey
    ];

    private void GoOnline()
    {
        _isOnline = true;
        PlcStatus = "Online · not logged in";
        RefreshPlcCommands();
    }

    private void GoOffline()
    {
        _isRunning = false;
        _isLoggedIn = false;
        _isOnline = false;
        PlcStatus = "Offline";
        RefreshPlcCommands();
    }

    private void Login()
    {
        _isLoggedIn = true;
        PlcStatus = "Online · STOP";
        RefreshPlcCommands();
    }

    private void Logout()
    {
        _isRunning = false;
        _isLoggedIn = false;
        PlcStatus = "Online · not logged in";
        RefreshPlcCommands();
    }

    private void Run()
    {
        _isRunning = true;
        PlcStatus = "Online · RUN";
        RefreshPlcCommands();
    }

    private void Stop()
    {
        _isRunning = false;
        PlcStatus = "Online · STOP";
        RefreshPlcCommands();
    }

    private void ResetPlc()
    {
        _isRunning = false;
        PlcStatus = "Online · reset complete · STOP";
        RefreshPlcCommands();
    }

    private void Build()
    {
        PlcStatus = _isOnline ? "Online · building…" : "Offline · building…";
        try
        {
            _factory.SaveAllDocuments();
            _factory.Build();
        }
        catch
        {
            OnBuildExited(-1);
        }
    }

    private void OnBuildExited(int exitCode)
    {
        var result = exitCode == 0 ? "build succeeded" : $"build failed ({exitCode})";
        PlcStatus = _isOnline ? $"Online · {result}" : $"Offline · {result}";
    }

    private void Download()
    {
        PlcStatus = "Online · download complete · STOP";
    }

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

public sealed record ThemeOption
{
    public ThemeOption(
        string name,
        ThemeVariant variant,
        string activeCaption,
        string gradientCaption,
        string highlight)
    {
        Name = name;
        Variant = variant;
        ActiveCaption = Color.Parse(activeCaption);
        GradientCaption = Color.Parse(gradientCaption);
        Highlight = Color.Parse(highlight);
    }

    public string Name { get; }
    public ThemeVariant Variant { get; }
    public Color ActiveCaption { get; }
    public Color GradientCaption { get; }
    public Color Highlight { get; }
}

public static class AppResourceKeys
{
    public const string DarkMode = "RetroPLC.Shell.DarkMode";
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

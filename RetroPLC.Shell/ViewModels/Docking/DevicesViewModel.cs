using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using RetroPLC.LanguageServerHost;
using Dock.Model.Mvvm.Controls;
using RetroPLC.Shell.Models;

namespace RetroPLC.Shell.ViewModels.Docking;

public sealed class DevicesViewModel : Tool
{
    private readonly Action<string>? _openDocument;
    private readonly Action<StrucppLocation>? _navigateToLocation;
    private readonly Action<NewPouDefinition>? _addPou;
    private readonly Action<string>? _openLibrary;
    private readonly Action<CodesysLibraryImport>? _importCodesysLibrary;
    private readonly Dictionary<string, IReadOnlyList<StrucppDocumentSymbol>> _documentSymbols =
        new(StringComparer.OrdinalIgnoreCase);
    private ProjectDocument? _currentProject;
    private string? _projectDirectory;

    public DevicesViewModel(
        Action<string>? openDocument = null,
        Action<NewPouDefinition>? addPou = null,
        Action<string>? openLibrary = null,
        Action<CodesysLibraryImport>? importCodesysLibrary = null,
        Action<StrucppLocation>? navigateToLocation = null)
    {
        _openDocument = openDocument;
        _navigateToLocation = navigateToLocation;
        _addPou = addPou;
        _openLibrary = openLibrary;
        _importCodesysLibrary = importCodesysLibrary;
        Nodes = [];
    }

    public ObservableCollection<DeviceTreeNode> Nodes { get; }

    public static IReadOnlyList<DeviceTreeNode> CreateDefaultNodes(string projectName) =>
    [
        new(projectName, DeviceIcons.Application,
        [
            new("Software", DeviceIcons.Software,
            [
                new("Application", DeviceIcons.Application,
                [
                    new("POUs", DeviceIcons.Pous,
                    [
                        new("Programs", DeviceIcons.Folder,
                        [
                            new("Main (PROGRAM)", DeviceIcons.Program,
                                filePath: "ProjectFiles/POUs/Programs/Main.st"),
                            new("Blink (PROGRAM)", DeviceIcons.Program,
                                filePath: "ProjectFiles/POUs/Programs/Blink.st")
                        ], false),
                        new("Function Blocks", DeviceIcons.Folder,
                        [
                            new("Counter (FUNCTION_BLOCK)", DeviceIcons.Program,
                                filePath: "ProjectFiles/POUs/FunctionBlocks/Counter.st"),
                            new("MotorController (FUNCTION_BLOCK)", DeviceIcons.Program,
                                filePath: "ProjectFiles/POUs/FunctionBlocks/MotorController.st"),
                            new("PID_Controller (FUNCTION_BLOCK)", DeviceIcons.Program,
                                filePath: "ProjectFiles/POUs/FunctionBlocks/PID_Controller.st")
                        ], false),
                        new("Functions", DeviceIcons.Folder,
                        [
                            new("ScaleAnalog (FUNCTION)", DeviceIcons.Program,
                                filePath: "ProjectFiles/POUs/Functions/ScaleAnalog.st")
                        ], false),
                        new("Interfaces", DeviceIcons.Folder,
                        [
                            new("IRunnable (INTERFACE)", DeviceIcons.Program,
                                filePath: "ProjectFiles/POUs/Interfaces/IRunnable.st")
                        ], false)
                    ]),
                    new("Data Types", DeviceIcons.DataTypes,
                    [
                        new("Structures", DeviceIcons.Folder,
                        [
                            new("MachineConfig (STRUCT)", DeviceIcons.Settings,
                                filePath: "ProjectFiles/DataTypes/MachineConfig.st")
                        ], false),
                        new("Enumerations", DeviceIcons.Folder,
                        [
                            new("MotorState (ENUM)", DeviceIcons.Settings,
                                filePath: "ProjectFiles/DataTypes/MotorState.st")
                        ], false),
                        new("Aliases and Subranges", DeviceIcons.Folder, isExpanded: false)
                    ], false),
                    new("Global Variable Lists", DeviceIcons.Globe,
                    [
                        new("GVL", DeviceIcons.Globe,
                            filePath: "ProjectFiles/GlobalVariables/GVL.st"),
                        new("Persistent Variables", DeviceIcons.Globe,
                            filePath: "ProjectFiles/GlobalVariables/PersistentVariables.st"),
                        new("Global Constants", DeviceIcons.Globe,
                            filePath: "ProjectFiles/GlobalVariables/GlobalConstants.st")
                    ], false),
                    new("Tests", DeviceIcons.Task,
                    [
                        new("CounterTests", DeviceIcons.Task,
                            filePath: "ProjectFiles/Tests/CounterTests.st"),
                        new("MotorControlTests", DeviceIcons.Task,
                            filePath: "ProjectFiles/Tests/MotorControlTests.st"),
                        new("PIDControllerTests", DeviceIcons.Task,
                            filePath: "ProjectFiles/Tests/PIDControllerTests.st")
                    ], false)
                ]),
                new("Libraries", DeviceIcons.Library,
                    CreateLibraryNodes(), false),
                new("Build and Deployment", DeviceIcons.Settings,
                [
                    new("Compiler Settings (C++17)", DeviceIcons.Settings),
                    new("Runtime Library", DeviceIcons.Library),
                    new("Generated C++ Sources", DeviceIcons.Program)
                ], false),
                new("Project Documentation", DeviceIcons.Folder, isExpanded: false)
            ]),
            new("Hardware", DeviceIcons.Controller,
            [
                new("MainConfiguration (CONFIGURATION)", DeviceIcons.Settings,
                [
                    CreateController("PLC_1 (RESOURCE · 192.168.0.10)", true),
                    CreateController("PLC_2 (RESOURCE · 192.168.0.11)", false)
                ]),
                new("I/O Mapping", DeviceIcons.Network,
                [
                    new("Process Inputs", DeviceIcons.Device),
                    new("Process Outputs", DeviceIcons.Device)
                ], false)
            ])
        ])
    ];

    public void LoadProject(ProjectDocument document, string projectDirectory)
    {
        if (!string.Equals(_projectDirectory, projectDirectory, StringComparison.OrdinalIgnoreCase))
            _documentSymbols.Clear();
        _currentProject = document;
        _projectDirectory = projectDirectory;
        SynchronizeProjectFiles(document, projectDirectory);
        SynchronizeLibraries(document, projectDirectory);
        LoadProjectTree(document, projectDirectory);
    }

    public bool RefreshProject(ProjectDocument document, string projectDirectory)
    {
        var changed = SynchronizeProjectFiles(document, projectDirectory);
        SynchronizeLibraries(document, projectDirectory);
        LoadProjectTree(document, projectDirectory);
        return changed;
    }

    public void SetDocumentSymbols(
        ProjectDocument document,
        string projectDirectory,
        IReadOnlyDictionary<string, IReadOnlyList<StrucppDocumentSymbol>> symbols)
    {
        if (!ReferenceEquals(_currentProject, document) ||
            !string.Equals(_projectDirectory, projectDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _documentSymbols.Clear();
        foreach (var (filePath, documentSymbols) in symbols)
            _documentSymbols[Path.GetFullPath(filePath)] = documentSymbols;
        LoadProjectTree(document, projectDirectory);
    }

    public void SetDocumentSymbols(
        string filePath,
        IReadOnlyList<StrucppDocumentSymbol> symbols)
    {
        if (_currentProject is null || _projectDirectory is null)
            return;

        _documentSymbols[Path.GetFullPath(filePath)] = symbols;
        LoadProjectTree(_currentProject, _projectDirectory);
    }

    private void LoadProjectTree(ProjectDocument document, string projectDirectory)
    {
        Nodes.Clear();
        for (var index = 0; index < document.Tree.Count; index++)
        {
            var definition = document.Tree[index];
            Nodes.Add(index == 0
                ? CreateProjectRootNode(definition, projectDirectory)
                : CreateTreeNode(definition, projectDirectory));
        }
    }

    public bool TryOpenNode(DeviceTreeNode node)
    {
        if (node.Location is { } location && _navigateToLocation is not null)
        {
            _navigateToLocation(location);
            return true;
        }

        if (node.LibraryFileName is not null && _openLibrary is not null)
        {
            _openLibrary(node.LibraryFileName);
            return true;
        }

        if (node.FilePath is null || _openDocument is null)
        {
            return false;
        }

        _openDocument(node.FilePath);
        return true;
    }

    public void AddPou(NewPouDefinition definition) =>
        (_addPou ?? throw new InvalidOperationException("The project is not available."))(definition);

    public void ImportCodesysLibrary(CodesysLibraryImport import) =>
        (_importCodesysLibrary ?? throw new InvalidOperationException("The project is not available."))(import);

    private static IReadOnlyList<DeviceTreeNode> CreateLibraryNodes() =>
        StrucppToolchain.GetBundledLibraries()
            .Select(library => new DeviceTreeNode(
                $"{library.DisplayName} ({Path.GetFileNameWithoutExtension(library.FileName)})",
                DeviceIcons.Library,
                libraryFileName: library.FileName))
            .ToList();

    private DeviceTreeNode CreateProjectRootNode(
        ProjectNodeDefinition definition,
        string projectDirectory)
    {
        var children = definition.Children
            .Where(child => !string.Equals(child.Name, "Build", StringComparison.Ordinal))
            .Select(child => CreateTreeNode(child, projectDirectory))
            .ToList();
        children.Add(CreateBuildNode(projectDirectory));

        return new DeviceTreeNode(
            definition.Name,
            DeviceIcons.Get(definition.Icon),
            children,
            definition.IsExpanded,
            definition.FilePath,
            definition.LibraryFileName);
    }

    private DeviceTreeNode CreateTreeNode(
        ProjectNodeDefinition definition,
        string projectDirectory)
    {
        var children = definition.Children
            .Select(child => CreateTreeNode(child, projectDirectory))
            .ToList();
        var symbolNodes = CreateDocumentSymbolNodes(definition, projectDirectory);
        children.AddRange(symbolNodes);

        return new DeviceTreeNode(
            definition.Name,
            DeviceIcons.Get(definition.Icon),
            children,
            symbolNodes.Count == 0 && definition.IsExpanded,
            definition.FilePath,
            definition.LibraryFileName);
    }

    private IReadOnlyList<DeviceTreeNode> CreateDocumentSymbolNodes(
        ProjectNodeDefinition definition,
        string projectDirectory)
    {
        if (definition.FilePath is not { } relativePath ||
            !IsStructuredTextPath(relativePath))
        {
            return [];
        }

        var fullPath = Path.GetFullPath(Path.Combine(
            projectDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!_documentSymbols.TryGetValue(fullPath, out var symbols))
            return [];

        var visibleSymbols = symbols.Count == 1 && IsDocumentContainerSymbol(symbols[0])
            ? symbols[0].Children
            : symbols;
        return visibleSymbols
            .Select(symbol => CreateDocumentSymbolNode(symbol, relativePath, fullPath))
            .ToList();
    }

    private static DeviceTreeNode CreateDocumentSymbolNode(
        StrucppDocumentSymbol symbol,
        string relativePath,
        string fullPath) =>
        new(
            FormatDocumentSymbol(symbol),
            GetDocumentSymbolIcon(symbol.Kind),
            symbol.Children
                .Select(child => CreateDocumentSymbolNode(child, relativePath, fullPath))
                .ToList(),
            false,
            filePath: relativePath,
            location: new StrucppLocation(fullPath, symbol.SelectionRange),
            isTransient: true);

    private static string FormatDocumentSymbol(StrucppDocumentSymbol symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol.Detail))
            return symbol.Name;

        var detail = symbol.Detail.Trim();
        return detail.StartsWith(':')
            ? $"{symbol.Name} {detail}"
            : $"{symbol.Name} ({detail})";
    }

    private static IImage GetDocumentSymbolIcon(int kind) => kind switch
    {
        2 or 3 or 4 => DeviceIcons.Folder,
        5 or 6 or 9 or 11 or 12 or 23 => DeviceIcons.Program,
        _ => DeviceIcons.Settings
    };

    private static bool IsDocumentContainerSymbol(StrucppDocumentSymbol symbol) =>
        symbol.Detail?.Trim().ToUpperInvariant() is
            "PROGRAM" or
            "FUNCTION_BLOCK" or
            "FUNCTION" or
            "INTERFACE";

    private static bool IsStructuredTextPath(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".st", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".iecst", StringComparison.OrdinalIgnoreCase);
    }

    private static DeviceTreeNode CreateBuildNode(string projectDirectory)
    {
        var buildDirectory = Path.Combine(projectDirectory, "Build");
        var children = Directory.Exists(buildDirectory)
            ? CreateBuildChildren(projectDirectory, buildDirectory)
            : [];
        return new DeviceTreeNode("Build", DeviceIcons.Build, children, false);
    }

    private static IReadOnlyList<DeviceTreeNode> CreateBuildChildren(
        string projectDirectory,
        string directory)
    {
        var directories = Directory.EnumerateDirectories(directory)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .Select(path => new DeviceTreeNode(
                Path.GetFileName(path),
                DeviceIcons.Folder,
                CreateBuildChildren(projectDirectory, path),
                false));
        var files = Directory.EnumerateFiles(directory)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                var extension = Path.GetExtension(path);
                var isCppDocument = extension.Equals(".cpp", StringComparison.OrdinalIgnoreCase) ||
                                    extension.Equals(".hpp", StringComparison.OrdinalIgnoreCase);
                var relativePath = Path.GetRelativePath(projectDirectory, path)
                    .Replace(Path.DirectorySeparatorChar, '/');
                return new DeviceTreeNode(
                    Path.GetFileName(path),
                    isCppDocument ? DeviceIcons.Program : DeviceIcons.Settings,
                    filePath: isCppDocument ? relativePath : null);
            });

        return directories.Concat(files).ToList();
    }

    private static void SynchronizeLibraries(ProjectDocument document, string projectDirectory)
    {
        var root = document.Tree.FirstOrDefault();
        var software = root?.Children.FirstOrDefault(node => node.Name == "Software");
        if (software is null)
            return;

        var application = software.Children.FirstOrDefault(node => node.Name == "Application");
        var oldLibraries = application?.Children.FirstOrDefault(node => node.Name == "Libraries");
        var libraries = software.Children.FirstOrDefault(node => node.Name == "Libraries")
                        ?? oldLibraries
                        ?? new ProjectNodeDefinition { Name = "Libraries", Icon = "library", IsExpanded = false };

        if (oldLibraries is not null)
            application!.Children.Remove(oldLibraries);
        if (!software.Children.Contains(libraries))
        {
            var applicationIndex = application is null ? -1 : software.Children.IndexOf(application);
            software.Children.Insert(applicationIndex + 1, libraries);
        }

        libraries.Icon = "library";
        libraries.Children = StrucppToolchain.GetProjectLibraries(projectDirectory)
            .Select(library => new ProjectNodeDefinition
            {
                Name = $"{library.DisplayName} ({Path.GetFileNameWithoutExtension(library.FileName)})",
                Icon = "library",
                IsExpanded = true,
                LibraryFileName = library.FileName
            })
            .ToList();
    }

    private static bool SynchronizeProjectFiles(
        ProjectDocument document,
        string projectDirectory)
    {
        var changed = RemoveMissingProjectFiles(document.Tree, projectDirectory);
        var referencedPaths = EnumerateDefinitions(document.Tree)
            .Select(node => node.FilePath)
            .Where(path => path is not null)
            .Cast<string>()
            .Select(NormalizeRelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var projectFilesDirectory = Path.Combine(projectDirectory, "ProjectFiles");
        if (!Directory.Exists(projectFilesDirectory))
            return changed;

        foreach (var filePath in Directory.EnumerateFiles(
                     projectFilesDirectory,
                     "*",
                     SearchOption.AllDirectories))
        {
            var relativePath = NormalizeRelativePath(Path.GetRelativePath(projectDirectory, filePath));
            if (referencedPaths.Contains(relativePath))
                continue;

            var category = FindCategoryForProjectFile(document, relativePath);
            if (category is null)
                continue;

            category.Children.Add(new ProjectNodeDefinition
            {
                Name = CreateProjectFileDisplayName(relativePath),
                Icon = GetProjectFileIcon(relativePath),
                FilePath = relativePath,
                IsExpanded = true
            });
            category.Children = category.Children
                .OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            referencedPaths.Add(relativePath);
            changed = true;
        }

        return changed;
    }

    private static bool RemoveMissingProjectFiles(
        List<ProjectNodeDefinition> nodes,
        string projectDirectory)
    {
        var changed = false;
        for (var index = nodes.Count - 1; index >= 0; index--)
        {
            var node = nodes[index];
            if (node.FilePath is { } relativePath &&
                NormalizeRelativePath(relativePath).StartsWith(
                    "ProjectFiles/",
                    StringComparison.OrdinalIgnoreCase) &&
                !File.Exists(Path.Combine(
                    projectDirectory,
                    relativePath.Replace('/', Path.DirectorySeparatorChar))))
            {
                nodes.RemoveAt(index);
                changed = true;
                continue;
            }

            changed |= RemoveMissingProjectFiles(node.Children, projectDirectory);
        }

        return changed;
    }

    private static ProjectNodeDefinition? FindCategoryForProjectFile(
        ProjectDocument document,
        string relativePath)
    {
        var root = document.Tree.FirstOrDefault();
        var software = FindChild(root, "Software");
        var application = FindChild(software, "Application");
        if (application is null)
            return null;

        var segments = relativePath.Split('/');
        if (segments.Length >= 4 && segments[1].Equals("POUs", StringComparison.OrdinalIgnoreCase))
        {
            var pous = FindChild(application, "POUs");
            var categoryName = segments[2].ToLowerInvariant() switch
            {
                "programs" => "Programs",
                "functionblocks" => "Function Blocks",
                "functions" => "Functions",
                "interfaces" => "Interfaces",
                _ => null
            };
            return categoryName is null ? pous : FindChild(pous, categoryName) ?? pous;
        }

        if (segments.Length >= 3 && segments[1].Equals("DataTypes", StringComparison.OrdinalIgnoreCase))
        {
            var dataTypes = FindChild(application, "Data Types");
            var categoryName = segments.Length >= 4
                ? segments[2].ToLowerInvariant() switch
                {
                    "structures" => "Structures",
                    "enumerations" => "Enumerations",
                    "aliases" or "aliasesandsubranges" => "Aliases and Subranges",
                    _ => null
                }
                : null;
            return categoryName is null ? dataTypes : FindChild(dataTypes, categoryName) ?? dataTypes;
        }

        if (segments.Length >= 3 &&
            segments[1].Equals("GlobalVariables", StringComparison.OrdinalIgnoreCase))
            return FindChild(application, "Global Variable Lists");

        if (segments.Length >= 3 && segments[1].Equals("Tests", StringComparison.OrdinalIgnoreCase))
            return FindChild(application, "Tests");

        return application;
    }

    private static ProjectNodeDefinition? FindChild(ProjectNodeDefinition? parent, string name) =>
        parent?.Children.FirstOrDefault(child =>
            string.Equals(child.Name, name, StringComparison.Ordinal));

    private static IEnumerable<ProjectNodeDefinition> EnumerateDefinitions(
        IEnumerable<ProjectNodeDefinition> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in EnumerateDefinitions(node.Children))
                yield return child;
        }
    }

    private static string CreateProjectFileDisplayName(string relativePath)
    {
        var name = Path.GetFileNameWithoutExtension(relativePath);
        var normalized = NormalizeRelativePath(relativePath);
        if (normalized.Contains("/POUs/Programs/", StringComparison.OrdinalIgnoreCase))
            return $"{name} (PROGRAM)";
        if (normalized.Contains("/POUs/FunctionBlocks/", StringComparison.OrdinalIgnoreCase))
            return $"{name} (FUNCTION_BLOCK)";
        if (normalized.Contains("/POUs/Functions/", StringComparison.OrdinalIgnoreCase))
            return $"{name} (FUNCTION)";
        if (normalized.Contains("/POUs/Interfaces/", StringComparison.OrdinalIgnoreCase))
            return $"{name} (INTERFACE)";
        return name;
    }

    private static string GetProjectFileIcon(string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        if (normalized.Contains("/GlobalVariables/", StringComparison.OrdinalIgnoreCase))
            return "globe";
        if (normalized.Contains("/Tests/", StringComparison.OrdinalIgnoreCase))
            return "task";
        if (normalized.Contains("/DataTypes/", StringComparison.OrdinalIgnoreCase))
            return "settings";
        return "program";
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace('\\', '/');

    private static DeviceTreeNode CreateController(string name, bool isExpanded) =>
        new(name, DeviceIcons.Controller,
        [
            new("Runtime", DeviceIcons.Settings,
            [
                new("Target: STruC++ C++17 Runtime", DeviceIcons.Program),
                new("Cycle and Watchdog Settings", DeviceIcons.Settings)
            ], false),
            new("Task Configuration", DeviceIcons.Task,
            [
                new("MainTask · cyclic 10 ms", DeviceIcons.Task,
                [
                    new("Main : Main", DeviceIcons.Program,
                        filePath: "ProjectFiles/POUs/Programs/Main.st")
                ]),
                new("BackgroundTask · cyclic 100 ms", DeviceIcons.Task,
                [
                    new("Diagnostics : Blink", DeviceIcons.Program,
                        filePath: "ProjectFiles/POUs/Programs/Blink.st")
                ], false)
            ]),
            new("Local I/O", DeviceIcons.Device,
            [
                new("Digital Input Module", DeviceIcons.Device,
                [
                    new("DI0 · StartButton · %IX0.0", DeviceIcons.Settings),
                    new("DI1 · StopButton · %IX0.1", DeviceIcons.Settings)
                ], false),
                new("Digital Output Module", DeviceIcons.Device,
                [
                    new("DO0 · MotorEnable · %QX0.0", DeviceIcons.Settings),
                    new("DO1 · RunLamp · %QX0.1", DeviceIcons.Settings)
                ], false),
                new("Analog I/O Module", DeviceIcons.Device, isExpanded: false)
            ], false),
            new("Network Interfaces", DeviceIcons.Network,
            [
                new("Ethernet · 192.168.0.10/24", DeviceIcons.Network)
            ], false),
            new("Fieldbuses", DeviceIcons.Network,
            [
                new("PROFIBUS-DP Master (CIF50-PB)", DeviceIcons.Network,
                [
                    new("DPSlave_1 (WAGO 750-333)", DeviceIcons.Device,
                    [
                        new("750-333 Bus Coupler", DeviceIcons.Device),
                        new("750-610 Digital I/O", DeviceIcons.Device)
                    ], false),
                    new("DPSlave_2 (WAGO 750-333)", DeviceIcons.Device)
                ], false)
            ], false)
        ], isExpanded);
}

public sealed class DeviceTreeNode(
    string name,
    IImage icon,
    IReadOnlyList<DeviceTreeNode>? children = null,
    bool isExpanded = true,
    string? filePath = null,
    string? libraryFileName = null,
    StrucppLocation? location = null,
    bool isTransient = false)
{
    public string Name { get; } = name;
    public IImage Icon { get; } = icon;
    public IReadOnlyList<DeviceTreeNode> Children { get; } = children ?? [];
    public bool IsExpanded { get; } = isExpanded;
    public string? FilePath { get; } = filePath;
    public string? LibraryFileName { get; } = libraryFileName;
    public StrucppLocation? Location { get; } = location;
    public bool IsTransient { get; } = isTransient;

    public ProjectNodeDefinition ToDefinition() => new()
    {
        Name = Name,
        Icon = DeviceIcons.GetName(Icon),
        IsExpanded = IsExpanded,
        FilePath = FilePath,
        LibraryFileName = LibraryFileName,
        Children = Children
            .Where(child => !child.IsTransient)
            .Select(child => child.ToDefinition())
            .ToList()
    };

    public static DeviceTreeNode FromDefinition(ProjectNodeDefinition definition) =>
        new(
            definition.Name,
            DeviceIcons.Get(definition.Icon),
            definition.Children.Select(FromDefinition).ToList(),
            definition.IsExpanded,
            definition.FilePath,
            definition.LibraryFileName);
}

internal static class DeviceIcons
{
    public static IImage Application { get; } = Load("application.png");
    public static IImage Build { get; } = Load("build16.png");
    public static IImage Controller { get; } = Load("controller.png");
    public static IImage Device { get; } = Load("device.png");
    public static IImage Display { get; } = Load("display.png");
    public static IImage Folder { get; } = Load("folder.png");
    public static IImage Globe { get; } = Load("globe.png");
    public static IImage Library { get; } = Load("library.png");
    public static IImage Network { get; } = Load("network.png");
    public static IImage Program { get; } = Load("program.png");
    public static IImage Settings { get; } = Load("settings.png");
    public static IImage Software { get; } = Load("software.png");
    public static IImage Pous { get; } = Load("pous.png");
    public static IImage DataTypes { get; } = Load("data-types.png");
    public static IImage Task { get; } = Load("task.png");

    public static IImage Get(string name) => name switch
    {
        "application" => Application,
        "build" => Build,
        "controller" => Controller,
        "device" => Device,
        "display" => Display,
        "globe" => Globe,
        "library" => Library,
        "network" => Network,
        "program" => Program,
        "settings" => Settings,
        "software" => Software,
        "pous" => Pous,
        "data-types" => DataTypes,
        "task" => Task,
        _ => Folder
    };

    public static string GetName(IImage icon)
    {
        if (ReferenceEquals(icon, Application)) return "application";
        if (ReferenceEquals(icon, Build)) return "build";
        if (ReferenceEquals(icon, Controller)) return "controller";
        if (ReferenceEquals(icon, Device)) return "device";
        if (ReferenceEquals(icon, Display)) return "display";
        if (ReferenceEquals(icon, Globe)) return "globe";
        if (ReferenceEquals(icon, Library)) return "library";
        if (ReferenceEquals(icon, Network)) return "network";
        if (ReferenceEquals(icon, Program)) return "program";
        if (ReferenceEquals(icon, Settings)) return "settings";
        if (ReferenceEquals(icon, Software)) return "software";
        if (ReferenceEquals(icon, Pous)) return "pous";
        if (ReferenceEquals(icon, DataTypes)) return "data-types";
        if (ReferenceEquals(icon, Task)) return "task";
        return "folder";
    }

    private static Bitmap Load(string fileName)
    {
        var uri = new Uri($"avares://RetroPLC.Shell/Assets/Icons/Chicago95/{fileName}");
        return new Bitmap(AssetLoader.Open(uri));
    }
}

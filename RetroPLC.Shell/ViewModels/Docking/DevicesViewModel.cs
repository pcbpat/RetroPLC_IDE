// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using RetroPLC.LanguageServerHost;
using RetroPLC.Icons;
using Dock.Model.Mvvm.Controls;
using RetroPLC.Shell.Models;

namespace RetroPLC.Shell.ViewModels.Docking;

public sealed class DevicesViewModel : Tool
{
    private readonly Action<string>? _openDocument;
    private readonly Action<StrucppLocation>? _navigateToLocation;
    private readonly Action<NewPouDefinition>? _addPou;
    private readonly Action<NewDataTypeDefinition>? _addDataType;
    private readonly Action<string>? _openLibrary;
    private readonly Action<CodesysLibraryImport>? _importCodesysLibrary;
    private readonly Action<string>? _addConfiguration;
    private readonly Action<NewResourceDefinition, string>? _addResource;
    private readonly Action<NewTaskDefinition, string, string>? _addTask;
    private readonly Func<StrucppLocation, CancellationToken,
        Task<StrucppPrepareRenameResult?>>? _prepareRename;
    private readonly Func<StrucppLocation, string, CancellationToken, Task<int>>? _rename;
    private readonly Dictionary<string, IReadOnlyList<StrucppDocumentSymbol>> _documentSymbols =
        new(StringComparer.OrdinalIgnoreCase);
    private ProjectDocument? _currentProject;
    private string? _projectDirectory;
    private DeviceTreeNode? _buildOutputNode;

    public DevicesViewModel(
        Action<string>? openDocument = null,
        Action<NewPouDefinition>? addPou = null,
        Action<NewDataTypeDefinition>? addDataType = null,
        Action<string>? openLibrary = null,
        Action<CodesysLibraryImport>? importCodesysLibrary = null,
        Action<StrucppLocation>? navigateToLocation = null,
        Action<string>? addConfiguration = null,
        Action<NewResourceDefinition, string>? addResource = null,
        Action<NewTaskDefinition, string, string>? addTask = null,
        Func<StrucppLocation, CancellationToken,
            Task<StrucppPrepareRenameResult?>>? prepareRename = null,
        Func<StrucppLocation, string, CancellationToken, Task<int>>? rename = null)
    {
        _openDocument = openDocument;
        _navigateToLocation = navigateToLocation;
        _addPou = addPou;
        _addDataType = addDataType;
        _openLibrary = openLibrary;
        _importCodesysLibrary = importCodesysLibrary;
        _addConfiguration = addConfiguration;
        _addResource = addResource;
        _addTask = addTask;
        _prepareRename = prepareRename;
        _rename = rename;
        Nodes = [];
    }

    public ObservableCollection<DeviceTreeNode> Nodes { get; }

    public static IReadOnlyList<DeviceTreeNode> CreateDefaultNodes(string projectName) =>
    [
        new(projectName, DeviceIcons.Application,
        [
            new("Data Types", DeviceIcons.DataTypes, [], false),
            new("POUs", DeviceIcons.Pous,
            [
                new("Programs", DeviceIcons.Folder,
                [
                    new("Main", DeviceIcons.Program,
                        filePath: "ProjectFiles/POUs/Programs/Main.st"),
                    new("Blink", DeviceIcons.Program,
                        filePath: "ProjectFiles/POUs/Programs/Blink.st")
                ], false),
                new("Function Blocks", DeviceIcons.Folder,
                [
                    new("MotorController", DeviceIcons.Program,
                        filePath: "ProjectFiles/POUs/FunctionBlocks/MotorController.st")
                ], false),
                new("Functions", DeviceIcons.Folder,
                [
                    new("Scale", DeviceIcons.Program,
                        filePath: "ProjectFiles/POUs/Functions/Scale.st")
                ], false)
            ]),
            new("Interfaces", DeviceIcons.Folder,
            [
                new("IMotor", DeviceIcons.Program,
                    filePath: "ProjectFiles/Interfaces/IMotor.st"),
                new("IController", DeviceIcons.Program,
                    filePath: "ProjectFiles/Interfaces/IController.st")
            ], false),
            new("Configurations", DeviceIcons.Controller, [], true),
            new("Libraries", DeviceIcons.Library,
                CreateLibraryNodes(), false),
            new("Tests", DeviceIcons.Task, [], false)
        ])
    ];

    public void LoadProject(ProjectDocument document, string projectDirectory)
    {
        var isSameProject = string.Equals(
            _projectDirectory,
            projectDirectory,
            StringComparison.OrdinalIgnoreCase);
        if (!isSameProject)
            _documentSymbols.Clear();
        _currentProject = document;
        _projectDirectory = projectDirectory;
        _buildOutputNode = CreateBuildOutputNode(projectDirectory);
        MigrateProjectTree(document);
        SynchronizeProjectFiles(document, projectDirectory);
        SynchronizeLibraries(document, projectDirectory);
        LoadProjectTree(document, projectDirectory, isSameProject);
    }

    /// <summary>
    /// Canonical top-level section order for the flattened project tree.
    /// </summary>
    private static readonly string[] TopLevelSectionOrder =
    [
        "Data Types",
        "POUs",
        "Interfaces",
        "Configurations",
        "Libraries",
        "Tests"
    ];

    /// <summary>
    /// Restructures project trees saved by earlier previews: the Software /
    /// Application / Hardware wrappers are flattened away, Configuration
    /// entries are moved to the top level, and RESOURCE / TASK children of
    /// Configuration nodes are discarded because they are now parsed from the
    /// CONFIGURATION source file itself.
    /// </summary>
    internal static void MigrateProjectTree(ProjectDocument document)
    {
        var root = document.Tree.FirstOrDefault();
        if (root is null)
            return;

        var software = root.Children.FirstOrDefault(node => node.Name == "Software");
        var hardware = root.Children.FirstOrDefault(node => node.Name == "Hardware");

        if (software is null && hardware is null)
        {
            PruneLegacyConfigurationEntries(root.Children);
            CleanConfigurationNodes(root.Children);
            LiftInterfacesToTopLevel(root);
            DropObsoleteSections(root.Children);
            ReorderTopLevelSections(root);
            return;
        }

        var moved = new List<ProjectNodeDefinition>();

        if (software is not null)
        {
            var application = software.Children.FirstOrDefault(node => node.Name == "Application");
            if (application is not null)
            {
                moved.AddRange(TakeNamed(
                    application.Children,
                    ["POUs", "Data Types", "Tests"]));
                moved.AddRange(application.Children);
            }

            moved.AddRange(TakeNamed(
                software.Children,
                ["Libraries", "Build and Deployment", "Project Documentation"]));
            root.Children.Remove(software);
        }

        if (hardware is not null)
        {
            var configurations = hardware.Children.FirstOrDefault(node =>
                string.Equals(node.Name, "Configurations", StringComparison.Ordinal));
            if (configurations is not null)
                hardware.Children.Remove(configurations);
            root.Children.Remove(hardware);
            if (configurations is not null)
                moved.Add(configurations);
        }

        foreach (var sectionName in TopLevelSectionOrder)
        {
            var section = moved.FirstOrDefault(node => node.Name == sectionName);
            if (section is not null &&
                root.Children.All(node => node.Name != sectionName))
            {
                root.Children.Add(section);
            }
        }

        foreach (var section in moved)
        {
            if (!root.Children.Contains(section))
                root.Children.Add(section);
        }

        PruneLegacyConfigurationEntries(root.Children);
        CleanConfigurationNodes(root.Children);
        LiftInterfacesToTopLevel(root);
        DropObsoleteSections(root.Children);
        ReorderTopLevelSections(root);
    }

    /// <summary>
    /// Places top-level sections in the canonical order, keeping any unknown
    /// sections (or extra top-level nodes) after the known ones.
    /// </summary>
    private static void ReorderTopLevelSections(ProjectNodeDefinition root)
    {
        var ordered = new List<ProjectNodeDefinition>();
        foreach (var sectionName in TopLevelSectionOrder)
        {
            var section = root.Children.FirstOrDefault(node => node.Name == sectionName);
            if (section is null)
                continue;
            ordered.Add(section);
            root.Children.Remove(section);
        }

        ordered.AddRange(root.Children);
        root.Children.Clear();
        root.Children.AddRange(ordered);
    }

    /// <summary>
    /// Interfaces used to be a folder under POUs; it is now a top-level
    /// section placed after POUs.
    /// </summary>
    private static void LiftInterfacesToTopLevel(ProjectNodeDefinition root)
    {
        var pous = root.Children.FirstOrDefault(node => node.Name == "POUs");
        var interfaces = pous?.Children.FirstOrDefault(node => node.Name == "Interfaces");
        if (interfaces is null)
            return;

        pous!.Children.Remove(interfaces);
        var existing = root.Children.FirstOrDefault(node => node.Name == "Interfaces");
        if (existing is not null)
        {
            existing.Children.AddRange(interfaces.Children);
            return;
        }

        var index = root.Children.FindIndex(node => node.Name == "POUs") + 1;
        root.Children.Insert(index, interfaces);
    }

    /// <summary>
    /// Sections that are no longer part of the project-tree design. Standalone
    /// GVLs are a vendor extension; IEC globals belong to a configuration or
    /// resource.
    /// </summary>
    private static void DropObsoleteSections(List<ProjectNodeDefinition> nodes)
    {
        nodes.RemoveAll(node => node.Name is
            "Build and Deployment" or
            "Project Documentation" or
            "Global Variable Lists");
    }

    /// <summary>
    /// Drops display-only CONFIGURATION / RESOURCE / TASK entries saved by
    /// earlier previews (they carry no source file) and normalizes the names
    /// of real configuration nodes to the bare IEC identifier.
    /// </summary>
    private static void PruneLegacyConfigurationEntries(IEnumerable<ProjectNodeDefinition> nodes)
    {
        foreach (var node in EnumerateDefinitions(nodes))
        {
            if (!IsConfigurationPath(node.FilePath))
                continue;
            node.Name = ProjectNodeKinds.GetElementName(node.Name);
        }

        var configurations = EnumerateDefinitions(nodes)
            .FirstOrDefault(node => node.Name == "Configurations");
        if (configurations is null)
            return;

        configurations.Children = configurations.Children
            .Where(child => child.FilePath is not null)
            .ToList();
        foreach (var child in configurations.Children)
        {
            child.Kind = ProjectNodeKinds.Configuration;
            child.Icon = "controller";
        }
    }

    private static IReadOnlyList<ProjectNodeDefinition> TakeNamed(
        IList<ProjectNodeDefinition> source,
        IEnumerable<string> names)
    {
        var taken = new List<ProjectNodeDefinition>();
        foreach (var name in names)
        {
            var node = source.FirstOrDefault(candidate => candidate.Name == name);
            if (node is null)
                continue;
            source.Remove(node);
            taken.Add(node);
        }

        return taken;
    }

    private static void CleanConfigurationNodes(IEnumerable<ProjectNodeDefinition> nodes)
    {
        foreach (var node in EnumerateDefinitions(nodes))
        {
            if (!IsConfigurationPath(node.FilePath))
                continue;
            node.Icon = "controller";
            // RESOURCE / TASK / PROGRAM-instance / VAR_GLOBAL structure is
            // parsed from the CONFIGURATION source file, so manifest children
            // saved by earlier previews are stale and discarded.
            node.Children.Clear();
        }
    }

    public bool RefreshProject(ProjectDocument document, string projectDirectory)
    {
        var changed = SynchronizeProjectFiles(document, projectDirectory);
        SynchronizeLibraries(document, projectDirectory);
        LoadProjectTree(document, projectDirectory);
        return changed;
    }

    public void RefreshBuildOutputs()
    {
        if (_currentProject is null || _projectDirectory is null)
            return;

        _buildOutputNode = CreateBuildOutputNode(_projectDirectory);
        LoadProjectTree(_currentProject, _projectDirectory);
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

    private void LoadProjectTree(
        ProjectDocument document,
        string projectDirectory,
        bool preserveExpansion = true)
    {
        var expansionState = preserveExpansion
            ? CaptureExpansionState(Nodes)
            : [];
        var newNodes = new List<DeviceTreeNode>();

        for (var index = 0; index < document.Tree.Count; index++)
        {
            var definition = document.Tree[index];
            if (index == 0)
            {
                newNodes.Add(CreateProjectRootNode(definition, projectDirectory));
                continue;
            }

            foreach (var node in CreateTreeNodes(definition, projectDirectory))
                newNodes.Add(node);
        }

        RestoreExpansionState(newNodes, expansionState);
        Nodes.Clear();
        foreach (var node in newNodes)
            Nodes.Add(node);
    }

    private static Dictionary<string, bool> CaptureExpansionState(
        IEnumerable<DeviceTreeNode> nodes)
    {
        var state = new Dictionary<string, bool>(StringComparer.Ordinal);
        VisitNodes(nodes, "", (key, node) => state[key] = node.IsExpanded);
        return state;
    }

    private static void RestoreExpansionState(
        IEnumerable<DeviceTreeNode> nodes,
        IReadOnlyDictionary<string, bool> state) =>
        VisitNodes(nodes, "", (key, node) =>
        {
            if (state.TryGetValue(key, out var isExpanded))
                node.IsExpanded = isExpanded;
        });

    private static void VisitNodes(
        IEnumerable<DeviceTreeNode> nodes,
        string parentKey,
        Action<string, DeviceTreeNode> visit)
    {
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            var identity = $"{node.Name}\u001f{node.FilePath}\u001f{node.LibraryFileName}";
            occurrences.TryGetValue(identity, out var occurrence);
            occurrences[identity] = occurrence + 1;
            var key = $"{parentKey}/{identity}\u001f{occurrence}";
            visit(key, node);
            VisitNodes(node.Children, key, visit);
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

    public void AddDataType(NewDataTypeDefinition definition) =>
        (_addDataType ?? throw new InvalidOperationException("The project is not available."))(definition);

    public void AddConfiguration(string name) =>
        (_addConfiguration ?? throw new InvalidOperationException("The project is not available."))(name);

    public void AddResource(NewResourceDefinition definition, DeviceTreeNode configurationNode) =>
        (_addResource ?? throw new InvalidOperationException("The project is not available."))(
            definition,
            configurationNode.FilePath
            ?? throw new InvalidOperationException("The configuration has no source file."));

    public void AddTask(NewTaskDefinition definition, DeviceTreeNode resourceNode) =>
        (_addTask ?? throw new InvalidOperationException("The project is not available."))(
            definition,
            resourceNode.FilePath
            ?? throw new InvalidOperationException("The resource has no source file."),
            ProjectNodeKinds.GetElementName(resourceNode.Name));

    public void ImportCodesysLibrary(CodesysLibraryImport import) =>
        (_importCodesysLibrary ?? throw new InvalidOperationException("The project is not available."))(import);

    public Task<StrucppPrepareRenameResult?> PrepareRenameAsync(
        DeviceTreeNode node,
        CancellationToken cancellationToken = default)
    {
        if (!node.SupportsLanguageServerRename || node.Location is not { } location)
            return Task.FromResult<StrucppPrepareRenameResult?>(null);

        return (_prepareRename ?? throw new InvalidOperationException(
            "The STruC++ language server is not available."))(
            location,
            cancellationToken);
    }

    public Task<int> RenameAsync(
        DeviceTreeNode node,
        string newName,
        CancellationToken cancellationToken = default)
    {
        if (!node.SupportsLanguageServerRename || node.Location is not { } location)
            return Task.FromResult(0);

        return (_rename ?? throw new InvalidOperationException(
            "The STruC++ language server is not available."))(
            location,
            newName,
            cancellationToken);
    }

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
            .SelectMany(child => CreateTreeNodes(child, projectDirectory))
            .ToList();

        if (_buildOutputNode is { } buildOutputNode)
        {
            var testsIndex = children.FindIndex(child =>
                string.Equals(child.Name, "Tests", StringComparison.Ordinal));
            children.Insert(testsIndex < 0 ? children.Count : testsIndex + 1, buildOutputNode);
        }

        return new DeviceTreeNode(
            GetProjectTreeNodeDisplayName(definition),
            DeviceIcons.Get(definition.Icon),
            children,
            definition.IsExpanded,
            definition.FilePath,
            definition.LibraryFileName,
            kind: definition.Kind);
    }

    private static DeviceTreeNode CreateBuildOutputNode(string projectDirectory)
    {
        var buildDirectory = Path.Combine(projectDirectory, "Build");
        var children = Directory.Exists(buildDirectory)
            ? CreateBuildFileSystemNodes(buildDirectory, projectDirectory)
            : [];
        return new DeviceTreeNode(
            "Build",
            DeviceIcons.Folder,
            children,
            false,
            isTransient: true);
    }

    private static IReadOnlyList<DeviceTreeNode> CreateBuildFileSystemNodes(
        string directory,
        string projectDirectory)
    {
        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(directory);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return entries
            .OrderBy(path => !Directory.Exists(path))
            .ThenBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .Select(path => CreateBuildFileSystemNode(path, projectDirectory))
            .ToList();
    }

    private static DeviceTreeNode CreateBuildFileSystemNode(
        string path,
        string projectDirectory)
    {
        if (Directory.Exists(path))
        {
            var isReparsePoint = (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            return new DeviceTreeNode(
                Path.GetFileName(path),
                DeviceIcons.Folder,
                isReparsePoint ? [] : CreateBuildFileSystemNodes(path, projectDirectory),
                false,
                isTransient: true);
        }

        var extension = Path.GetExtension(path);
        var canOpen = IsBuildTextFile(extension);
        return new DeviceTreeNode(
            Path.GetFileName(path),
            GetBuildFileIcon(extension),
            [],
            false,
            filePath: canOpen
                ? NormalizeRelativePath(Path.GetRelativePath(projectDirectory, path))
                : null,
            isTransient: true);
    }

    private static bool IsBuildTextFile(string extension) =>
        extension.Equals(".c", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".cpp", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".h", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".hpp", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".map", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".conf", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".config", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".dts", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".yml", StringComparison.OrdinalIgnoreCase);

    private static IImage GetBuildFileIcon(string extension) => extension.ToLowerInvariant() switch
    {
        ".c" or ".cpp" or ".h" or ".hpp" => DeviceIcons.Program,
        ".elf" or ".hex" or ".bin" or ".uf2" => DeviceIcons.Binary,
        _ => DeviceIcons.Settings
    };

    private DeviceTreeNode CreateTreeNode(
        ProjectNodeDefinition definition,
        string projectDirectory)
    {
        var children = definition.Children
            .SelectMany(child => CreateTreeNodes(child, projectDirectory))
            .ToList();

        return new DeviceTreeNode(
            GetProjectTreeNodeDisplayName(definition),
            DeviceIcons.Get(definition.Icon),
            children,
            definition.IsExpanded,
            definition.FilePath,
            definition.LibraryFileName,
            kind: definition.Kind);
    }

    private IReadOnlyList<DeviceTreeNode> CreateTreeNodes(
        ProjectNodeDefinition definition,
        string projectDirectory)
    {
        var symbolNodes = CreateDocumentSymbolNodes(definition, projectDirectory);
        return symbolNodes.Count > 0
            ? symbolNodes
            : [CreateTreeNode(definition, projectDirectory)];
    }

    private static bool IsConfigurationPath(string? filePath)
    {
        if (filePath is null)
            return false;
        var normalized = NormalizeRelativePath(filePath);
        return normalized.StartsWith("ProjectFiles/Configurations/", StringComparison.OrdinalIgnoreCase);
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

        return symbols
            .Select(symbol => CreateDocumentSymbolNode(
                IsPouLikePath(relativePath) ? symbol with { Detail = null } : symbol,
                relativePath,
                fullPath))
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
            isTransient: true,
            supportsLanguageServerRename: IsLanguageServerRenameCandidate(symbol));

    /// <summary>
    /// STruC++ currently provides complete prepareRename/reference coverage
    /// for variable declarations. Other document-symbol kinds (including
    /// interfaces, type declarations and enum members) are published for
    /// navigation but are not complete rename targets, so the project tree
    /// must not advertise Rename for them.
    /// </summary>
    private static bool IsLanguageServerRenameCandidate(StrucppDocumentSymbol symbol) =>
        // LSP SymbolKind.Property is also used for VAR_INPUT/VAR_OUTPUT/
        // VAR_IN_OUT. prepareRename remains the final authority because the
        // same kind can represent an unsupported IEC PROPERTY declaration.
        symbol.Kind is 7 or 13;

    /// <summary>
    /// POUs and Interfaces render as bare identifiers (the declaration kind
    /// is obvious from the tree folder), so their document-symbol detail is
    /// suppressed.
    /// </summary>
    private static bool IsPouLikePath(string path)
    {
        var normalized = NormalizeRelativePath(path);
        return normalized.Contains("/POUs/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/Interfaces/", StringComparison.OrdinalIgnoreCase);
    }

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

    private static bool IsStructuredTextPath(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".st", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".iecst", StringComparison.OrdinalIgnoreCase);
    }

    private static void SynchronizeLibraries(ProjectDocument document, string projectDirectory)
    {
        var root = document.Tree.FirstOrDefault();
        if (root is null)
            return;

        var libraries = root.Children.FirstOrDefault(node => node.Name == "Libraries")
                        ?? new ProjectNodeDefinition { Name = "Libraries", Icon = "library", IsExpanded = false };
        if (!root.Children.Contains(libraries))
        {
            root.Children.Add(libraries);
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
                Kind = IsConfigurationPath(relativePath)
                    ? ProjectNodeKinds.Configuration
                    : null,
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
        if (root is null)
            return null;

        var segments = relativePath.Split('/');
        if (segments.Length >= 4 && segments[1].Equals("POUs", StringComparison.OrdinalIgnoreCase))
        {
            if (segments[2].Equals("Interfaces", StringComparison.OrdinalIgnoreCase))
                return FindChild(root, "Interfaces") ?? FindChild(root, "POUs");

            var pous = FindChild(root, "POUs");
            var categoryName = segments[2].ToLowerInvariant() switch
            {
                "programs" => "Programs",
                "functionblocks" => "Function Blocks",
                "functions" => "Functions",
                _ => null
            };
            return categoryName is null ? pous : FindChild(pous, categoryName) ?? pous;
        }

        if (segments.Length >= 3 && segments[1].Equals("Interfaces", StringComparison.OrdinalIgnoreCase))
            return FindChild(root, "Interfaces") ?? FindChild(root, "POUs");

        if (segments.Length >= 3 && segments[1].Equals("DataTypes", StringComparison.OrdinalIgnoreCase))
        {
            var dataTypes = FindChild(root, "Data Types");
            var categoryName = segments.Length >= 4
                ? segments[2].ToLowerInvariant() switch
                {
                    "structures" => "Structures",
                    "enumerations" => "Enumerations",
                    "aliases" or "aliasesandsubranges" => "Aliases and Subranges",
                    "arrays" => "Arrays",
                    _ => null
                }
                : null;
            return categoryName is null ? dataTypes : FindChild(dataTypes, categoryName) ?? dataTypes;
        }

        if (segments.Length >= 3 && segments[1].Equals("Tests", StringComparison.OrdinalIgnoreCase))
            return FindChild(root, "Tests");

        if (segments.Length >= 3 &&
            segments[1].Equals("Configurations", StringComparison.OrdinalIgnoreCase))
            return FindChild(root, "Configurations");

        return null;
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
        => Path.GetFileNameWithoutExtension(relativePath);

    private static string GetProjectTreeNodeDisplayName(ProjectNodeDefinition definition) =>
        definition.Kind is null &&
        definition.FilePath is { } filePath &&
        IsStructuredTextPath(filePath)
            ? Path.GetFileNameWithoutExtension(filePath)
            : definition.Name;

    private static string GetProjectFileIcon(string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        if (normalized.Contains("/Tests/", StringComparison.OrdinalIgnoreCase))
            return "task";
        if (normalized.Contains("/DataTypes/", StringComparison.OrdinalIgnoreCase))
            return "settings";
        if (normalized.Contains("/Configurations/", StringComparison.OrdinalIgnoreCase))
            return "controller";
        return "program";
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace('\\', '/');
}

public sealed class DeviceTreeNode(
    string name,
    IImage icon,
    IReadOnlyList<DeviceTreeNode>? children = null,
    bool isExpanded = true,
    string? filePath = null,
    string? libraryFileName = null,
    StrucppLocation? location = null,
    bool isTransient = false,
    string? kind = null,
    bool supportsLanguageServerRename = false)
{
    public string Name { get; } = name;
    public IImage Icon { get; } = icon;
    public IReadOnlyList<DeviceTreeNode> Children { get; } = children ?? [];
    public bool IsExpanded { get; set; } = isExpanded;
    public string? FilePath { get; } = filePath;
    public string? LibraryFileName { get; } = libraryFileName;
    public StrucppLocation? Location { get; } = location;
    public bool IsTransient { get; } = isTransient;
    public string? Kind { get; } = kind;
    public bool SupportsLanguageServerRename { get; } = supportsLanguageServerRename;

    public ProjectNodeDefinition ToDefinition() => new()
    {
        Name = Name,
        Icon = DeviceIcons.GetName(Icon),
        IsExpanded = IsExpanded,
        FilePath = FilePath,
        LibraryFileName = LibraryFileName,
        Kind = Kind,
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
            definition.LibraryFileName,
            kind: definition.Kind);
}

internal static class DeviceIcons
{
    public static IImage Application { get; } = Se98Icons.Apps.Size16.Codeblocks;
    public static IImage Controller { get; } = Se98Icons.Devices.Size16.Computer;
    public static IImage Device { get; } = Se98Icons.Devices.Size16.DriveHarddisk;
    public static IImage Display { get; } = Se98Icons.Devices.Size16.VideoDisplay;
    public static IImage Folder { get; } = Se98Icons.Places.Size16.Folder;
    public static IImage Globe { get; } = Se98Icons.Places.Size16.NetworkWorkgroup;
    public static IImage Library { get; } = Se98Icons.Mimes.Size16.PackageXGeneric;
    public static IImage Network { get; } = Se98Icons.Devices.Size16.NetworkWired;
    public static IImage Program { get; } = Se98Icons.Mimes.Size16.TextXScript;
    public static IImage Settings { get; } = Se98Icons.Apps.Size16.PreferencesSystem;
    public static IImage Pous { get; } = Se98Icons.Places.Size16.FolderDocuments;
    public static IImage DataTypes { get; } = Se98Icons.Mimes.Size16.TextXGeneric;
    public static IImage Task { get; } = Se98Icons.Actions.Size16.Appointment;
    public static IImage Binary { get; } = Se98Icons.Mimes.Size16.ApplicationOctetStream;

    public static IImage Get(string name) => name switch
    {
        "application" => Application,
        "controller" => Controller,
        "device" => Device,
        "display" => Display,
        "globe" => Globe,
        "library" => Library,
        "network" => Network,
        "program" => Program,
        "settings" => Settings,
        "pous" => Pous,
        "data-types" => DataTypes,
        "task" => Task,
        _ => Folder
    };

    public static string GetName(IImage icon)
    {
        if (ReferenceEquals(icon, Application)) return "application";
        if (ReferenceEquals(icon, Controller)) return "controller";
        if (ReferenceEquals(icon, Device)) return "device";
        if (ReferenceEquals(icon, Display)) return "display";
        if (ReferenceEquals(icon, Globe)) return "globe";
        if (ReferenceEquals(icon, Library)) return "library";
        if (ReferenceEquals(icon, Network)) return "network";
        if (ReferenceEquals(icon, Program)) return "program";
        if (ReferenceEquals(icon, Settings)) return "settings";
        if (ReferenceEquals(icon, Pous)) return "pous";
        if (ReferenceEquals(icon, DataTypes)) return "data-types";
        if (ReferenceEquals(icon, Task)) return "task";
        return "folder";
    }
}

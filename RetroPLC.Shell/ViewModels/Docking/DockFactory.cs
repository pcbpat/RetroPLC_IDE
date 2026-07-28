using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using Dock.Settings;
using RetroPLC.LanguageServerHost;
using RetroPLC.Shell.Language;
using RetroPLC.Shell.Models;

namespace RetroPLC.Shell.ViewModels.Docking;

public sealed class DockFactory : Factory
{
    private const double DefaultFloatingWidth = 420;
    private const double DefaultFloatingHeight = 320;
    private const double MaximumFloatingWidth = 640;
    private const double MaximumFloatingHeight = 480;

    private readonly MainWindowViewModel _settings;
    private IRootDock? _rootDock;
    private IDocumentDock? _documentDock;
    private IToolDock? _terminalDock;
    private IToolDock? _appearanceDock;
    private BuildViewModel? _buildTool;
    private ReferencesViewModel? _referencesTool;
    private MessagesViewModel? _messagesTool;
    private DevicesViewModel? _projectTool;
    private AppearanceViewModel? _appearanceTool;
    private ProjectDocument? _currentProject;
    private string? _projectDirectory;
    private int _terminalNumber;
    private readonly object _projectWatcherLock = new();
    private FileSystemWatcher? _projectWatcher;
    private Timer? _projectRefreshTimer;
    private readonly IStrucppLanguageService _languageClient = new StrucppLanguageService();
    private readonly SemaphoreSlim _languageServerLifecycle = new(1, 1);
    private readonly Dictionary<string, IReadOnlyList<StrucppDiagnostic>> _diagnostics =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _languageServerDocuments =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<StrucppDocumentSymbol>> _projectSymbols =
        new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _languageServerCancellation;

    public DockFactory(MainWindowViewModel settings)
    {
        _settings = settings;
        _languageClient.DiagnosticsPublished += OnDiagnosticsPublished;
        _languageClient.ServerError += (_, args) =>
            System.Diagnostics.Debug.WriteLine(args.Message);
    }

    public override IRootDock CreateLayout()
    {
        var tool1 = new DevicesViewModel(
                OpenDocument,
                AddPou,
                OpenLibrary,
                ImportCodesysLibrary,
                NavigateToLocation)
            { Id = "Project", Title = "Project", CanPin = true };
        _projectTool = tool1;
        if (_currentProject is not null && _projectDirectory is not null)
        {
            tool1.LoadProject(_currentProject, _projectDirectory);
            tool1.SetDocumentSymbols(_currentProject, _projectDirectory, _projectSymbols);
        }
        var tool2 = new ToolViewModel { Id = "Tool2", Title = "Tool2", CanPin = true };
        var tool3 = new ToolViewModel { Id = "Tool3", Title = "Tool3", CanPin = true };
        var tool4 = new ToolViewModel { Id = "Tool4", Title = "Tool4", CanPin = true };
        var tool5 = new ToolViewModel { Id = "Tool5", Title = "Tool5", CanPin = true };
        var tool6 = new BuildViewModel
        {
            Id = "Build",
            Title = "Build",
            CanPin = true,
            CanClose = true
        };
        tool6.BuildExited += OnBuildExited;
        _buildTool = tool6;
        var messagesTool = new MessagesViewModel(NavigateToLocation)
        {
            Id = "Messages",
            Title = "Messages",
            CanPin = true,
            CanClose = false
        };
        _messagesTool = messagesTool;
        if (_currentProject is not null && _projectDirectory is not null)
            messagesTool.SetProject(_currentProject.Name, _projectDirectory);
        var referencesTool = new ReferencesViewModel(NavigateToLocation)
        {
            Id = "References",
            Title = "References",
            CanPin = true,
            CanClose = true
        };
        _referencesTool = referencesTool;
        var tool7 = new ToolViewModel { Id = "Tool7", Title = "Tool7", CanPin = true };
        var tool8 = new AppearanceViewModel(_settings)
        {
            Id = "Appearance",
            Title = "Appearance",
            CanPin = true,
            CanClose = true
        };
        _appearanceTool = tool8;

        var leftDock = new ToolDock
        {
            Proportion = 0.22,
            Alignment = Alignment.Left,
            ActiveDockable = tool1,
            VisibleDockables = CreateList<IDockable>(tool1, tool3, tool4, tool2)
        };

        var documents = new DocumentDock
        {
            Id = "Documents",
            IsCollapsable = false,
            VisibleDockables = CreateList<IDockable>(),
            CanCreateDocument = true
        };

        var bottomDock = new ToolDock
        {
            Proportion = 0.27,
            Alignment = Alignment.Bottom,
            CanCloseLastDockable = true,
            ActiveDockable = tool6,
            VisibleDockables = CreateList<IDockable>(tool6, messagesTool)
        };

        var centerDock = new ProportionalDock
        {
            Proportion = 0.54,
            Orientation = Dock.Model.Core.Orientation.Vertical,
            VisibleDockables = CreateList<IDockable>(
                documents,
                new ProportionalDockSplitter(),
                bottomDock)
        };

        var appearanceDock = new ToolDock
        {
            ActiveDockable = tool8,
            Alignment = Alignment.Right,
            VisibleDockables = CreateList<IDockable>(tool8, tool5)
        };
        _appearanceDock = appearanceDock;

        var rightDock = new ProportionalDock
        {
            Proportion = 0.25,
            Orientation = Dock.Model.Core.Orientation.Vertical,
            VisibleDockables = CreateList<IDockable>(
                appearanceDock,
                new ProportionalDockSplitter(),
                new ToolDock
                {
                    ActiveDockable = tool7,
                    Alignment = Alignment.Right,
                    VisibleDockables = CreateList<IDockable>(tool7)
                })
        };

        var mainDock = new ProportionalDock
        {
            Orientation = Dock.Model.Core.Orientation.Horizontal,
            VisibleDockables = CreateList<IDockable>(
                leftDock,
                new ProportionalDockSplitter(),
                centerDock,
                new ProportionalDockSplitter(),
                rightDock)
        };

        var root = CreateRootDock();
        root.Id = "Root";
        root.IsCollapsable = false;
        root.ActiveDockable = mainDock;
        root.DefaultDockable = mainDock;
        root.VisibleDockables = CreateList<IDockable>(mainDock);
        root.LeftPinnedDockables = CreateList<IDockable>();
        root.RightPinnedDockables = CreateList<IDockable>();
        root.TopPinnedDockables = CreateList<IDockable>();
        root.BottomPinnedDockables = CreateList<IDockable>();
        root.PinnedDockDisplayMode = PinnedDockDisplayMode.Overlay;

        _rootDock = root;
        _documentDock = documents;
        _terminalDock = bottomDock;
        return root;
    }

    private void OpenDocument(string relativePath)
    {
        if (_documentDock is null)
        {
            return;
        }

        var baseDirectory = _projectDirectory ?? AppContext.BaseDirectory;
        var filePath = Path.GetFullPath(Path.Combine(baseDirectory, relativePath));
        var existing = _documentDock.VisibleDockables?
            .OfType<DocumentViewModel>()
            .FirstOrDefault(document =>
                string.Equals(document.FilePath, filePath, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            SetActiveDockable(existing);
            SetFocusedDockable(_documentDock, existing);
            return;
        }

        if (!File.Exists(filePath))
        {
            return;
        }

        var document = DocumentViewModel.LoadFromFile(filePath);
        document.ContentChanged += OnDocumentContentChanged;
        document.Saved += OnDocumentSaved;
        document.CompletionProvider = GetDocumentCompletionsAsync;
        document.PrepareRenameProvider = PrepareDocumentRenameAsync;
        document.RenameProvider = RenameDocumentAsync;
        document.DefinitionProvider = GoToDocumentDefinitionAsync;
        document.ReferencesProvider = FindDocumentReferencesAsync;
        document.FormatProvider = FormatDocumentAsync;
        if (_diagnostics.TryGetValue(filePath, out var diagnostics))
            document.SetDiagnostics(diagnostics);
        AddDockable(_documentDock, document);
        SetActiveDockable(document);
        SetFocusedDockable(_documentDock, document);
    }

    private void OpenLibrary(string libraryFileName)
    {
        if (_documentDock is null)
            return;

        if (_projectDirectory is null)
            return;

        var libraryPath = StrucppToolchain.GetProjectLibraryPath(
            _projectDirectory,
            libraryFileName);
        var existing = Find(dockable => dockable is LibraryDetailsViewModel)
            .OfType<LibraryDetailsViewModel>()
            .FirstOrDefault(document =>
                string.Equals(document.LibraryPath, libraryPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SetActiveDockable(existing);
            SetFocusedDockable(_documentDock, existing);
            return;
        }

        var document = LibraryDetailsViewModel.Load(libraryPath);
        AddDockable(_documentDock, document);
        SetActiveDockable(document);
        SetFocusedDockable(_documentDock, document);
    }

    public void OpenProject(ProjectDocument document, string projectDirectory)
    {
        _currentProject = document;
        _projectDirectory = projectDirectory;
        _projectSymbols.Clear();
        _projectTool?.LoadProject(document, projectDirectory);
        _messagesTool?.SetProject(document.Name, projectDirectory);
        ConfigureProjectWatcher(projectDirectory);
        CloseAllDocuments();
        StartLanguageServer(projectDirectory);
    }

    private void ConfigureProjectWatcher(string projectDirectory)
    {
        lock (_projectWatcherLock)
        {
            _projectWatcher?.Dispose();
            _projectRefreshTimer?.Dispose();

            _projectRefreshTimer = new Timer(
                _ => Dispatcher.UIThread.Post(RefreshProjectFromFileSystem),
                null,
                Timeout.Infinite,
                Timeout.Infinite);
            _projectWatcher = new FileSystemWatcher(projectDirectory)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName |
                               NotifyFilters.DirectoryName |
                               NotifyFilters.LastWrite |
                               NotifyFilters.Size
            };
            _projectWatcher.Created += OnProjectFileSystemChanged;
            _projectWatcher.Changed += OnProjectFileSystemChanged;
            _projectWatcher.Deleted += OnProjectFileSystemChanged;
            _projectWatcher.Renamed += OnProjectFileSystemChanged;
            _projectWatcher.Error += (_, _) => ScheduleProjectRefresh();
            _projectWatcher.EnableRaisingEvents = true;
        }
    }

    private void OnProjectFileSystemChanged(object sender, FileSystemEventArgs e)
    {
        var fileName = Path.GetFileName(e.FullPath);
        if (fileName.StartsWith(ProjectStore.ManifestFileName, StringComparison.OrdinalIgnoreCase))
            return;

        ScheduleProjectRefresh();
    }

    private void ScheduleProjectRefresh()
    {
        lock (_projectWatcherLock)
        {
            try
            {
                _projectRefreshTimer?.Change(250, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
                // A project switch disposed the previous debounce timer.
            }
        }
    }

    private void RefreshProjectFromFileSystem()
    {
        if (_currentProject is null || _projectDirectory is null || _projectTool is null)
            return;

        try
        {
            _projectTool.RefreshProject(_currentProject, _projectDirectory);
            if (Directory.Exists(_projectDirectory))
            {
                ProjectStore.Save(
                    _currentProject,
                    Path.Combine(_projectDirectory, ProjectStore.ManifestFileName));
            }
            _ = SynchronizeProjectSourcesAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A rename can briefly invalidate an enumerated path. Retry after
            // the filesystem has settled instead of exposing a partial tree.
            ScheduleProjectRefresh();
        }
    }

    public void AddPou(NewPouDefinition definition)
    {
        if (_currentProject is null || _projectDirectory is null)
            throw new InvalidOperationException("Open a project before adding a POU.");

        ValidatePouDefinition(definition);
        var (folderName, categoryName) = definition.Kind switch
        {
            PouKind.Program => ("Programs", "Programs"),
            PouKind.FunctionBlock => ("FunctionBlocks", "Function Blocks"),
            PouKind.Function => ("Functions", "Functions"),
            _ => throw new ArgumentOutOfRangeException(nameof(definition.Kind))
        };

        var category = FindPouCategory(_currentProject, categoryName);
        var relativePath = $"ProjectFiles/POUs/{folderName}/{definition.Name}.st";
        if (category.Children.Any(child =>
                string.Equals(child.FilePath, relativePath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new IOException($"A POU named '{definition.Name}' already exists.");
        }

        var filePath = Path.Combine(
            _projectDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(filePath))
            throw new IOException($"The source file '{relativePath}' already exists.");

        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        using (var stream = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write(CreatePouSource(definition));
        }

        var node = new ProjectNodeDefinition
        {
            Name = definition.Name,
            Icon = "program",
            FilePath = relativePath,
            IsExpanded = true
        };
        category.Children.Add(node);
        category.IsExpanded = true;

        try
        {
            ProjectStore.Save(
                _currentProject,
                Path.Combine(_projectDirectory, ProjectStore.ManifestFileName));
        }
        catch
        {
            category.Children.Remove(node);
            File.Delete(filePath);
            throw;
        }

        _projectTool?.LoadProject(_currentProject, _projectDirectory);
        OpenDocument(relativePath);
    }

    public void OpenTerminal()
    {
        if (_terminalDock is null)
        {
            return;
        }

        var number = ++_terminalNumber;
        var terminal = new TerminalViewModel
        {
            Id = $"Terminal-{number}",
            Title = $"Terminal {number}",
            CanClose = true
        };
        terminal.RequestFocus();

        AddDockable(_terminalDock, terminal);
        SetActiveDockable(terminal);
        SetFocusedDockable(_terminalDock, terminal);
    }

    public void ImportCodesysLibrary(CodesysLibraryImport import)
    {
        if (_terminalDock is null || _projectDirectory is null || _currentProject is null)
            throw new InvalidOperationException("Open a project before importing a library.");

        var tool = new LibraryImportViewModel
        {
            Id = $"LibraryImport-{Guid.NewGuid():N}",
            Title = $"Import {import.LibraryName}",
            CanPin = true,
            CanClose = true
        };
        tool.ImportExited += exitCode =>
        {
            if (exitCode != 0)
                return;

            Dispatcher.UIThread.Post(() =>
            {
                if (_currentProject is null || _projectDirectory is null)
                    return;

                _projectTool?.LoadProject(_currentProject, _projectDirectory);
                ProjectStore.Save(
                    _currentProject,
                    Path.Combine(_projectDirectory, ProjectStore.ManifestFileName));
            });
        };

        tool.PrepareRun(_projectDirectory, import);
        AddDockable(_terminalDock, tool);
        SetActiveDockable(tool);
        SetFocusedDockable(_terminalDock, tool);
    }

    public void OpenAppearance()
    {
        if (_appearanceDock is null || _appearanceTool is null)
        {
            return;
        }

        if (_appearanceDock.VisibleDockables?.Contains(_appearanceTool) != true)
        {
            AddDockable(_appearanceDock, _appearanceTool);
        }

        SetActiveDockable(_appearanceTool);
        SetFocusedDockable(_appearanceDock, _appearanceTool);
    }

    private static ProjectNodeDefinition FindPouCategory(ProjectDocument project, string categoryName)
    {
        ProjectNodeDefinition FindChild(ProjectNodeDefinition parent, string name) =>
            parent.Children.FirstOrDefault(child => string.Equals(child.Name, name, StringComparison.Ordinal))
            ?? throw new InvalidDataException($"The project tree does not contain '{name}'.");

        var projectRoot = project.Tree.FirstOrDefault()
                          ?? throw new InvalidDataException("The project tree is empty.");
        var software = FindChild(projectRoot, "Software");
        var application = FindChild(software, "Application");
        var pous = FindChild(application, "POUs");
        return FindChild(pous, categoryName);
    }

    private static void ValidatePouDefinition(NewPouDefinition definition)
    {
        if (!IecIdentifier.IsValid(definition.Name))
            throw new InvalidDataException("The POU name is not a valid IEC identifier.");
        if (definition.Extends is { } extends && !IecIdentifier.IsValid(extends))
            throw new InvalidDataException("The EXTENDS type is not a valid IEC identifier.");
        if (definition.Implements is { } implements && !IecIdentifier.IsValid(implements))
            throw new InvalidDataException("The IMPLEMENTS type is not a valid IEC identifier.");
        if (definition.IsAbstract && definition.IsFinal)
            throw new InvalidDataException("A function block cannot be both ABSTRACT and FINAL.");
    }

    private static string CreatePouSource(NewPouDefinition definition)
    {
        return definition.Kind switch
        {
            PouKind.Program => $"PROGRAM {definition.Name}\nVAR\nEND_VAR\n\nEND_PROGRAM\n",
            PouKind.FunctionBlock => CreateFunctionBlockSource(definition),
            PouKind.Function =>
                $"FUNCTION {definition.Name} : {definition.ReturnType}\n" +
                "VAR_INPUT\nEND_VAR\n\n" +
                $"{definition.Name} := {GetDefaultValue(definition.ReturnType)};\n" +
                "END_FUNCTION\n",
            _ => throw new ArgumentOutOfRangeException(nameof(definition.Kind))
        };
    }

    private static string CreateFunctionBlockSource(NewPouDefinition definition)
    {
        var modifier = definition.IsAbstract
            ? " ABSTRACT"
            : definition.IsFinal
                ? " FINAL"
                : string.Empty;
        var extends = definition.Extends is { } baseType ? $" EXTENDS {baseType}" : string.Empty;
        var implements = definition.Implements is { } interfaceType
            ? $" IMPLEMENTS {interfaceType}"
            : string.Empty;
        return $"FUNCTION_BLOCK{modifier} {definition.Name}{extends}{implements}\n" +
               "VAR\nEND_VAR\n\nEND_FUNCTION_BLOCK\n";
    }

    private static string GetDefaultValue(string returnType) => returnType.ToUpperInvariant() switch
    {
        "BOOL" => "FALSE",
        "REAL" or "LREAL" => "0.0",
        "STRING" => "''",
        "TIME" => "T#0s",
        _ => "0"
    };

    public event Action<int>? BuildExited;

    private void OnBuildExited(int exitCode)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_currentProject is not null && _projectDirectory is not null)
                _projectTool?.LoadProject(_currentProject, _projectDirectory);

            BuildExited?.Invoke(exitCode);
        });
    }

    public void Build()
    {
        if (_terminalDock is null || _buildTool is null)
        {
            return;
        }

        if (_projectDirectory is null || _currentProject is null)
        {
            return;
        }

        _buildTool.PrepareRun(_projectDirectory, _currentProject.Name);
        if (_terminalDock.VisibleDockables?.Contains(_buildTool) != true)
        {
            AddDockable(_terminalDock, _buildTool);
        }

        SetActiveDockable(_buildTool);
        SetFocusedDockable(_terminalDock, _buildTool);
    }

    public void SaveAllDocuments()
    {
        foreach (var document in Find(dockable => dockable is DocumentViewModel)
                     .OfType<DocumentViewModel>())
        {
            document.Save();
        }
    }

    public void SaveActiveDocument()
    {
        Find(dockable => dockable is DocumentViewModel { IsActive: true })
            .OfType<DocumentViewModel>()
            .FirstOrDefault()
            ?.Save();
    }

    public void RequestRenameActiveDocument()
    {
        Find(dockable => dockable is DocumentViewModel { IsActive: true })
            .OfType<DocumentViewModel>()
            .FirstOrDefault()
            ?.RequestRename();
    }

    public void RequestGoToDefinitionActiveDocument()
    {
        Find(dockable => dockable is DocumentViewModel { IsActive: true })
            .OfType<DocumentViewModel>()
            .FirstOrDefault()
            ?.RequestGoToDefinition();
    }

    public void RequestFindReferencesActiveDocument()
    {
        Find(dockable => dockable is DocumentViewModel { IsActive: true })
            .OfType<DocumentViewModel>()
            .FirstOrDefault()
            ?.RequestFindReferences();
    }

    public void RequestFormatActiveDocument()
    {
        Find(dockable => dockable is DocumentViewModel { IsActive: true })
            .OfType<DocumentViewModel>()
            .FirstOrDefault()
            ?.RequestFormat();
    }

    public void CloseAllDocuments()
    {
        var documents = Find(dockable => dockable is DocumentViewModel)
            .OfType<DocumentViewModel>()
            .ToList();

        foreach (var document in documents)
        {
            CloseDockable(document);
        }
    }

    public async Task ShutdownAsync()
    {
        _languageServerCancellation?.Cancel();
        _languageServerCancellation?.Dispose();
        _languageServerCancellation = null;
        await _languageClient.StopAsync().ConfigureAwait(false);
    }

    private void StartLanguageServer(string projectDirectory)
    {
        _languageServerCancellation?.Cancel();
        _languageServerCancellation?.Dispose();
        _languageServerCancellation = new CancellationTokenSource();
        _ = StartLanguageServerAsync(projectDirectory, _languageServerCancellation.Token);
    }

    private async Task StartLanguageServerAsync(
        string projectDirectory,
        CancellationToken cancellationToken)
    {
        await _languageServerLifecycle.WaitAsync();
        try
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            _diagnostics.Clear();
            _languageServerDocuments.Clear();
            _projectSymbols.Clear();
            await _languageClient.StartAsync(projectDirectory, cancellationToken);

            foreach (var filePath in EnumerateStructuredTextFiles(projectDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var openDocument = Find(dockable => dockable is DocumentViewModel)
                    .OfType<DocumentViewModel>()
                    .FirstOrDefault(document =>
                        string.Equals(document.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
                var text = openDocument?.Document.Text ?? await File.ReadAllTextAsync(filePath, cancellationToken);
                var version = openDocument?.Version ?? 1;
                await _languageClient.OpenDocumentAsync(filePath, text, version, cancellationToken);
                _languageServerDocuments.Add(filePath);
            }

            await RefreshProjectSymbolsAsync(projectDirectory, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Unable to start the STruCpp language server: {exception}");
        }
        finally
        {
            _languageServerLifecycle.Release();
        }
    }

    private async Task SynchronizeProjectSourcesAsync(
        CancellationToken cancellationToken = default)
    {
        var projectDirectory = _projectDirectory;
        if (projectDirectory is null || !_languageClient.IsRunning)
            return;

        var currentFiles = EnumerateStructuredTextFiles(projectDirectory)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removedFiles = _languageServerDocuments
            .Where(path => !currentFiles.Contains(path))
            .ToArray();
        var addedFiles = currentFiles
            .Where(path => !_languageServerDocuments.Contains(path))
            .ToArray();

        try
        {
            foreach (var filePath in removedFiles)
            {
                await _languageClient.CloseDocumentAsync(filePath, cancellationToken);
                _languageServerDocuments.Remove(filePath);
                _diagnostics.Remove(filePath);
                _messagesTool?.RemoveDiagnostics(filePath);
            }

            foreach (var filePath in addedFiles)
            {
                await _languageClient.OpenDocumentAsync(
                    filePath,
                    await File.ReadAllTextAsync(filePath, cancellationToken),
                    version: 1,
                    cancellationToken);
                _languageServerDocuments.Add(filePath);
            }

            await RefreshProjectSymbolsAsync(projectDirectory, cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Unable to synchronize STruCpp project sources: {exception.Message}");
        }
    }

    private void OnDocumentContentChanged(
        DocumentViewModel document,
        string text,
        int version) =>
        _ = SendDocumentChangeAsync(document.FilePath, text, version);

    private async Task<IReadOnlyList<StrucppCompletionItem>> GetDocumentCompletionsAsync(
        DocumentViewModel document,
        int line,
        int character,
        string? triggerCharacter,
        CancellationToken cancellationToken)
    {
        if (!_languageClient.IsRunning)
            return [];

        try
        {
            // Completion is latency-sensitive and must see the character that
            // triggered it, rather than waiting for the diagnostics debounce.
            await _languageClient.ChangeDocumentAsync(
                document.FilePath,
                document.Document.Text,
                document.Version,
                cancellationToken);
            return await _languageClient.GetCompletionsAsync(
                document.FilePath,
                line,
                character,
                triggerCharacter,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return [];
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Unable to complete STruCpp document '{document.FilePath}': {exception.Message}");
            return [];
        }
    }

    private async Task<StrucppPrepareRenameResult?> PrepareDocumentRenameAsync(
        DocumentViewModel document,
        int line,
        int character,
        CancellationToken cancellationToken)
    {
        if (!_languageClient.IsRunning)
            return null;

        await _languageClient.ChangeDocumentAsync(
            document.FilePath,
            document.Document.Text,
            document.Version,
            cancellationToken);
        return await _languageClient.PrepareRenameAsync(
            document.FilePath,
            line,
            character,
            cancellationToken);
    }

    private async Task<bool> GoToDocumentDefinitionAsync(
        DocumentViewModel document,
        int line,
        int character,
        CancellationToken cancellationToken)
    {
        if (!_languageClient.IsRunning)
            return false;

        await _languageClient.ChangeDocumentAsync(
            document.FilePath,
            document.Document.Text,
            document.Version,
            cancellationToken);
        var definitions = await _languageClient.GetDefinitionsAsync(
            document.FilePath,
            line,
            character,
            cancellationToken);
        var definition = definitions.FirstOrDefault(IsNavigableLocation);
        if (definition is null)
            return false;

        await Dispatcher.UIThread.InvokeAsync(() => NavigateToLocation(definition));
        return true;
    }

    private async Task<int> FindDocumentReferencesAsync(
        DocumentViewModel document,
        int line,
        int character,
        CancellationToken cancellationToken)
    {
        if (!_languageClient.IsRunning ||
            _projectDirectory is null ||
            _currentProject is null)
            return 0;

        await _languageClient.ChangeDocumentAsync(
            document.FilePath,
            document.Document.Text,
            document.Version,
            cancellationToken);
        var references = await _languageClient.FindReferencesAsync(
            document.FilePath,
            line,
            character,
            includeDeclaration: true,
            cancellationToken);
        var projectReferences = references
            .Where(IsNavigableLocation)
            .OrderBy(location => location.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(location => location.Range.Start.Line)
            .ThenBy(location => location.Range.Start.Character)
            .ToArray();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_referencesTool is null ||
                _terminalDock is null ||
                _projectDirectory is null ||
                _currentProject is null)
                return;

            _referencesTool.SetResults(
                _currentProject.Name,
                _projectDirectory,
                projectReferences);
            if (_terminalDock.VisibleDockables?.Contains(_referencesTool) != true)
                AddDockable(_terminalDock, _referencesTool);
            SetActiveDockable(_referencesTool);
            SetFocusedDockable(_terminalDock, _referencesTool);
        });
        return projectReferences.Length;
    }

    private async Task<IReadOnlyList<StrucppTextEdit>> FormatDocumentAsync(
        DocumentViewModel document,
        int tabSize,
        bool insertSpaces,
        CancellationToken cancellationToken)
    {
        if (!_languageClient.IsRunning)
            return [];

        await _languageClient.ChangeDocumentAsync(
            document.FilePath,
            document.Document.Text,
            document.Version,
            cancellationToken);
        return await _languageClient.FormatDocumentAsync(
            document.FilePath,
            tabSize,
            insertSpaces,
            cancellationToken);
    }

    private bool IsNavigableLocation(StrucppLocation location)
    {
        if (_projectDirectory is null)
            return false;

        var fullPath = Path.GetFullPath(location.FilePath);
        return File.Exists(fullPath) &&
               IsPathInsideProject(fullPath, Path.GetFullPath(_projectDirectory)) &&
               IsStructuredTextExtension(Path.GetExtension(fullPath));
    }

    private void NavigateToLocation(StrucppLocation location)
    {
        if (_projectDirectory is null || !IsNavigableLocation(location))
            return;

        var fullPath = Path.GetFullPath(location.FilePath);
        OpenDocument(Path.GetRelativePath(_projectDirectory, fullPath));
        var document = Find(dockable => dockable is DocumentViewModel)
            .OfType<DocumentViewModel>()
            .FirstOrDefault(candidate =>
                string.Equals(candidate.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (document is null)
            return;

        Dispatcher.UIThread.Post(
            () => document.NavigateTo(location.Range),
            DispatcherPriority.Loaded);
    }

    private async Task<int> RenameDocumentAsync(
        DocumentViewModel document,
        int line,
        int character,
        string newName,
        CancellationToken cancellationToken)
    {
        if (!_languageClient.IsRunning || _projectDirectory is null)
            return 0;

        await _languageClient.ChangeDocumentAsync(
            document.FilePath,
            document.Document.Text,
            document.Version,
            cancellationToken);
        var workspaceEdit = await _languageClient.RenameAsync(
            document.FilePath,
            line,
            character,
            newName,
            cancellationToken);
        if (workspaceEdit is null)
            return 0;

        return await ApplyWorkspaceEditAsync(
            workspaceEdit,
            _projectDirectory,
            cancellationToken);
    }

    private async Task<int> ApplyWorkspaceEditAsync(
        StrucppWorkspaceEdit workspaceEdit,
        string projectDirectory,
        CancellationToken cancellationToken)
    {
        var updatedTexts = new List<(string FilePath, DocumentViewModel? OpenDocument, string Text)>();
        var projectRoot = Path.GetFullPath(projectDirectory);

        foreach (var (filePath, edits) in workspaceEdit.Changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(filePath);
            if (!IsPathInsideProject(fullPath, projectRoot) ||
                !IsStructuredTextExtension(Path.GetExtension(fullPath)))
            {
                throw new InvalidOperationException(
                    "The language server returned a rename outside the current project.");
            }

            var openDocument = Find(dockable => dockable is DocumentViewModel)
                .OfType<DocumentViewModel>()
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
            var textDocument = new AvaloniaEdit.Document.TextDocument(
                openDocument?.Document.Text ?? await File.ReadAllTextAsync(fullPath, cancellationToken));

            textDocument.BeginUpdate();
            try
            {
                foreach (var edit in edits
                             .OrderByDescending(item => item.Range.Start.Line)
                             .ThenByDescending(item => item.Range.Start.Character))
                {
                    var startOffset = GetOffset(textDocument, edit.Range.Start);
                    var endOffset = GetOffset(textDocument, edit.Range.End);
                    if (endOffset < startOffset)
                        throw new InvalidDataException("The language server returned an invalid rename range.");
                    textDocument.Replace(startOffset, endOffset - startOffset, edit.NewText);
                }
            }
            finally
            {
                textDocument.EndUpdate();
            }

            updatedTexts.Add((fullPath, openDocument, textDocument.Text));
        }

        foreach (var update in updatedTexts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (update.OpenDocument is { } openDocument)
            {
                openDocument.Document.Text = update.Text;
                openDocument.Save();
            }
            else
            {
                await File.WriteAllTextAsync(update.FilePath, update.Text, cancellationToken);
                await _languageClient.SaveDocumentAsync(
                    update.FilePath,
                    update.Text,
                    cancellationToken);
            }
        }

        return updatedTexts.Sum(update =>
            workspaceEdit.Changes[update.FilePath].Count);
    }

    private static int GetOffset(
        AvaloniaEdit.Document.TextDocument document,
        StrucppPosition position)
    {
        var lineNumber = position.Line + 1;
        if (lineNumber < 1 || lineNumber > document.LineCount)
            throw new InvalidDataException("The language server returned a rename line outside the document.");

        var line = document.GetLineByNumber(lineNumber);
        if (position.Character < 0 || position.Character > line.Length)
            throw new InvalidDataException("The language server returned a rename column outside the document.");
        return line.Offset + position.Character;
    }

    private static bool IsPathInsideProject(string filePath, string projectRoot)
    {
        var relativePath = Path.GetRelativePath(projectRoot, filePath);
        return !Path.IsPathRooted(relativePath) &&
               relativePath != ".." &&
               !relativePath.StartsWith(
                   $"..{Path.DirectorySeparatorChar}",
                   StringComparison.Ordinal);
    }

    private async Task SendDocumentChangeAsync(string filePath, string text, int version)
    {
        if (!_languageClient.IsRunning)
            return;

        try
        {
            await _languageClient.ChangeDocumentAsync(filePath, text, version);
            await RefreshDocumentSymbolsAsync(filePath);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Unable to update STruCpp document '{filePath}': {exception.Message}");
        }
    }

    private void OnDocumentSaved(DocumentViewModel document, string text) =>
        _ = SendDocumentSaveAsync(document.FilePath, text);

    private async Task SendDocumentSaveAsync(string filePath, string text)
    {
        if (!_languageClient.IsRunning)
            return;

        try
        {
            await _languageClient.SaveDocumentAsync(filePath, text);
            await RefreshDocumentSymbolsAsync(filePath);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Unable to save STruCpp document '{filePath}': {exception.Message}");
        }
    }

    private void OnDiagnosticsPublished(
        object? sender,
        StrucppDiagnosticsEventArgs args) =>
        Dispatcher.UIThread.Post(() =>
        {
            _diagnostics[args.FilePath] = args.Diagnostics;
            _messagesTool?.UpdateDiagnostics(args.FilePath, args.Diagnostics);
            Find(dockable => dockable is DocumentViewModel)
                .OfType<DocumentViewModel>()
                .FirstOrDefault(document =>
                    string.Equals(
                        document.FilePath,
                        args.FilePath,
                        StringComparison.OrdinalIgnoreCase))
                ?.SetDiagnostics(args.Diagnostics);
        });

    private async Task RefreshProjectSymbolsAsync(
        string projectDirectory,
        CancellationToken cancellationToken)
    {
        var symbols = new Dictionary<string, IReadOnlyList<StrucppDocumentSymbol>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var filePath in EnumerateStructuredTextFiles(projectDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                symbols[filePath] = await _languageClient.GetDocumentSymbolsAsync(
                    filePath,
                    cancellationToken);
            }
            catch (Exception exception) when (
                exception is IOException or InvalidOperationException or
                    InvalidDataException or ObjectDisposedException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Unable to read STruCpp document symbols for '{filePath}': {exception.Message}");
            }
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_currentProject is null ||
                _projectDirectory is null ||
                !string.Equals(
                    Path.GetFullPath(_projectDirectory),
                    Path.GetFullPath(projectDirectory),
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _projectSymbols.Clear();
            foreach (var (filePath, documentSymbols) in symbols)
                _projectSymbols[filePath] = documentSymbols;
            _projectTool?.SetDocumentSymbols(
                _currentProject,
                _projectDirectory,
                _projectSymbols);
        });
    }

    private async Task RefreshDocumentSymbolsAsync(string filePath)
    {
        if (!_languageClient.IsRunning)
            return;

        try
        {
            var symbols = await _languageClient.GetDocumentSymbolsAsync(filePath);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_currentProject is null ||
                    _projectDirectory is null ||
                    !IsPathInsideProject(
                        Path.GetFullPath(filePath),
                        Path.GetFullPath(_projectDirectory)))
                {
                    return;
                }

                _projectSymbols[filePath] = symbols;
                _projectTool?.SetDocumentSymbols(filePath, symbols);
            });
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or
                InvalidDataException or ObjectDisposedException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Unable to refresh STruCpp document symbols for '{filePath}': {exception.Message}");
        }
    }

    private static IEnumerable<string> EnumerateStructuredTextFiles(string projectDirectory)
    {
        var sourceRoot = Path.Combine(projectDirectory, "ProjectFiles");
        if (!Directory.Exists(sourceRoot))
            return [];

        return Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Where(path => IsStructuredTextExtension(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsStructuredTextExtension(string extension) =>
        extension.Equals(".st", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".iecst", StringComparison.OrdinalIgnoreCase);

    public override IDockWindow? CreateWindowFrom(IDockable dockable)
    {
        var window = base.CreateWindowFrom(dockable);
        if (window is null)
        {
            return null;
        }

        var title = dockable is IDock { ActiveDockable: { } activeDockable }
            ? activeDockable.Title
            : dockable.Title;
        title = string.IsNullOrWhiteSpace(title) ? "Dock MVVM Sample" : title;

        window.Title = title;
        if (window.Layout is { } floatingRoot)
        {
            floatingRoot.Title = title;

            // The Classic HostWindow theme displays ActiveDockable.Title.
            if (floatingRoot.ActiveDockable is { } floatingContainer)
            {
                floatingContainer.Title = title;
            }
        }

        return window;
    }

    public override void SplitToWindow(
        IDock dock,
        IDockable dockable,
        double x,
        double y,
        double width,
        double height,
        DockWindowOptions? options)
    {
        width = NormalizeFloatingSize(width, DefaultFloatingWidth, MaximumFloatingWidth, 280);
        height = NormalizeFloatingSize(height, DefaultFloatingHeight, MaximumFloatingHeight, 200);
        base.SplitToWindow(dock, dockable, x, y, width, height, options);
    }

    private static double NormalizeFloatingSize(double value, double fallback, double maximum, double minimum)
    {
        return double.IsFinite(value) && value > 0
            ? Math.Clamp(value, minimum, maximum)
            : fallback;
    }

    public override void InitLayout(IDockable layout)
    {
        ContextLocator = new Dictionary<string, Func<object?>>
        {
            ["Tool1"] = () => new Tool1(),
            ["Tool2"] = () => new Tool2(),
            ["Tool3"] = () => new Tool3(),
            ["Tool4"] = () => new Tool4(),
            ["Tool5"] = () => new Tool5(),
            ["Tool6"] = () => new Tool6(),
            ["Tool7"] = () => new Tool7(),
            ["Tool8"] = () => new Tool8()
        };

        DockableLocator = new Dictionary<string, Func<IDockable?>>
        {
            ["Root"] = () => _rootDock,
            ["Documents"] = () => _documentDock
        };

        HostWindowLocator = new Dictionary<string, Func<IHostWindow?>>
        {
            [nameof(IDockWindow)] = () => DockSettings.UseManagedWindows
                ? new ManagedHostWindow()
                : new HostWindow()
        };

        base.InitLayout(layout);
    }
}

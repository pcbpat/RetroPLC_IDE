using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AvaloniaEdit.Document;
using Dock.Model.Mvvm.Controls;
using RetroPLC.LanguageServerHost;

namespace RetroPLC.Shell.ViewModels.Docking;

public sealed class DocumentViewModel : Document
{
    private CancellationTokenSource? _changeDebounce;
    private int _version = 1;
    private StrucppRange? _pendingNavigation;

    private DocumentViewModel(string filePath, TextDocument document)
    {
        FilePath = filePath;
        Document = document;
        Id = filePath;
        Title = Path.GetFileName(filePath);
        Document.TextChanged += OnDocumentTextChanged;
    }

    public event Action<DocumentViewModel, string, int>? ContentChanged;
    public event Action<DocumentViewModel, string>? Saved;
    public event Action? RenameRequested;
    public event Action? GoToDefinitionRequested;
    public event Action? FindReferencesRequested;
    public event Action? NavigationRequested;

    public Func<
        DocumentViewModel,
        int,
        int,
        string?,
        CancellationToken,
        Task<IReadOnlyList<StrucppCompletionItem>>>? CompletionProvider { get; set; }

    public Func<
        DocumentViewModel,
        int,
        int,
        CancellationToken,
        Task<StrucppPrepareRenameResult?>>? PrepareRenameProvider { get; set; }

    public Func<
        DocumentViewModel,
        int,
        int,
        string,
        CancellationToken,
        Task<int>>? RenameProvider { get; set; }

    public Func<
        DocumentViewModel,
        int,
        int,
        CancellationToken,
        Task<bool>>? DefinitionProvider { get; set; }

    public Func<
        DocumentViewModel,
        int,
        int,
        CancellationToken,
        Task<int>>? ReferencesProvider { get; set; }

    public string FilePath { get; }

    public TextDocument Document { get; }

    public ObservableCollection<StrucppDiagnostic> Diagnostics { get; } = [];

    public int Version => _version;

    public void Save()
    {
        var text = Document.Text;
        File.WriteAllText(FilePath, text);
        Saved?.Invoke(this, text);
    }

    public void SetDiagnostics(IReadOnlyList<StrucppDiagnostic> diagnostics)
    {
        Diagnostics.Clear();
        foreach (var diagnostic in diagnostics)
            Diagnostics.Add(diagnostic);
    }

    public Task<IReadOnlyList<StrucppCompletionItem>> GetCompletionsAsync(
        int line,
        int character,
        string? triggerCharacter,
        CancellationToken cancellationToken = default) =>
        CompletionProvider?.Invoke(
            this,
            line,
            character,
            triggerCharacter,
            cancellationToken) ??
        Task.FromResult<IReadOnlyList<StrucppCompletionItem>>([]);

    public Task<StrucppPrepareRenameResult?> PrepareRenameAsync(
        int line,
        int character,
        CancellationToken cancellationToken = default) =>
        PrepareRenameProvider?.Invoke(
            this,
            line,
            character,
            cancellationToken) ??
        Task.FromResult<StrucppPrepareRenameResult?>(null);

    public Task<int> RenameAsync(
        int line,
        int character,
        string newName,
        CancellationToken cancellationToken = default) =>
        RenameProvider?.Invoke(
            this,
            line,
            character,
            newName,
            cancellationToken) ??
        Task.FromResult(0);

    public Task<bool> GoToDefinitionAsync(
        int line,
        int character,
        CancellationToken cancellationToken = default) =>
        DefinitionProvider?.Invoke(this, line, character, cancellationToken) ??
        Task.FromResult(false);

    public Task<int> FindReferencesAsync(
        int line,
        int character,
        CancellationToken cancellationToken = default) =>
        ReferencesProvider?.Invoke(this, line, character, cancellationToken) ??
        Task.FromResult(0);

    public void RequestRename() => RenameRequested?.Invoke();

    public void RequestGoToDefinition() => GoToDefinitionRequested?.Invoke();

    public void RequestFindReferences() => FindReferencesRequested?.Invoke();

    public void NavigateTo(StrucppRange range)
    {
        _pendingNavigation = range;
        NavigationRequested?.Invoke();
    }

    public bool TryConsumeNavigation(out StrucppRange range)
    {
        if (_pendingNavigation is not { } pending)
        {
            range = default!;
            return false;
        }

        _pendingNavigation = null;
        range = pending;
        return true;
    }

    public override bool OnClose()
    {
        if (!base.OnClose())
            return false;

        _changeDebounce?.Cancel();
        _changeDebounce?.Dispose();
        _changeDebounce = null;
        Document.TextChanged -= OnDocumentTextChanged;
        ContentChanged = null;
        Saved = null;
        RenameRequested = null;
        GoToDefinitionRequested = null;
        FindReferencesRequested = null;
        NavigationRequested = null;
        CompletionProvider = null;
        PrepareRenameProvider = null;
        RenameProvider = null;
        DefinitionProvider = null;
        ReferencesProvider = null;
        return true;
    }

    public static DocumentViewModel LoadFromFile(string filePath) =>
        new(filePath, new TextDocument(File.ReadAllText(filePath)));

    private void OnDocumentTextChanged(object? sender, EventArgs e)
    {
        var version = Interlocked.Increment(ref _version);
        var text = Document.Text;
        var previousDebounce = Interlocked.Exchange(
            ref _changeDebounce,
            new CancellationTokenSource());
        previousDebounce?.Cancel();
        previousDebounce?.Dispose();
        _ = PublishChangeAfterDelayAsync(text, version, _changeDebounce.Token);
    }

    private async Task PublishChangeAfterDelayAsync(
        string text,
        int version,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(350, cancellationToken);
            ContentChanged?.Invoke(this, text, version);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}

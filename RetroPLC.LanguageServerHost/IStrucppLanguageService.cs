namespace RetroPLC.LanguageServerHost;

/// <summary>
/// Typed boundary between editor events and the STruC++ JSON-RPC language server.
/// UI code never needs to construct JSON-RPC payloads or manage the server process.
/// </summary>
public interface IStrucppLanguageService : IAsyncDisposable
{
    event EventHandler<StrucppDiagnosticsEventArgs>? DiagnosticsPublished;

    event EventHandler<StrucppLanguageServerErrorEventArgs>? ServerError;

    bool IsRunning { get; }

    Task StartAsync(string projectDirectory, CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task OpenDocumentAsync(
        string filePath,
        string text,
        int version,
        CancellationToken cancellationToken = default);

    Task ChangeDocumentAsync(
        string filePath,
        string text,
        int version,
        CancellationToken cancellationToken = default);

    Task SaveDocumentAsync(
        string filePath,
        string text,
        CancellationToken cancellationToken = default);

    Task CloseDocumentAsync(
        string filePath,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StrucppCompletionItem>> GetCompletionsAsync(
        string filePath,
        int line,
        int character,
        string? triggerCharacter,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StrucppLocation>> GetDefinitionsAsync(
        string filePath,
        int line,
        int character,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StrucppLocation>> FindReferencesAsync(
        string filePath,
        int line,
        int character,
        bool includeDeclaration = true,
        CancellationToken cancellationToken = default);

    Task<StrucppPrepareRenameResult?> PrepareRenameAsync(
        string filePath,
        int line,
        int character,
        CancellationToken cancellationToken = default);

    Task<StrucppWorkspaceEdit?> RenameAsync(
        string filePath,
        int line,
        int character,
        string newName,
        CancellationToken cancellationToken = default);
}

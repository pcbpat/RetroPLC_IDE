using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace RetroPLC.LanguageServerHost;

public sealed class StrucppLanguageService : IStrucppLanguageService
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly ConcurrentDictionary<string, string> _documentTexts =
        new(StringComparer.OrdinalIgnoreCase);
    private OmniSharpLspClient? _client;
    private string? _projectDirectory;

    public event EventHandler<StrucppDiagnosticsEventArgs>? DiagnosticsPublished;

    public event EventHandler<StrucppLanguageServerErrorEventArgs>? ServerError;

    public bool IsRunning => _client?.IsRunning == true;

    public async Task StartAsync(
        string projectDirectory,
        CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);

            _projectDirectory = Path.GetFullPath(projectDirectory);
            var client = await OmniSharpLspClient.StartAsync(
                StrucppToolchain.GetLanguageServerPath(),
                _projectDirectory,
                HandleServerRequestAsync,
                HandleNotification,
                RaiseServerError,
                cancellationToken).ConfigureAwait(false);
            _client = client;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task OpenDocumentAsync(
        string filePath,
        string text,
        int version,
        CancellationToken cancellationToken = default)
    {
        _documentTexts[NormalizePath(filePath)] = text;
        await Client.NotifyAsync(
            "textDocument/didOpen",
            new JsonObject
            {
                ["textDocument"] = new JsonObject
                {
                    ["uri"] = ToFileUri(filePath),
                    ["languageId"] = "structured-text",
                    ["version"] = version,
                    ["text"] = text
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task ChangeDocumentAsync(
        string filePath,
        string text,
        int version,
        CancellationToken cancellationToken = default)
    {
        _documentTexts[NormalizePath(filePath)] = text;
        await Client.NotifyAsync(
            "textDocument/didChange",
            new JsonObject
            {
                ["textDocument"] = new JsonObject
                {
                    ["uri"] = ToFileUri(filePath),
                    ["version"] = version
                },
                ["contentChanges"] = new JsonArray
                {
                    new JsonObject { ["text"] = text }
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveDocumentAsync(
        string filePath,
        string text,
        CancellationToken cancellationToken = default)
    {
        _documentTexts[NormalizePath(filePath)] = text;
        await Client.NotifyAsync(
            "textDocument/didSave",
            new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = ToFileUri(filePath) },
                ["text"] = text
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task CloseDocumentAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        _documentTexts.TryRemove(NormalizePath(filePath), out _);
        await Client.NotifyAsync(
            "textDocument/didClose",
            new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = ToFileUri(filePath) }
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StrucppCompletionItem>> GetCompletionsAsync(
        string filePath,
        int line,
        int character,
        string? triggerCharacter,
        CancellationToken cancellationToken = default)
    {
        var result = await Client.RequestAsync(
            "textDocument/completion",
            PositionParameters(
                filePath,
                line,
                character,
                new JsonObject
                {
                    ["triggerKind"] = triggerCharacter is null ? 1 : 2,
                    ["triggerCharacter"] = triggerCharacter
                }),
            cancellationToken).ConfigureAwait(false);

        var items = result as JsonArray ?? result?["items"] as JsonArray;
        if (items is null)
            return [];

        return items.OfType<JsonObject>()
            .Where(item => item["label"] is not null)
            .Select(item => new StrucppCompletionItem(
                item["label"]!.GetValue<string>(),
                item["kind"]?.GetValue<int>() ?? 1,
                item["detail"]?.GetValue<string>(),
                item["sortText"]?.GetValue<string>(),
                item["insertText"]?.GetValue<string>(),
                item["insertTextFormat"]?.GetValue<int>() ?? 1))
            .ToArray();
    }

    public async Task<IReadOnlyList<StrucppDocumentSymbol>> GetDocumentSymbolsAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var result = await Client.RequestAsync(
            "textDocument/documentSymbol",
            new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = ToFileUri(filePath) }
            },
            cancellationToken).ConfigureAwait(false);
        return ParseDocumentSymbols(result);
    }

    public async Task<IReadOnlyList<StrucppLocation>> GetDefinitionsAsync(
        string filePath,
        int line,
        int character,
        CancellationToken cancellationToken = default)
    {
        var result = await Client.RequestAsync(
            "textDocument/definition",
            PositionParameters(filePath, line, character),
            cancellationToken).ConfigureAwait(false);
        return ParseLocations(result);
    }

    public async Task<IReadOnlyList<StrucppLocation>> FindReferencesAsync(
        string filePath,
        int line,
        int character,
        bool includeDeclaration = true,
        CancellationToken cancellationToken = default)
    {
        var parameters = PositionParameters(filePath, line, character);
        parameters["context"] = new JsonObject
        {
            ["includeDeclaration"] = includeDeclaration
        };
        var result = await Client.RequestAsync(
            "textDocument/references",
            parameters,
            cancellationToken).ConfigureAwait(false);
        return ParseLocations(result);
    }

    public async Task<StrucppPrepareRenameResult?> PrepareRenameAsync(
        string filePath,
        int line,
        int character,
        CancellationToken cancellationToken = default)
    {
        var result = await Client.RequestAsync(
            "textDocument/prepareRename",
            PositionParameters(filePath, line, character),
            cancellationToken).ConfigureAwait(false);
        if (result is not JsonObject resultObject)
            return null;

        var rangeNode = resultObject["range"] as JsonObject ?? resultObject;
        if (!TryParseRange(rangeNode, out var range))
            return null;

        var placeholder = resultObject["placeholder"]?.GetValue<string>() ?? string.Empty;
        if (TryGetIdentifierAt(filePath, line, character, out _, out var identifierRange))
            range = identifierRange;
        return new StrucppPrepareRenameResult(range, placeholder);
    }

    public async Task<StrucppWorkspaceEdit?> RenameAsync(
        string filePath,
        int line,
        int character,
        string newName,
        CancellationToken cancellationToken = default)
    {
        var parameters = PositionParameters(filePath, line, character);
        parameters["newName"] = newName;
        var result = await Client.RequestAsync(
            "textDocument/rename", parameters, cancellationToken).ConfigureAwait(false);
        TryGetIdentifierAt(filePath, line, character, out var oldName, out _);
        return ParseWorkspaceEdit(result, oldName);
    }

    private OmniSharpLspClient Client =>
        _client ?? throw new InvalidOperationException(
            "The STruC++ language service has not been started.");

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        var client = _client;
        _client = null;
        _projectDirectory = null;
        _documentTexts.Clear();
        if (client is null)
            return;

        try
        {
            await client.ShutdownAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private Task<JsonNode?> HandleServerRequestAsync(
        string method,
        JsonNode? parameters,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return method switch
        {
            "workspace/configuration" => Task.FromResult<JsonNode?>(
                BuildConfigurationResponse(parameters)),
            _ => throw new NotSupportedException(
                $"Unsupported language-server request: {method}")
        };
    }

    private void HandleNotification(string method, JsonNode? parameters)
    {
        if (method != "textDocument/publishDiagnostics" ||
            parameters is not JsonObject payload ||
            payload["uri"]?.GetValue<string>() is not { } uri)
            return;

        var diagnostics = (payload["diagnostics"] as JsonArray)?
            .OfType<JsonObject>()
            .Select(ParseDiagnostic)
            .ToArray() ?? [];
        DiagnosticsPublished?.Invoke(
            this,
            new StrucppDiagnosticsEventArgs(ToFilePath(uri), diagnostics));
    }

    private void RaiseServerError(string message) =>
        ServerError?.Invoke(this, new StrucppLanguageServerErrorEventArgs(message));

    private JsonArray BuildConfigurationResponse(JsonNode? parameters)
    {
        var count = parameters?["items"] is JsonArray items ? items.Count : 1;
        var result = new JsonArray();
        for (var index = 0; index < count; index++)
            result.Add(BuildConfiguration());
        return result;
    }

    private JsonObject BuildConfiguration() => new()
    {
        ["libraryPaths"] = new JsonArray
        {
            StrucppToolchain.GetProjectLibraryDirectory(
                _projectDirectory ?? Environment.CurrentDirectory)
        },
        ["autoDiscoverLibraries"] = true,
        ["outputDirectory"] = "./Build",
        ["gppPath"] = "g++",
        ["ccPath"] = "cc",
        ["cxxFlags"] = string.Empty,
        ["globalConstants"] = new JsonObject(),
        ["autoAnalyze"] = true,
        ["analyzeDelay"] = 350,
        ["formatOnSave"] = false
    };

    private static IReadOnlyList<StrucppDocumentSymbol> ParseDocumentSymbols(JsonNode? result)
    {
        if (result is not JsonArray symbols)
            return [];

        return DeduplicateDocumentSymbols(
            symbols
                .OfType<JsonObject>()
                .Select(ParseDocumentSymbol));
    }

    private static StrucppDocumentSymbol ParseDocumentSymbol(JsonObject symbol)
    {
        var rangeNode = symbol["range"] as JsonObject ??
                        symbol["location"]?["range"] as JsonObject ??
                        throw new InvalidDataException(
                            "The language server returned a document symbol without a range.");
        var range = ParseRange(rangeNode);
        var selectionRange = symbol["selectionRange"] is JsonObject selectionRangeNode
            ? ParseRange(selectionRangeNode)
            : range;
        var children = symbol["children"] is JsonArray childNodes
            ? DeduplicateDocumentSymbols(
                childNodes
                    .OfType<JsonObject>()
                    .Select(ParseDocumentSymbol))
            : [];

        return new StrucppDocumentSymbol(
            symbol["name"]?.GetValue<string>() ?? string.Empty,
            symbol["detail"]?.GetValue<string>(),
            symbol["kind"]?.GetValue<int>() ?? 1,
            range,
            selectionRange,
            children);
    }

    private static IReadOnlyList<StrucppDocumentSymbol> DeduplicateDocumentSymbols(
        IEnumerable<StrucppDocumentSymbol> symbols) =>
        symbols
            .DistinctBy(symbol => (
                symbol.Name,
                symbol.Kind,
                symbol.SelectionRange.Start.Line,
                symbol.SelectionRange.Start.Character,
                symbol.SelectionRange.End.Line,
                symbol.SelectionRange.End.Character))
            .ToArray();

    private static JsonObject PositionParameters(
        string filePath,
        int line,
        int character,
        JsonObject? context = null)
    {
        var parameters = new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = ToFileUri(filePath) },
            ["position"] = new JsonObject
            {
                ["line"] = line,
                ["character"] = character
            }
        };
        if (context is not null)
            parameters["context"] = context;
        return parameters;
    }

    private static StrucppDiagnostic ParseDiagnostic(JsonObject diagnostic) =>
        new(
            ParseRange((JsonObject)diagnostic["range"]!),
            diagnostic["severity"]?.GetValue<int>() ?? 1,
            diagnostic["message"]?.GetValue<string>() ?? string.Empty,
            diagnostic["source"]?.GetValue<string>() ?? "strucpp",
            GetDiagnosticCode(diagnostic["code"]));

    private static string? GetDiagnosticCode(JsonNode? code)
    {
        if (code is not JsonValue value)
            return null;
        if (value.TryGetValue<string>(out var text))
            return text;
        if (value.TryGetValue<int>(out var number))
            return number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return code.ToJsonString();
    }

    private static IReadOnlyList<StrucppLocation> ParseLocations(JsonNode? result)
    {
        if (result is null)
            return [];

        IEnumerable<JsonObject> locations = result switch
        {
            JsonArray array => array.OfType<JsonObject>(),
            JsonObject single => [single],
            _ => []
        };

        return locations
            .Select(ParseLocation)
            .Where(location => location is not null)
            .Cast<StrucppLocation>()
            .ToArray();
    }

    private static StrucppLocation? ParseLocation(JsonObject location)
    {
        // Definition can return either Location or LocationLink.
        var uri = location["uri"]?.GetValue<string>()
                  ?? location["targetUri"]?.GetValue<string>();
        var range = location["range"] as JsonObject
                    ?? location["targetSelectionRange"] as JsonObject
                    ?? location["targetRange"] as JsonObject;
        return uri is not null && range is not null
            ? new StrucppLocation(ToFilePath(uri), ParseRange(range))
            : null;
    }

    private StrucppWorkspaceEdit? ParseWorkspaceEdit(JsonNode? result, string? oldName)
    {
        if (result is not JsonObject edit)
            return null;

        var changes = new Dictionary<string, List<StrucppTextEdit>>(
            StringComparer.OrdinalIgnoreCase);
        if (edit["changes"] is JsonObject changeMap)
        {
            foreach (var (uri, edits) in changeMap)
                AddEdits(changes, ToFilePath(uri), edits as JsonArray, oldName);
        }

        if (edit["documentChanges"] is JsonArray documentChanges)
        {
            foreach (var documentChange in documentChanges.OfType<JsonObject>())
            {
                var uri = documentChange["textDocument"]?["uri"]?.GetValue<string>();
                if (uri is not null)
                    AddEdits(
                        changes,
                        ToFilePath(uri),
                        documentChange["edits"] as JsonArray,
                        oldName);
            }
        }

        return new StrucppWorkspaceEdit(
            changes.ToDictionary(
                item => item.Key,
                IReadOnlyList<StrucppTextEdit> (item) => item.Value,
                StringComparer.OrdinalIgnoreCase));
    }

    private void AddEdits(
        IDictionary<string, List<StrucppTextEdit>> changes,
        string filePath,
        JsonArray? edits,
        string? oldName)
    {
        if (edits is null)
            return;
        if (!changes.TryGetValue(filePath, out var target))
        {
            target = [];
            changes[filePath] = target;
        }

        foreach (var edit in edits.OfType<JsonObject>())
        {
            if (edit["range"] is JsonObject range &&
                edit["newText"]?.GetValue<string>() is { } newText)
            {
                var parsedRange = ParseRange(range);
                if (!string.IsNullOrWhiteSpace(oldName))
                    parsedRange = ConstrainToIdentifier(filePath, parsedRange, oldName);
                target.Add(new StrucppTextEdit(parsedRange, newText));
            }
        }
    }

    private StrucppRange ConstrainToIdentifier(
        string filePath,
        StrucppRange range,
        string identifier)
    {
        if (!_documentTexts.TryGetValue(NormalizePath(filePath), out var text))
            return range;

        var startOffset = GetOffset(text, range.Start);
        var endOffset = GetOffset(text, range.End);
        if (endOffset < startOffset)
            throw new InvalidDataException("The language server returned an invalid rename range.");

        var selectedText = text[startOffset..endOffset];
        if (string.Equals(selectedText, identifier, StringComparison.OrdinalIgnoreCase))
            return range;

        var matchOffsets = new List<int>();
        var searchOffset = 0;
        while (searchOffset <= selectedText.Length - identifier.Length)
        {
            var candidate = selectedText.IndexOf(
                identifier,
                searchOffset,
                StringComparison.OrdinalIgnoreCase);
            if (candidate < 0)
                break;

            var beforeIsIdentifier = candidate > 0 &&
                                     IsIdentifierCharacter(selectedText[candidate - 1]);
            var afterIndex = candidate + identifier.Length;
            var afterIsIdentifier = afterIndex < selectedText.Length &&
                                    IsIdentifierCharacter(selectedText[afterIndex]);
            if (!beforeIsIdentifier && !afterIsIdentifier)
                matchOffsets.Add(candidate);

            searchOffset = candidate + identifier.Length;
        }

        if (matchOffsets.Count == 0)
        {
            throw new InvalidDataException(
                "The language server returned a rename range that does not contain the symbol.");
        }

        // STruC++ may return the span of an entire call expression for its
        // callee. In `timer(IN := NOT timer.Q, ...)` that range contains two
        // occurrences, but the callee represented by the AST node is the token
        // at the start of the span. The nested member access is returned as a
        // separate reference/edit.
        var matchOffset = matchOffsets.Contains(0)
            ? 0
            : matchOffsets.Count == 1
                ? matchOffsets[0]
                : throw new InvalidDataException(
                    "The language server returned an ambiguous rename range.");

        var identifierStart = startOffset + matchOffset;
        return new StrucppRange(
            GetPosition(text, identifierStart),
            GetPosition(text, identifierStart + identifier.Length));
    }

    private bool TryGetIdentifierAt(
        string filePath,
        int line,
        int character,
        out string? identifier,
        out StrucppRange range)
    {
        identifier = null;
        range = default!;
        if (!_documentTexts.TryGetValue(NormalizePath(filePath), out var text))
            return false;

        var offset = GetOffset(text, new StrucppPosition(line, character));
        if (offset == text.Length ||
            (offset > 0 &&
             !IsIdentifierCharacter(text[offset]) &&
             IsIdentifierCharacter(text[offset - 1])))
            offset--;
        if (offset < 0 || offset >= text.Length || !IsIdentifierCharacter(text[offset]))
            return false;

        var start = offset;
        while (start > 0 && IsIdentifierCharacter(text[start - 1]))
            start--;
        var end = offset + 1;
        while (end < text.Length && IsIdentifierCharacter(text[end]))
            end++;

        identifier = text[start..end];
        range = new StrucppRange(GetPosition(text, start), GetPosition(text, end));
        return true;
    }

    private static int GetOffset(string text, StrucppPosition position)
    {
        if (position.Line < 0 || position.Character < 0)
            throw new InvalidDataException("The language server returned a negative position.");

        var line = 0;
        var lineStart = 0;
        while (line < position.Line)
        {
            var newline = text.IndexOf('\n', lineStart);
            if (newline < 0)
                throw new InvalidDataException(
                    "The language server returned a line outside the document.");
            lineStart = newline + 1;
            line++;
        }

        var lineEnd = text.IndexOf('\n', lineStart);
        if (lineEnd < 0)
            lineEnd = text.Length;
        if (lineEnd > lineStart && text[lineEnd - 1] == '\r')
            lineEnd--;
        if (position.Character > lineEnd - lineStart)
            throw new InvalidDataException(
                "The language server returned a column outside the document.");
        return lineStart + position.Character;
    }

    private static StrucppPosition GetPosition(string text, int offset)
    {
        if (offset < 0 || offset > text.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));

        var line = 0;
        var lineStart = 0;
        for (var index = 0; index < offset; index++)
        {
            if (text[index] != '\n')
                continue;
            line++;
            lineStart = index + 1;
        }

        return new StrucppPosition(line, offset - lineStart);
    }

    private static bool IsIdentifierCharacter(char character) =>
        char.IsLetterOrDigit(character) || character == '_';

    private static bool TryParseRange(JsonObject range, out StrucppRange result)
    {
        if (range["start"] is JsonObject && range["end"] is JsonObject)
        {
            result = ParseRange(range);
            return true;
        }

        result = default!;
        return false;
    }

    private static StrucppRange ParseRange(JsonObject range) =>
        new(ParsePosition((JsonObject)range["start"]!), ParsePosition((JsonObject)range["end"]!));

    private static StrucppPosition ParsePosition(JsonObject position) =>
        new(position["line"]!.GetValue<int>(), position["character"]!.GetValue<int>());

    private static string ToFileUri(string path) =>
        new Uri(Path.GetFullPath(path)).AbsoluteUri;

    private static string ToFilePath(string uri) =>
        Path.GetFullPath(new Uri(uri).LocalPath);

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path);

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
    }
}

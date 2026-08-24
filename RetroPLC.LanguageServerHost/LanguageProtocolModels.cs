// SPDX-License-Identifier: GPL-3.0-or-later
namespace RetroPLC.LanguageServerHost;

public sealed record StrucppPosition(int Line, int Character);

public sealed record StrucppRange(StrucppPosition Start, StrucppPosition End);

public sealed record StrucppDiagnostic(
    StrucppRange Range,
    int Severity,
    string Message,
    string Source,
    string? Code)
{
    public string SeverityName => Severity switch
    {
        1 => "Error",
        2 => "Warning",
        3 => "Information",
        4 => "Hint",
        _ => "Diagnostic"
    };

    public string DisplayText =>
        $"{SeverityName} ({Range.Start.Line + 1},{Range.Start.Character + 1}): {Message}";
}

public sealed record StrucppCompletionItem(
    string Label,
    int Kind,
    string? Detail,
    string? SortText,
    string? InsertText,
    int InsertTextFormat);

public sealed record StrucppDocumentSymbol(
    string Name,
    string? Detail,
    int Kind,
    StrucppRange Range,
    StrucppRange SelectionRange,
    IReadOnlyList<StrucppDocumentSymbol> Children);

public sealed record StrucppPrepareRenameResult(
    StrucppRange Range,
    string Placeholder);

public sealed record StrucppTextEdit(
    StrucppRange Range,
    string NewText);

public sealed record StrucppWorkspaceEdit(
    IReadOnlyDictionary<string, IReadOnlyList<StrucppTextEdit>> Changes);

public sealed record StrucppLocation(
    string FilePath,
    StrucppRange Range);

public sealed class StrucppDiagnosticsEventArgs(
    string filePath,
    IReadOnlyList<StrucppDiagnostic> diagnostics) : EventArgs
{
    public string FilePath { get; } = filePath;

    public IReadOnlyList<StrucppDiagnostic> Diagnostics { get; } = diagnostics;
}

public sealed class StrucppLanguageServerErrorEventArgs(string message) : EventArgs
{
    public string Message { get; } = message;
}

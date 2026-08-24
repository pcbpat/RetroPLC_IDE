// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Editing;
using AvaloniaEdit.TextMate;
using Classic.CommonControls.Dialogs;
using RetroPLC.LanguageServerHost;
using RetroPLC.Shell.ViewModels;
using RetroPLC.Shell.ViewModels.Docking;
using RetroPLC.Theme;
using TextMateSharp.Grammars;

namespace RetroPLC.Shell.Views.Docking;

public partial class DocumentView : UserControl
{
    private readonly RegistryOptions _registryOptions;
    private readonly TextMate.Installation _textMateInstallation;
    private readonly HashSet<string> _loadedGrammarScopes = [];
    private string? _activeGrammarScope;
    private CompletionWindow? _completionWindow;
    private CancellationTokenSource? _completionRequest;
    private CancellationTokenSource? _renameRequest;
    private CancellationTokenSource? _navigationRequest;
    private CancellationTokenSource? _formatRequest;
    private DocumentViewModel? _renameDocument;

    private const string StructuredTextScope = "source.iec61131-st";
    private const string CppScope = "source.cpp";

    public DocumentView()
    {
        InitializeComponent();

        _registryOptions = new RegistryOptions(IsDarkMode()
            ? ThemeName.DarkPlus
            : ThemeName.LightPlus);
        _textMateInstallation = Editor.InstallTextMate(_registryOptions);

        DataContextChanged += DocumentViewOnDataContextChanged;
        AttachedToVisualTree += DocumentViewOnAttachedToVisualTree;
        Editor.TextArea.TextEntered += EditorOnTextEntered;
        Editor.TextArea.KeyDown += EditorOnKeyDown;
        Editor.TextArea.PointerPressed += EditorOnPointerPressed;
        DetachedFromVisualTree += DocumentViewOnDetachedFromVisualTree;
        ApplySyntaxHighlighting();
    }

    private void DocumentViewOnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (Application.Current is { } application)
            application.ActualThemeVariantChanged += ApplicationOnActualThemeVariantChanged;

        Dispatcher.UIThread.Post(ApplyDocumentGrammar, DispatcherPriority.Loaded);
    }

    private void DocumentViewOnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (Application.Current is { } application)
            application.ActualThemeVariantChanged -= ApplicationOnActualThemeVariantChanged;

        CancelCompletion();
        CancelRename();
        CancelNavigationRequest();
        CancelFormat();
    }

    private void ApplicationOnActualThemeVariantChanged(object? sender, EventArgs e) =>
        ApplySyntaxHighlighting();

    private void DocumentViewOnDataContextChanged(object? sender, EventArgs e)
    {
        if (_renameDocument is not null)
        {
            _renameDocument.RenameRequested -= OnRenameRequested;
            _renameDocument.GoToDefinitionRequested -= OnGoToDefinitionRequested;
            _renameDocument.FindReferencesRequested -= OnFindReferencesRequested;
            _renameDocument.FormatRequested -= OnFormatRequested;
            _renameDocument.NavigationRequested -= OnNavigationRequested;
        }
        _renameDocument = DataContext as DocumentViewModel;
        if (_renameDocument is not null)
        {
            _renameDocument.RenameRequested += OnRenameRequested;
            _renameDocument.GoToDefinitionRequested += OnGoToDefinitionRequested;
            _renameDocument.FindReferencesRequested += OnFindReferencesRequested;
            _renameDocument.FormatRequested += OnFormatRequested;
            _renameDocument.NavigationRequested += OnNavigationRequested;
        }

        Dispatcher.UIThread.Post(ApplyDocumentGrammar, DispatcherPriority.Loaded);
        Dispatcher.UIThread.Post(NavigateToPendingLocation, DispatcherPriority.Loaded);
    }

    private void ApplyDocumentGrammar()
    {
        if (DataContext is not DocumentViewModel document)
            return;

        var isCpp = IsCppExtension(Path.GetExtension(document.FilePath));
        SetDocumentGrammar(
            isCpp ? CppScope : StructuredTextScope,
            isCpp ? GetCppGrammarPath() : GetStructuredTextGrammarPath());

        // The XAML binding normally owns this assignment. Keep an explicit
        // fallback after grammar loading because the dock's content presenter
        // can establish its inherited DataContext after the binding pass.
        if (!ReferenceEquals(Editor.Document, document.Document))
            Editor.Document = document.Document;
    }

    private void SetDocumentGrammar(string scope, string grammarPath)
    {
        if (_activeGrammarScope == scope)
            return;

        if (_loadedGrammarScopes.Contains(scope))
        {
            _textMateInstallation.SetGrammar(scope);
            _activeGrammarScope = scope;
            return;
        }

        try
        {
            _textMateInstallation.SetGrammarFile(grammarPath);
        }
        catch (ArgumentException exception) when (
            exception.Message.Contains("same key", StringComparison.OrdinalIgnoreCase))
        {
            // The registry can already contain a bundled grammar or a dependency
            // (for example source.c). Select the requested scope without adding it again.
            _textMateInstallation.SetGrammar(scope);
        }

        _loadedGrammarScopes.Add(scope);
        _activeGrammarScope = scope;
    }

    private static bool IsCppExtension(string extension) =>
        extension.Equals(".cpp", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".hpp", StringComparison.OrdinalIgnoreCase);

    private static string GetStructuredTextGrammarPath() => Path.Combine(
        AppContext.BaseDirectory,
        "Assets",
        "Syntax",
        "st.tmLanguage.json");

    private static string GetCppGrammarPath() => Path.Combine(
        AppContext.BaseDirectory,
        "Assets",
        "Syntax",
        "cpp.tmLanguage.json");

    private void ApplySyntaxHighlighting()
    {
        _textMateInstallation.SetTheme(_registryOptions.LoadTheme(IsDarkMode()
            ? ThemeName.DarkPlus
            : ThemeName.LightPlus));
    }

    private static bool IsDarkMode() =>
        Application.Current?.ActualThemeVariant == ThemeVariants.Dark;

    private void Save_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DocumentViewModel document)
        {
            document.Save();
        }

        Editor.Focus();
    }

    private void Cut_OnClick(object? sender, RoutedEventArgs e)
    {
        Editor.Cut();
        Editor.Focus();
    }

    private void Copy_OnClick(object? sender, RoutedEventArgs e)
    {
        Editor.Copy();
        Editor.Focus();
    }

    private void Paste_OnClick(object? sender, RoutedEventArgs e)
    {
        Editor.Paste();
        Editor.Focus();
    }

    private void Find_OnClick(object? sender, RoutedEventArgs e) =>
        Editor.SearchPanel.Open();

    private void Undo_OnClick(object? sender, RoutedEventArgs e)
    {
        Editor.Undo();
        Editor.Focus();
    }

    private void Redo_OnClick(object? sender, RoutedEventArgs e)
    {
        Editor.Redo();
        Editor.Focus();
    }

    private void EditorOnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (DataContext is not DocumentViewModel document ||
            IsCppExtension(Path.GetExtension(document.FilePath)) ||
            string.IsNullOrEmpty(e.Text))
            return;

        var triggerCharacter = e.Text is "." or ":" ? e.Text : null;
        if (triggerCharacter is not null)
        {
            _completionWindow?.Hide();
            _ = RequestCompletionAsync(triggerCharacter, delay: false);
            return;
        }

        if (char.IsWhiteSpace(e.Text[0]) && IsAfterTypeAnnotationColon())
        {
            _completionWindow?.Hide();
            _ = RequestCompletionAsync(":", delay: false);
            return;
        }

        if (_completionWindow is not null)
            return;

        var character = e.Text[0];
        if (char.IsLetterOrDigit(character) || character == '_')
            _ = RequestCompletionAsync(triggerCharacter: null, delay: true);
    }

    private void EditorOnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(Editor.TextArea).Properties.PointerUpdateKind !=
            PointerUpdateKind.RightButtonPressed)
            return;

        var textView = Editor.TextArea.TextView;
        if (textView.GetPosition(e.GetPosition(textView)) is { } position)
            Editor.TextArea.Caret.Position = position;
    }

    private void EditorOnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F12 &&
            DataContext is DocumentViewModel navigationDocument &&
            !IsCppExtension(Path.GetExtension(navigationDocument.FilePath)))
        {
            e.Handled = true;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                _ = FindReferencesAsync();
            else
                _ = GoToDefinitionAsync();
            return;
        }

        if (e.Key == Key.F2 &&
            DataContext is DocumentViewModel renameDocument &&
            !IsCppExtension(Path.GetExtension(renameDocument.FilePath)))
        {
            e.Handled = true;
            _ = RenameSymbolAsync();
            return;
        }

        if (e.Key == Key.F &&
            e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Alt) &&
            DataContext is DocumentViewModel formatDocument &&
            !IsCppExtension(Path.GetExtension(formatDocument.FilePath)))
        {
            e.Handled = true;
            _ = FormatDocumentAsync();
            return;
        }

        if (e.Key != Key.Space ||
            !e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
            DataContext is not DocumentViewModel document ||
            IsCppExtension(Path.GetExtension(document.FilePath)))
            return;

        e.Handled = true;
        _completionWindow?.Hide();
        _ = RequestCompletionAsync(triggerCharacter: null, delay: false);
    }

    private void OnRenameRequested() => _ = RenameSymbolAsync();

    private void OnGoToDefinitionRequested() => _ = GoToDefinitionAsync();

    private void OnFindReferencesRequested() => _ = FindReferencesAsync();

    private void OnFormatRequested() => _ = FormatDocumentAsync();

    private void OnNavigationRequested() => NavigateToPendingLocation();

    private void GoToDefinition_OnClick(object? sender, RoutedEventArgs e) =>
        _ = GoToDefinitionAsync();

    private void FindReferences_OnClick(object? sender, RoutedEventArgs e) =>
        _ = FindReferencesAsync();

    private void Rename_OnClick(object? sender, RoutedEventArgs e) =>
        _ = RenameSymbolAsync();

    private void FormatDocument_OnClick(object? sender, RoutedEventArgs e) =>
        _ = FormatDocumentAsync();

    private async Task FormatDocumentAsync()
    {
        CancelCompletion();
        CancelFormat();
        _formatRequest = new CancellationTokenSource();
        var cancellationToken = _formatRequest.Token;

        try
        {
            if (DataContext is not DocumentViewModel document ||
                Editor.Document is null ||
                IsCppExtension(Path.GetExtension(document.FilePath)))
                return;

            var version = document.Version;
            var edits = await document.FormatAsync(
                tabSize: 4,
                insertSpaces: true,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(DataContext, document) ||
                document.Version != version ||
                edits.Count == 0)
                return;

            var replacements = edits
                .Select(edit =>
                {
                    var startOffset = GetFormattingOffset(edit.Range.Start);
                    var endOffset = GetFormattingOffset(edit.Range.End);
                    if (endOffset < startOffset)
                        throw new InvalidDataException(
                            "The language server returned an invalid formatting range.");
                    return new
                    {
                        StartOffset = startOffset,
                        Length = endOffset - startOffset,
                        edit.NewText
                    };
                })
                .OrderByDescending(edit => edit.StartOffset)
                .ToArray();

            Editor.Document.BeginUpdate();
            try
            {
                foreach (var replacement in replacements)
                {
                    Editor.Document.Replace(
                        replacement.StartOffset,
                        replacement.Length,
                        replacement.NewText);
                }
            }
            finally
            {
                Editor.Document.EndUpdate();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await ShowLanguageFeatureMessageAsync(
                exception.Message,
                "Format Document",
                MessageBoxIcon.Error);
        }
        finally
        {
            if (_formatRequest?.Token == cancellationToken)
            {
                _formatRequest.Dispose();
                _formatRequest = null;
            }
            Editor.Focus();
        }
    }

    private int GetFormattingOffset(StrucppPosition position)
    {
        if (Editor.Document is null)
            throw new InvalidOperationException("The document is not available.");

        var lineNumber = position.Line + 1;
        if (lineNumber < 1 || lineNumber > Editor.Document.LineCount)
            throw new InvalidDataException(
                "The language server returned a formatting line outside the document.");
        var line = Editor.Document.GetLineByNumber(lineNumber);
        if (position.Character < 0 || position.Character > line.Length)
            throw new InvalidDataException(
                "The language server returned a formatting column outside the document.");
        return line.Offset + position.Character;
    }

    private void CancelFormat()
    {
        _formatRequest?.Cancel();
        _formatRequest?.Dispose();
        _formatRequest = null;
    }

    private async Task GoToDefinitionAsync()
    {
        CancelNavigationRequest();
        _navigationRequest = new CancellationTokenSource();
        var cancellationToken = _navigationRequest.Token;
        try
        {
            if (!TryGetStructuredTextCaret(out var document, out var line, out var character))
                return;

            if (!await document.GoToDefinitionAsync(
                    line, character, cancellationToken))
            {
                await ShowLanguageFeatureMessageAsync(
                    "No definition was found at the caret.",
                    "Go to Definition",
                    MessageBoxIcon.Information);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await ShowLanguageFeatureMessageAsync(
                exception.Message,
                "Go to Definition",
                MessageBoxIcon.Error);
        }
        finally
        {
            CompleteNavigationRequest(cancellationToken);
        }
    }

    private async Task FindReferencesAsync()
    {
        CancelNavigationRequest();
        _navigationRequest = new CancellationTokenSource();
        var cancellationToken = _navigationRequest.Token;
        try
        {
            if (!TryGetStructuredTextCaret(out var document, out var line, out var character))
                return;
            await document.FindReferencesAsync(line, character, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await ShowLanguageFeatureMessageAsync(
                exception.Message,
                "Find All References",
                MessageBoxIcon.Error);
        }
        finally
        {
            CompleteNavigationRequest(cancellationToken);
        }
    }

    private bool TryGetStructuredTextCaret(
        out DocumentViewModel document,
        out int line,
        out int character)
    {
        document = null!;
        line = 0;
        character = 0;
        if (DataContext is not DocumentViewModel current ||
            Editor.Document is null ||
            IsCppExtension(Path.GetExtension(current.FilePath)))
            return false;

        var location = Editor.Document.GetLocation(Editor.TextArea.Caret.Offset);
        document = current;
        line = location.Line - 1;
        character = location.Column - 1;
        return true;
    }

    private async Task ShowLanguageFeatureMessageAsync(
        string message,
        string title,
        MessageBoxIcon icon)
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            await MessageBox.ShowDialog(
                owner,
                message,
                title,
                MessageBoxButtons.Ok,
                icon);
        }
    }

    private void CancelNavigationRequest()
    {
        _navigationRequest?.Cancel();
        _navigationRequest?.Dispose();
        _navigationRequest = null;
    }

    private void CompleteNavigationRequest(CancellationToken cancellationToken)
    {
        if (_navigationRequest?.Token != cancellationToken)
            return;
        _navigationRequest.Dispose();
        _navigationRequest = null;
    }

    private void NavigateToPendingLocation()
    {
        if (DataContext is not DocumentViewModel document ||
            Editor.Document is null ||
            !document.TryConsumeNavigation(out var range))
            return;

        var startOffset = GetNavigationOffset(range.Start);
        var endOffset = GetNavigationOffset(range.End);
        if (endOffset < startOffset)
            endOffset = startOffset;

        Editor.TextArea.Selection = Selection.Create(
            Editor.TextArea,
            startOffset,
            endOffset);
        Editor.TextArea.Caret.Offset = endOffset;
        Editor.ScrollTo(range.Start.Line + 1, range.Start.Character + 1);
        Editor.Focus();
    }

    private int GetNavigationOffset(StrucppPosition position)
    {
        var lineNumber = Math.Clamp(position.Line + 1, 1, Editor.Document.LineCount);
        var documentLine = Editor.Document.GetLineByNumber(lineNumber);
        var column = Math.Clamp(position.Character, 0, documentLine.Length);
        return documentLine.Offset + column;
    }

    private async Task RenameSymbolAsync()
    {
        CancelCompletion();
        CancelRename();
        _renameRequest = new CancellationTokenSource();
        var cancellationToken = _renameRequest.Token;

        try
        {
            if (DataContext is not DocumentViewModel document ||
                Editor.Document is null ||
                IsCppExtension(Path.GetExtension(document.FilePath)))
                return;

            var location = Editor.Document.GetLocation(Editor.TextArea.Caret.Offset);
            var line = location.Line - 1;
            var character = location.Column - 1;
            var preparation = await document.PrepareRenameAsync(
                line,
                character,
                cancellationToken);
            if (preparation is null || TopLevel.GetTopLevel(this) is not Window owner)
            {
                await ShowRenameMessageAsync(
                    "The caret is not on a symbol that can be renamed.",
                    MessageBoxIcon.Information);
                return;
            }

            var dialog = new RenameSymbolWindow(preparation.Placeholder);
            if (!await dialog.ShowDialog<bool>(owner) ||
                dialog.Result is not { } newName ||
                string.Equals(newName, preparation.Placeholder, StringComparison.Ordinal))
                return;

            var editCount = await document.RenameAsync(
                line,
                character,
                newName,
                cancellationToken);
            if (editCount == 0)
            {
                await ShowRenameMessageAsync(
                    "No matching declarations or references were found.",
                    MessageBoxIcon.Information);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await ShowRenameMessageAsync(exception.Message, MessageBoxIcon.Error);
        }
        finally
        {
            if (_renameRequest?.Token == cancellationToken)
            {
                _renameRequest.Dispose();
                _renameRequest = null;
            }
            Editor.Focus();
        }
    }

    private async Task ShowRenameMessageAsync(string message, MessageBoxIcon icon)
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            await MessageBox.ShowDialog(
                owner,
                message,
                "Rename Symbol",
                MessageBoxButtons.Ok,
                icon);
        }
    }

    private void CancelRename()
    {
        _renameRequest?.Cancel();
        _renameRequest?.Dispose();
        _renameRequest = null;
    }

    private async Task RequestCompletionAsync(string? triggerCharacter, bool delay)
    {
        _completionRequest?.Cancel();
        _completionRequest?.Dispose();
        _completionRequest = new CancellationTokenSource();
        var cancellationToken = _completionRequest.Token;

        try
        {
            if (delay)
                await Task.Delay(140, cancellationToken);

            if (DataContext is not DocumentViewModel document ||
                Editor.Document is null)
                return;

            var caretOffset = Editor.TextArea.Caret.Offset;
            var version = document.Version;
            var location = Editor.Document.GetLocation(caretOffset);
            var completions = await document.GetCompletionsAsync(
                location.Line - 1,
                location.Column - 1,
                triggerCharacter,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(DataContext, document) ||
                document.Version != version ||
                Editor.TextArea.Caret.Offset != caretOffset ||
                completions.Count == 0)
                return;

            var startOffset = triggerCharacter is null
                ? FindIdentifierStart(Editor.Document.Text, caretOffset)
                : caretOffset;
            var prefix = Editor.Document.GetText(startOffset, caretOffset - startOffset);
            var window = new CompletionWindow(Editor.TextArea)
            {
                StartOffset = startOffset,
                EndOffset = caretOffset,
                CloseWhenCaretAtBeginning = triggerCharacter is null
            };
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_completionWindow, window))
                    _completionWindow = null;
            };

            foreach (var completion in completions
                         .OrderBy(item => item.SortText, StringComparer.Ordinal)
                         .ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase))
            {
                window.CompletionList.CompletionData.Add(
                    new StrucppCompletionData(completion));
            }

            _completionWindow = window;
            window.CompletionList.SelectItem(prefix);
            window.Show();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void CancelCompletion()
    {
        _completionRequest?.Cancel();
        _completionRequest?.Dispose();
        _completionRequest = null;
        _completionWindow?.Hide();
        _completionWindow = null;
    }

    private static int FindIdentifierStart(string text, int caretOffset)
    {
        var offset = caretOffset;
        while (offset > 0)
        {
            var character = text[offset - 1];
            if (!char.IsLetterOrDigit(character) && character != '_')
                break;
            offset--;
        }
        return offset;
    }

    private bool IsAfterTypeAnnotationColon()
    {
        var document = Editor.Document;
        if (document is null)
            return false;

        var caretOffset = Editor.TextArea.Caret.Offset;
        var line = document.GetLineByOffset(caretOffset);
        var prefix = document.GetText(line.Offset, caretOffset - line.Offset);
        return prefix.TrimEnd().EndsWith(':');
    }

}

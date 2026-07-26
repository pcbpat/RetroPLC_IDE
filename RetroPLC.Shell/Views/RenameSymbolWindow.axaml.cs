using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RetroPLC.Shell.Views;

public partial class RenameSymbolWindow : Window
{
    private static readonly Regex IdentifierPattern =
        new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

    private static readonly HashSet<string> Keywords = new(
    [
        "IF", "THEN", "ELSE", "ELSIF", "END_IF", "WHILE", "DO", "END_WHILE",
        "FOR", "TO", "BY", "END_FOR", "REPEAT", "UNTIL", "END_REPEAT",
        "CASE", "OF", "END_CASE", "VAR", "VAR_INPUT", "VAR_OUTPUT", "VAR_IN_OUT",
        "VAR_GLOBAL", "VAR_TEMP", "VAR_EXTERNAL", "CONSTANT", "RETAIN", "END_VAR",
        "PROGRAM", "END_PROGRAM", "FUNCTION", "END_FUNCTION", "FUNCTION_BLOCK",
        "END_FUNCTION_BLOCK", "METHOD", "END_METHOD", "TYPE", "END_TYPE",
        "STRUCT", "END_STRUCT", "ARRAY", "STRING", "WSTRING", "TRUE", "FALSE",
        "AND", "OR", "XOR", "NOT", "MOD", "RETURN", "EXIT", "CONTINUE",
        "BOOL", "BYTE", "WORD", "DWORD", "LWORD", "SINT", "INT", "DINT", "LINT",
        "USINT", "UINT", "UDINT", "ULINT", "REAL", "LREAL", "TIME", "DATE",
        "TIME_OF_DAY", "DATE_AND_TIME", "TOD", "DT"
    ], StringComparer.OrdinalIgnoreCase);

    public RenameSymbolWindow() : this(string.Empty)
    {
    }

    public RenameSymbolWindow(string currentName)
    {
        InitializeComponent();
        NameBox.Text = currentName;
        Opened += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
        ValidateName();
    }

    public string? Result { get; private set; }

    private void NameBox_OnTextChanged(object? sender, TextChangedEventArgs e) =>
        ValidateName();

    private void ValidateName()
    {
        var name = NameBox.Text?.Trim() ?? string.Empty;
        var isIdentifier = IdentifierPattern.IsMatch(name);
        var isKeyword = Keywords.Contains(name);
        RenameButton.IsEnabled = isIdentifier && !isKeyword;
        ValidationText.Text = !isIdentifier && name.Length > 0
            ? "Use a valid IEC identifier."
            : isKeyword
                ? "IEC keywords cannot be used as symbol names."
                : string.Empty;
    }

    private void Rename_OnClick(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? string.Empty;
        if (!IdentifierPattern.IsMatch(name) || Keywords.Contains(name))
        {
            ValidateName();
            return;
        }

        Result = name;
        Close(true);
    }

    private void Cancel_OnClick(object? sender, RoutedEventArgs e) => Close(false);
}

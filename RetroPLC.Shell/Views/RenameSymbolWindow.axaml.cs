// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RetroPLC.Shell.Language;

namespace RetroPLC.Shell.Views;

public partial class RenameSymbolWindow : Window
{
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
        var isIdentifier = IecIdentifier.IsLexicallyValid(name);
        var isKeyword = IecIdentifier.IsKeyword(name);
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
        if (!IecIdentifier.IsValid(name))
        {
            ValidateName();
            return;
        }

        Result = name;
        Close(true);
    }

    private void Cancel_OnClick(object? sender, RoutedEventArgs e) => Close(false);
}

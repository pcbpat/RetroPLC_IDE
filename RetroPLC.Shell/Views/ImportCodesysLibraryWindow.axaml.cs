// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RetroPLC.Shell.Models;

namespace RetroPLC.Shell.Views;

public partial class ImportCodesysLibraryWindow : Window
{
    private static readonly Regex LibraryNamePattern =
        new("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant);

    public ImportCodesysLibraryWindow()
    {
        InitializeComponent();
    }

    public ImportCodesysLibraryWindow(string sourcePath) : this()
    {
        SourceBox.Text = sourcePath;
        NameBox.Text = MakeDefaultName(Path.GetFileNameWithoutExtension(sourcePath));
        Opened += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
        ValidateInput();
    }

    public CodesysLibraryImport? Result { get; private set; }

    private void Input_OnChanged(object? sender, TextChangedEventArgs e) => ValidateInput();

    private void ValidateInput()
    {
        if (ImportButton is null)
            return;

        var name = NameBox.Text?.Trim() ?? string.Empty;
        var version = VersionBox.Text?.Trim() ?? string.Empty;
        ImportButton.IsEnabled = LibraryNamePattern.IsMatch(name) && version.Length > 0;
        ValidationText.Text = name.Length > 0 && !LibraryNamePattern.IsMatch(name)
            ? "Use letters, digits, '.', '_' or '-'."
            : version.Length == 0
                ? "Enter a library version."
                : string.Empty;
    }

    private void Import_OnClick(object? sender, RoutedEventArgs e)
    {
        ValidateInput();
        if (!ImportButton.IsEnabled)
            return;

        Result = new CodesysLibraryImport(
            SourceBox.Text!,
            NameBox.Text!.Trim(),
            VersionBox.Text!.Trim(),
            string.IsNullOrWhiteSpace(NamespaceBox.Text) ? null : NamespaceBox.Text.Trim(),
            IncludeSourceBox.IsChecked == true);
        Close(true);
    }

    private void Cancel_OnClick(object? sender, RoutedEventArgs e) => Close(false);

    private static string MakeDefaultName(string value)
    {
        var name = Regex.Replace(value, "[^A-Za-z0-9._-]+", "-").Trim('-', '.');
        return string.IsNullOrEmpty(name) ? "codesys-library" : name;
    }
}

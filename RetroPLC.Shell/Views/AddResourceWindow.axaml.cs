// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Interactivity;
using RetroPLC.Shell.Language;
using RetroPLC.Shell.Models;

namespace RetroPLC.Shell.Views;

public partial class AddResourceWindow : Window
{
    public AddResourceWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
        Validate();
    }

    public NewResourceDefinition? Result { get; private set; }

    private void NameBox_OnTextChanged(object? sender, TextChangedEventArgs e) => Validate();

    private void Validate()
    {
        var name = NameBox.Text?.Trim() ?? string.Empty;
        var processor = ProcessorBox.Text?.Trim() ?? string.Empty;
        var isValid = IecIdentifier.IsValid(name) && IecIdentifier.IsValid(processor);
        AddButton.IsEnabled = isValid;
        ValidationText.Text = isValid || name.Length == 0
            ? string.Empty
            : "Use valid IEC identifiers.";
    }

    private void Add_OnClick(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? string.Empty;
        var processor = ProcessorBox.Text?.Trim() ?? string.Empty;
        if (!IecIdentifier.IsValid(name) || !IecIdentifier.IsValid(processor))
        {
            Validate();
            return;
        }

        Result = new NewResourceDefinition(name, processor);
        Close(true);
    }

    private void Cancel_OnClick(object? sender, RoutedEventArgs e) => Close(false);
}

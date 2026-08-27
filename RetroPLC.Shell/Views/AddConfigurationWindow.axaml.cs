// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Interactivity;
using RetroPLC.Shell.Language;

namespace RetroPLC.Shell.Views;

public partial class AddConfigurationWindow : Window
{
    public AddConfigurationWindow()
    {
        InitializeComponent();
        Validate();
    }

    public string? ResultName { get; private set; }

    private void NameBox_OnTextChanged(object? sender, TextChangedEventArgs e) => Validate();

    private void Validate()
    {
        if (NameBox is null || AddButton is null || ValidationText is null)
            return;

        var name = NameBox.Text?.Trim() ?? string.Empty;
        var isValid = IecIdentifier.IsValid(name);
        AddButton.IsEnabled = isValid;
        ValidationText.Text = isValid || name.Length == 0
            ? string.Empty
            : "Use a valid IEC identifier.";
    }

    private void Add_OnClick(object? sender, RoutedEventArgs e)
    {
        ResultName = NameBox.Text?.Trim();
        Close(true);
    }

    private void Cancel_OnClick(object? sender, RoutedEventArgs e)
    {
        ResultName = null;
        Close(false);
    }
}

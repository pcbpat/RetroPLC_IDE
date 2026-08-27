// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RetroPLC.Shell.Language;
using RetroPLC.Shell.Models;

namespace RetroPLC.Shell.Views;

public partial class AddDataTypeWindow : Window
{
    private static readonly string[] DefaultDefinitions =
    [
        "Value : INT;",
        "RED, YELLOW, GREEN",
        "INT",
        "INT(0..100)",
        "ARRAY[0..9] OF INT"
    ];

    private static readonly string[] Labels =
    [
        "Fields:",
        "Members:",
        "Base type:",
        "Type and range:",
        "Array type:"
    ];

    private static readonly string[] HelpTexts =
    [
        "Enter one or more field declarations, for example: Value : INT;",
        "Enter comma-separated members. Explicit values such as Idle := 0 are supported.",
        "Enter an existing elementary or derived type, for example: DINT",
        "Enter the base type and bounds, for example: INT(-10..10)",
        "Enter the complete array expression, for example: ARRAY[1..10] OF REAL"
    ];

    public AddDataTypeWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
        UpdateKind(resetDefinition: true);
    }

    public NewDataTypeDefinition? Result { get; private set; }

    private void KindBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        UpdateKind(resetDefinition: true);

    private void Input_OnChanged(object? sender, TextChangedEventArgs e) => Validate();

    private void UpdateKind(bool resetDefinition)
    {
        if (DefinitionBox is null || DefinitionLabel is null || DefinitionHelp is null)
            return;

        var index = Math.Clamp(KindBox.SelectedIndex, 0, DefaultDefinitions.Length - 1);
        DefinitionLabel.Text = Labels[index];
        DefinitionHelp.Text = HelpTexts[index];
        if (resetDefinition)
            DefinitionBox.Text = DefaultDefinitions[index];
        Validate();
    }

    private void Validate()
    {
        if (AddButton is null || ValidationText is null)
            return;

        var name = NameBox.Text?.Trim() ?? string.Empty;
        var definition = DefinitionBox.Text?.Trim() ?? string.Empty;
        var validName = IecIdentifier.IsValid(name);
        AddButton.IsEnabled = validName && definition.Length > 0;
        ValidationText.Text = !validName && name.Length > 0
            ? "Use a valid IEC identifier."
            : definition.Length == 0
                ? "Enter a type definition."
                : string.Empty;
    }

    private void Add_OnClick(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? string.Empty;
        var definition = DefinitionBox.Text?.Trim() ?? string.Empty;
        if (!IecIdentifier.IsValid(name) || definition.Length == 0)
        {
            Validate();
            return;
        }

        Result = new NewDataTypeDefinition(
            name,
            (DataTypeKind)Math.Clamp(KindBox.SelectedIndex, 0, 4),
            definition);
        Close(true);
    }

    private void Cancel_OnClick(object? sender, RoutedEventArgs e) => Close(false);
}

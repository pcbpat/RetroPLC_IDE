// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RetroPLC.Shell.Language;
using RetroPLC.Shell.Models;

namespace RetroPLC.Shell.Views;

public partial class AddPouWindow : Window
{
    public AddPouWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
        UpdateOptions();
        ValidateName();
    }

    public NewPouDefinition? Result { get; private set; }

    private void NameBox_OnTextChanged(object? sender, TextChangedEventArgs e) => ValidateName();

    private void PouType_OnChecked(object? sender, RoutedEventArgs e) => UpdateOptions();

    private void PouOption_OnChanged(object? sender, RoutedEventArgs e)
    {
        if (sender == FinalCheckBox && FinalCheckBox.IsChecked == true)
            AbstractCheckBox.IsChecked = false;
        else if (sender == AbstractCheckBox && AbstractCheckBox.IsChecked == true)
            FinalCheckBox.IsChecked = false;

        UpdateOptions();
    }

    private void UpdateOptions()
    {
        var isFunctionBlock = FunctionBlockButton.IsChecked == true;
        var isFunction = FunctionButton.IsChecked == true;

        ExtendsCheckBox.IsEnabled = isFunctionBlock;
        ImplementsCheckBox.IsEnabled = isFunctionBlock;
        FinalCheckBox.IsEnabled = isFunctionBlock;
        AbstractCheckBox.IsEnabled = isFunctionBlock;
        ExtendsBox.IsEnabled = isFunctionBlock && ExtendsCheckBox.IsChecked == true;
        ImplementsBox.IsEnabled = isFunctionBlock && ImplementsCheckBox.IsChecked == true;
        ReturnTypeBox.IsEnabled = isFunction;
    }

    private void ValidateName()
    {
        var name = NameBox.Text?.Trim() ?? string.Empty;
        var isValid = IecIdentifier.IsValid(name);
        AddButton.IsEnabled = isValid;
        ValidationText.Text = isValid || name.Length == 0
            ? string.Empty
            : "Use a valid IEC identifier.";
    }

    private void Add_OnClick(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? string.Empty;
        if (!IecIdentifier.IsValid(name))
        {
            ValidateName();
            return;
        }

        var kind = FunctionBlockButton.IsChecked == true
            ? PouKind.FunctionBlock
            : FunctionButton.IsChecked == true
                ? PouKind.Function
                : PouKind.Program;
        var returnType = (ReturnTypeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "BOOL";

        Result = new NewPouDefinition(
            name,
            kind,
            returnType,
            GetOptionalIdentifier(ExtendsCheckBox, ExtendsBox),
            GetOptionalIdentifier(ImplementsCheckBox, ImplementsBox),
            FinalCheckBox.IsChecked == true,
            AbstractCheckBox.IsChecked == true);
        Close(true);
    }

    private static string? GetOptionalIdentifier(CheckBox option, TextBox value) =>
        option.IsChecked == true && !string.IsNullOrWhiteSpace(value.Text)
            ? value.Text.Trim()
            : null;

    private void Cancel_OnClick(object? sender, RoutedEventArgs e) => Close(false);
}

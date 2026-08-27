// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RetroPLC.Shell.Language;
using RetroPLC.Shell.Models;

namespace RetroPLC.Shell.Views;

public partial class AddTaskWindow : Window
{
    public AddTaskWindow()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
        UpdateTrigger();
        Validate();
    }

    public NewTaskDefinition? Result { get; private set; }

    private void NameBox_OnTextChanged(object? sender, TextChangedEventArgs e) => Validate();

    private void Trigger_OnChecked(object? sender, RoutedEventArgs e) => UpdateTrigger();

    private void UpdateTrigger()
    {
        var isCyclic = CyclicButton.IsChecked == true;
        IntervalBox.IsEnabled = isCyclic;
        EventExpressionBox.IsEnabled = !isCyclic;
    }

    private void Validate()
    {
        var name = NameBox.Text?.Trim() ?? string.Empty;
        var interval = IntervalBox.Text?.Trim() ?? string.Empty;
        var eventExpression = EventExpressionBox.Text?.Trim() ?? string.Empty;
        var isCyclic = CyclicButton.IsChecked == true;

        var validName = IecIdentifier.IsValid(name);
        var validSchedule = isCyclic
            ? !string.IsNullOrWhiteSpace(interval)
            : !string.IsNullOrWhiteSpace(eventExpression);
        var validPriority = int.TryParse(PriorityBox.Text, out var priority) && priority >= 0;

        AddButton.IsEnabled = validName && validSchedule && validPriority;
        ValidationText.Text = !validName && name.Length > 0
            ? "Use a valid IEC identifier."
            : !validSchedule
                ? isCyclic
                    ? "A cyclic task requires an interval."
                    : "An interrupt task requires a SINGLE expression."
                : !validPriority
                    ? "Priority must be a non-negative number."
                    : string.Empty;
    }

    private void Add_OnClick(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? string.Empty;
        var interval = IntervalBox.Text?.Trim() ?? string.Empty;
        var eventExpression = EventExpressionBox.Text?.Trim() ?? string.Empty;
        if (!int.TryParse(PriorityBox.Text, out var priority))
            priority = 0;

        var isCyclic = CyclicButton.IsChecked == true;
        var trigger = isCyclic ? "Cyclic" : "Interrupt";
        if (isCyclic && string.IsNullOrWhiteSpace(interval))
            interval = "T#20ms";
        if (!isCyclic && string.IsNullOrWhiteSpace(eventExpression))
            eventExpression = "TRUE";

        if (!IecIdentifier.IsValid(name))
        {
            Validate();
            return;
        }

        Result = new NewTaskDefinition(name, trigger, interval, eventExpression, priority);
        Close(true);
    }

    private void Cancel_OnClick(object? sender, RoutedEventArgs e) => Close(false);
}

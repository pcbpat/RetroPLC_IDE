// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace RetroPLC.Shell.Views;

public partial class NewProjectWindow : Window
{
    private static readonly Dictionary<string, string> Descriptions = new()
    {
        ["EmptyProject"] = "An empty PLC project with no predefined program objects."
    };

    public NewProjectWindow()
    {
        InitializeComponent();

        LocationBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "RetroPLC");

        if (CategoryTree.Items[0] is TreeViewItem projects)
            projects.IsSelected = true;
        TemplateList.SelectedItem = FindTemplate("EmptyProject");
    }

    public NewProjectResult? Result { get; private set; }

    private ListBoxItem? FindTemplate(string tag)
    {
        foreach (var item in TemplateList.Items)
        {
            if (item is ListBoxItem template && Equals(template.Tag, tag))
                return template;
        }

        return null;
    }

    private void TemplateList_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (TemplateList.SelectedItem is not ListBoxItem template || template.Tag is not string key)
            return;

        DescriptionText.Text = Descriptions[key];
        ProjectNameBox.Text = "Project1";
    }

    private async void Browse_OnClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose project location",
            AllowMultiple = false
        });

        if (folders.Count == 1 && folders[0].TryGetLocalPath() is { } path)
            LocationBox.Text = path;
    }

    private void TemplateList_OnDoubleTapped(object? sender, RoutedEventArgs e) => CreateProject();

    private void Create_OnClick(object? sender, RoutedEventArgs e) => CreateProject();

    private void CreateProject()
    {
        if (TemplateList.SelectedItem is not ListBoxItem template ||
            template.Tag is not string templateKey ||
            string.IsNullOrWhiteSpace(ProjectNameBox.Text))
            return;

        Result = new NewProjectResult(
            templateKey,
            ProjectNameBox.Text.Trim(),
            LocationBox.Text?.Trim() ?? string.Empty);
        Close(true);
    }

    private void Cancel_OnClick(object? sender, RoutedEventArgs e) => Close(false);
}

public sealed record NewProjectResult(string Template, string Name, string Location);

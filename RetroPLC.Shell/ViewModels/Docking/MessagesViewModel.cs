// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia.Media;
using Dock.Model.Mvvm.Controls;
using RetroPLC.LanguageServerHost;

namespace RetroPLC.Shell.ViewModels.Docking;

public sealed class MessagesViewModel(Action<StrucppLocation> navigate) : Tool
{
    private readonly Dictionary<string, IReadOnlyList<StrucppDiagnostic>> _diagnostics =
        new(StringComparer.OrdinalIgnoreCase);
    private string _projectName = string.Empty;
    private string _projectDirectory = string.Empty;
    private bool _showErrors = true;
    private bool _showWarnings = true;
    private bool _showMessages = true;

    public ObservableCollection<LanguageMessageItem> Items { get; } = [];

    public int ErrorCount =>
        _diagnostics.Values.SelectMany(items => items).Count(item => item.Severity == 1);

    public int WarningCount =>
        _diagnostics.Values.SelectMany(items => items).Count(item => item.Severity == 2);

    public int MessageCount =>
        _diagnostics.Values.SelectMany(items => items).Count(item => item.Severity >= 3);

    public string ErrorFilterLabel => $"{ErrorCount} Error{(ErrorCount == 1 ? string.Empty : "s")}";

    public string WarningFilterLabel =>
        $"{WarningCount} Warning{(WarningCount == 1 ? string.Empty : "s")}";

    public string MessageFilterLabel =>
        $"{MessageCount} Message{(MessageCount == 1 ? string.Empty : "s")}";

    public bool ShowErrors
    {
        get => _showErrors;
        set
        {
            if (_showErrors == value)
                return;
            _showErrors = value;
            RefreshItems();
        }
    }

    public bool ShowWarnings
    {
        get => _showWarnings;
        set
        {
            if (_showWarnings == value)
                return;
            _showWarnings = value;
            RefreshItems();
        }
    }

    public bool ShowMessages
    {
        get => _showMessages;
        set
        {
            if (_showMessages == value)
                return;
            _showMessages = value;
            RefreshItems();
        }
    }

    public void SetProject(string projectName, string projectDirectory)
    {
        _projectName = projectName;
        _projectDirectory = Path.GetFullPath(projectDirectory);
        _diagnostics.Clear();
        RefreshItems();
    }

    public void UpdateDiagnostics(
        string filePath,
        IReadOnlyList<StrucppDiagnostic> diagnostics)
    {
        var fullPath = Path.GetFullPath(filePath);
        if (!string.IsNullOrEmpty(_projectDirectory))
        {
            var relativePath = Path.GetRelativePath(_projectDirectory, fullPath);
            if (Path.IsPathRooted(relativePath) ||
                relativePath.Equals("..", StringComparison.Ordinal) ||
                relativePath.StartsWith(
                    $"..{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal))
            {
                return;
            }
        }

        if (diagnostics.Count == 0)
            _diagnostics.Remove(fullPath);
        else
            _diagnostics[fullPath] = diagnostics;
        RefreshItems();
    }

    public void RemoveDiagnostics(string filePath)
    {
        if (_diagnostics.Remove(Path.GetFullPath(filePath)))
            RefreshItems();
    }

    public bool TryNavigate(LanguageMessageItem item)
    {
        navigate(item.Location);
        return true;
    }

    private void RefreshItems()
    {
        var items = _diagnostics
            .SelectMany(pair => pair.Value.Select(diagnostic =>
                CreateItem(pair.Key, diagnostic)))
            .Where(item => IsSeverityVisible(item.Severity))
            .OrderBy(item => item.Severity)
            .ThenBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Line)
            .ThenBy(item => item.Column)
            .ToList();

        Items.Clear();
        foreach (var item in items)
            Items.Add(item);

        OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(MessageCount));
        OnPropertyChanged(nameof(ErrorFilterLabel));
        OnPropertyChanged(nameof(WarningFilterLabel));
        OnPropertyChanged(nameof(MessageFilterLabel));
    }

    private LanguageMessageItem CreateItem(
        string fullPath,
        StrucppDiagnostic diagnostic)
    {
        var filePath = string.IsNullOrEmpty(_projectDirectory)
            ? fullPath
            : Path.GetRelativePath(_projectDirectory, fullPath);
        return new LanguageMessageItem(
            diagnostic,
            _projectName,
            filePath.Replace(Path.DirectorySeparatorChar, '/'),
            new StrucppLocation(fullPath, diagnostic.Range));
    }

    private bool IsSeverityVisible(int severity) =>
        severity switch
        {
            1 => _showErrors,
            2 => _showWarnings,
            _ => _showMessages
        };

}

public sealed class LanguageMessageItem
{
    public LanguageMessageItem(
        StrucppDiagnostic diagnostic,
        string project,
        string filePath,
        StrucppLocation location)
    {
        Severity = diagnostic.Severity;
        Description = diagnostic.Message
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
        Code = diagnostic.Code ?? string.Empty;
        Source = diagnostic.Source;
        Project = project;
        FilePath = filePath;
        Line = diagnostic.Range.Start.Line + 1;
        Column = diagnostic.Range.Start.Character + 1;
        Location = location;
    }

    public int Severity { get; }
    public string Description { get; }
    public string Code { get; }
    public string Source { get; }
    public string Project { get; }
    public string FilePath { get; }
    public int Line { get; }
    public int Column { get; }
    public StrucppLocation Location { get; }

    public string SeverityGlyph => Severity switch
    {
        1 => "●",
        2 => "▲",
        _ => "ⓘ"
    };

    public string SeverityText => Severity switch
    {
        1 => "Error",
        2 => "Warning",
        3 => "Information",
        4 => "Hint",
        _ => "Message"
    };

    public IBrush SeverityBrush => Severity switch
    {
        1 => Brushes.Red,
        2 => Brushes.DarkGoldenrod,
        _ => Brushes.DodgerBlue
    };
}

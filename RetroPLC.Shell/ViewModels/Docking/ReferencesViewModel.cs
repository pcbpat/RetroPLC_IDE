using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using RetroPLC.LanguageServerHost;
using Dock.Model.Mvvm.Controls;

namespace RetroPLC.Shell.ViewModels.Docking;

public sealed class ReferencesViewModel(Action<StrucppLocation> navigate) : Tool
{
    public ObservableCollection<ReferenceTreeNode> Nodes { get; } = [];

    public void SetResults(
        string projectName,
        string projectDirectory,
        IReadOnlyList<StrucppLocation> locations)
    {
        var projectNode = new ReferenceTreeNode(
            $"{projectName} ({locations.Count} reference{(locations.Count == 1 ? string.Empty : "s")})")
        {
            IsExpanded = true
        };

        foreach (var fileGroup in locations
                     .GroupBy(
                         location => Path.GetFullPath(location.FilePath),
                         StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var relativePath = Path.GetRelativePath(projectDirectory, fileGroup.Key);
            var fileNode = new ReferenceTreeNode(relativePath) { IsExpanded = true };
            foreach (var location in fileGroup
                         .OrderBy(item => item.Range.Start.Line)
                         .ThenBy(item => item.Range.Start.Character))
            {
                fileNode.Children.Add(new ReferenceTreeNode(
                    $"{relativePath} ({location.Range.Start.Line + 1}," +
                    $"{location.Range.Start.Character + 1})",
                    location));
            }

            projectNode.Children.Add(fileNode);
        }

        Nodes.Clear();
        Nodes.Add(projectNode);
    }

    public bool TryNavigate(ReferenceTreeNode node)
    {
        if (node.Location is not { } location)
            return false;
        navigate(location);
        return true;
    }
}

public sealed class ReferenceTreeNode(
    string displayText,
    StrucppLocation? location = null)
{
    public string DisplayText { get; } = displayText;

    public StrucppLocation? Location { get; } = location;

    public ObservableCollection<ReferenceTreeNode> Children { get; } = [];

    public bool IsExpanded { get; set; }
}

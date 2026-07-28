using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using RetroPLC.LanguageServerHost;
using System.Text.Json.Serialization;

namespace RetroPLC.Shell.Models;

public sealed class ProjectDocument
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public string Name { get; set; } = string.Empty;
    public string Template { get; set; } = "StandardProject";
    public List<ProjectNodeDefinition> Tree { get; set; } = [];
}

public sealed class ProjectNodeDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Icon { get; set; } = "folder";
    public bool IsExpanded { get; set; } = true;
    public string? FilePath { get; set; }
    public string? LibraryFileName { get; set; }
    public List<ProjectNodeDefinition> Children { get; set; } = [];
}

public sealed record OpenedProject(ProjectDocument Document, string DirectoryPath, string ManifestPath);

public static class ProjectStore
{
    public const string ManifestFileName = "project.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static OpenedProject Create(
        string parentDirectory,
        string name,
        string template,
        IEnumerable<ProjectNodeDefinition> tree)
    {
        if (string.IsNullOrWhiteSpace(parentDirectory))
            throw new InvalidOperationException("Choose a location for the project.");

        ValidateProjectName(name);
        var projectDirectory = Path.GetFullPath(Path.Combine(parentDirectory, name));
        var manifestPath = Path.Combine(projectDirectory, ManifestFileName);

        if (File.Exists(manifestPath))
            throw new IOException($"A project already exists in '{projectDirectory}'.");

        Directory.CreateDirectory(projectDirectory);

        var document = new ProjectDocument
        {
            Name = name,
            Template = template,
            Tree = tree.ToList()
        };

        if (document.Tree.Count == 0)
            throw new InvalidOperationException("A project must contain at least one tree element.");

        CopyTemplateSources(projectDirectory);
        PopulateProjectLibraries(document, projectDirectory);
        Save(document, manifestPath);
        return new OpenedProject(document, projectDirectory, manifestPath);
    }

    public static OpenedProject Open(string projectDirectoryOrManifest)
    {
        var manifestPath = Directory.Exists(projectDirectoryOrManifest)
            ? Path.Combine(projectDirectoryOrManifest, ManifestFileName)
            : projectDirectoryOrManifest;
        manifestPath = Path.GetFullPath(manifestPath);

        if (!File.Exists(manifestPath))
            throw new FileNotFoundException(
                $"The selected folder does not contain {ManifestFileName}.", manifestPath);

        var json = File.ReadAllText(manifestPath);
        var document = JsonSerializer.Deserialize<ProjectDocument>(json, JsonOptions)
                       ?? throw new InvalidDataException("The project manifest is empty or invalid.");

        if (document.FormatVersion != ProjectDocument.CurrentFormatVersion)
            throw new InvalidDataException(
                $"Project format {document.FormatVersion} is not supported by this version of RetroPLC IDE.");
        if (string.IsNullOrWhiteSpace(document.Name))
            throw new InvalidDataException("The project manifest does not contain a project name.");
        if (document.Tree.Count == 0)
            throw new InvalidDataException("The project manifest does not contain a project tree.");

        var projectDirectory = Path.GetDirectoryName(manifestPath)!;
        PopulateProjectLibraries(document, projectDirectory);
        return new OpenedProject(document, projectDirectory, manifestPath);
    }

    public static void Save(ProjectDocument document, string manifestPath)
    {
        var json = JsonSerializer.Serialize(document, JsonOptions);
        var temporaryPath = manifestPath + ".tmp";
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, manifestPath, true);
    }

    private static void ValidateProjectName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Enter a project name.");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
            throw new InvalidOperationException("The project name contains characters that cannot be used in a folder name.");
    }

    private static void CopyTemplateSources(string projectDirectory)
    {
        var sourceRoot = Path.Combine(AppContext.BaseDirectory, "ProjectFiles");
        if (!Directory.Exists(sourceRoot))
            return;

        foreach (var sourcePath in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceRoot, sourcePath);
            var destinationPath = Path.Combine(projectDirectory, "ProjectFiles", relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath, false);
        }
    }

    private static void PopulateProjectLibraries(ProjectDocument document, string projectDirectory)
    {
        var libraryDirectory = StrucppToolchain.GetProjectLibraryDirectory(projectDirectory);
        if (Directory.Exists(libraryDirectory))
            return;

        var referencedLibraries = EnumerateNodes(document.Tree)
            .Select(node => node.LibraryFileName)
            .Where(fileName => !string.IsNullOrWhiteSpace(fileName))
            .Cast<string>()
            .ToList();

        // Older project manifests predate explicit library references. Seed
        // those projects once, then treat their local Libraries folder as the
        // authoritative set from that point forward.
        if (referencedLibraries.Count == 0)
        {
            referencedLibraries = StrucppToolchain.GetBundledLibraries()
                .Select(library => library.FileName)
                .ToList();
        }

        StrucppToolchain.PopulateProjectLibraries(projectDirectory, referencedLibraries);
    }

    private static IEnumerable<ProjectNodeDefinition> EnumerateNodes(
        IEnumerable<ProjectNodeDefinition> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in EnumerateNodes(node.Children))
                yield return child;
        }
    }
}

// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Text.Json;

namespace RetroPLC.LanguageServerHost;

public sealed record StrucppLibrarySummary(string FileName, string DisplayName);

public sealed record StrucppToolCommand(
    string ExecutablePath,
    IReadOnlyList<string> PrefixArguments);

public static class StrucppToolchain
{
    private const string ToolsDirectoryEnvironmentVariable = "RETROPLC_TOOLS_DIRECTORY";

    private static readonly Lazy<string> ResolvedToolsDirectory = new(ResolveToolsDirectory);

    public static string ToolsDirectory => ResolvedToolsDirectory.Value;

    public static string StrucppDirectory => Path.Combine(ToolsDirectory, "strucpp");

    public static string CompilerDirectory =>
        Path.Combine(StrucppDirectory, "dist", "node");

    public static string BundledLibraryDirectory =>
        Path.Combine(StrucppDirectory, "libs");

    public static string GetNodePath() => OperatingSystem.IsWindows()
        ? Path.Combine(ToolsDirectory, "toolchain", "node", "node.exe")
        : Path.Combine(ToolsDirectory, "toolchain", "node", "bin", "node");

    public static string GetCompilerPath() =>
        Path.Combine(CompilerDirectory, "cli.js");

    public static string GetLanguageServerPath() => Path.Combine(
        StrucppDirectory,
        "vscode-extension",
        "out",
        "server",
        "src",
        "server.js");

    public static StrucppToolCommand GetCompilerCommand() =>
        CreateNodeCommand(GetCompilerPath(), "compiler");

    public static StrucppToolCommand GetLanguageServerCommand() =>
        CreateNodeCommand(GetLanguageServerPath(), "language server");

    public static string GetProjectLibraryDirectory(string projectDirectory) =>
        Path.Combine(projectDirectory, "Libraries");

    public static string GetProjectLibraryPath(string projectDirectory, string fileName)
    {
        if (!string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
            throw new InvalidDataException("The library file name is invalid.");

        return Path.Combine(GetProjectLibraryDirectory(projectDirectory), fileName);
    }

    public static IReadOnlyList<StrucppLibrarySummary> GetBundledLibraries() =>
        GetLibraries(BundledLibraryDirectory);

    public static IReadOnlyList<StrucppLibrarySummary> GetProjectLibraries(string projectDirectory) =>
        GetLibraries(GetProjectLibraryDirectory(projectDirectory));

    public static void PopulateProjectLibraries(
        string projectDirectory,
        IEnumerable<string> libraryFileNames)
    {
        var projectLibraryDirectory = GetProjectLibraryDirectory(projectDirectory);
        Directory.CreateDirectory(projectLibraryDirectory);

        foreach (var fileName in libraryFileNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
                continue;

            var sourcePath = Path.Combine(BundledLibraryDirectory, fileName);
            if (File.Exists(sourcePath))
                File.Copy(sourcePath, Path.Combine(projectLibraryDirectory, fileName), false);
        }
    }

    public static void EnsureExecutable(string executablePath)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(executablePath))
            return;

        try
        {
            var mode = File.GetUnixFileMode(executablePath);
            File.SetUnixFileMode(executablePath, mode | UnixFileMode.UserExecute);
        }
        catch (Exception) when (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ||
                                RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Packaged tools normally retain their executable bit. The process
            // start error remains actionable if a read-only package did not.
        }
    }

    private static StrucppToolCommand CreateNodeCommand(string scriptPath, string toolName)
    {
        var nodePath = GetNodePath();
        if (!File.Exists(nodePath))
            throw new FileNotFoundException(
                "The RetroPLC-managed Node.js runtime was not found. Run setup.sh first.",
                nodePath);
        if (!File.Exists(scriptPath))
            throw new FileNotFoundException(
                $"The STruC++ {toolName} was not found. Run setup.sh first.",
                scriptPath);

        EnsureExecutable(nodePath);
        return new StrucppToolCommand(nodePath, [scriptPath]);
    }

    private static string ResolveToolsDirectory()
    {
        var configured = Environment.GetEnvironmentVariable(ToolsDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);

        foreach (var startPath in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var directory = new DirectoryInfo(Path.GetFullPath(startPath));
                 directory is not null;
                 directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "Tools");
                if (Directory.Exists(Path.Combine(candidate, "strucpp")) ||
                    Directory.Exists(Path.Combine(candidate, "toolchain")))
                {
                    return candidate;
                }
            }
        }

        return Path.Combine(AppContext.BaseDirectory, "Tools");
    }

    private static IReadOnlyList<StrucppLibrarySummary> GetLibraries(string directory)
    {
        if (!Directory.Exists(directory))
            return [];

        return Directory.EnumerateFiles(directory, "*.stlib", SearchOption.TopDirectoryOnly)
            .Select(ReadSummary)
            .OrderBy(library => library.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static StrucppLibrarySummary ReadSummary(string path)
    {
        var fileName = Path.GetFileName(path);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var manifest = document.RootElement.GetProperty("manifest");
            var displayName = GetString(manifest, "displayName")
                              ?? GetString(manifest, "name")
                              ?? Path.GetFileNameWithoutExtension(path);
            return new StrucppLibrarySummary(fileName, displayName);
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or KeyNotFoundException)
        {
            return new StrucppLibrarySummary(fileName, Path.GetFileNameWithoutExtension(path));
        }
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}

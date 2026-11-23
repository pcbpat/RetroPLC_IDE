// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;

namespace RetroPLC.LanguageServerHost;

public sealed record StrucppLibrarySummary(string FileName, string DisplayName);

public sealed record StrucppToolCommand(
    string ExecutablePath,
    IReadOnlyList<string> PrefixArguments);

public static class StrucppToolchain
{
    private const string ToolsDirectoryEnvironmentVariable = "RETROPLC_TOOLS_DIRECTORY";

    public static string ApplicationDirectory { get; } = ResolveApplicationDirectory();

    private static readonly Lazy<string> ResolvedToolsDirectory = new(ResolveToolsDirectory);

    public static string ToolsDirectory => ResolvedToolsDirectory.Value;

    public static string StrucppDirectory => Path.Combine(ToolsDirectory, "strucpp");

    public static string CompilerDirectory =>
        Path.Combine(StrucppDirectory, "dist", "node");

    public static string BundledLibraryDirectory =>
        Path.Combine(StrucppDirectory, "libs");

    public static string GetNodePath() => OperatingSystem.IsWindows()
        ? Path.Combine(ToolsDirectory, "node", "node.exe")
        : Path.Combine(ToolsDirectory, "node", "bin", "node");

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

    public static StrucppToolCommand GetNativeCompilerCommand(string installationDirectory)
    {
        var module = Path.Combine(CompilerDirectory, "index.js");
        var command = CreateNodeCommand(module, "compiled API");
        var helper = Path.Combine(installationDirectory, "tools", "generate-strucpp-debug.mjs");
        if (!File.Exists(helper))
            throw new FileNotFoundException($"The RetroPLC STruC++ build helper was not found at '{helper}'.", helper);

        return new StrucppToolCommand(command.ExecutablePath, [helper, StrucppDirectory]);
    }

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
        if (!OperatingSystem.IsWindows() &&
            (File.GetUnixFileMode(executablePath) &
                (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
            throw new UnauthorizedAccessException(
                $"The managed executable at '{executablePath}' is not executable. Check its file permissions.");
    }

    private static StrucppToolCommand CreateNodeCommand(string scriptPath, string toolName)
    {
        var nodePath = GetNodePath();
        if (!File.Exists(nodePath))
            throw new FileNotFoundException(
                $"The RetroPLC-managed Node.js runtime was not found at '{nodePath}'. Run setup.sh or check {ToolsDirectoryEnvironmentVariable}.",
                nodePath);
        if (!File.Exists(scriptPath))
            throw new FileNotFoundException(
                $"The STruC++ {toolName} was not found at '{scriptPath}'. Run setup.sh to build tools/strucpp.",
                scriptPath);

        EnsureExecutable(nodePath);
        return new StrucppToolCommand(nodePath, [scriptPath]);
    }

    private static string ResolveToolsDirectory()
    {
        var configured = Environment.GetEnvironmentVariable(ToolsDirectoryEnvironmentVariable);
        if (configured is null)
            return Path.Combine(ApplicationDirectory, "tools");

        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException($"Set {ToolsDirectoryEnvironmentVariable} to the tools directory or unset it.");

        string directory;
        try
        {
            directory = Path.GetFullPath(configured, ApplicationDirectory);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException($"{ToolsDirectoryEnvironmentVariable} contains an invalid path.", exception);
        }

        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException(
                $"{ToolsDirectoryEnvironmentVariable} points to missing directory '{directory}'. Correct the override or unset it.");

        return directory;
    }

    private static string ResolveApplicationDirectory()
    {
        var applicationDirectory = AppContext.BaseDirectory;
        if (Directory.Exists(Path.Combine(applicationDirectory, "tools", "strucpp")) ||
            Directory.Exists(Path.Combine(applicationDirectory, "tools", "node")) ||
            Directory.Exists(Path.Combine(applicationDirectory, ".zinstaller")) ||
            Directory.Exists(Path.Combine(applicationDirectory, ".zephyr")))
            return applicationDirectory;

        for (var directory = new DirectoryInfo(applicationDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RetroPLC.sln")) &&
                Directory.Exists(Path.Combine(directory.FullName, "RetroPLC.LanguageServerHost")))
                return directory.FullName;
        }

        return applicationDirectory;
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

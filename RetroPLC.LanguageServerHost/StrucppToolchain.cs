// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Text.Json;

namespace RetroPLC.LanguageServerHost;

public sealed record StrucppLibrarySummary(string FileName, string DisplayName);

public static class StrucppToolchain
{
    private const string ToolDirectoryName = "StrucppTools";

    public static string CompilerDirectory =>
        Path.Combine(AppContext.BaseDirectory, ToolDirectoryName, "compiler");

    public static string BundledLibraryDirectory =>
        Path.Combine(CompilerDirectory, "libs");

    public static string GetCompilerPath() => Path.Combine(
        CompilerDirectory,
        OperatingSystem.IsWindows() ? "strucpp-win.exe" :
        OperatingSystem.IsMacOS() ? "strucpp-macos" :
        "strucpp-linux");

    public static string GetLanguageServerPath() => Path.Combine(
        AppContext.BaseDirectory,
        ToolDirectoryName,
        "lsp",
        OperatingSystem.IsWindows() ? "strucpp-lsp-win.exe" :
        OperatingSystem.IsMacOS() ? "strucpp-lsp-macos" :
        "strucpp-lsp-linux");

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

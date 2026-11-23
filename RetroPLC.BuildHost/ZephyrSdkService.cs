// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;

namespace RetroPLC.BuildHost;

internal sealed record ZephyrSdkInfo(string Directory, string Version, IReadOnlyList<string> Toolchains);

internal static class ZephyrSdkService
{
    internal static ZephyrSdkInfo ValidateInstallation(string directory)
    {
        var versionFile = Path.Combine(directory, "sdk_version");
        var cmakeConfig = Path.Combine(directory, "cmake", "Zephyr-sdkConfig.cmake");
        if (!File.Exists(versionFile) || !File.Exists(cmakeConfig) || new FileInfo(cmakeConfig).Length == 0)
            throw new IOException($"The Zephyr SDK at '{directory}' is missing or incomplete. " +
                                  "Expected sdk_version and cmake/Zephyr-sdkConfig.cmake. " +
                                  "Run setup.sh or check the explicit SDK override.");

        var version = File.ReadLines(versionFile).FirstOrDefault()?.Trim();
        var compilerSuffix = OperatingSystem.IsWindows() ? ".exe" : "";
        var compilerNames = new[] { "-gcc", "-clang" };
        var layouts = new[] { directory, Path.Combine(directory, "gnu"), Path.Combine(directory, "llvm") };
        var toolchains = layouts.Where(Directory.Exists)
            .SelectMany(layout => Directory.EnumerateDirectories(layout))
            .Where(toolchain => compilerNames.Any(suffix =>
                IsExecutable(Path.Combine(toolchain, "bin", Path.GetFileName(toolchain) + suffix + compilerSuffix))))
            .Select(toolchain => Path.GetFileName(toolchain))
            .ToList();
        if (IsExecutable(Path.Combine(directory, "llvm", "bin", "clang" + compilerSuffix)))
            toolchains.Add("llvm");

        if (version is null || !Regex.IsMatch(version, @"^[0-9]+\.[0-9]+\.[0-9]+(?:[.+-][a-zA-Z0-9._-]+)?$"))
            throw new IOException($"Invalid SDK version metadata at '{versionFile}'. " +
                                  "Run setup.sh or check the explicit SDK override.");
        if (toolchains.Count == 0)
            throw new IOException($"No usable compiler was found in the Zephyr SDK at '{directory}'. " +
                                  "Run setup.sh or check the explicit SDK override.");

        return new ZephyrSdkInfo(directory, version, toolchains.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    private static bool IsExecutable(string file) => File.Exists(file) && new FileInfo(file).Length > 0 && (OperatingSystem.IsWindows() ||
        (File.GetUnixFileMode(file) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0);
}

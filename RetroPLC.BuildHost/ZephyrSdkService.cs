// SPDX-License-Identifier: GPL-3.0-or-later
namespace RetroPLC.BuildHost;

internal static class ZephyrSdkService
{
    internal static void RequireDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException(
                $"Zephyr SDK directory '{directory}' does not exist. Run setup.sh or check the configured SDK path.");
    }
}

// SPDX-License-Identifier: GPL-3.0-or-later
namespace RetroPLC.BuildHost;

/// <summary>Legacy project settings retained for compatibility diagnostics.</summary>
public sealed record ZephyrBuildConfiguration
{
    public string? HostToolsDirectory { get; init; }
    public string? WorkspaceDirectory { get; init; }
    public string? SdkDirectory { get; init; }
    public string? BoardTarget { get; init; }
    public string? ApplicationDirectory { get; init; }
    public string? BuildDirectory { get; init; }
    public bool? UseSysbuild { get; init; }
    public string? PristineMode { get; init; }
    public Dictionary<string, string>? CMakeOptions { get; init; }

    internal bool HasOverrides => HostToolsDirectory is not null || WorkspaceDirectory is not null ||
        SdkDirectory is not null || BoardTarget is not (null or "arduino_opta/stm32h747xx/m7") ||
        ApplicationDirectory is not null || BuildDirectory is not null || UseSysbuild == false ||
        PristineMode is not (null or "always") || CMakeOptions is { Count: > 0 };
}

// SPDX-License-Identifier: GPL-3.0-or-later
using RetroPLC.BuildHost.ZephyrBackend;

namespace RetroPLC.BuildHost;

/// <summary>Optional settings for an IDE, project, or individual build operation.</summary>
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
}

public sealed class ZephyrConfiguration : IAsyncDisposable
{
    private readonly ZephyrBuildConfiguration _defaults;
    private readonly bool _ownsEnvironment;

    public ZephyrConfiguration(Ac6Environment? environment = null, ZephyrBuildConfiguration? defaults = null)
    {
        _ownsEnvironment = environment is null;
        Environment = environment ?? new Ac6Environment();
        _defaults = defaults ?? new ZephyrBuildConfiguration();
    }

    public Ac6Environment Environment { get; }

    public Task<ZephyrBuildConfiguration> ResolveAsync(
        string projectDirectory, ZephyrBuildConfiguration? project = null,
        ZephyrBuildConfiguration? overrides = null, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<ZephyrBuildConfiguration>(cancellationToken);

        var root = Environment.Installation.ApplicationDirectory;
        var projectRoot = Path.GetFullPath(projectDirectory, root);
        var workspace = ResolvePath(overrides?.WorkspaceDirectory, project?.WorkspaceDirectory,
            _defaults.WorkspaceDirectory, "RETROPLC_ZEPHYR_WORKSPACE", ".zephyr/workspace", projectRoot)!;
        var cmakeOptions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var layer in new[] { _defaults.CMakeOptions, project?.CMakeOptions, overrides?.CMakeOptions })
        {
            if (layer is not null)
                foreach (var (name, value) in layer)
                    cmakeOptions[name] = value;
        }

        return Task.FromResult(new ZephyrBuildConfiguration
        {
            HostToolsDirectory = ResolveHostTools(projectRoot, project, overrides),
            WorkspaceDirectory = workspace,
            SdkDirectory = ResolvePath(overrides?.SdkDirectory, project?.SdkDirectory,
                _defaults.SdkDirectory, "RETROPLC_ZEPHYR_SDK", ".zephyr/sdk", projectRoot),
            BoardTarget = overrides?.BoardTarget ?? project?.BoardTarget ?? _defaults.BoardTarget ??
                System.Environment.GetEnvironmentVariable("RETROPLC_ZEPHYR_BOARD") ?? ZephyrBoardService.DefaultTarget,
            ApplicationDirectory = ResolvePath(overrides?.ApplicationDirectory, project?.ApplicationDirectory,
                _defaults.ApplicationDirectory, "RETROPLC_ZEPHYR_APPLICATION", null, projectRoot) ??
                Path.Combine(workspace, "retroplc-runtime", "app"),
            BuildDirectory = ResolvePath(overrides?.BuildDirectory, project?.BuildDirectory,
                _defaults.BuildDirectory, null, null, projectRoot),
            UseSysbuild = overrides?.UseSysbuild ?? project?.UseSysbuild ?? _defaults.UseSysbuild ?? true,
            PristineMode = overrides?.PristineMode ?? project?.PristineMode ?? _defaults.PristineMode ?? "auto",
            CMakeOptions = cmakeOptions
        });
    }

    public Task<BuildProcess> CreateTerminalAsync(string? projectDirectory,
        ZephyrBuildConfiguration? project = null, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<BuildProcess>(cancellationToken);

        var root = Environment.Installation.ApplicationDirectory;
        var directory = Path.GetFullPath(projectDirectory ?? root, root);
        return Task.FromResult(Environment.CreateTerminal(directory, ResolveHostTools(directory, project)));
    }

    public IZephyrBoardService GetBoardService(
        string projectDirectory, ZephyrBuildConfiguration? project = null,
        ZephyrBuildConfiguration? overrides = null) => new ZephyrBoardService();

    public Task<ZephyrWorkspaceInfo> GetWorkspaceInfoAsync(string workspaceDirectory, CancellationToken cancellationToken = default) =>
        Environment.Backend.GetWorkspaceInfoAsync(workspaceDirectory, cancellationToken);

    public Task<IReadOnlyList<ZephyrProjectInfo>> GetProjectsAsync(string workspaceDirectory,
        IReadOnlyList<string> names, CancellationToken cancellationToken = default) =>
        Environment.Backend.GetProjectsAsync(workspaceDirectory, names, cancellationToken);

    public Task<WestConfigurationInfo> GetWestConfigAsync(string workspaceDirectory,
        IReadOnlyList<string> options, CancellationToken cancellationToken = default) =>
        Environment.Backend.GetWestConfigAsync(workspaceDirectory, options, cancellationToken);

    private string? ResolveHostTools(string projectRoot, ZephyrBuildConfiguration? project,
        ZephyrBuildConfiguration? overrides = null) => ResolvePath(overrides?.HostToolsDirectory,
        project?.HostToolsDirectory, _defaults.HostToolsDirectory, "RETROPLC_AC6_DIRECTORY", null, projectRoot);

    private string? ResolvePath(string? operation, string? project, string? defaults,
        string? variable, string? fallback, string projectRoot)
    {
        var value = operation ?? project ?? defaults ??
            (variable is null ? null : System.Environment.GetEnvironmentVariable(variable)) ?? fallback;
        if (value is null)
            return null;
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("A configured Zephyr directory is empty. Provide a path or remove the setting.");
        return Path.GetFullPath(value, operation is not null || project is not null
            ? projectRoot : Environment.Installation.ApplicationDirectory);
    }

    public ValueTask DisposeAsync() => _ownsEnvironment ? Environment.DisposeAsync() : ValueTask.CompletedTask;
}

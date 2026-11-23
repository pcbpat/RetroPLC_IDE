// SPDX-License-Identifier: GPL-3.0-or-later
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

public sealed class ZephyrConfiguration
{
    internal const string DefaultBoard = "arduino_opta/stm32h747xx/m7";

    private readonly ZephyrBuildConfiguration _defaults;

    public ZephyrConfiguration(Ac6Environment? environment = null, ZephyrBuildConfiguration? defaults = null)
    {
        Environment = environment ?? new Ac6Environment();
        _defaults = defaults ?? new ZephyrBuildConfiguration();
    }

    public Ac6Environment Environment { get; }

    public BuildProcess CreateTerminal(string? projectDirectory, ZephyrBuildConfiguration? project = null)
    {
        var directory = Path.GetFullPath(projectDirectory ?? Environment.Installation.ApplicationDirectory,
            Environment.Installation.ApplicationDirectory);
        return Environment.CreateTerminal(directory, ResolveHostTools(directory, project));
    }

    public IZephyrBoardService GetBoardService(
        string projectDirectory, ZephyrBuildConfiguration? project = null,
        ZephyrBuildConfiguration? overrides = null)
    {
        var projectRoot = Path.GetFullPath(projectDirectory, Environment.Installation.ApplicationDirectory);
        return new ZephyrBoardService(Environment, RequireWorkspace(ResolveWorkspace(projectRoot, project, overrides)),
            ResolveHostTools(projectRoot, project, overrides));
    }

    public ZephyrBuildConfiguration Resolve(
        string projectDirectory, ZephyrBuildConfiguration? project = null,
        ZephyrBuildConfiguration? overrides = null)
    {
        var appRoot = Environment.Installation.ApplicationDirectory;
        var projectRoot = Path.GetFullPath(projectDirectory, appRoot);
        project ??= new();
        overrides ??= new();

        string? ResolvePath(string? operation, string? projectValue, string? ideValue) =>
            operation is not null || projectValue is not null
                ? ResolveDirectory(operation ?? projectValue, projectRoot)
                : ResolveDirectory(ideValue, appRoot);

        var workspace = ResolveWorkspace(projectRoot, project, overrides);
        var application = ResolvePath(overrides.ApplicationDirectory, project.ApplicationDirectory,
            _defaults.ApplicationDirectory ?? ReadVariable("RETROPLC_ZEPHYR_APPLICATION"));
        var sdk = ResolvePath(overrides.SdkDirectory, project.SdkDirectory,
            _defaults.SdkDirectory ?? ReadVariable("RETROPLC_ZEPHYR_SDK") ?? Path.Combine(".zephyr", "sdk"));

        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var layer in new[] { _defaults.CMakeOptions, project.CMakeOptions, overrides.CMakeOptions })
        {
            if (layer is null)
                continue;

            foreach (var option in layer)
                options[option.Key] = option.Value;
        }

        return new ZephyrBuildConfiguration
        {
            HostToolsDirectory = ResolveHostTools(projectRoot, project, overrides),
            WorkspaceDirectory = workspace,
            SdkDirectory = sdk,
            BoardTarget = overrides.BoardTarget ?? project.BoardTarget ?? _defaults.BoardTarget ??
                          ReadVariable("RETROPLC_ZEPHYR_BOARD") ?? DefaultBoard,
            ApplicationDirectory = application ?? (workspace is null ? null : Path.Combine(workspace, "retroplc-runtime", "app")),
            BuildDirectory = ResolvePath(overrides.BuildDirectory, project.BuildDirectory, _defaults.BuildDirectory),
            UseSysbuild = overrides.UseSysbuild ?? project.UseSysbuild ?? _defaults.UseSysbuild ?? true,
            PristineMode = overrides.PristineMode ?? project.PristineMode ?? _defaults.PristineMode ?? "auto",
            CMakeOptions = options
        };
    }

    internal string? ResolveHostTools(string projectDirectory, ZephyrBuildConfiguration? project,
        ZephyrBuildConfiguration? overrides = null) =>
        overrides?.HostToolsDirectory is not null || project?.HostToolsDirectory is not null
            ? ResolveDirectory(overrides?.HostToolsDirectory ?? project?.HostToolsDirectory,
                Path.GetFullPath(projectDirectory, Environment.Installation.ApplicationDirectory))
            : ResolveDirectory(_defaults.HostToolsDirectory ?? ReadVariable("RETROPLC_AC6_DIRECTORY"),
                Environment.Installation.ApplicationDirectory);

    private string? ResolveWorkspace(string projectRoot, ZephyrBuildConfiguration? project,
        ZephyrBuildConfiguration? overrides)
    {
        var configured = overrides?.WorkspaceDirectory ?? project?.WorkspaceDirectory;
        return configured is null
            ? ResolveDirectory(_defaults.WorkspaceDirectory ?? ReadVariable("RETROPLC_ZEPHYR_WORKSPACE") ??
                Path.Combine(".zephyr", "workspace"),
                Environment.Installation.ApplicationDirectory)
            : ResolveDirectory(configured, projectRoot);
    }

    internal static string RequireWorkspace(string? workspace)
    {
        if (workspace is null)
            throw new InvalidOperationException(
                "RetroPLC Zephyr workspace was not found. Run setup.sh to initialize the build environment.");

        if (!File.Exists(Path.Combine(workspace, ".west", "config")))
            throw new DirectoryNotFoundException(
                $"RetroPLC Zephyr workspace was not found at '{workspace}' (missing .west/config). " +
                "Run setup.sh or check your explicit workspace override.");

        return workspace;
    }

    internal static string? ReadVariable(string name) => System.Environment.GetEnvironmentVariable(name);

    private static string? ResolveDirectory(string? value, string baseDirectory)
    {
        if (value is null)
            return null;
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("A configured Zephyr directory is empty. Provide a path or remove the setting.");

        return Path.GetFullPath(value, baseDirectory);
    }
}

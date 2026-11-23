// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using RetroPLC.LanguageServerHost;

namespace RetroPLC.BuildHost;

public enum BuildOperation
{
    Verify,
    Build,
    Rebuild,
    Clean,
    Download
}

public sealed record BuildProcess(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory);

public sealed record BuildProcessOutcome(BuildProcess? NextProcess, int? ExitCode)
{
    public bool IsComplete => ExitCode.HasValue;
    public string? ErrorMessage { get; init; }
}

public sealed class FirmwareBuildSession
{
    private readonly FirmwareBuildService _service;
    private readonly ZephyrBuildContext? _zephyrContext;
    private readonly BuildProcess? _zephyrProcess;
    private BuildStage _stage;
    private byte[]? _debugMap;

    internal FirmwareBuildSession(
        FirmwareBuildService service,
        BuildOperation operation,
        BuildProcess initialProcess,
        BuildStage stage,
        ZephyrBuildContext? zephyrContext,
        string pristineMode = "auto")
    {
        _service = service;
        Operation = operation;
        InitialProcess = initialProcess;
        _stage = stage;
        _zephyrContext = zephyrContext;
        if (zephyrContext is not null)
            _zephyrProcess = service.CreateZephyrBuildProcess(zephyrContext, pristineMode);
    }

    public BuildOperation Operation { get; }

    public BuildProcess InitialProcess { get; }

    public BuildProcessOutcome ProcessExited(int exitCode)
    {
        if (exitCode != 0)
            return Complete(exitCode);

        if (Operation is BuildOperation.Build or BuildOperation.Rebuild or BuildOperation.Download &&
            _stage == BuildStage.StructuredText &&
            _zephyrProcess is { } zephyrProcess)
        {
            try
            {
                _debugMap = FirmwareBuildService.ReadGeneratedDebugMap(_zephyrContext!);
                _stage = BuildStage.Zephyr;
                return new BuildProcessOutcome(zephyrProcess, null);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                return Complete(-1) with { ErrorMessage = exception.Message };
            }
        }

        if (Operation is BuildOperation.Build or BuildOperation.Rebuild or BuildOperation.Download &&
            _stage == BuildStage.Zephyr &&
            _zephyrContext is { } completedContext)
        {
            try
            {
                FirmwareBuildService.PublishBuildArtifacts(completedContext, _debugMap!);

                if (Operation == BuildOperation.Download)
                {
                    _stage = BuildStage.FirmwareUpdate;
                    return new BuildProcessOutcome(
                        _service.CreateFirmwareUpdateProcess(completedContext),
                        null);
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return Complete(-1) with { ErrorMessage = exception.Message };
            }
        }

        return Complete(0);
    }

    private BuildProcessOutcome Complete(int exitCode)
    {
        _stage = BuildStage.Complete;
        return new BuildProcessOutcome(null, exitCode);
    }
}

public sealed class FirmwareBuildService : IAsyncDisposable
{
    private readonly bool _ownsConfiguration;

    public FirmwareBuildService() : this(new ZephyrConfiguration())
    {
        _ownsConfiguration = true;
    }

    public FirmwareBuildService(ZephyrConfiguration configuration)
    {
        Configuration = configuration;
    }

    public ZephyrConfiguration Configuration { get; }

    public ValueTask DisposeAsync() => _ownsConfiguration ? Configuration.DisposeAsync() : ValueTask.CompletedTask;

    public FirmwareBuildSession StartVerify(string projectDirectory, string projectName) =>
        new(
            this,
            BuildOperation.Verify,
            CreateStrucppCompilerProcess(projectDirectory),
            BuildStage.StructuredText,
            null);

    public async Task<FirmwareBuildSession> StartBuildAsync(string projectDirectory, string projectName,
        ZephyrBuildConfiguration? project = null, ZephyrBuildConfiguration? overrides = null,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveZephyrBuildContextAsync(projectDirectory, project, overrides, cancellationToken).ConfigureAwait(false);
        return new FirmwareBuildSession(
            this,
            BuildOperation.Build,
            CreateStrucppCompilerProcess(projectDirectory),
            BuildStage.StructuredText,
            context,
            context.Settings.PristineMode!);
    }

    public async Task<FirmwareBuildSession> StartRebuildAsync(string projectDirectory, string projectName,
        ZephyrBuildConfiguration? project = null, ZephyrBuildConfiguration? overrides = null,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveZephyrBuildContextAsync(projectDirectory, project, overrides, cancellationToken).ConfigureAwait(false);
        return new FirmwareBuildSession(
            this,
            BuildOperation.Rebuild,
            CreateStrucppCompilerProcess(projectDirectory),
            BuildStage.StructuredText,
            context,
            "always");
    }

    public async Task<FirmwareBuildSession> StartCleanAsync(string projectDirectory, ZephyrBuildConfiguration? project = null,
        ZephyrBuildConfiguration? overrides = null, CancellationToken cancellationToken = default)
    {
        projectDirectory = GetProjectDirectory(projectDirectory);
        var settings = await Configuration.ResolveAsync(projectDirectory, project, overrides, cancellationToken).ConfigureAwait(false);
        return new FirmwareBuildSession(
            this,
            BuildOperation.Clean,
            Configuration.Environment.CreateCMake(
                projectDirectory, settings.HostToolsDirectory,
                ["-E", "remove_directory", Path.Combine(projectDirectory, "Build")]),
            BuildStage.Clean,
            null);
    }

    public async Task<FirmwareBuildSession> StartDownloadAsync(string projectDirectory, string projectName,
        ZephyrBuildConfiguration? project = null, ZephyrBuildConfiguration? overrides = null,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveZephyrBuildContextAsync(projectDirectory, project, overrides, cancellationToken).ConfigureAwait(false);
        return new FirmwareBuildSession(
            this,
            BuildOperation.Download,
            CreateStrucppCompilerProcess(projectDirectory),
            BuildStage.StructuredText,
            context,
            context.Settings.PristineMode!);
    }

    private BuildProcess CreateStrucppCompilerProcess(string projectDirectory)
    {
        projectDirectory = GetProjectDirectory(projectDirectory);
        var compiler = StrucppToolchain.GetNativeCompilerCommand(
            Configuration.Environment.Installation.ApplicationDirectory);
        return new BuildProcess(compiler.ExecutablePath,
        [
            .. compiler.PrefixArguments,
            Path.Combine(projectDirectory, "Build", "Generated"),
            Path.Combine(projectDirectory, "ProjectFiles"),
            "--lib-dir", StrucppToolchain.GetProjectLibraryDirectory(projectDirectory),
            "--no-default-libs"
        ], projectDirectory);
    }

    internal BuildProcess CreateZephyrBuildProcess(ZephyrBuildContext context, string pristineMode)
    {
        var sysbuild = context.Settings.UseSysbuild!.Value;
        var generatedOption = sysbuild ? $"{context.ApplicationName}_RETROPLC_GENERATED_DIR" : "RETROPLC_GENERATED_DIR";
        var sdkDirectory = context.Settings.SdkDirectory!;
        var arguments = new List<string>
        {
            "build", "-p", pristineMode, sysbuild ? "--sysbuild" : "--no-sysbuild", "-b", context.Settings.BoardTarget!,
            "-s", context.Settings.ApplicationDirectory!, "-d", context.BuildDirectory, "--"
        };

        foreach (var option in context.Settings.CMakeOptions!)
            arguments.Add($"-D{option.Key}={option.Value}");

        arguments.Add($"-DBOARD:STRING={context.Settings.BoardTarget}");
        arguments.Add($"-D{generatedOption}={context.GeneratedDirectory}");
        arguments.Add($"-DZEPHYR_SDK_INSTALL_DIR:PATH={sdkDirectory}");
        arguments.Add($"-DZephyr-sdk_DIR:PATH={Path.Combine(sdkDirectory, "cmake")}");
        if (sysbuild)
        {
            arguments.Add($"-D{context.ApplicationName}_BOARD={context.Settings.BoardTarget}");
            arguments.Add($"-D{context.ApplicationName}_ZEPHYR_SDK_INSTALL_DIR={sdkDirectory}");
            arguments.Add($"-D{context.ApplicationName}_Zephyr-sdk_DIR={Path.Combine(sdkDirectory, "cmake")}");
        }
        return Configuration.Environment.CreateWest(context.Settings.WorkspaceDirectory!,
            context.Settings.HostToolsDirectory, context.Settings.SdkDirectory, arguments);
    }

    internal BuildProcess CreateFirmwareUpdateProcess(ZephyrBuildContext context)
    {
        var firmwarePath = Path.Combine(context.ArtifactsDirectory, "firmware.bin");
        if (!File.Exists(firmwarePath))
            throw new FileNotFoundException(
                "The firmware binary was not produced by the Zephyr build.",
                firmwarePath);

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException(
                "The setup-managed mcumgrctl firmware updater supports Linux and macOS.");

        var executablePath = Path.Combine(
            StrucppToolchain.ToolsDirectory,
            "mcumgr",
            "mcumgrctl");
        if (!File.Exists(executablePath))
            throw new FileNotFoundException(
                $"The managed mcumgrctl firmware updater was not found at '{executablePath}'. Run setup.sh or check RETROPLC_TOOLS_DIRECTORY.",
                executablePath);
        StrucppToolchain.EnsureExecutable(executablePath);

        return CreatePlatformProcess(
            executablePath,
            ["-u", ".", "firmware", "update", "--timeout", "5000", firmwarePath],
            context.ProjectBuildDirectory);
    }

    private static BuildProcess CreatePlatformProcess(
        string executable,
        List<string> arguments,
        string workingDirectory)
    {
        if (OperatingSystem.IsWindows())
            return new BuildProcess(executable, arguments, workingDirectory);

        arguments.Insert(0, executable);
        return new BuildProcess("/usr/bin/env", arguments, workingDirectory);
    }

    private async Task<ZephyrBuildContext> ResolveZephyrBuildContextAsync(string projectDirectory,
        ZephyrBuildConfiguration? project, ZephyrBuildConfiguration? overrides, CancellationToken cancellationToken)
    {
        var settings = await Configuration.ResolveAsync(projectDirectory, project, overrides, cancellationToken).ConfigureAwait(false);
        var backend = Configuration.Environment.Backend;
        var validation = await backend.ValidateWorkspaceAsync(settings.WorkspaceDirectory!, cancellationToken).ConfigureAwait(false);
        if (!validation.Workspace.ZephyrCloned)
            throw new DirectoryNotFoundException(
                $"The manifest's Zephyr checkout is missing at '{validation.Workspace.ZephyrDirectory}'. Run setup.sh.");
        settings = settings with { WorkspaceDirectory = validation.Workspace.Directory };
        if (settings.ApplicationDirectory is null ||
            !File.Exists(Path.Combine(settings.ApplicationDirectory, "CMakeLists.txt")))
            throw new DirectoryNotFoundException(
                $"No Zephyr application exists at '{settings.ApplicationDirectory}'. " +
                "Run setup.sh or check the applicationDirectory override.");

        ZephyrSdkService.RequireDirectory(settings.SdkDirectory!);
        if (settings.PristineMode is not ("auto" or "always" or "never"))
            throw new InvalidOperationException("Zephyr pristine mode must be auto, always, or never.");
        foreach (var option in settings.CMakeOptions!)
        {
            if (string.IsNullOrWhiteSpace(option.Key) || option.Key.Contains('='))
                throw new InvalidOperationException($"Invalid CMake option name '{option.Key}'.");
        }

        var buildRoot = Path.Combine(GetProjectDirectory(projectDirectory), "Build");
        return new ZephyrBuildContext(
            buildRoot,
            Path.Combine(buildRoot, "Generated"),
            settings.BuildDirectory ?? Path.Combine(buildRoot, "Zephyr", MakeSafeDirectoryName(settings.BoardTarget!)),
            Path.Combine(buildRoot, "Artifacts"),
            Path.GetFileName(Path.TrimEndingDirectorySeparator(settings.ApplicationDirectory)),
            settings);
    }

    internal static byte[] ReadGeneratedDebugMap(ZephyrBuildContext context)
    {
        var path = Path.Combine(context.GeneratedDirectory, "debug_map.json");
        var bytes = File.ReadAllBytes(path);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out var value) || value != 2 ||
            !root.TryGetProperty("md5", out var identity) || identity.ValueKind != JsonValueKind.String ||
            identity.GetString() is not { Length: 32 } ||
            !root.TryGetProperty("arrays", out var arrays) || arrays.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("leaves", out var leaves) || leaves.ValueKind != JsonValueKind.Array)
            throw new IOException($"The generated debug map at '{path}' is incomplete.");

        return bytes;
    }

    internal static void PublishBuildArtifacts(ZephyrBuildContext context, byte[] debugMap)
    {
        var imageBuildDirectory = context.Settings.UseSysbuild!.Value
            ? Path.Combine(context.BuildDirectory, context.ApplicationName)
            : context.BuildDirectory;
        var zephyrOutputDirectory = Path.Combine(imageBuildDirectory, "zephyr");
        var artifacts = new[]
        {
            (Source: "zephyr.elf", Destination: "firmware.elf"),
            (Source: "zephyr.signed.hex", Destination: "firmware.hex"),
            (Source: "zephyr.signed.bin", Destination: "firmware.bin"),
            (Source: "zephyr.uf2", Destination: "firmware.uf2"),
            (Source: "zephyr.map", Destination: "firmware.map")
        };

        if (!File.ReadAllBytes(Path.Combine(context.GeneratedDirectory, "debug_map.json")).AsSpan().SequenceEqual(debugMap))
            throw new IOException("Generated debug symbols changed during the firmware build. Rebuild the project.");

        var destination = context.ArtifactsDirectory;
        // Move the complete set within the project filesystem, including on
        // installations where the system temporary directory is on another disk.
        var publication = Path.Combine(context.ProjectBuildDirectory, ".artifacts-" + Guid.NewGuid().ToString("N"));
        var next = Path.Combine(publication, "new");
        var previous = Path.Combine(publication, "previous");
        var preserveBackup = false;
        try
        {
            Directory.CreateDirectory(next);
            foreach (var artifact in artifacts)
            {
                var source = Path.Combine(zephyrOutputDirectory, artifact.Source);
                if (File.Exists(source) || artifact.Source == "zephyr.elf")
                    File.Copy(source, Path.Combine(next, artifact.Destination));
            }
            File.WriteAllBytes(Path.Combine(next, "debug_map.json"), debugMap);

            var hadPrevious = Directory.Exists(destination);
            if (hadPrevious)
                Directory.Move(destination, previous);
            try
            {
                Directory.Move(next, destination);
            }
            catch
            {
                if (hadPrevious)
                {
                    preserveBackup = true;
                    Directory.Move(previous, destination);
                    preserveBackup = false;
                }
                throw;
            }
        }
        finally
        {
            if (!preserveBackup && Directory.Exists(publication))
                Directory.Delete(publication, recursive: true);
        }
    }

    private string GetProjectDirectory(string projectDirectory) =>
        Path.GetFullPath(projectDirectory, Configuration.Environment.Installation.ApplicationDirectory);

    private static string MakeSafeDirectoryName(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var name = new string(value
            .Select(character =>
                character is '/' or '\\' || invalidCharacters.Contains(character)
                    ? '_'
                    : character)
            .ToArray());
        return name is "" or "." or ".." ? "_" + name : name;
    }
}

internal enum BuildStage
{
    StructuredText,
    Zephyr,
    FirmwareUpdate,
    Clean,
    Complete
}

internal sealed record ZephyrBuildContext(
    string ProjectBuildDirectory,
    string GeneratedDirectory,
    string BuildDirectory,
    string ArtifactsDirectory,
    string ApplicationName,
    ZephyrBuildConfiguration Settings);

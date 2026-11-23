// SPDX-License-Identifier: GPL-3.0-or-later
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
            _stage = BuildStage.Zephyr;
            return new BuildProcessOutcome(zephyrProcess, null);
        }

        if (Operation is BuildOperation.Build or BuildOperation.Rebuild or BuildOperation.Download &&
            _stage == BuildStage.Zephyr &&
            _zephyrContext is { } completedContext)
        {
            try
            {
                FirmwareBuildService.PublishBuildArtifacts(completedContext);

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

public sealed class FirmwareBuildService
{
    public FirmwareBuildService() : this(new ZephyrConfiguration())
    {
    }

    public FirmwareBuildService(ZephyrConfiguration configuration)
    {
        Configuration = configuration;
    }

    public ZephyrConfiguration Configuration { get; }

    public FirmwareBuildSession StartVerify(string projectDirectory, string projectName) =>
        new(
            this,
            BuildOperation.Verify,
            CreateStrucppCompilerProcess(projectDirectory, projectName),
            BuildStage.StructuredText,
            null);

    public FirmwareBuildSession StartBuild(string projectDirectory, string projectName) =>
        StartBuild(projectDirectory, projectName, null);

    public FirmwareBuildSession StartBuild(string projectDirectory, string projectName,
        ZephyrBuildConfiguration? project, ZephyrBuildConfiguration? overrides = null)
    {
        var context = ResolveZephyrBuildContext(projectDirectory, project, overrides);
        return new FirmwareBuildSession(
            this,
            BuildOperation.Build,
            CreateStrucppCompilerProcess(projectDirectory, projectName),
            BuildStage.StructuredText,
            context,
            context.Settings.PristineMode!);
    }

    public FirmwareBuildSession StartRebuild(string projectDirectory, string projectName) =>
        StartRebuild(projectDirectory, projectName, null);

    public FirmwareBuildSession StartRebuild(string projectDirectory, string projectName,
        ZephyrBuildConfiguration? project, ZephyrBuildConfiguration? overrides = null)
    {
        var context = ResolveZephyrBuildContext(projectDirectory, project, overrides);
        return new FirmwareBuildSession(
            this,
            BuildOperation.Rebuild,
            CreateStrucppCompilerProcess(projectDirectory, projectName),
            BuildStage.StructuredText,
            context,
            "always");
    }

    public FirmwareBuildSession StartClean(string projectDirectory) =>
        StartClean(projectDirectory, null);

    public FirmwareBuildSession StartClean(string projectDirectory, ZephyrBuildConfiguration? project,
        ZephyrBuildConfiguration? overrides = null)
    {
        projectDirectory = GetProjectDirectory(projectDirectory);
        return new FirmwareBuildSession(
            this,
            BuildOperation.Clean,
            Configuration.Environment.CreateCMake(
                projectDirectory, Configuration.ResolveHostTools(projectDirectory, project, overrides),
                ["-E", "remove_directory", Path.Combine(projectDirectory, "Build")]),
            BuildStage.Clean,
            null);
    }

    public FirmwareBuildSession StartDownload(string projectDirectory, string projectName) =>
        StartDownload(projectDirectory, projectName, null);

    public FirmwareBuildSession StartDownload(string projectDirectory, string projectName,
        ZephyrBuildConfiguration? project, ZephyrBuildConfiguration? overrides = null)
    {
        var context = ResolveZephyrBuildContext(projectDirectory, project, overrides);
        return new FirmwareBuildSession(
            this,
            BuildOperation.Download,
            CreateStrucppCompilerProcess(projectDirectory, projectName),
            BuildStage.StructuredText,
            context,
            context.Settings.PristineMode!);
    }

    private BuildProcess CreateStrucppCompilerProcess(
        string projectDirectory,
        string projectName)
    {
        projectDirectory = GetProjectDirectory(projectDirectory);
        var compiler = StrucppToolchain.GetCompilerCommand();

        var sourceRoot = Path.Combine(projectDirectory, "ProjectFiles");
        var sourcePaths = Directory.Exists(sourceRoot)
            ? Directory.EnumerateFiles(sourceRoot, "*.st", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(projectDirectory, path))
                .Where(path => !IsTestSource(path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

        Directory.CreateDirectory(Path.Combine(projectDirectory, "Build", "Generated"));
        var outputPath = Path.Combine(
            "Build",
            "Generated",
            $"{MakeSafeFileName(projectName)}.cpp");

        var arguments = new List<string>(compiler.PrefixArguments);
        arguments.AddRange(sourcePaths);
        arguments.AddRange(
        [
            "-o",
            outputPath,
            "--no-default-libs",
            "-L",
            "Libraries"
        ]);

        return CreatePlatformProcess(compiler.ExecutablePath, arguments, projectDirectory);
    }

    internal BuildProcess CreateZephyrBuildProcess(ZephyrBuildContext context, string pristineMode)
    {
        var sysbuild = context.Settings.UseSysbuild!.Value;
        var generatedOption = sysbuild ? $"{context.ApplicationName}_RETROPLC_GENERATED_DIR" : "RETROPLC_GENERATED_DIR";
        var sdkDirectory = context.Settings.SdkDirectory!;
        var cachePath = Path.Combine(context.BuildDirectory, "CMakeCache.txt");
        if (pristineMode != "always" && File.Exists(cachePath))
        {
            var cachedSdk = ReadCMakeCachePath(cachePath, "ZEPHYR_SDK_INSTALL_DIR");
            var cachedSysbuild = ReadCMakeCachePath(cachePath, "APP_DIR") is not null;
            if (cachedSdk is null || !SameDirectory(cachedSdk, sdkDirectory) || cachedSysbuild != sysbuild)
            {
                if (pristineMode == "never")
                    throw new InvalidOperationException(
                        "The build directory uses different SDK or sysbuild settings. Rebuild to apply the selected configuration.");
                pristineMode = "always";
            }
        }
        var arguments = new List<string>
        {
            "build", "-p", pristineMode, sysbuild ? "--sysbuild" : "--no-sysbuild", "-b", context.Settings.BoardTarget!,
            "-d", context.BuildDirectory, context.Settings.ApplicationDirectory!, "--"
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

    private ZephyrBuildContext ResolveZephyrBuildContext(string projectDirectory,
        ZephyrBuildConfiguration? project, ZephyrBuildConfiguration? overrides)
    {
        var settings = Configuration.Resolve(projectDirectory, project, overrides);
        if (!ZephyrBoardService.IsBoardTarget(settings.BoardTarget))
            throw new InvalidOperationException(
                "Select a valid Zephyr board target in the project or set RETROPLC_ZEPHYR_BOARD.");
        ZephyrConfiguration.RequireWorkspace(settings.WorkspaceDirectory);
        if (settings.ApplicationDirectory is null ||
            !File.Exists(Path.Combine(settings.ApplicationDirectory, "CMakeLists.txt")))
            throw new DirectoryNotFoundException(
                $"No Zephyr application exists at '{settings.ApplicationDirectory}'. " +
                "Set the project applicationDirectory or RETROPLC_ZEPHYR_APPLICATION.");

        if (settings.SdkDirectory is null)
            throw new InvalidOperationException("Select a Zephyr SDK in the project or set RETROPLC_ZEPHYR_SDK.");
        ZephyrSdkService.ValidateInstallation(settings.SdkDirectory);
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

    internal static void PublishBuildArtifacts(ZephyrBuildContext context)
    {
        var imageBuildDirectory = context.Settings.UseSysbuild!.Value
            ? Path.Combine(context.BuildDirectory, context.ApplicationName)
            : context.BuildDirectory;
        var sdkPackage = ReadCMakeCachePath(Path.Combine(imageBuildDirectory, "CMakeCache.txt"), "Zephyr-sdk_DIR");
        if (sdkPackage is not null && !SameDirectory(sdkPackage, Path.Combine(context.Settings.SdkDirectory!, "cmake")))
            throw new IOException($"Zephyr configured SDK at '{sdkPackage}' instead of the selected SDK at '{context.Settings.SdkDirectory}'.");

        var zephyrOutputDirectory = Path.Combine(imageBuildDirectory, "zephyr");
        var artifacts = new[]
        {
            (Source: "zephyr.elf", Destination: "firmware.elf"),
            (Source: "zephyr.signed.hex", Destination: "firmware.hex"),
            (Source: "zephyr.signed.bin", Destination: "firmware.bin"),
            (Source: "zephyr.uf2", Destination: "firmware.uf2"),
            (Source: "zephyr.map", Destination: "firmware.map")
        };

        Directory.CreateDirectory(context.ArtifactsDirectory);
        foreach (var artifact in artifacts)
        {
            var sourcePath = Path.Combine(zephyrOutputDirectory, artifact.Source);
            var destinationPath = Path.Combine(context.ArtifactsDirectory, artifact.Destination);
            if (File.Exists(destinationPath))
                File.Delete(destinationPath);
            if (!File.Exists(sourcePath))
                continue;

            File.Copy(sourcePath, destinationPath, overwrite: true);
        }
    }

    private string GetProjectDirectory(string projectDirectory) =>
        Path.GetFullPath(projectDirectory, Configuration.Environment.Installation.ApplicationDirectory);

    private static string? ReadCMakeCachePath(string cachePath, string key) =>
        File.Exists(cachePath) ? File.ReadLines(cachePath).FirstOrDefault(line =>
            line.StartsWith(key + ":", StringComparison.Ordinal))?.Split('=', 2).Last() : null;

    private static bool SameDirectory(string first, string second)
    {
        if (!Path.IsPathFullyQualified(first) || !Path.IsPathFullyQualified(second))
            return false;

        return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static bool IsTestSource(string relativePath) =>
        relativePath.StartsWith(
            $"ProjectFiles{Path.DirectorySeparatorChar}Tests{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase) ||
        relativePath.StartsWith("ProjectFiles/Tests/", StringComparison.OrdinalIgnoreCase);

    private static string MakeSafeFileName(string projectName)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        return new string(projectName
            .Select(character => invalidCharacters.Contains(character) ? '_' : character)
            .ToArray());
    }

    private static string MakeSafeDirectoryName(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        return new string(value
            .Select(character =>
                character is '/' or '\\' || invalidCharacters.Contains(character)
                    ? '_'
                    : character)
            .ToArray());
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

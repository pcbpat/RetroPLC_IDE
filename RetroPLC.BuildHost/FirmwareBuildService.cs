using RetroPLC.LanguageServerHost;

namespace RetroPLC.BuildHost;

public enum BuildOperation
{
    Verify,
    Build,
    Download
}

public sealed record BuildProcess(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory);

public sealed record BuildProcessOutcome(BuildProcess? NextProcess, int? ExitCode)
{
    public bool IsComplete => ExitCode.HasValue;
}

public sealed class FirmwareBuildSession
{
    private readonly FirmwareBuildService _service;
    private readonly ZephyrBuildContext? _zephyrContext;
    private BuildStage _stage;

    internal FirmwareBuildSession(
        FirmwareBuildService service,
        BuildOperation operation,
        BuildProcess initialProcess,
        BuildStage stage,
        ZephyrBuildContext? zephyrContext)
    {
        _service = service;
        Operation = operation;
        InitialProcess = initialProcess;
        _stage = stage;
        _zephyrContext = zephyrContext;
    }

    public BuildOperation Operation { get; }

    public BuildProcess InitialProcess { get; }

    public BuildProcessOutcome ProcessExited(int exitCode)
    {
        if (exitCode != 0)
            return Complete(exitCode);

        if (Operation == BuildOperation.Build &&
            _stage == BuildStage.StructuredText &&
            _zephyrContext is { } context)
        {
            _stage = BuildStage.Zephyr;
            return new BuildProcessOutcome(_service.CreateZephyrBuildProcess(context), null);
        }

        if (Operation == BuildOperation.Build &&
            _stage == BuildStage.Zephyr &&
            _zephyrContext is { } completedContext)
        {
            try
            {
                FirmwareBuildService.PublishBuildArtifacts(completedContext);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Unable to publish Zephyr build artifacts: {exception.Message}");
                return Complete(-1);
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
    private const string DefaultBoard = "arduino_opta/stm32h747xx/m7";

    public FirmwareBuildSession StartVerify(string projectDirectory, string projectName) =>
        new(
            this,
            BuildOperation.Verify,
            CreateStrucppCompilerProcess(projectDirectory, projectName),
            BuildStage.StructuredText,
            null);

    public FirmwareBuildSession StartBuild(string projectDirectory, string projectName)
    {
        var context = ResolveZephyrBuildContext(projectDirectory);
        return new FirmwareBuildSession(
            this,
            BuildOperation.Build,
            CreateStrucppCompilerProcess(projectDirectory, projectName),
            BuildStage.StructuredText,
            context);
    }

    public FirmwareBuildSession StartDownload(string projectDirectory)
    {
        var context = ResolveZephyrBuildContext(projectDirectory);
        return new FirmwareBuildSession(
            this,
            BuildOperation.Download,
            CreateWestProcess(
                context,
                ["flash", "-d", context.BuildDirectory]),
            BuildStage.Zephyr,
            context);
    }

    private static BuildProcess CreateStrucppCompilerProcess(
        string projectDirectory,
        string projectName)
    {
        var compilerPath = StrucppToolchain.GetCompilerPath();
        if (!File.Exists(compilerPath))
            throw new FileNotFoundException(
                "The STruC++ compiler executable was not found.", compilerPath);
        StrucppToolchain.EnsureExecutable(compilerPath);

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

        var arguments = new List<string>(sourcePaths)
        {
            "-o",
            outputPath,
            "--no-default-libs",
            "-L",
            "Libraries"
        };

        return CreatePlatformProcess(compilerPath, arguments, projectDirectory);
    }

    internal BuildProcess CreateZephyrBuildProcess(ZephyrBuildContext context) =>
        CreateWestProcess(
            context,
            [
                "build",
                "-p", "always",
                "-b", context.Board,
                "-d", context.BuildDirectory,
                context.ApplicationDirectory,
                "--",
                $"-DRETROPLC_GENERATED_DIR={context.GeneratedDirectory}",
                "-DEXTRA_CFLAGS=-w",
                "-DEXTRA_CXXFLAGS=-w"
            ]);

    private static BuildProcess CreateWestProcess(
        ZephyrBuildContext context,
        List<string> arguments) =>
        CreatePlatformProcess(context.WestExecutable, arguments, context.WorkspaceDirectory);

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

    private static ZephyrBuildContext ResolveZephyrBuildContext(string projectDirectory)
    {
        var workspaceDirectory = Environment.GetEnvironmentVariable("RETROPLC_ZEPHYR_WORKSPACE");
        if (string.IsNullOrWhiteSpace(workspaceDirectory))
        {
            workspaceDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Opta_Zephyr_Test",
                "zephyrproject");
        }

        workspaceDirectory = Path.GetFullPath(workspaceDirectory);
        if (!Directory.Exists(workspaceDirectory))
            throw new DirectoryNotFoundException(
                $"The Zephyr workspace was not found at '{workspaceDirectory}'. " +
                "Set RETROPLC_ZEPHYR_WORKSPACE to the T2 workspace directory.");

        var applicationDirectory = Path.Combine(workspaceDirectory, "retroplc-runtime", "app");
        if (!Directory.Exists(applicationDirectory))
            throw new DirectoryNotFoundException(
                $"The RetroPLC Zephyr application was not found at '{applicationDirectory}'.");

        var configuredWest = Environment.GetEnvironmentVariable("RETROPLC_WEST_EXECUTABLE");
        var bundledWest = OperatingSystem.IsWindows()
            ? Path.Combine(workspaceDirectory, ".venv", "Scripts", "west.exe")
            : Path.Combine(workspaceDirectory, ".venv", "bin", "west");
        var westExecutable = !string.IsNullOrWhiteSpace(configuredWest)
            ? configuredWest
            : File.Exists(bundledWest) ? bundledWest : "west";

        var board = Environment.GetEnvironmentVariable("RETROPLC_ZEPHYR_BOARD");
        if (string.IsNullOrWhiteSpace(board))
            board = DefaultBoard;

        var buildRoot = Path.Combine(projectDirectory, "Build");
        return new ZephyrBuildContext(
            workspaceDirectory,
            applicationDirectory,
            Path.Combine(buildRoot, "Generated"),
            Path.Combine(buildRoot, "Zephyr", MakeSafeDirectoryName(board)),
            Path.Combine(buildRoot, "Artifacts"),
            westExecutable,
            board);
    }

    internal static void PublishBuildArtifacts(ZephyrBuildContext context)
    {
        var zephyrOutputDirectory = Path.Combine(context.BuildDirectory, "zephyr");
        var artifacts = new[]
        {
            (Source: "zephyr.elf", Destination: "firmware.elf"),
            (Source: "zephyr.hex", Destination: "firmware.hex"),
            (Source: "zephyr.bin", Destination: "firmware.bin"),
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
    Complete
}

internal sealed record ZephyrBuildContext(
    string WorkspaceDirectory,
    string ApplicationDirectory,
    string GeneratedDirectory,
    string BuildDirectory,
    string ArtifactsDirectory,
    string WestExecutable,
    string Board);

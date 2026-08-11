using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Mvvm.Controls;
using Iciclecreek.Terminal;
using RetroPLC.LanguageServerHost;
using RetroPLC.Shell.Controls;

namespace RetroPLC.Shell.ViewModels.Docking;

public enum BuildOperation
{
    Verify,
    Build,
    Download
}

public sealed class BuildViewModel : Tool
{
    private const string DefaultBoard = "arduino_opta/stm32h747xx/m7";
    private BuildOperation _activeOperation;
    private bool _compileBeforeZephyrBuild;
    private ZephyrBuildContext? _zephyrBuildContext;

    public BuildViewModel()
    {
        Session = new BuildTerminalSession();
    }

    public event Action<BuildOperation, int>? OperationExited;

    public BuildTerminalSession Session { get; }

    public void PrepareVerify(string projectDirectory, string projectName)
    {
        _activeOperation = BuildOperation.Verify;
        _compileBeforeZephyrBuild = false;
        _zephyrBuildContext = null;
        StartStrucppCompiler(projectDirectory, projectName);
    }

    public void PrepareBuild(string projectDirectory, string projectName)
    {
        _activeOperation = BuildOperation.Build;
        _compileBeforeZephyrBuild = true;
        _zephyrBuildContext = ResolveZephyrBuildContext(projectDirectory);
        StartStrucppCompiler(projectDirectory, projectName);
    }

    public void PrepareDownload(string projectDirectory)
    {
        _activeOperation = BuildOperation.Download;
        _compileBeforeZephyrBuild = false;
        _zephyrBuildContext = ResolveZephyrBuildContext(projectDirectory);
        StartWest(
            _zephyrBuildContext,
            ["flash", "-d", _zephyrBuildContext.BuildDirectory]);
    }

    private void StartStrucppCompiler(string projectDirectory, string projectName)
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

        var outputDirectory = GetGeneratedDirectory(projectDirectory);
        Directory.CreateDirectory(outputDirectory);
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

        string process;
        if (!OperatingSystem.IsWindows())
        {
            process = "/usr/bin/env";
            arguments.Insert(0, compilerPath);
        }
        else
        {
            process = compilerPath;
        }

        StartProcess(process, arguments, projectDirectory);
    }

    private void StartZephyrBuild(ZephyrBuildContext context)
    {
        _compileBeforeZephyrBuild = false;
        StartWest(
            context,
            [
                "build",
                "-p", "always",
                "-b", context.Board,
                "-d", context.BuildDirectory,
                context.ApplicationDirectory,
                "--",
                $"-DRETROPLC_GENERATED_DIR={context.GeneratedDirectory}"
            ]);
    }

    private void StartWest(ZephyrBuildContext context, List<string> arguments)
    {
        string process;
        if (!OperatingSystem.IsWindows())
        {
            process = "/usr/bin/env";
            arguments.Insert(0, context.WestExecutable);
        }
        else
        {
            process = context.WestExecutable;
        }

        StartProcess(process, arguments, context.WorkspaceDirectory);
    }

    private void StartProcess(string process, IReadOnlyList<string> arguments, string startingDirectory)
    {
        // TerminalView launches automatically from its Loaded event.
        var terminal = new ReadOnlyDockTerminalView
        {
            Process = process,
            Args = arguments.ToList(),
            StartingDirectory = startingDirectory
        };
        terminal.ProcessExited += OnProcessExited;

        var previousTerminal = Session.Terminal;
        previousTerminal.ProcessExited -= OnProcessExited;
        Session.RequestFocus();
        Session.Terminal = terminal;
        previousTerminal.Shutdown();
    }

    public override bool OnClose()
    {
        if (!base.OnClose())
        {
            return false;
        }

        Session.Terminal.ProcessExited -= OnProcessExited;
        Session.Terminal.Shutdown();
        return true;
    }

    private void OnProcessExited(object? sender, ProcessExitedEventArgs e)
    {
        if (_activeOperation == BuildOperation.Build &&
            _compileBeforeZephyrBuild &&
            e.ExitCode == 0 &&
            _zephyrBuildContext is { } context)
        {
            Dispatcher.UIThread.Post(() => StartZephyrBuild(context));
            return;
        }

        var exitCode = e.ExitCode;
        if (_activeOperation == BuildOperation.Build &&
            exitCode == 0 &&
            _zephyrBuildContext is { } completedContext)
        {
            try
            {
                PublishBuildArtifacts(completedContext);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Unable to publish Zephyr build artifacts: {exception.Message}");
                exitCode = -1;
            }
        }

        OperationExited?.Invoke(_activeOperation, exitCode);
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

    private static string GetGeneratedDirectory(string projectDirectory) =>
        Path.Combine(projectDirectory, "Build", "Generated");

    private static void PublishBuildArtifacts(ZephyrBuildContext context)
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

            File.Copy(
                sourcePath,
                destinationPath,
                overwrite: true);
        }
    }

    private static bool IsTestSource(string relativePath) =>
        relativePath.StartsWith($"ProjectFiles{Path.DirectorySeparatorChar}Tests{Path.DirectorySeparatorChar}",
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

internal sealed record ZephyrBuildContext(
    string WorkspaceDirectory,
    string ApplicationDirectory,
    string GeneratedDirectory,
    string BuildDirectory,
    string ArtifactsDirectory,
    string WestExecutable,
    string Board);

public sealed partial class BuildTerminalSession : ObservableObject
{
    private bool _focusPending;

    [ObservableProperty]
    private ReadOnlyDockTerminalView _terminal = new();

    public void RequestFocus() => _focusPending = true;

    public bool ConsumeFocusRequest()
    {
        if (!_focusPending)
        {
            return false;
        }

        _focusPending = false;
        return true;
    }
}

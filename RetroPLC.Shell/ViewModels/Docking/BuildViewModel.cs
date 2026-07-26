using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Mvvm.Controls;
using Iciclecreek.Terminal;
using RetroPLC.LanguageServerHost;
using RetroPLC.Shell.Controls;

namespace RetroPLC.Shell.ViewModels.Docking;

public sealed class BuildViewModel : Tool
{
    public BuildViewModel()
    {
        Session = new BuildTerminalSession();
    }

    public event Action<int>? BuildExited;

    public BuildTerminalSession Session { get; }

    public void PrepareRun(string projectDirectory, string projectName)
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

        var outputDirectory = Path.Combine(projectDirectory, "Build");
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine("Build", $"{MakeSafeFileName(projectName)}.cpp");

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

        // Follow the terminal project's Process -> Run example: prepare Process,
        // Args, and StartingDirectory before placing the control in the visual
        // tree. TerminalView launches automatically from its Loaded event.
        var terminal = new ReadOnlyDockTerminalView
        {
            Process = process,
            Args = arguments,
            StartingDirectory = projectDirectory
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

    private void OnProcessExited(object? sender, ProcessExitedEventArgs e) =>
        BuildExited?.Invoke(e.ExitCode);

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

}

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

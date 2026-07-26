using System;
using System.Collections.Generic;
using System.IO;
using Dock.Model.Mvvm.Controls;
using Iciclecreek.Terminal;
using RetroPLC.LanguageServerHost;
using RetroPLC.Shell.Controls;
using RetroPLC.Shell.Models;

namespace RetroPLC.Shell.ViewModels.Docking;

public sealed class LibraryImportViewModel : Tool
{
    public LibraryImportViewModel()
    {
        Session = new BuildTerminalSession();
    }

    public event Action<int>? ImportExited;

    public BuildTerminalSession Session { get; }

    public void PrepareRun(string projectDirectory, CodesysLibraryImport import)
    {
        var extension = Path.GetExtension(import.SourcePath);
        if (!extension.Equals(".lib", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".library", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Choose a CODESYS V2.3 .lib or V3 .library file.");
        }

        var outputDirectory = StrucppToolchain.GetProjectLibraryDirectory(projectDirectory);
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, import.LibraryName + ".stlib");
        if (File.Exists(outputPath))
            throw new IOException($"A project library named '{import.LibraryName}' already exists.");

        var compilerPath = StrucppToolchain.GetCompilerPath();
        if (!File.Exists(compilerPath))
            throw new FileNotFoundException("The bundled STruCpp compiler was not found.", compilerPath);
        StrucppToolchain.EnsureExecutable(compilerPath);

        var arguments = new List<string>
        {
            "--import-lib",
            import.SourcePath,
            "-o",
            "Libraries",
            "--lib-name",
            import.LibraryName,
            "--lib-version",
            import.Version,
            "-L",
            "Libraries"
        };
        if (!string.IsNullOrWhiteSpace(import.Namespace))
        {
            arguments.Add("--lib-namespace");
            arguments.Add(import.Namespace);
        }
        if (!import.IncludeSource)
            arguments.Add("--no-source");

        string process;
        if (OperatingSystem.IsWindows())
        {
            process = compilerPath;
        }
        else
        {
            process = "/usr/bin/env";
            arguments.Insert(0, compilerPath);
        }

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
            return false;

        Session.Terminal.ProcessExited -= OnProcessExited;
        Session.Terminal.Shutdown();
        return true;
    }

    private void OnProcessExited(object? sender, ProcessExitedEventArgs e) =>
        ImportExited?.Invoke(e.ExitCode);

}

// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Diagnostics;

namespace RetroPLC.BuildHost;

public sealed record BuildProcess(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory);

public sealed class BuildEnvironment(string? directory = null, string? pixiExecutable = null)
{
    public BuildProcess CreateWestCommand(string command, string project, params string[] options) =>
        CreateCommand(["run", "--quiet", "west", command, Path.GetFullPath(project), .. options]);

    public BuildProcess CreateTerminal() => CreateCommand(["shell", "--no-install", "--locked"]);

    private BuildProcess CreateCommand(IReadOnlyList<string> arguments)
    {
        var root = Path.GetFullPath(directory ??
            Environment.GetEnvironmentVariable("RETROPLC_BUILD_ENVIRONMENT") ??
            Path.Combine(AppContext.BaseDirectory, "RetroPLC_BuildEnvironment"));
        if (!File.Exists(Path.Combine(root, "pixi.toml")) || !File.Exists(Path.Combine(root, "pixi.lock")))
            throw new DirectoryNotFoundException(
                $"BuildEnvironment is missing or incomplete at '{root}'. Set RETROPLC_BUILD_ENVIRONMENT to the installed workspace.");

        var executable = pixiExecutable ?? Environment.GetEnvironmentVariable("RETROPLC_PIXI_EXECUTABLE") ??
            (OperatingSystem.IsWindows() ? "pixi.exe" : "pixi");
        var candidates = Path.GetFileName(executable) != executable
            ? [Path.GetFullPath(executable)]
            : (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(path => Path.GetFullPath(Path.Combine(path, executable)));
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return new BuildProcess(candidate, arguments, root);
        }

        throw new FileNotFoundException($"Pixi executable '{executable}' was not found. Check PATH or RETROPLC_PIXI_EXECUTABLE.");
    }

    internal static async Task<int> RunAsync(BuildProcess command, Action<string> stdout, Action<string> stderr,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(command.Executable)
        {
            WorkingDirectory = command.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.Environment["PIXI_NO_INSTALL"] = "true";
        start.Environment["PIXI_LOCKED"] = "true";
        foreach (var argument in command.Arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new IOException($"Could not start '{command.Executable}'.");
        var output = DrainAsync(process.StandardOutput, stdout, cancellationToken);
        var errors = DrainAsync(process.StandardError, stderr, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(output, errors).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception error) when ((error is InvalidOperationException or Win32Exception) && process.HasExited)
            {
            }
            await process.WaitForExitAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(output, errors).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            throw;
        }
    }

    private static async Task DrainAsync(StreamReader stream, Action<string> report, CancellationToken cancellationToken)
    {
        while (await stream.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            report(line);
    }
}

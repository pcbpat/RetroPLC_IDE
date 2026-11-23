// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using RetroPLC.LanguageServerHost;

namespace RetroPLC.BuildHost;

/// <summary>Resolves the application installation independently of the working directory.</summary>
public sealed class RetroPlcInstallation
{
    public RetroPlcInstallation(string? applicationDirectory = null)
    {
        ApplicationDirectory = applicationDirectory is null
            ? StrucppToolchain.ApplicationDirectory
            : Path.GetFullPath(applicationDirectory, AppContext.BaseDirectory);
    }

    public string ApplicationDirectory { get; }

    public string ResolveHostTools(string? directory = null) =>
        Path.GetFullPath(directory ??
                         System.Environment.GetEnvironmentVariable("RETROPLC_AC6_DIRECTORY") ??
                         ".zinstaller", ApplicationDirectory);

}

/// <summary>Creates process-local AC6 sessions for both PTY and captured commands.</summary>
public sealed class Ac6Environment
{
    // Keep initialization in the child. This applies AC6's actual configuration
    // without serializing inherited environment variables into PTY arguments.
    private const string Initialize = """
        root=$(cd -- "$1" && pwd -P) || exit 1
        mode=$2
        sdk=$3
        shift 3

        fail() { printf 'RetroPLC AC6: %s\n' "$*" >&2; exit 1; }
        unset BASH_ENV PYTHONHOME PYTHONPATH VIRTUAL_ENV
        export PYTHON_VENV_PATH="$root/.venv"
        venv_bin="$PYTHON_VENV_PATH/bin"
        export PATH="$venv_bin:$PATH"

        for tool in python west; do
            [[ -x "$venv_bin/$tool" ]] ||
                fail "Missing or unusable $venv_bin/$tool. Repair the AC6 virtual environment."
        done
        # A venv symlink must point to managed Python, not a system interpreter.
        python_target="$venv_bin/python"
        while [[ -L "$python_target" ]]; do
            target=$(/usr/bin/readlink "$python_target") || fail "Could not resolve managed Python."
            case "$target" in
                /*) python_target="$target" ;;
                *) python_target="${python_target%/*}/$target" ;;
            esac
        done
        python_directory=$(cd -- "${python_target%/*}" && pwd -P) || fail "Could not resolve managed Python."
        [[ "$python_directory" == "$root/"* ]] || fail "The AC6 venv points to a Python outside $root."
        if [[ $OSTYPE == linux* && "$python_target" == *.AppImage && ! -w /dev/fuse &&
              -z ${APPIMAGE_EXTRACT_AND_RUN+x} ]]; then
            export APPIMAGE_EXTRACT_AND_RUN=1
        fi
        "$venv_bin/python" --version >/dev/null ||
            fail "Managed Python could not start. For AppImage Python, check FUSE or set APPIMAGE_EXTRACT_AND_RUN=1."
        source "$root/env.sh" >&2 || fail "Could not load $root/env.sh."
        if [[ ${VIRTUAL_ENV:-} != "$PYTHON_VENV_PATH" ]]; then
            source "$venv_bin/activate" >&2 || fail "Could not activate $PYTHON_VENV_PATH."
        fi
        [[ ${VIRTUAL_ENV:-} == "$PYTHON_VENV_PATH" ]] ||
            fail "The AC6 venv points elsewhere. Repair the hosttools installation at $root."
        export PATH="$venv_bin:$PATH"
        unset -f python west cmake ninja
        for tool in cmake ninja; do
            executable=$(type -P "$tool") || fail "Configure $tool in $root/env.yml."
            executable=$("$venv_bin/python" -c 'import os, sys; print(os.path.realpath(sys.argv[1]))' "$executable") ||
                fail "Could not resolve $tool."
            [[ "$executable" == "$root/tools/"* && -x "$executable" ]] ||
                fail "$tool must resolve under $root/tools. Check $root/env.yml."
        done
        case "$mode" in
            west)
                unset ZEPHYR_BASE ZEPHYR_SDK_INSTALL_DIR ZEPHYR_TOOLCHAIN_VARIANT
                if [[ -n "$sdk" ]]; then
                    export ZEPHYR_SDK_INSTALL_DIR="$sdk" ZEPHYR_TOOLCHAIN_VARIANT=zephyr
                fi
                export WEST_CONFIG_LOCAL="$PWD/.west/config"
                # Run the managed entry point with the exact managed interpreter.
                exec "$venv_bin/python" "$venv_bin/west" "$@"
                ;;
            cmake) exec "$(type -P cmake)" "$@" ;;
            shell) exec /bin/bash --noprofile --norc -i ;;
        esac
        """;

    private const string InitializeWindows = """
        $ErrorActionPreference = 'Stop'
        Remove-Item Env:PYTHONHOME, Env:PYTHONPATH, Env:VIRTUAL_ENV -ErrorAction SilentlyContinue
        $env:PYTHON_VENV_PATH = Join-Path $root '.venv'
        $venvBin = Join-Path $env:PYTHON_VENV_PATH 'Scripts'
        $python = Join-Path $venvBin 'python.exe'
        $west = Join-Path $venvBin 'west.exe'
        foreach ($tool in @($python, $west)) {
            if (!(Test-Path -LiteralPath $tool -PathType Leaf)) {
                throw "Missing $tool. Repair the AC6 virtual environment."
            }
        }

        $env:PATH = "$venvBin;$env:PATH"
        $basePrefix = & $python -c 'import sys; print(sys.base_prefix)'
        $prefix = $root.TrimEnd('\') + '\'
        if ($LASTEXITCODE -ne 0 -or !$basePrefix -or
            !$basePrefix.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "The AC6 venv requires managed Python under $root."
        }

        . (Join-Path $root 'env.ps1') | ForEach-Object { [Console]::Error.WriteLine($_) }
        if ($env:VIRTUAL_ENV -ne $env:PYTHON_VENV_PATH) {
            . (Join-Path $venvBin 'Activate.ps1')
        }
        if ($env:VIRTUAL_ENV -ne $env:PYTHON_VENV_PATH) {
            throw "The AC6 venv points elsewhere. Repair the hosttools installation at $root."
        }

        $env:PATH = "$venvBin;$env:PATH"
        $toolsPrefix = Join-Path $root 'tools'
        $toolsPrefix += '\'
        $managed = @{}
        foreach ($tool in @('cmake.exe', 'ninja.exe')) {
            $found = (Get-Command $tool -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1)
            if (!$found -or !$found.Source.StartsWith($toolsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw "$tool must resolve under $toolsPrefix. Check env.yml."
            }
            $managed[$tool] = $found.Source
        }

        if ($mode -eq 'west') {
            Remove-Item Env:ZEPHYR_BASE, Env:ZEPHYR_SDK_INSTALL_DIR, Env:ZEPHYR_TOOLCHAIN_VARIANT -ErrorAction SilentlyContinue
            if ($sdk) {
                $env:ZEPHYR_SDK_INSTALL_DIR = $sdk
                $env:ZEPHYR_TOOLCHAIN_VARIANT = 'zephyr'
            }
            $env:WEST_CONFIG_LOCAL = Join-Path (Get-Location).Path '.west\config'
            & $west @commandArguments
            exit $LASTEXITCODE
        }
        if ($mode -eq 'cmake') {
            & $managed['cmake.exe'] @commandArguments
            exit $LASTEXITCODE
        }
        """;

    public Ac6Environment(RetroPlcInstallation? installation = null)
    {
        Installation = installation ?? new RetroPlcInstallation();
    }

    public RetroPlcInstallation Installation { get; }

    public BuildProcess CreateTerminal(string? workingDirectory = null, string? hostToolsDirectory = null) =>
        CreateProcess("shell", workingDirectory ?? Installation.ApplicationDirectory, hostToolsDirectory, null, []);

    internal BuildProcess CreateWest(
        string workspace, string? hostToolsDirectory, string? sdkDirectory, IReadOnlyList<string> arguments) =>
        CreateProcess("west", workspace, hostToolsDirectory, sdkDirectory, arguments);

    internal BuildProcess CreateCMake(
        string workingDirectory, string? hostToolsDirectory, IReadOnlyList<string> arguments) =>
        CreateProcess("cmake", workingDirectory, hostToolsDirectory, null, arguments);

    private BuildProcess CreateProcess(
        string mode, string workingDirectory, string? hostToolsDirectory, string? sdkDirectory,
        IReadOnlyList<string> arguments)
    {
        var root = Installation.ResolveHostTools(hostToolsDirectory);
        var windows = OperatingSystem.IsWindows();
        if (!windows && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Managed AC6 launches require Bash or Windows PowerShell.");

        if (mode == "west")
        {
            var west = windows ? Path.Combine(root, ".venv", "Scripts", "west.exe")
                : Path.Combine(root, ".venv", "bin", "west");
            if (!File.Exists(west))
                throw new FileNotFoundException(
                    $"AC6 managed west was not found at '{west}'. Check the AC6 hosttools installation.", west);
        }

        var envScript = Path.Combine(root, windows ? "env.ps1" : "env.sh");
        foreach (var file in new[] { envScript, Path.Combine(root, "env.yml"), Path.Combine(root, "env.py") })
        {
            if (!File.Exists(file))
                throw new FileNotFoundException(
                    $"AC6 hosttools are incomplete at '{root}'. Install AC6 hosttools there or set RETROPLC_AC6_DIRECTORY.", file);
        }

        if (windows)
            return CreateWindowsProcess(root, mode, sdkDirectory, arguments, workingDirectory);

        return new BuildProcess("/usr/bin/env",
            ["-u", "BASH_ENV", "/bin/bash", "--noprofile", "--norc", "-c", Initialize, "retroplc-ac6",
                root, mode, sdkDirectory ?? string.Empty, .. arguments], workingDirectory);
    }

    private static BuildProcess CreateWindowsProcess(string root, string mode, string? sdkDirectory,
        IReadOnlyList<string> arguments, string workingDirectory)
    {
        static string Literal(string value) => "'" + value.Replace("'", "''") + "'";

        // EncodedCommand avoids passing script text through the PTY's Windows command line.
        var script = $"$root = {Literal(Path.TrimEndingDirectorySeparator(root))}\n" +
                     $"$mode = {Literal(mode)}\n$sdk = {Literal(sdkDirectory ?? "")}\n" +
                     $"$commandArguments = @({string.Join(", ", arguments.Select(Literal))})\n" +
                     "try {\n" + InitializeWindows + "\n} catch { [Console]::Error.WriteLine($_); exit 1 }";
        var executable = Path.Combine(System.Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(executable))
            throw new FileNotFoundException("Windows PowerShell is required to load AC6 env.ps1.", executable);

        List<string> launchArguments = ["-NoLogo", "-NoProfile"];
        launchArguments.Add(mode == "shell" ? "-NoExit" : "-NonInteractive");
        launchArguments.AddRange(["-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))]);
        return new BuildProcess(executable, launchArguments, workingDirectory);
    }

    internal static async Task<CommandResult> RunAsync(
        BuildProcess command, CancellationToken cancellationToken, IProgress<string>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo(command.Executable)
        {
            WorkingDirectory = command.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        foreach (var argument in command.Arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception exception)
        {
            throw new IOException($"Could not launch '{command.Executable}' in '{command.WorkingDirectory}'.", exception);
        }

        process.StandardInput.Close();
        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
                // The child may have exited between the check and the kill.
            }
        });

        var stdout = ReadOutputAsync(process.StandardOutput, progress, cancellationToken);
        var stderr = ReadOutputAsync(process.StandardError, progress, cancellationToken);
        await Task.WhenAll(process.WaitForExitAsync(cancellationToken), stdout, stderr).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new CommandResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static async Task<string> ReadOutputAsync(
        StreamReader reader, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (progress is null)
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        var output = new System.Text.StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            output.AppendLine(line);
            progress.Report(line);
        }

        return output.ToString();
    }
}

internal sealed record CommandResult(int ExitCode, string Output, string Error)
{
    public void EnsureSuccess(string operation)
    {
        if (ExitCode != 0)
            throw new InvalidOperationException(
                $"{operation} failed (exit {ExitCode}). {string.Join(" ", new[] { Error.Trim(), Output.Trim() }.Where(s => s.Length > 0))}");
    }
}

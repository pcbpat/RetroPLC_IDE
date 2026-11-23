// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using RetroPLC.BuildHost;
using RetroPLC.LanguageServerHost;

if (!OperatingSystem.IsLinux())
{
    Console.WriteLine("The AC6 process fixture requires Linux.");
    return 0;
}

if (args is ["--live", var liveProject])
{
    await using var live = new FirmwareBuildService();
    var projectPath = Path.GetFullPath(liveProject);
    var settings = await live.Configuration.ResolveAsync(projectPath);
    var metadata = await live.Configuration.GetWorkspaceInfoAsync(settings.WorkspaceDirectory!);
    var projects = await live.Configuration.GetProjectsAsync(metadata.Directory, ["zephyr"]);
    var configInfo = await live.Configuration.GetWestConfigAsync(metadata.Directory, ["manifest.path"]);
    Check(metadata.ZephyrCloned && projects.Single().Directory == metadata.ZephyrDirectory &&
          configInfo.Values["manifest.path"] is not null, "live workspace and Zephyr project metadata");
    Console.WriteLine($"west manifest {metadata.ManifestFile}, Zephyr checkout {metadata.ZephyrDirectory}.");

    await RunSessionAsync(await live.StartBuildAsync(projectPath, "Test"));
    await RunSessionAsync(await live.StartCleanAsync(projectPath));
    Check(!Directory.Exists(Path.Combine(projectPath, "Build")), "live clean removed build output");
    await RunSessionAsync(await live.StartRebuildAsync(projectPath, "Test"));
    Check(File.Exists(Path.Combine(projectPath, "Build", "Artifacts", "firmware.bin")), "live rebuild published firmware");

    var missing = await ExpectAsync<DirectoryNotFoundException>(() => live.StartBuildAsync(projectPath, "Test",
        overrides: new() { SdkDirectory = "missing-sdk" }));
    Check(missing.Message.Contains("missing-sdk"), "live missing SDK path diagnostic");
    Console.WriteLine(missing.Message);

    Directory.CreateDirectory(Path.Combine(projectPath, "empty-sdk"));
    var incompatible = await live.StartBuildAsync(projectPath, "Test",
        overrides: new() { SdkDirectory = "empty-sdk", BuildDirectory = "sdk-error-build" });
    var source = await RunAsync(incompatible.InitialProcess);
    Check(source.ExitCode == 0, "live SDK failure reached Zephyr after ST compilation");
    var failedSdkBuild = await RunAsync(incompatible.ProcessExited(source.ExitCode).NextProcess!, timeoutSeconds: 60);
    Console.Error.WriteLine(failedSdkBuild.Error);
    Check(failedSdkBuild.ExitCode != 0 && failedSdkBuild.Error.Contains("Zephyr-sdk", StringComparison.OrdinalIgnoreCase),
        "CMake rejected the incomplete SDK");
    return 0;
}

Trace.Listeners.Add(new ConsoleTraceListener(useErrorStream: true));
var root = Directory.CreateTempSubdirectory("retroplc-buildhost-").FullName;
try
{
    var development = new RetroPlcInstallation();
    Check(File.Exists(Path.Combine(development.ApplicationDirectory, "RetroPLC.sln")),
        "development root from application directory");
    var project = Directory.CreateDirectory(Path.Combine(root, "project with spaces")).FullName;
    var workspace = Directory.CreateDirectory(Path.Combine(root, ".zephyr", "workspace")).FullName;
    Directory.CreateDirectory(Path.Combine(workspace, ".west"));
    File.WriteAllText(Path.Combine(workspace, ".west", "config"), "[manifest]\npath = retroplc-runtime\n");
    var manifest = Path.Combine(workspace, "retroplc-runtime", "west.yml");
    Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
    File.WriteAllText(manifest, """
        manifest:
          projects:
            - name: zephyr
              url: https://example.invalid/zephyr
              path: zephyr
        """);
    var application = Directory.CreateDirectory(Path.Combine(workspace, "retroplc-runtime", "app")).FullName;
    File.WriteAllText(Path.Combine(application, "CMakeLists.txt"), "# fixture");
    var zephyrDirectory = Directory.CreateDirectory(Path.Combine(workspace, "zephyr")).FullName;
    var git = await RunAsync(new BuildProcess("git", ["init", "--quiet"], zephyrDirectory));
    Check(git.ExitCode == 0, "temporary Zephyr Git checkout");
    var sdkDirectory = Directory.CreateDirectory(Path.Combine(root, ".zephyr", "sdk")).FullName;

    var ac6 = Directory.CreateDirectory(Path.Combine(root, ".zinstaller")).FullName;
    File.WriteAllText(Path.Combine(ac6, "env.yml"), "# fixture");
    File.WriteAllText(Path.Combine(ac6, "env.py"), "# fixture");
    File.WriteAllText(Path.Combine(ac6, "env.sh"), """
        export VIRTUAL_ENV="$PYTHON_VENV_PATH"
        export PATH="${BASH_SOURCE[0]%/*}/tools/host:$PATH"
        echo "AC6 startup output"
        """);
    var python = Path.Combine(ac6, ".venv", "bin", "python");
    var managedPython = Path.Combine(development.ApplicationDirectory, ".zinstaller", ".venv", "bin", "python");
    if (!File.Exists(managedPython))
        throw new FileNotFoundException("Run setup.sh to install the managed Python used by the gRPC fixture.", managedPython);
    var backendPackage = Directory.CreateDirectory(Path.Combine(root, "tools", "zephyr-backend", "retroplc_zephyr")).FullName;
    foreach (var file in Directory.EnumerateFiles(Path.Combine(development.ApplicationDirectory,
                 "tools", "zephyr-backend", "retroplc_zephyr"), "*.py"))
        File.Copy(file, Path.Combine(backendPackage, Path.GetFileName(file)));
    File.WriteAllText(Path.Combine(backendPackage, "__main__.py"), """
        import os
        import sys
        from pathlib import Path
        from time import sleep
        from .transport import Backend, main

        get_projects = Backend.get_projects

        def delayed_projects(self, request):
            if request.get("names") == ["slow-fixture"]:
                (self.root / "request-started").touch()
                sleep(1)
                return {"projects": []}
            return get_projects(self, request)

        Backend.get_projects = delayed_projects

        root = Path(sys.argv[sys.argv.index("--root") + 1])
        endpoint = Path(sys.argv[sys.argv.index("--endpoint-file") + 1])
        with (root / "startup.log").open("a") as log:
            log.write(f"{os.getpid()}\n{endpoint}\n{endpoint.parent.stat().st_mode & 0o777}\n")
        main()
        """);
    WriteExecutable(python, $$"""
        case "$1" in
            -s) exec "{{managedPython}}" "$@" ;;
            --version) echo "Python fixture" ;;
            -c) exec /usr/bin/readlink -f -- "$3" ;;
            *) exec "$@" ;;
        esac
        """);
    WriteExecutable(Path.Combine(ac6, ".venv", "bin", "west"), """
        case "$1 $2" in
            'build -p')
                printf '%s\n' "$ZEPHYR_SDK_INSTALL_DIR" "$ZEPHYR_TOOLCHAIN_VARIANT" "$@"
                [[ "$WEST_CONFIG_LOCAL" == "$PWD/.west/config" ]] || exit 9
                for argument in "$@"; do
                    if [[ "$argument" == '-DBOARD:STRING=invalid_board' ]]; then
                        echo "CMake: unsupported board invalid_board" >&2
                        exit 7
                    fi
                done
                ;;
            *) echo 'West fixture' ;;
        esac
        """);
    WriteExecutable(Path.Combine(ac6, "tools", "host", "cmake"), """
        if [[ "$1 $2" == "-E remove_directory" ]]; then
            rm -rf -- "$3"
        fi
        """);
    WriteExecutable(Path.Combine(ac6, "tools", "host", "ninja"), "exit 0");

    await using var environment = new Ac6Environment(new RetroPlcInstallation(root));
    Environment.SetEnvironmentVariable("RETROPLC_AC6_DIRECTORY", null);
    Check(environment.Installation.ResolveHostTools() == ac6, "installation path");
    Environment.SetEnvironmentVariable("RETROPLC_AC6_DIRECTORY", "missing");
    Expect<FileNotFoundException>(() => environment.CreateTerminal(project));
    Environment.SetEnvironmentVariable("RETROPLC_AC6_DIRECTORY", null);

    var ide = new ZephyrBuildConfiguration
    {
        BoardTarget = "ide_board",
        WorkspaceDirectory = ".zephyr/workspace",
        HostToolsDirectory = ".zinstaller",
        CMakeOptions = new() { ["MERGED"] = "ide", ["IDE_ONLY"] = "ON" }
    };
    var config = new ZephyrConfiguration(environment, ide);
    Environment.SetEnvironmentVariable("RETROPLC_ZEPHYR_WORKSPACE", null);
    Environment.SetEnvironmentVariable("RETROPLC_ZEPHYR_APPLICATION", null);
    Environment.SetEnvironmentVariable("RETROPLC_ZEPHYR_SDK", null);
    Environment.SetEnvironmentVariable("RETROPLC_ZEPHYR_BOARD", null);
    Environment.SetEnvironmentVariable("ZEPHYR_SDK_INSTALL_DIR", "/unrelated/system/sdk");
    var defaultConfig = new ZephyrConfiguration(environment);
    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel();
        await ExpectAsync<OperationCanceledException>(() => defaultConfig.ResolveAsync(project, cancellationToken: cancelled.Token));
    }
    var startupLog = Path.Combine(root, "startup.log");
    Check(!File.Exists(startupLog), "pre-cancelled request does not start Python");
    var previousDirectory = Environment.CurrentDirectory;
    try
    {
        Environment.CurrentDirectory = Path.GetTempPath();
        var defaults = await defaultConfig.ResolveAsync(Path.GetRelativePath(root, project));
        Check(!File.Exists(startupLog), "local settings do not launch Python");
        var requests = await Task.WhenAll(Enumerable.Range(0, 8).Select(
            _ => defaultConfig.GetWorkspaceInfoAsync(".zephyr/workspace")));
        Check(requests.All(info => info.ManifestFile == manifest && info.ZephyrCloned &&
                                 info.ZephyrDirectory == zephyrDirectory), "workspace RPC returns west metadata");
        var startup = File.ReadAllLines(startupLog);
        Check(startup.Length == 3, "concurrent requests use one backend process");
        Check(!Directory.Exists(Path.GetDirectoryName(startup[1])), "private endpoint directory was removed");
        Check(startup[2] == "448", "endpoint directory is private (0700)");
        Check(defaults.WorkspaceDirectory == workspace && defaults.ApplicationDirectory == application &&
              defaults.SdkDirectory == sdkDirectory && defaults.BoardTarget == "arduino_opta/stm32h747xx/m7",
            "flat SDK, single workspace, lowercase Runtime and board defaults are independent of cwd");
    }
    finally
    {
        Environment.CurrentDirectory = previousDirectory;
    }

    var customSdk = Path.Combine(root, "custom sdk");
    var customDefaults = new ZephyrConfiguration(environment, ide with { SdkDirectory = "custom sdk" });
    Check((await customDefaults.ResolveAsync(project)).SdkDirectory == customSdk, "configured IDE SDK");
    Environment.SetEnvironmentVariable("RETROPLC_ZEPHYR_SDK", "custom sdk");
    Check((await defaultConfig.ResolveAsync(project)).SdkDirectory == customSdk, "explicit SDK environment override");
    Environment.SetEnvironmentVariable("RETROPLC_ZEPHYR_SDK", null);

    var settings = new ZephyrBuildConfiguration
    {
        BoardTarget = "fixture_board@1.0/soc/cpu",
        SdkDirectory = "../.zephyr/sdk",
        CMakeOptions = new() { ["MERGED"] = "project", ["DTC_OVERLAY_FILE"] = "board overlay.dts" }
    };
    var resolved = await config.ResolveAsync(project, settings, new() { BoardTarget = "override", CMakeOptions = new() { ["MERGED"] = "operation" } });
    Check(resolved.BoardTarget == "override" && resolved.SdkDirectory == sdkDirectory, "operation and project precedence");
    Check(resolved.WorkspaceDirectory == workspace && resolved.HostToolsDirectory == ac6, "IDE relative paths");
    Check(resolved.CMakeOptions!["MERGED"] == "operation" && resolved.CMakeOptions["IDE_ONLY"] == "ON", "CMake options");
    var alternate = await defaultConfig.ResolveAsync(project, new() { WorkspaceDirectory = "alternate-workspace" },
        new() { ApplicationDirectory = "alternate app", BuildDirectory = "alternate build" });
    Check(alternate.WorkspaceDirectory == Path.Combine(project, "alternate-workspace") &&
          alternate.ApplicationDirectory == Path.Combine(project, "alternate app") &&
          alternate.BuildDirectory == Path.Combine(project, "alternate build"),
        "project and operation directory overrides stay relative to the project");
    await ExpectAsync<FileNotFoundException>(() => config.GetWorkspaceInfoAsync("missing"));
    var projects = await config.GetProjectsAsync(workspace, ["zephyr"]);
    var westConfig = await config.GetWestConfigAsync(workspace, ["manifest.path", "manifest.unknown"]);
    Check(projects.Single() is { Name: "zephyr", Cloned: true } &&
          westConfig.Values["manifest.path"] == "retroplc-runtime" && westConfig.Values["manifest.unknown"] is null,
        "project and west configuration RPCs");

    var boards = config.GetBoardService(project, settings);
    await ExpectAsync<NotSupportedException>(() => boards.GetAvailableBoardsAsync());
    await ExpectAsync<NotSupportedException>(() => boards.ValidateBoardAsync(settings.BoardTarget!));
    using (var cancelled = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
    {
        var request = config.GetProjectsAsync(workspace, ["slow-fixture"], cancelled.Token);
        while (!File.Exists(Path.Combine(root, "request-started")))
            await Task.Delay(10, cancelled.Token);
        await cancelled.CancelAsync();
#pragma warning disable VSTHRD003
        await ExpectAsync<OperationCanceledException>(() => request);
#pragma warning restore VSTHRD003
    }

    var terminal = await config.CreateTerminalAsync(project, settings);
    var terminalRun = await RunAsync(terminal, "pwd\nwhich python\nwhich west\nwhich cmake\nwhich ninja\nexit\n");
    Check(terminalRun.ExitCode == 0 && terminalRun.Output.Contains(project) &&
        terminalRun.Output.Contains(Path.Combine(ac6, ".venv", "bin", "python")) &&
        terminalRun.Output.Contains(Path.Combine(ac6, ".venv", "bin", "west")) &&
        terminalRun.Output.Contains(Path.Combine(ac6, "tools", "host", "cmake")) &&
        terminalRun.Output.Contains(Path.Combine(ac6, "tools", "host", "ninja")), "interactive tool resolution");
    var ninja = Path.Combine(ac6, "tools", "host", "ninja");
    File.Move(ninja, ninja + ".disabled");
    var invalid = await RunAsync(terminal, "exit\n");
    Check(invalid.ExitCode != 0 && invalid.Error.Contains("ninja"), "system Ninja fallback rejected");
    File.Move(ninja + ".disabled", ninja);

    var tools = Directory.CreateDirectory(Path.Combine(root, "strucpp-tools")).FullName;
    Environment.SetEnvironmentVariable("RETROPLC_TOOLS_DIRECTORY", tools);
    var missingNode = Expect<FileNotFoundException>(() => StrucppToolchain.GetCompilerCommand());
    Check(missingNode.Message.Contains(Path.Combine(tools, "node", "bin", "node")), "missing private Node diagnostic");
    var node = Path.Combine(tools, "node", "bin", "node");
    WriteExecutable(node, "echo 'ST compiler fixture'");
    var missingCompiler = Expect<FileNotFoundException>(() => StrucppToolchain.GetCompilerCommand());
    Check(missingCompiler.Message.Contains("tools/strucpp"), "missing compiler diagnostic");
    var missingServer = Expect<FileNotFoundException>(() => StrucppToolchain.GetLanguageServerCommand());
    Check(missingServer.Message.Contains("language server"), "missing language-server diagnostic");
    Directory.CreateDirectory(Path.Combine(tools, "strucpp", "dist", "node"));
    File.WriteAllText(Path.Combine(tools, "strucpp", "dist", "node", "cli.js"), "// fixture");
    WriteExecutable(Path.Combine(tools, "mcumgr", "mcumgrctl"), "echo 'Firmware updater fixture'");

    var service = new FirmwareBuildService(config);
    var defaultBuild = new FirmwareBuildService(defaultConfig);
    var westPath = Path.Combine(ac6, ".venv", "bin", "west");
    foreach (var required in new[] { westPath, Path.Combine(application, "CMakeLists.txt") })
    {
        File.Move(required, required + ".fixture-hidden");
        try
        {
            await ExpectAsync<IOException>(() => defaultBuild.StartBuildAsync(project, "Test"));
        }
        finally
        {
            File.Move(required + ".fixture-hidden", required);
        }
    }
    await ExpectAsync<FileNotFoundException>(() => defaultBuild.StartBuildAsync(project, "Test",
        overrides: new() { WorkspaceDirectory = "missing-workspace" }));
    await ExpectAsync<DirectoryNotFoundException>(() => defaultBuild.StartBuildAsync(project, "Test",
        overrides: new() { SdkDirectory = "missing-sdk" }));
    Check((await defaultBuild.StartBuildAsync(project, "Test")).ProcessExited(0).NextProcess is { } defaultWest &&
          defaultWest.WorkingDirectory == workspace && defaultWest.Arguments.Contains("arduino_opta/stm32h747xx/m7"),
        "firmware stage uses provisioned paths and board without overrides");
    var invalidBoard = await service.StartBuildAsync(project, "Test", settings with { BoardTarget = "invalid_board" });
    var westFailure = await RunAsync(invalidBoard.ProcessExited(0).NextProcess!);
    Check(westFailure.Error.Contains("unsupported board invalid_board") &&
          invalidBoard.ProcessExited(westFailure.ExitCode) is { ExitCode: 7, NextProcess: null },
        "west diagnostics and failure exit code reach the build output pipeline");
    var verify = service.StartVerify(project, "Test");
    Check((await RunAsync(verify.InitialProcess)).ExitCode == 0 && verify.ProcessExited(0).ExitCode == 0, "ST verification stage");
    var failed = await service.StartDownloadAsync(project, "Test", settings);
    Check(failed.ProcessExited(7) is { ExitCode: 7, NextProcess: null }, "failed ST stage stops the build");

    var build = await service.StartBuildAsync(project, "Test", settings);
    var westBuild = build.ProcessExited(0).NextProcess!;
    var result = await RunAsync(westBuild);
    Check(result.ExitCode == 0 && result.Output.Contains(sdkDirectory) &&
        result.Output.Contains(settings.BoardTarget!) && result.Output.Contains("--sysbuild") &&
        result.Output.Contains("-Dapp_RETROPLC_GENERATED_DIR=" + Path.Combine(project, "Build", "Generated")) &&
        result.Output.Contains("-Dapp_ZEPHYR_SDK_INSTALL_DIR=" + sdkDirectory) &&
        result.Output.Contains("-Dapp_Zephyr-sdk_DIR=" + Path.Combine(sdkDirectory, "cmake")) &&
        westBuild.Arguments.Contains("-DDTC_OVERLAY_FILE=board overlay.dts") &&
        result.Output.Split('\n').Contains("zephyr") && westBuild.WorkingDirectory == workspace &&
        westBuild.Arguments[westBuild.Arguments.ToList().IndexOf("-s") + 1] == application,
        "West build input and environment");
    var buildDirectory = westBuild.Arguments[westBuild.Arguments.ToList().IndexOf("-d") + 1];
    var output = Directory.CreateDirectory(Path.Combine(buildDirectory, "app", "zephyr")).FullName;
    File.WriteAllText(Path.Combine(output, "zephyr.signed.bin"), "firmware fixture");
    Check(build.ProcessExited(0).ExitCode == 0 &&
        File.Exists(Path.Combine(project, "Build", "Artifacts", "firmware.bin")), "artifact publishing");
    Check((await service.StartBuildAsync(project, "Test", settings)).ProcessExited(0).NextProcess!.Arguments.Contains("auto"),
        "west controls automatic pristine handling");
    Check((await service.StartBuildAsync(project, "Test", settings with { PristineMode = "never" }))
          .ProcessExited(0).NextProcess!.Arguments.Contains("never"), "explicit pristine mode forwarded");
    Check((await service.StartRebuildAsync(project, "Test", settings)).ProcessExited(0).NextProcess!.Arguments.Contains("always"), "pristine rebuild");

    var download = await service.StartDownloadAsync(project, "Test", settings);
    download.ProcessExited(0);
    var updater = download.ProcessExited(0).NextProcess!;
    Check((await RunAsync(updater)).Output.Contains("Firmware updater fixture"), "download after a successful build");
    var nonSysbuild = (await service.StartBuildAsync(project, "Test", settings with { UseSysbuild = false })).ProcessExited(0).NextProcess!;
    Check(nonSysbuild.Arguments.Contains("--no-sysbuild") &&
        nonSysbuild.Arguments.Any(argument => argument.StartsWith("-DRETROPLC_GENERATED_DIR=")), "standalone Zephyr build");
    var clean = await service.StartCleanAsync(project, settings);
    Check((await RunAsync(clean.InitialProcess)).ExitCode == 0 &&
        !Directory.Exists(Path.Combine(project, "Build")), "managed clean");

    var pid = int.Parse(File.ReadLines(startupLog).First());
    using var backend = Process.GetProcessById(pid);
    await environment.DisposeAsync();
    Check(backend.HasExited, "managed backend process terminated");
    await environment.DisposeAsync();
    await ExpectAsync<ObjectDisposedException>(() => config.GetWorkspaceInfoAsync(workspace));

    await using var brokenEnvironment = new Ac6Environment(new RetroPlcInstallation(Path.Combine(root, "missing")));
    var broken = new ZephyrConfiguration(brokenEnvironment);
    var missingPython = await ExpectAsync<FileNotFoundException>(() => broken.GetWorkspaceInfoAsync(workspace));
    Check(missingPython.Message.Contains("Managed Python"), "missing Python diagnostic");

    WriteExecutable(python, $$"""exec "{{managedPython}}" -S "$@" """);
    await using var retryEnvironment = new Ac6Environment(new RetroPlcInstallation(root));
    await using var retry = new ZephyrConfiguration(retryEnvironment);
    var missingGrpc = await ExpectAsync<IOException>(() => retry.GetWorkspaceInfoAsync(workspace));
    Check(missingGrpc.Message.Contains("No module named 'grpc'") && missingGrpc.Message.Contains("setup.sh"),
        "missing gRPC diagnostic");
    WriteExecutable(python, $$"""exec "{{managedPython}}" "$@" """);
    Check((await retry.GetWorkspaceInfoAsync(workspace)).Directory == workspace, "backend startup can recover");
    await retryEnvironment.DisposeAsync();

    var stalledLog = Path.Combine(root, "stalled.log");
    WriteExecutable(python, $$"""
        printf '%s\n' "$$" "${8}" > "{{stalledLog}}"
        exec /usr/bin/sleep 60
        """);
    await using var stalledEnvironment = new Ac6Environment(new RetroPlcInstallation(root));
    var stalledConfig = new ZephyrConfiguration(stalledEnvironment);
    var pending = stalledConfig.GetWorkspaceInfoAsync(workspace);
    using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
    {
        while (!File.Exists(stalledLog) || File.ReadAllLines(stalledLog).Length < 2)
            await Task.Delay(15, deadline.Token);
    }
    var launchInfo = File.ReadAllLines(stalledLog);
    using var stalledProcess = Process.GetProcessById(int.Parse(launchInfo[0]));
    await stalledEnvironment.DisposeAsync();
    Check(stalledProcess.HasExited, "nonresponsive Python process terminated during shutdown");
    Check(!Directory.Exists(Path.GetDirectoryName(launchInfo[1])), "failed startup removed private endpoint directory");
    // Disposal cancels the startup task before it advertises an endpoint.
#pragma warning disable VSTHRD003
    await ExpectAsync<OperationCanceledException>(() => pending);
#pragma warning restore VSTHRD003
    Console.WriteLine("Passed AC6 launcher, configuration, SDK, board, and firmware stage checks (isolated fixtures).");
    return 0;
}
finally
{
    Directory.Delete(root, recursive: true);
}

static async Task RunSessionAsync(FirmwareBuildSession session)
{
    var command = session.InitialProcess;
    while (true)
    {
        var result = await RunAsync(command, timeoutSeconds: 300);
        Console.WriteLine(result.Output);
        Console.Error.WriteLine(result.Error);
        var outcome = session.ProcessExited(result.ExitCode);
        if (outcome.IsComplete)
        {
            Check(outcome.ExitCode == 0, $"{session.Operation} exit {outcome.ExitCode}: {outcome.ErrorMessage}");
            return;
        }
        command = outcome.NextProcess!;
    }
}

static void Check(bool condition, string description)
{
    if (!condition)
        throw new InvalidOperationException($"Failed {description}.");
}

static T Expect<T>(Action action) where T : Exception
{
    try
    {
        action();
    }
    catch (T exception)
    {
        return exception;
    }

    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

static async Task<T> ExpectAsync<T>(Func<Task> action) where T : Exception
{
    try
    {
        await action();
    }
    catch (T exception)
    {
        return exception;
    }

    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

static void WriteExecutable(string path, string body)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, "#!/bin/bash\n" + body + "\n");
    if (!OperatingSystem.IsWindows())
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
}

static async Task<(int ExitCode, string Output, string Error)> RunAsync(BuildProcess command, string input = "", int timeoutSeconds = 20)
{
    var info = new ProcessStartInfo(command.Executable)
    {
        WorkingDirectory = command.WorkingDirectory,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };
    foreach (var argument in command.Arguments)
        info.ArgumentList.Add(argument);

    using var process = Process.Start(info)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    var captured = Task.WhenAll(stdout, stderr);
    if (input.Length != 0)
    {
        try
        {
            await process.StandardInput.WriteAsync(input);
        }
        catch (IOException) when (process.HasExited)
        {
        }
    }
    process.StandardInput.Close();

    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
    try
    {
        await process.WaitForExitAsync(timeout.Token);
        // The stand-alone runner has no synchronization context, and both pipes
        // are drained concurrently from the moment the process starts.
#pragma warning disable VSTHRD003
        var output = await captured;
#pragma warning restore VSTHRD003
        return (process.ExitCode, output[0], output[1]);
    }
    catch (OperationCanceledException)
    {
        process.Kill(entireProcessTree: true);
        throw;
    }
}

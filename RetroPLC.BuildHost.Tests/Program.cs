// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using RetroPLC.BuildHost;
using RetroPLC.LanguageServerHost;

if (!OperatingSystem.IsLinux())
{
    Console.WriteLine("The AC6 process fixture requires Linux.");
    return 0;
}

var root = Directory.CreateTempSubdirectory("retroplc-buildhost-").FullName;
try
{
    var development = new RetroPlcInstallation();
    Check(File.Exists(Path.Combine(development.ApplicationDirectory, "RetroPLC.sln")),
        "development root from application directory");
    var project = Directory.CreateDirectory(Path.Combine(root, "project with spaces")).FullName;
    var workspace = Directory.CreateDirectory(Path.Combine(root, ".zephyr", "workspace")).FullName;
    Directory.CreateDirectory(Path.Combine(workspace, ".west"));
    File.WriteAllText(Path.Combine(workspace, ".west", "config"), "[manifest]\n");
    var application = Directory.CreateDirectory(Path.Combine(workspace, "retroplc-runtime", "app")).FullName;
    File.WriteAllText(Path.Combine(application, "CMakeLists.txt"), "# fixture");

    var pinDirectory = Directory.CreateDirectory(Path.Combine(workspace, "zephyr")).FullName;
    var sdkPin = Path.Combine(pinDirectory, "SDK_VERSION");
    File.WriteAllText(sdkPin, "1.2.3\n");
    var sdkDirectory = Path.Combine(root, ".zephyr", "sdk");
    Directory.CreateDirectory(Path.Combine(sdkDirectory, "cmake"));
    var sdkVersion = Path.Combine(sdkDirectory, "sdk_version");
    File.WriteAllText(sdkVersion, "1.2.3\n");
    File.WriteAllText(Path.Combine(sdkDirectory, "cmake", "Zephyr-sdkConfig.cmake"), "# fixture");
    var gcc = Path.Combine(sdkDirectory, "gnu", "arm-zephyr-eabi", "bin", "arm-zephyr-eabi-gcc");
    WriteExecutable(gcc, "exit 0");

    var ac6 = Directory.CreateDirectory(Path.Combine(root, ".zinstaller")).FullName;
    File.WriteAllText(Path.Combine(ac6, "env.yml"), "# fixture");
    File.WriteAllText(Path.Combine(ac6, "env.py"), "# fixture");
    File.WriteAllText(Path.Combine(ac6, "env.sh"), """
        export VIRTUAL_ENV="$PYTHON_VENV_PATH"
        export PATH="${BASH_SOURCE[0]%/*}/tools/host:$PATH"
        echo "AC6 startup output"
        """);
    WriteExecutable(Path.Combine(ac6, ".venv", "bin", "python"), """
        case "$1" in
            --version) echo "Python fixture" ;;
            -c) exec /usr/bin/readlink -f -- "$3" ;;
            *) exec "$@" ;;
        esac
        """);
    WriteExecutable(Path.Combine(ac6, ".venv", "bin", "west"), """
        case "$1 $2" in
            'boards --help') echo '--all-targets';;
            'boards --all-targets') printf '%s\n' 'fixture_board/soc/cpu' 'legacy_board';;
            'build -p')
                printf '%s\n' "$ZEPHYR_SDK_INSTALL_DIR" "$ZEPHYR_TOOLCHAIN_VARIANT" "$@"
                [[ "$WEST_CONFIG_LOCAL" == "$PWD/.west/config" ]] || exit 9
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

    var environment = new Ac6Environment(new RetroPlcInstallation(root));
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
    var previousDirectory = Environment.CurrentDirectory;
    try
    {
        Environment.CurrentDirectory = Path.GetTempPath();
        var defaults = defaultConfig.Resolve(Path.GetRelativePath(root, project));
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
    Check(customDefaults.Resolve(project).SdkDirectory == customSdk, "configured IDE SDK");
    Environment.SetEnvironmentVariable("RETROPLC_ZEPHYR_SDK", "custom sdk");
    Check(defaultConfig.Resolve(project).SdkDirectory == customSdk, "explicit SDK environment override");
    Environment.SetEnvironmentVariable("RETROPLC_ZEPHYR_SDK", null);

    var settings = new ZephyrBuildConfiguration
    {
        BoardTarget = "fixture_board/soc/cpu",
        SdkDirectory = "../.zephyr/sdk",
        CMakeOptions = new() { ["MERGED"] = "project", ["DTC_OVERLAY_FILE"] = "board overlay.dts" }
    };
    var resolved = config.Resolve(project, settings, new() { BoardTarget = "override", CMakeOptions = new() { ["MERGED"] = "operation" } });
    Check(resolved.BoardTarget == "override" && resolved.SdkDirectory == sdkDirectory, "operation and project precedence");
    Check(resolved.WorkspaceDirectory == workspace && resolved.HostToolsDirectory == ac6, "IDE relative paths");
    Check(resolved.CMakeOptions!["MERGED"] == "operation" && resolved.CMakeOptions["IDE_ONLY"] == "ON", "CMake options");
    var alternate = defaultConfig.Resolve(project, new() { WorkspaceDirectory = "alternate-workspace" },
        new() { ApplicationDirectory = "alternate app", BuildDirectory = "alternate build" });
    Check(alternate.WorkspaceDirectory == Path.Combine(project, "alternate-workspace") &&
          alternate.ApplicationDirectory == Path.Combine(project, "alternate app") &&
          alternate.BuildDirectory == Path.Combine(project, "alternate build"),
        "project and operation directory overrides stay relative to the project");
    Expect<DirectoryNotFoundException>(() => config.GetBoardService(project, settings, new() { WorkspaceDirectory = "missing" }));

    var boards = config.GetBoardService(project, settings);
    Check((await boards.GetAvailableBoardsAsync()).Count == 2, "board discovery and clean stdout");
    Check(await boards.ValidateBoardAsync(settings.BoardTarget!), "qualified board validation");
    Check(!await boards.ValidateBoardAsync("missing_board"), "unavailable board validation");

    var terminal = config.CreateTerminal(project, settings);
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
    var workspaceConfig = Path.Combine(workspace, ".west", "config");
    foreach (var required in new[] { sdkVersion, sdkPin, westPath, workspaceConfig, gcc,
                 Path.Combine(application, "CMakeLists.txt"), Path.Combine(sdkDirectory, "cmake", "Zephyr-sdkConfig.cmake") })
    {
        File.Move(required, required + ".fixture-hidden");
        try
        {
            Expect<IOException>(() => defaultBuild.StartBuild(project, "Test"));
        }
        finally
        {
            File.Move(required + ".fixture-hidden", required);
        }
    }
    WriteExecutable(Path.Combine(sdkDirectory, "gnu", "riscv64-zephyr-elf", "bin", "riscv64-zephyr-elf-gcc"), "exit 0");
    File.Move(gcc, gcc + ".fixture-hidden");
    try
    {
        Check(Expect<IOException>(() => defaultBuild.StartBuild(project, "Test")).Message.Contains("ARM compiler"),
            "Opta requires an ARM compiler even when another toolchain exists");
    }
    finally
    {
        File.Move(gcc + ".fixture-hidden", gcc);
    }
    Check(defaultBuild.StartBuild(project, "Test").ProcessExited(0).NextProcess is { } defaultWest &&
          defaultWest.WorkingDirectory == workspace && defaultWest.Arguments.Contains("arduino_opta/stm32h747xx/m7"),
        "firmware stage uses provisioned paths and board without overrides");
    Expect<InvalidOperationException>(() => service.StartBuild(project, "Test", settings with { BoardTarget = "" }));
    var verify = service.StartVerify(project, "Test");
    Check((await RunAsync(verify.InitialProcess)).ExitCode == 0 && verify.ProcessExited(0).ExitCode == 0, "ST verification stage");
    var failed = service.StartDownload(project, "Test", settings);
    Check(failed.ProcessExited(7) is { ExitCode: 7, NextProcess: null }, "failed ST stage stops the build");

    var build = service.StartBuild(project, "Test", settings);
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
    File.WriteAllText(Path.Combine(buildDirectory, "CMakeCache.txt"), "ZEPHYR_SDK_INSTALL_DIR:PATH=/another-sdk\n");
    var upgraded = service.StartBuild(project, "Test", settings).ProcessExited(0).NextProcess!;
    Check(upgraded.Arguments.Contains("always"), "SDK changes invalidate the existing build");
    Expect<InvalidOperationException>(() => service.StartBuild(project, "Test", settings with { PristineMode = "never" }));
    var cachePath = Path.Combine(buildDirectory, "CMakeCache.txt");
    File.WriteAllText(cachePath,
        $"ZEPHYR_SDK_INSTALL_DIR:PATH={sdkDirectory}\nAPP_DIR:PATH={application}\nRETROPLC_SDK_VERSION:INTERNAL=1.2.3\n");
    Check(service.StartBuild(project, "Test", settings).ProcessExited(0).NextProcess!.Arguments.Contains("auto"),
        "matching cache remains incremental");
    File.WriteAllText(sdkVersion, "1.2.4\n");
    Check(service.StartBuild(project, "Test", settings).ProcessExited(0).NextProcess!.Arguments.Contains("always"),
        "SDK version change at fixed SDK path invalidates the existing build");
    File.WriteAllText(sdkVersion, "1.2.3\n");
    Check(service.StartRebuild(project, "Test", settings).ProcessExited(0).NextProcess!.Arguments.Contains("always"), "pristine rebuild");

    var download = service.StartDownload(project, "Test", settings);
    download.ProcessExited(0);
    var updater = download.ProcessExited(0).NextProcess!;
    Check((await RunAsync(updater)).Output.Contains("Firmware updater fixture"), "download after a successful build");
    var wrongSdk = service.StartBuild(project, "Test", settings);
    wrongSdk.ProcessExited(0);
    File.WriteAllText(Path.Combine(buildDirectory, "app", "CMakeCache.txt"), "Zephyr-sdk_DIR:PATH=/another-sdk/cmake\n");
    Check(wrongSdk.ProcessExited(0) is { ExitCode: -1, ErrorMessage: not null }, "unexpected SDK blocks artifact publishing");
    var nonSysbuild = service.StartBuild(project, "Test", settings with { UseSysbuild = false }).ProcessExited(0).NextProcess!;
    Check(nonSysbuild.Arguments.Contains("--no-sysbuild") &&
        nonSysbuild.Arguments.Any(argument => argument.StartsWith("-DRETROPLC_GENERATED_DIR=")), "standalone Zephyr build");
    var clean = service.StartClean(project, settings);
    Check((await RunAsync(clean.InitialProcess)).ExitCode == 0 &&
        !Directory.Exists(Path.Combine(project, "Build")), "managed clean");

    Console.WriteLine("Passed AC6 launcher, configuration, SDK, board, and firmware stage checks (isolated fixtures).");
    return 0;
}
finally
{
    Directory.Delete(root, recursive: true);
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

static void WriteExecutable(string path, string body)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, "#!/bin/bash\n" + body + "\n");
    if (!OperatingSystem.IsWindows())
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
}

static async Task<(int ExitCode, string Output, string Error)> RunAsync(BuildProcess command, string input = "")
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

    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
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

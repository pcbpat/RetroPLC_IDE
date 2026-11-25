// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using RetroPLC.BuildHost;

await DebugMonitorTests.RunAsync();
if (args is ["--debug-monitor"])
    return;

if (args is ["--live", var liveProject])
{
    var live = new FirmwareBuildService();
    foreach (var operation in new[] { BuildOperation.Verify, BuildOperation.Build, BuildOperation.Rebuild, BuildOperation.Clean })
    {
        var result = await live.RunAsync(operation, liveProject, output: new InlineProgress<string>(Console.WriteLine));
        Check(result.Success, $"{operation}: {result.Error}");
        Console.WriteLine($"{operation} passed.");
    }
    return;
}

if (!OperatingSystem.IsLinux())
{
    Console.WriteLine("The simulated Pixi executable requires Linux.");
    return;
}

var root = Directory.CreateTempSubdirectory("retroplc pixi tests ").FullName;
try
{
    var project = Directory.CreateDirectory(Path.Combine(root, "sample project")).FullName;
    var installation = Directory.CreateDirectory(Path.Combine(root, "installed workspace")).FullName;
    var pixi = Path.Combine(root, "fake pixi");
    File.WriteAllText(Path.Combine(installation, "pixi.toml"), "");
    File.WriteAllText(Path.Combine(installation, "pixi.lock"), "");
    File.WriteAllText(pixi, """
        #!/bin/sh
        set -eu
        test "$#" -ge 5
        test "$1 $2 $3" = "run --quiet west"
        test "$PIXI_NO_INSTALL $PIXI_LOCKED" = "true true"
        test -f pixi.toml
        printf '%s\n' "$@" >> calls
        printf '%s\n' 'compiler diagnostic' >&2
        command=$4
        project=$5
        mode=$(cat mode)
        case "$command" in
          plc-transpile|plc-build|plc-rebuild)
            case "$mode" in
              fail) exit 23 ;;
              slow)
                sleep 30 &
                echo "$!" > child
                wait
                ;;
              no-artifacts) echo '{"event":"result","success":true}'; exit 0 ;;
              empty) exit 0 ;;
              invalid) echo '{broken'; exit 0 ;;
              no-result) echo '{"event":"progress","step":"Compiling"}'; exit 0 ;;
              nonzero) echo '{"event":"result","success":true}'; exit 29 ;;
            esac
            generated="$project/Build/Generated"
            mkdir -p "$generated"
            printf 'debug' > "$generated/generated_debug.cpp"
            printf '%s' '{"version":2,"md5":"0123456789abcdef0123456789abcdef","arrays":[],"leaves":[]}' > "$generated/debug_map.json"
            if [ "$mode" = bad-map ]; then printf '{}' > "$generated/debug_map.json"; fi
            if [ "$command" != plc-transpile ]; then
              output="$project/Build/Zephyr/app/zephyr"
              mkdir -p "$output"
              for artifact in zephyr.elf zephyr.signed.bin zephyr.signed.hex zephyr.map; do
                printf 'firmware' > "$output/$artifact"
              done
            fi
            echo '{"event":"progress","step":"Compiling"}'
            echo 'Compiler output' >&2
            echo '{"event":"result","success":true,"status":"completed"}'
            ;;
          plc-clean)
            rm -rf "$project/Build/Generated" "$project/Build/Zephyr"
            echo '{"event":"result","success":true}'
            ;;
          plc-upload)
            case "$mode" in
              device-error)
                echo '{"event":"error","success":false,"code":"device_error","command":"plc-upload","message":"Device did not respond"}'
                exit 17
                ;;
              device-no-result) exit 0 ;;
              device-invalid) echo 'not json'; exit 0 ;;
            esac
            echo '{"event":"progress","step":"Uploading","transmitted":65536,"total":245760}'
            if [ "$mode" = installed ]; then
              echo '{"event":"result","success":true,"status":"already_installed"}'
            else
              echo '{"event":"result","success":true,"status":"update_requested"}'
            fi
            ;;
          plc-verify)
            confirmed=false
            for argument in "$@"; do
              if [ "$argument" = --confirm ]; then confirmed=true; fi
            done
            printf '{"event":"result","success":true,"verified":true,"hash":"abc123","version":"1.0.0","confirmed":%s}\n' "$confirmed"
            ;;
          *) exit 30 ;;
        esac
        """);
    File.SetUnixFileMode(pixi, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

    var environment = new BuildEnvironment(installation, pixi);
    var command = environment.CreateWestCommand("plc-build", project);
    Check(command.WorkingDirectory == installation && command.Executable == pixi &&
          command.Arguments.SequenceEqual(new[] { "run", "--quiet", "west", "plc-build", project }), "command and cwd");
    Check(environment.CreateTerminal().WorkingDirectory == installation, "terminal cwd");
    Check((await new FirmwareBuildService(new BuildEnvironment(root, pixi)).RunAsync(BuildOperation.Verify, project))
        .Error?.Contains("RETROPLC_BUILD_ENVIRONMENT") == true, "missing environment");
    Check((await new FirmwareBuildService(new BuildEnvironment(installation, Path.Combine(root, "absent"))).RunAsync(BuildOperation.Verify, project))
        .Error?.Contains("RETROPLC_PIXI_EXECUTABLE") == true, "missing Pixi");
    var invalidExecutable = Path.Combine(root, "not executable");
    File.WriteAllText(invalidExecutable, "");
    Check(!(await new FirmwareBuildService(new BuildEnvironment(installation, invalidExecutable))
        .RunAsync(BuildOperation.Verify, project)).Success, "startup failure");

    var service = new FirmwareBuildService(environment);
    var events = new List<BuildEvent>();
    var output = new System.Collections.Concurrent.ConcurrentQueue<string>();
    var device = new DeviceConnection("/dev/serial with space", 230400, 2500);
    var calls = Path.Combine(installation, "calls");
    void Mode(string value)
    {
        File.WriteAllText(Path.Combine(installation, "mode"), value);
        File.WriteAllText(calls, "");
        events.Clear();
    }

    Task<BuildResult> Run(BuildOperation operation, CancellationToken cancellationToken = default) =>
        service.RunAsync(operation, project, device: device, output: new InlineProgress<string>(output.Enqueue),
            events: new InlineProgress<BuildEvent>(events.Add), cancellationToken: cancellationToken);

    foreach (var (operation, westCommand) in new[]
    {
        (BuildOperation.Verify, "plc-transpile"),
        (BuildOperation.Build, "plc-build"),
        (BuildOperation.Rebuild, "plc-rebuild")
    })
    {
        Mode("ok");
        output.Clear();
        Check((await Run(operation)).Success &&
              File.ReadAllLines(calls).SequenceEqual(new[] { "run", "--quiet", "west", westCommand, project }),
            $"{operation} command");
        Check(events.Any(e => e is { Kind: "progress", Step: "Compiling" }) &&
              output.Contains("Compiler output") && output.Contains("compiler diagnostic"),
            $"{operation} progress and both output streams");
    }
    Mode("invalid");
    Check((await Run(BuildOperation.Rebuild)).Error == "plc-rebuild returned malformed JSON.", "rebuild protocol error");
    Mode("fail");
    Check((await Run(BuildOperation.Rebuild)).ExitCode == 23, "rebuild process failure");
    Mode("fail");
    Check((await Run(BuildOperation.Download)).ExitCode == 23 && !File.ReadAllLines(calls).Contains("plc-upload"),
        "failed build blocks upload even with existing firmware");
    Mode("installed");
    Check((await Run(BuildOperation.Download)).Event?.Status == "already_installed" &&
          events.Any(e => e is { Kind: "progress", Transmitted: 65536, Total: 245760 }), "upload progress and already installed");
    Check(File.ReadAllLines(calls).SequenceEqual(new[]
    {
        "run", "--quiet", "west", "plc-build", project, "run", "--quiet", "west", "plc-upload", project,
        "--serial", device.SerialPort, "--baud", "230400", "--timeout-ms", "2500"
    }), "upload command order and options");
    Mode("ok");
    Check((await Run(BuildOperation.Download)).Event?.Status == "update_requested", "upload request");
    var verification = await service.VerifyFirmwareAsync(project, device);
    Check(verification.Success && verification.Event is { Verified: true, Hash: "abc123", Version: "1.0.0", Confirmed: false }
          && !File.ReadAllLines(calls).Contains("--confirm"), "verify without implicit confirmation");
    var confirmation = await service.VerifyFirmwareAsync(project, device, confirm: true);
    Check(confirmation.Success && confirmation.Event?.Confirmed == true && File.ReadAllLines(calls).Contains("--confirm"), "explicit confirm");
    Mode("device-error");
    var error = await Run(BuildOperation.Download);
    Check(error is { ExitCode: 17, Error: "Device did not respond", Event.Code: "device_error" }, "structured error");
    foreach (var mode in new[] { "device-no-result", "device-invalid", "invalid", "no-result", "empty", "bad-map" })
    {
        Mode(mode);
        Check(!(await Run(BuildOperation.Download)).Success, mode);
    }
    Mode("nonzero");
    Check((await Run(BuildOperation.Build)).ExitCode == 29, "nonzero overrides JSON success");

    var childFile = Path.Combine(installation, "child");
    foreach (var operation in new[] { BuildOperation.Download, BuildOperation.Rebuild })
    {
        Mode("slow");
        File.Delete(childFile);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = Run(operation, cancellation.Token);
        while (!File.Exists(childFile) || new FileInfo(childFile).Length == 0)
        {
            Check(!running.IsCompleted, $"{operation} fixture started");
            await Task.Delay(20, cancellation.Token);
        }
        var child = int.Parse(File.ReadAllText(childFile));

        if (operation == BuildOperation.Download)
        {
            using var waitingCancellation = new CancellationTokenSource();
            var waitingRebuild = Run(BuildOperation.Rebuild, waitingCancellation.Token);
            Check(!waitingRebuild.IsCompleted, "rebuild waits while another operation is active");
            await waitingCancellation.CancelAsync();
            await CheckCancelled(waitingRebuild);
            Check(!File.ReadAllLines(calls).Contains("plc-rebuild"), "queued rebuild did not start");
        }

        await cancellation.CancelAsync();
        await CheckCancelled(running);
        Check(!File.ReadAllLines(calls).Contains("plc-upload"), "cancelled build blocks upload");
        var stat = $"/proc/{child}/stat";
        var deadline = Stopwatch.StartNew();
        while (ChildIsRunning(stat) && deadline.Elapsed < TimeSpan.FromSeconds(2))
            await Task.Delay(20);
        Check(!ChildIsRunning(stat), $"{operation} process tree stopped");
    }

    Mode("ok");
    Check((await Run(BuildOperation.Clean)).Success && !Directory.Exists(Path.Combine(project, "Build", "Generated")) &&
          !Directory.Exists(Path.Combine(project, "Build", "Zephyr")), "clean");
    Mode("no-artifacts");
    Check(!(await Run(BuildOperation.Download)).Success && !File.ReadAllLines(calls).Contains("plc-upload"), "missing artifacts");
    Check(!(await service.RunAsync(BuildOperation.Build, project, new() { BoardTarget = "custom" })).Success, "legacy override");
    Directory.CreateDirectory(Path.Combine(project, "Libraries"));
    File.WriteAllText(Path.Combine(project, "Libraries", "example.stlib"), "");
    Check((await Run(BuildOperation.Verify)).Error?.Contains("Libraries") == true, "unsupported project libraries");
    Console.WriteLine("BuildHost simulated Pixi tests passed.");
}
finally
{
    Directory.Delete(root, recursive: true);
}

static async Task CheckCancelled(Task<BuildResult> operation)
{
    try
    {
        await operation.WaitAsync(TimeSpan.FromSeconds(5));
        throw new InvalidOperationException("Expected cancellation.");
    }
    catch (OperationCanceledException)
    {
    }
}

static bool ChildIsRunning(string path)
{
    try
    {
        var stat = File.ReadAllText(path);
        return stat[stat.LastIndexOf(')') + 2] != 'Z';
    }
    catch (FileNotFoundException)
    {
        return false;
    }
    catch (DirectoryNotFoundException)
    {
        return false;
    }
}

static void Check(bool condition, string label)
{
    if (!condition)
        throw new InvalidOperationException($"Failed: {label}");
}

sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

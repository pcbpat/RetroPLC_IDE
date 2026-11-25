// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RetroPLC.BuildHost;

public enum BuildOperation
{
    Verify,
    Build,
    Rebuild,
    Clean,
    Download
}

public sealed record DeviceConnection(string? SerialPort = null, int BaudRate = 115200,
    int TimeoutMilliseconds = 10000, string? UdpHostname = null, int UdpPort = 1337)
{
    internal string[] GetArguments()
    {
        if (TimeoutMilliseconds <= 0 || BaudRate <= 0 || UdpPort is < 1 or > 65535)
            throw new ArgumentException("Device baud rate, timeout and UDP port must be in range.");
        var serial = !string.IsNullOrWhiteSpace(SerialPort);
        if (serial == !string.IsNullOrWhiteSpace(UdpHostname))
            throw new ArgumentException("Configure exactly one device connection using device.serialPort or device.udpHostname in project.json.");
        return
        [
            .. serial ? new[] { "--serial", SerialPort!, "--baud", BaudRate.ToString(CultureInfo.InvariantCulture) }
                : new[] { "--udp", UdpHostname!, "--port", UdpPort.ToString(CultureInfo.InvariantCulture) },
            "--timeout-ms", TimeoutMilliseconds.ToString(CultureInfo.InvariantCulture)
        ];
    }
}

public sealed record BuildEvent
{
    [JsonPropertyName("event")]
    public string? Kind { get; init; }
    public bool? Success { get; init; }
    public string? Step { get; init; }
    public long? Transmitted { get; init; }
    public long? Total { get; init; }
    public string? Status { get; init; }
    public bool? Verified { get; init; }
    public string? Hash { get; init; }
    public string? Version { get; init; }
    public bool? Confirmed { get; init; }
    public string? Code { get; init; }
    public string? Command { get; init; }
    public string? Message { get; init; }
}

public sealed record BuildResult(int ExitCode, string? Error = null, BuildEvent? Event = null)
{
    public bool Success => ExitCode == 0;
}

public sealed class FirmwareBuildService(BuildEnvironment? environment = null)
{
    private readonly BuildEnvironment _environment = environment ?? new();
    private readonly SemaphoreSlim _operation = new(1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<BuildResult> RunAsync(BuildOperation operation, string project,
        ZephyrBuildConfiguration? legacy = null, DeviceConnection? device = null,
        IProgress<string>? output = null, IProgress<BuildEvent>? events = null,
        CancellationToken cancellationToken = default) => RunExclusiveAsync(async () =>
    {
        project = Path.GetFullPath(project);
        var command = operation switch
        {
            BuildOperation.Verify => "plc-transpile",
            BuildOperation.Build or BuildOperation.Rebuild or BuildOperation.Download => "plc-build",
            BuildOperation.Clean => "plc-clean",
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        if (operation != BuildOperation.Clean)
            ValidateCompatibility(project, legacy, operation != BuildOperation.Verify);

        var uploadOptions = operation == BuildOperation.Download
            ? (device ?? new DeviceConnection()).GetArguments() : [];
        var result = await ExecuteAsync(command, project, [], output, events, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
            return result;

        if (operation != BuildOperation.Clean)
            ValidateArtifacts(project, operation != BuildOperation.Verify);

        return operation == BuildOperation.Download
            ? await ExecuteAsync("plc-upload", project, uploadOptions, output, events, cancellationToken).ConfigureAwait(false)
            : result;
    }, cancellationToken);

    public Task<BuildResult> VerifyFirmwareAsync(string project, DeviceConnection device, bool confirm = false,
        IProgress<string>? output = null, IProgress<BuildEvent>? events = null,
        CancellationToken cancellationToken = default) => RunExclusiveAsync(() =>
    {
        var options = device.GetArguments();
        return ExecuteAsync("plc-verify", project, confirm ? [.. options, "--confirm"] : options,
            output, events, cancellationToken);
    }, cancellationToken);

    private async Task<BuildResult> RunExclusiveAsync(Func<Task<BuildResult>> action, CancellationToken cancellationToken)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
                                      Win32Exception or InvalidOperationException or JsonException)
        {
            return new BuildResult(-1, error.Message);
        }
        finally
        {
            _operation.Release();
        }
    }

    private async Task<BuildResult> ExecuteAsync(string command, string project, string[] options,
        IProgress<string>? output, IProgress<BuildEvent>? events, CancellationToken cancellationToken)
    {
        BuildEvent? terminal = null;
        string? protocolError = null;
        var exit = await BuildEnvironment.RunAsync(_environment.CreateWestCommand(command, project, options), line =>
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

            try
            {
                var message = JsonSerializer.Deserialize<BuildEvent>(line, JsonOptions);
                switch (message?.Kind)
                {
                    case "progress":
                        events?.Report(message);
                        break;
                    case "result":
                    case "error":
                        if (terminal is not null)
                            protocolError ??= $"{command} returned multiple terminal events.";
                        terminal = message;
                        events?.Report(message);
                        break;
                    default:
                        protocolError ??= $"{command} returned an invalid build event.";
                        break;
                }
            }
            catch (JsonException)
            {
                protocolError ??= $"{command} returned malformed JSON.";
                output?.Report(line);
            }
        }, line => output?.Report(line), cancellationToken).ConfigureAwait(false);

        var error = protocolError;
        if (terminal is { Kind: "error" } or { Kind: "result", Success: not true })
            error ??= terminal.Message ?? $"{command} failed ({terminal.Code ?? "unspecified error"}).";
        if (terminal is null)
            error ??= $"{command} did not return a terminal result.";
        if (command == "plc-upload" && terminal is { Kind: "result", Status: not ("already_installed" or "update_requested") })
            error ??= $"{command} did not report an upload outcome.";
        if (command == "plc-verify" && terminal is { Kind: "result" } &&
            (terminal.Verified != true || (options.Contains("--confirm") && terminal.Confirmed != true)))
            error ??= $"{command} did not report the requested verification or confirmation.";
        if (exit != 0)
            error ??= $"{command} exited with code {exit}.";
        return new BuildResult(error is null ? 0 : exit == 0 ? -1 : exit, error, terminal);
    }

    private static void ValidateCompatibility(string project, ZephyrBuildConfiguration? legacy, bool compileFirmware)
    {
        var libraries = Path.Combine(project, "Libraries");
        if (Directory.Exists(libraries) && Directory.EnumerateFiles(libraries, "*.stlib").Any())
            throw new InvalidOperationException("The installed plc-transpile command needs support for project Libraries/*.stlib.");

        if (compileFirmware && (legacy?.HasOverrides == true ||
            new[] { "RETROPLC_AC6_DIRECTORY", "RETROPLC_ZEPHYR_WORKSPACE", "RETROPLC_ZEPHYR_SDK",
                "RETROPLC_ZEPHYR_BOARD", "RETROPLC_ZEPHYR_APPLICATION" }
                .Any(name => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))))
            throw new InvalidOperationException("Legacy Zephyr overrides are unsupported by plc-build. Remove the legacy settings or configure the build in BuildEnvironment.");
    }

    private static void ValidateArtifacts(string project, bool compiled)
    {
        var generated = Path.Combine(project, "Build", "Generated");
        RequireArtifact(Path.Combine(generated, "generated_debug.cpp"));
        var map = Path.Combine(generated, "debug_map.json");
        using var input = File.OpenRead(map);
        using var document = JsonDocument.Parse(input);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out var value) || value != 2 ||
            !root.TryGetProperty("md5", out var identity) || identity.ValueKind != JsonValueKind.String ||
            identity.GetString() is not { Length: 32 } ||
            !root.TryGetProperty("arrays", out var arrays) || arrays.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("leaves", out var leaves) || leaves.ValueKind != JsonValueKind.Array)
            throw new IOException($"The generated debug map at '{map}' is incomplete.");

        if (compiled)
            foreach (var name in new[] { "zephyr.elf", "zephyr.signed.bin", "zephyr.signed.hex", "zephyr.map" })
                RequireArtifact(Path.Combine(project, "Build", "Zephyr", "app", "zephyr", name));
    }

    private static void RequireArtifact(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new IOException($"Build did not produce a nonempty artifact at '{path}'.");
    }
}

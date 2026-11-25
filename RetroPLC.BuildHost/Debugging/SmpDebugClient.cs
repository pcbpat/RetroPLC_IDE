// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using RetroPLC.LanguageServerHost;

namespace RetroPLC.BuildHost.Debugging;

public sealed record DebugReading(uint Id, uint Type, byte[]? Data, uint? Status);
public sealed record DebugPage(uint Scan, uint Total, uint Offset, uint Count, uint Next, DebugReading[] Readings)
{
    // mcumgrctl raw --json converts CBOR byte strings into JSON byte arrays.
    public static DebugPage Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        try
        {
            var root = document.RootElement;
            var readings = root.GetProperty("vars").EnumerateArray().Select(entry =>
            {
                var hasData = entry.TryGetProperty("data", out var data);
                var hasStatus = entry.TryGetProperty("status", out var status);
                if (hasData == hasStatus)
                    throw new InvalidDataException("Expected either data or status in the SMP debug record.");

                return new DebugReading(entry.GetProperty("id").GetUInt32(), entry.GetProperty("type").GetUInt32(),
                    hasData ? data.EnumerateArray().Select(value => value.GetByte()).ToArray() : null,
                    hasStatus ? status.GetUInt32() : null);
            }).ToArray();

            return new DebugPage(root.GetProperty("scan").GetUInt32(), root.GetProperty("total").GetUInt32(),
                root.GetProperty("offset").GetUInt32(), root.GetProperty("count").GetUInt32(),
                root.GetProperty("next").GetUInt32(), readings);
        }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new InvalidDataException("Unexpected SMP symbolic debug response.", error);
        }
    }
}

public sealed class SmpDebugClient
{
    public async Task<DebugPage> ReadPageAsync(uint offset, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var executable = Path.Combine(StrucppToolchain.ToolsDirectory, "mcumgr", "mcumgrctl");
        if (!File.Exists(executable))
            throw new FileNotFoundException("The managed mcumgrctl tool is missing. Run setup.sh.", executable);
        StrucppToolchain.EnsureExecutable(executable);

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        // Same USB selector as firmware Download. Let the handler choose its
        // configured default limit and validate the returned page boundaries.
        string[] arguments =
        [
            "-u", ".", "raw", "--json", "--timeout", "6000", "--retries", "0",
            "--smp-frame-size", "4096", "read", "64", "1",
            "{\"offset\":" + offset.ToString(CultureInfo.InvariantCulture) + "}"
        ];
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(7));
        using var process = Process.Start(start) ?? throw new IOException("Unable to start mcumgrctl.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill();
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
            }

            await process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("The symbolic SMP request timed out.");
        }

        var output = await stdout.ConfigureAwait(false);
        var error = (await stderr.ConfigureAwait(false)).Trim();
        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            if (error.Contains("MGMT_ERR_ENOTSUP", StringComparison.Ordinal) ||
                error.Contains("MGMT_ERR_ENOENT", StringComparison.Ordinal))
                throw new IOException("Symbolic SMP reads are unavailable. Use Runtime firmware with generated debug support.");
            throw new IOException(string.IsNullOrEmpty(error) ? $"mcumgrctl exited with code {process.ExitCode}." : error);
        }

        return DebugPage.Parse(output);
    }
}

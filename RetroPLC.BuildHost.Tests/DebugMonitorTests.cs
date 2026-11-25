// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using RetroPLC.BuildHost.Debugging;

internal static class DebugMonitorTests
{
    public static async Task RunAsync()
    {
        using var project = new TestProject();
        await ExpectAsync<DirectoryNotFoundException>(() => Task.FromResult(DebugMap.Load(project.Directory)));
        project.WriteMap("Artifacts", MapJson);
        Check(DebugMap.Load(project.Directory).Symbols.Count == 3, "Legacy map fallback");
        project.WriteMap("Generated", MapJson.Replace("\"version\":2", "\"version\":1"));
        await ExpectAsync<InvalidDataException>(() => Task.FromResult(DebugMap.Load(project.Directory)));
        project.WriteMap("Generated", MapJson.Replace("\"elemIdx\":1", "\"elemIdx\":0"));
        await ExpectAsync<InvalidDataException>(() => Task.FromResult(DebugMap.Load(project.Directory)));
        project.WriteMap("Generated", MapJson);
        var map = DebugMap.Load(project.Directory);
        Check(map.Symbols[(0, 1)].Path == "MAIN.SPEED", "identifier and path");

        var first = DebugPage.Parse("""
            {"scan":4294967295,"total":2,"offset":0,"count":1,"next":1,"vars":[{"id":0,"type":0,"data":[1]}]}
            """);
        var second = DebugPage.Parse("""
            {"scan":0,"total":2,"offset":1,"count":1,"next":2,"vars":[{"id":1,"type":9,"data":[0,0,72,65]}]}
            """);
        var requests = new List<uint>();
        var monitor = new DebugMonitor(map, (offset, _) =>
        {
            requests.Add(offset);
            return Task.FromResult(offset == 0 ? first : second);
        });
        var values = await monitor.ReadValuesAsync(default);
        Check(requests.SequenceEqual([0U, 1U]) && values.Count == 2 &&
              values[0] == "TRUE" && values[1] == "12.5", "pagination, scan wrap and array 0 mapping");
        await ExpectAsync<InvalidDataException>(() => monitor.ReadValuesAsync(default));
        await ExpectAsync<InvalidDataException>(() => ReadFirst(first with { Next = 0 }));
        await ExpectAsync<InvalidDataException>(() => ReadFirst(first with { Total = 3 }));
        await ExpectAsync<InvalidDataException>(() => ReadFirst(first with { Readings = [new(1, 0, [1], null)] }));
        await ExpectAsync<InvalidDataException>(() => ReadFirst(first with { Readings = [new(0, 1, [1], null)] }));
        await ExpectAsync<InvalidDataException>(() => Task.FromResult(DebugPage.Parse("""{"rc":8}""")));
        await ExpectAsync<InvalidDataException>(() => Task.FromResult(DebugPage.Parse("""
            {"vars":[{"id":0,"type":0,"data":[256]}]}
            """)));
        await ExpectAsync<System.Text.Json.JsonException>(() => Task.FromResult(DebugPage.Parse("{")));
        var unsupported = DebugPage.Parse("""
            {"scan":4294967295,"total":2,"offset":0,"count":1,"next":1,"vars":[{"id":0,"type":0,"status":2}]}
            """);
        Check((await ReadFirst(unsupported))[0] == IecDebugValue.Unsupported, "Runtime status");

        using var cancellation = new CancellationTokenSource();
        var pending = new DebugMonitor(map, async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return first;
        }).ReadValuesAsync(cancellation.Token);
        await cancellation.CancelAsync();
        try
        {
            await pending;
            throw new InvalidOperationException("Expected the pending monitor read to cancel.");
        }
        catch (OperationCanceledException)
        {
        }

        Case("BOOL", [0], "FALSE");
        Case("BOOL", [2], IecDebugValue.Invalid);
        Case("SINT", [255], "-1");
        foreach (var type in new[] { "USINT", "BYTE" })
            Case(type, [255], "255");
        Case("INT", [0, 128], "-32768");
        foreach (var type in new[] { "UINT", "WORD" })
            Case(type, [255, 255], "65535");
        Case("DINT", [0, 0, 0, 128], "-2147483648");
        foreach (var type in new[] { "UDINT", "DWORD" })
            Case(type, [255, 255, 255, 255], "4294967295");
        Case("LINT", Int64Bytes(long.MinValue), "-9223372036854775808");
        foreach (var type in new[] { "ULINT", "LWORD" })
            Case(type, Int64Bytes(-1), "18446744073709551615");
        Case("REAL", [0, 0, 72, 65], "12.5");
        Case("LREAL", Int64Bytes(BitConverter.DoubleToInt64Bits(-0.125)), "-0.125");
        Case("TIME", Int64Bytes(-123), "T#-123ns");
        Case("DATE", Int64Bytes(1), "D#1970-01-02");
        Case("TOD", Int64Bytes(3_600_000_000_001), "TOD#01:00:00.000000001");
        Case("DT", Int64Bytes(-1), "DT#1969-12-31-23:59:59.999999999");
        Case("LTIME", Int64Bytes(1), IecDebugValue.Unsupported);
        Case("INT", [1], IecDebugValue.Invalid);
        Case("INT", [1, 0], IecDebugValue.Invalid, mapSize: 1);
        var narrow = new byte[127];
        new byte[] { 2, 0xC3, 0xA9 }.CopyTo(narrow, 0);
        Case("STRING", narrow, "é");
        narrow[0] = 127;
        Case("STRING", narrow, IecDebugValue.Invalid);
        var wide = new byte[253];
        new byte[] { 1, 0xA9, 0x03 }.CopyTo(wide, 0);
        Case("WSTRING", wide, "Ω");
        Console.WriteLine("Debug monitor map, IEC ABI, SMP pages and cancellation tests passed.");

        Task<IReadOnlyDictionary<uint, string>> ReadFirst(DebugPage page) =>
            new DebugMonitor(map, (offset, _) => Task.FromResult(offset == 0 ? page : second))
                .ReadValuesAsync(default);
    }

    private static byte[] Int64Bytes(long value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        return bytes;
    }

    private static void Case(string type, byte[] bytes, string expected, int? mapSize = null) =>
        Check(IecDebugValue.Decode(new DebugSymbol(0, 0, "MAIN.VAR", type, mapSize ?? bytes.Length, 0), bytes)
            == expected, type + " decode");

    private static void Check(bool condition, string name)
    {
        if (!condition)
            throw new InvalidOperationException("Debug fixture failed: " + name);
    }

    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private const string MapJson = """
        {"version":2,"typeTags":{"BOOL":0,"REAL":9},"arrays":[{"index":0,"count":2},{"index":1,"count":1}],
         "leaves":[{"arrayIdx":0,"elemIdx":0,"path":"MAIN.ENABLED","type":"BOOL","size":1},
                   {"arrayIdx":0,"elemIdx":1,"path":"MAIN.SPEED","type":"REAL","size":4},
                   {"arrayIdx":1,"elemIdx":0,"path":"MAIN.EXTRA","type":"BOOL","size":1}]}
        """;

    private sealed class TestProject : IDisposable
    {
        public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("retroplc-debug-").FullName;

        public void WriteMap(string subdirectory, string json)
        {
            var path = Path.Combine(Directory, "Build", subdirectory);
            System.IO.Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "debug_map.json"), json);
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}

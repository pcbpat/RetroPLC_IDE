// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;

namespace RetroPLC.BuildHost.Debugging;

public sealed record DebugSymbol(uint ArrayIndex, uint ElementIndex, string Path, string DataType, int Size, uint TypeTag);

public sealed class DebugMap
{
    public IReadOnlyDictionary<(uint ArrayIndex, uint ElementIndex), DebugSymbol> Symbols { get; }

    private DebugMap(Dictionary<(uint, uint), DebugSymbol> symbols) => Symbols = symbols;

    public static DebugMap Load(string directory)
    {
        var artifact = Path.Combine(directory, "Build", "Artifacts", "debug_map.json");
        var generated = Path.Combine(directory, "Build", "Generated", "debug_map.json");
        // Only older projects can have a map under Build/Artifacts.
        using var file = OpenMap(generated, artifact);
        using var document = JsonDocument.Parse(file);
        try
        {
            var root = document.RootElement;
            if (root.GetProperty("version").GetInt32() != 2)
                throw new InvalidDataException("Unsupported debug map version. Rebuild the PLC project.");

            // md5 is compiler metadata. The symbolic SMP response has no build identity.
            if (root.TryGetProperty("md5", out var md5) && md5.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Invalid debug map MD5 field.");

            var tags = root.GetProperty("typeTags").EnumerateObject()
                .ToDictionary(tag => tag.Name, tag => tag.Value.GetUInt32(), StringComparer.Ordinal);
            if (tags.Values.Distinct().Count() != tags.Count)
                throw new InvalidDataException("Duplicate debug map type tags.");

            var counts = root.GetProperty("arrays").EnumerateArray().ToDictionary(
                array => array.GetProperty("index").GetUInt32(),
                array => array.GetProperty("count").GetUInt32());
            var symbols = new Dictionary<(uint, uint), DebugSymbol>();
            foreach (var leaf in root.GetProperty("leaves").EnumerateArray())
            {
                var array = leaf.GetProperty("arrayIdx").GetUInt32();
                var element = leaf.GetProperty("elemIdx").GetUInt32();
                var path = leaf.GetProperty("path").GetString();
                var type = leaf.GetProperty("type").GetString();
                var size = leaf.GetProperty("size").GetInt32();
                if (!counts.TryGetValue(array, out var count) || element >= count ||
                    string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(type) || size <= 0 ||
                    !tags.TryGetValue(type, out var tag) ||
                    !symbols.TryAdd((array, element), new DebugSymbol(array, element, path, type, size, tag)))
                    throw new InvalidDataException("Invalid or duplicate debug map leaf.");
            }

            var actualCounts = symbols.Keys.GroupBy(key => key.Item1)
                .ToDictionary(group => group.Key, group => (uint)group.Count());
            if (counts.Any(array => actualCounts.GetValueOrDefault(array.Key) != array.Value))
                throw new InvalidDataException("Debug map array counts do not match the leaves.");

            return new DebugMap(symbols);
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or
                                      FormatException or ArgumentException)
        {
            throw new InvalidDataException("Invalid debug map structure.", error);
        }
    }

    private static FileStream OpenMap(string primary, string fallback)
    {
        try
        {
            return File.OpenRead(primary);
        }
        catch (IOException error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return File.OpenRead(fallback);
        }
    }
}

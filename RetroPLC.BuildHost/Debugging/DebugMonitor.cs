// SPDX-License-Identifier: GPL-3.0-or-later
namespace RetroPLC.BuildHost.Debugging;

public sealed class DebugMonitor(DebugMap map, Func<uint, CancellationToken, Task<DebugPage>> readPage)
{
    private uint? _lastScan;

    public async Task<IReadOnlyDictionary<uint, string>> ReadValuesAsync(CancellationToken cancellationToken)
    {
        var total = (uint)map.Symbols.Values.Count(symbol => symbol.ArrayIndex == 0);
        var values = new Dictionary<uint, string>();
        uint offset = 0;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await readPage(offset, cancellationToken).ConfigureAwait(false);
            if (page.Total != total)
                throw new InvalidDataException("Runtime debug array 0 count differs from the project debug map.");
            if (page.Offset != offset || page.Count != page.Readings.Length || page.Count > total - offset ||
                page.Next != offset + page.Count || (page.Count == 0 && offset < total))
                throw new InvalidDataException("Invalid SMP debug pagination.");
            // Each page is captured on a subsequent PLC scan. Compare with
            // wraparound, without assuming pages form a single scan snapshot.
            if (_lastScan is { } scan && unchecked((int)(page.Scan - scan)) <= 0)
                throw new InvalidDataException("Runtime returned a stale debug scan.");
            _lastScan = page.Scan;

            foreach (var reading in page.Readings)
            {
                if (reading.Id < offset || reading.Id >= page.Next ||
                    !map.Symbols.TryGetValue((0, reading.Id), out var symbol) || values.ContainsKey(reading.Id))
                    throw new InvalidDataException("Unexpected identifier in Runtime debug array 0.");
                // An invalid generated entry may have a sentinel tag.
                if (reading.Status != 1 && reading.Type != symbol.TypeTag)
                    throw new InvalidDataException($"Runtime type differs from the debug map for {symbol.Path}.");

                values.Add(reading.Id, reading.Status switch
                {
                    1 => IecDebugValue.Invalid,
                    2 => IecDebugValue.Unsupported,
                    3 => "<too large>",
                    null when reading.Data is { } data => IecDebugValue.Decode(symbol, data),
                    _ => throw new InvalidDataException("Unknown Runtime debug entry status.")
                });
            }
            offset = page.Next;
        } while (offset < total);

        return values;
    }
}

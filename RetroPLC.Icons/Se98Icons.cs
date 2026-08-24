// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace RetroPLC.Icons;

public static partial class Se98Icons
{
    private const string ResourceRoot =
        "avares://RetroPLC.Icons/Win98SE/SE98/";

    private static readonly ConcurrentDictionary<string, Bitmap> Cache =
        new(StringComparer.Ordinal);

    private static Bitmap Get(string relativePath) =>
        Cache.GetOrAdd(relativePath, static path =>
        {
            using var stream = AssetLoader.Open(new Uri(ResourceRoot + path));
            return new Bitmap(stream);
        });
}

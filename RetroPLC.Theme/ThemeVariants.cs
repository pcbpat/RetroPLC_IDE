// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Styling;
using Classic.Avalonia.Theme;

namespace RetroPLC.Theme;

public static class ThemeVariants
{
    public static ThemeVariant Light { get; } =
        new("RetroPLC.Light", ClassicTheme.Classic);

    public static ThemeVariant Dark { get; } =
        new("RetroPLC.Dark", ClassicTheme.Classic);
}

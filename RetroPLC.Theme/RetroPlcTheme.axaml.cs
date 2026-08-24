// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace RetroPLC.Theme;

public sealed class RetroPlcTheme : Styles
{
    public RetroPlcTheme(IServiceProvider? serviceProvider = null)
    {
        AvaloniaXamlLoader.Load(serviceProvider, this);
    }
}

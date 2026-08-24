// SPDX-License-Identifier: GPL-3.0-or-later
using Dock.Model.Mvvm.Controls;
using RetroPLC.Shell.Controls;

namespace RetroPLC.Shell.ViewModels.Docking;

// A terminal behaves as a tool so Dock accepts Devices/Properties/etc. as
// docking targets. CanDockAsDocument keeps the central editor area valid too.
public sealed class TerminalViewModel : Tool
{
    private bool _focusPending;

    public TerminalViewModel()
    {
        Terminal = new DockTerminalView();
    }

    public DockTerminalView Terminal { get; }

    public void RequestFocus() => _focusPending = true;

    public bool ConsumeFocusRequest()
    {
        if (!_focusPending)
        {
            return false;
        }

        _focusPending = false;
        return true;
    }

    public override bool OnClose()
    {
        if (!base.OnClose())
        {
            return false;
        }

        Terminal.Shutdown();
        return true;
    }
}

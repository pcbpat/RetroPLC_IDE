// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using RetroPLC.Shell.Controls;

namespace RetroPLC.Shell.ViewModels.Docking;

public sealed partial class BuildTerminalSession : ObservableObject
{
    private bool _focusPending;

    [ObservableProperty]
    private ReadOnlyDockTerminalView _terminal = new();

    public void RequestFocus() => _focusPending = true;

    public bool ConsumeFocusRequest()
    {
        if (!_focusPending)
            return false;

        _focusPending = false;
        return true;
    }
}

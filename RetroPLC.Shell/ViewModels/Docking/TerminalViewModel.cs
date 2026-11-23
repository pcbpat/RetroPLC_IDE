// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.IO;
using System.Linq;
using RetroPLC.BuildHost;
using Dock.Model.Mvvm.Controls;
using RetroPLC.Shell.Controls;

namespace RetroPLC.Shell.ViewModels.Docking;

// A terminal behaves as a tool so Dock accepts Devices/Properties/etc. as
// docking targets. CanDockAsDocument keeps the central editor area valid too.
public sealed class TerminalViewModel : Tool
{
    private bool _focusPending;

    public TerminalViewModel(ZephyrConfiguration configuration, string? projectDirectory,
        ZephyrBuildConfiguration? project)
    {
        try
        {
            var launch = configuration.CreateTerminal(projectDirectory, project);
            Terminal = new DockTerminalView
            {
                Process = launch.Executable,
                Args = launch.Arguments.ToList(),
                StartingDirectory = launch.WorkingDirectory
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           PlatformNotSupportedException or ArgumentException or InvalidOperationException)
        {
            LaunchError = exception.Message;
        }
    }

    public DockTerminalView? Terminal { get; }
    public string? LaunchError { get; }
    public bool HasLaunchError => LaunchError is not null;

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

        Terminal?.Shutdown();
        return true;
    }
}

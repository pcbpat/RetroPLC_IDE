// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using Avalonia.LogicalTree;
using Iciclecreek.Terminal;

namespace RetroPLC.Shell.Controls;

/// <summary>
/// Terminal renderer whose PTY survives Dock's temporary logical-tree removal.
/// </summary>
public class DockTerminalView : TerminalView
{
    private bool _shutdown;

    public void Shutdown()
    {
        if (_shutdown)
        {
            return;
        }

        _shutdown = true;
        try
        {
            Kill();
        }
        catch (Exception)
        {
            // Iciclecreek.Avalonia.Terminal 2.0.3 can throw while Kill races
            // PTY startup/exit, and also when a terminal is closed before its
            // visual initialization has created the PTY. Shutdown must never
            // veto Dock's close operation.
        }
    }

    protected override void OnDetachedFromLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        if (!_shutdown)
        {
            // This override runs on the exact control that owns the PTY, before
            // TerminalView's base implementation decides whether to clean it up.
            BeginReparent();
        }

        base.OnDetachedFromLogicalTree(e);
    }

    protected override void OnAttachedToLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        EndReparent();
    }
}

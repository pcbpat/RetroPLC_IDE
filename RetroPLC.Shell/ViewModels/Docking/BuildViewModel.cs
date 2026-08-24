// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Mvvm.Controls;
using Iciclecreek.Terminal;
using RetroPLC.BuildHost;
using RetroPLC.Shell.Controls;

namespace RetroPLC.Shell.ViewModels.Docking;

public sealed class BuildViewModel : Tool
{
    private readonly FirmwareBuildService _buildService = new();
    private FirmwareBuildSession? _buildSession;

    public BuildViewModel()
    {
        Session = new BuildTerminalSession();
    }

    public event Action<BuildOperation, int>? OperationExited;

    public BuildTerminalSession Session { get; }

    public void PrepareVerify(string projectDirectory, string projectName) =>
        StartSession(_buildService.StartVerify(projectDirectory, projectName));

    public void PrepareBuild(string projectDirectory, string projectName) =>
        StartSession(_buildService.StartBuild(projectDirectory, projectName));

    public void PrepareRebuild(string projectDirectory, string projectName) =>
        StartSession(_buildService.StartRebuild(projectDirectory, projectName));

    public void PrepareClean(string projectDirectory) =>
        StartSession(_buildService.StartClean(projectDirectory));

    public void PrepareDownload(string projectDirectory, string projectName) =>
        StartSession(_buildService.StartDownload(projectDirectory, projectName));

    private void StartSession(FirmwareBuildSession session)
    {
        _buildSession = session;
        StartProcess(session.InitialProcess);
    }

    private void StartProcess(BuildProcess process)
    {
        // TerminalView launches automatically from its Loaded event.
        var terminal = new ReadOnlyDockTerminalView
        {
            Process = process.Executable,
            Args = process.Arguments.ToList(),
            StartingDirectory = process.WorkingDirectory
        };
        terminal.ProcessExited += OnProcessExited;

        var previousTerminal = Session.Terminal;
        previousTerminal.ProcessExited -= OnProcessExited;
        Session.RequestFocus();
        Session.Terminal = terminal;
        previousTerminal.Shutdown();
    }

    public override bool OnClose()
    {
        if (!base.OnClose())
            return false;

        Session.Terminal.ProcessExited -= OnProcessExited;
        Session.Terminal.Shutdown();
        return true;
    }

    private void OnProcessExited(object? sender, ProcessExitedEventArgs e)
    {
        if (_buildSession is not { } session)
            return;

        var outcome = session.ProcessExited(e.ExitCode);
        if (outcome.NextProcess is { } nextProcess)
        {
            Dispatcher.UIThread.Post(() => StartProcess(nextProcess));
            return;
        }

        _buildSession = null;
        OperationExited?.Invoke(session.Operation, outcome.ExitCode ?? -1);
    }
}

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

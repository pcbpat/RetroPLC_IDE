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
    private readonly FirmwareBuildService _buildService;
    private FirmwareBuildSession? _buildSession;

    public BuildViewModel() : this(new FirmwareBuildService())
    {
    }

    public BuildViewModel(FirmwareBuildService buildService)
    {
        _buildService = buildService;
        Session = new BuildTerminalSession();
    }

    public event Action<BuildOperation, int>? OperationExited;

    public BuildTerminalSession Session { get; }
    public string? OperationError { get; private set; }

    public void PrepareVerify(string projectDirectory, string projectName) =>
        StartSession(_buildService.StartVerify(projectDirectory, projectName));

    public void PrepareBuild(string projectDirectory, string projectName) =>
        PrepareBuild(projectDirectory, projectName, null);

    public void PrepareBuild(string projectDirectory, string projectName, ZephyrBuildConfiguration? project) =>
        StartSession(_buildService.StartBuild(projectDirectory, projectName, project));

    public void PrepareRebuild(string projectDirectory, string projectName) =>
        PrepareRebuild(projectDirectory, projectName, null);

    public void PrepareRebuild(string projectDirectory, string projectName, ZephyrBuildConfiguration? project) =>
        StartSession(_buildService.StartRebuild(projectDirectory, projectName, project));

    public void PrepareClean(string projectDirectory) =>
        PrepareClean(projectDirectory, null);

    public void PrepareClean(string projectDirectory, ZephyrBuildConfiguration? project) =>
        StartSession(_buildService.StartClean(projectDirectory, project));

    public void PrepareDownload(string projectDirectory, string projectName) =>
        PrepareDownload(projectDirectory, projectName, null);

    public void PrepareDownload(string projectDirectory, string projectName, ZephyrBuildConfiguration? project) =>
        StartSession(_buildService.StartDownload(projectDirectory, projectName, project));

    private void StartSession(FirmwareBuildSession session)
    {
        OperationError = null;
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

        _buildSession = null;
        Session.Terminal.ProcessExited -= OnProcessExited;
        Session.Terminal.Shutdown();
        return true;
    }

    private void OnProcessExited(object? sender, ProcessExitedEventArgs e) =>
        Dispatcher.UIThread.Post(() => CompleteStage(sender, e.ExitCode));

    private void CompleteStage(object? sender, int exitCode)
    {
        if (!ReferenceEquals(sender, Session.Terminal) || _buildSession is not { } session)
            return;

        var outcome = session.ProcessExited(exitCode);
        if (outcome.NextProcess is { } nextProcess)
        {
            StartProcess(nextProcess);
            return;
        }

        _buildSession = null;
        OperationError = outcome.ErrorMessage;
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

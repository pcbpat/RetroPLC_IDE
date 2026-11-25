// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Dock.Model.Mvvm.Controls;
using RetroPLC.BuildHost;

namespace RetroPLC.Shell.ViewModels.Docking;

public sealed class BuildViewModel : Tool
{
    private readonly FirmwareBuildService _buildService;
    private CancellationTokenSource? _operation;
    private BuildOperation? _activeOperation;

    public BuildViewModel() : this(new FirmwareBuildService())
    {
    }

    public BuildViewModel(FirmwareBuildService buildService) => _buildService = buildService;

    public event Action<BuildOperation, int>? OperationExited;
    public event Action? DownloadActivityChanged;

    public ObservableCollection<string> Output { get; } = [];
    public bool IsDownloadActive => _activeOperation == BuildOperation.Download;
    public string? OperationError { get; private set; }
    public string? OperationStatus { get; private set; }
    public Task ProcessCompletion { get; private set; } = Task.CompletedTask;

    public async Task RunAsync(BuildOperation operation, string directory, ZephyrBuildConfiguration? project,
        DeviceConnection? device, CancellationToken cancellationToken)
    {
        if (_operation is not null)
            return;

        using var active = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _operation = active;
        _activeOperation = operation;
        OperationError = null;
        OperationStatus = null;
        Output.Clear();
        Output.Add($"{operation} started.");
        if (IsDownloadActive)
            DownloadActivityChanged?.Invoke();

        // Progress captures the Avalonia synchronization context of the dock command.
        var output = new Progress<string>(line =>
        {
            if (ReferenceEquals(_operation, active))
                Output.Add(line);
        });
        var events = new Progress<BuildEvent>(message =>
        {
            if (!ReferenceEquals(_operation, active))
                return;

            var line = message.Kind switch
            {
                "progress" when message.Transmitted is { } sent && message.Total is { } total =>
                    $"{message.Step} {sent:N0} / {total:N0} bytes",
                "progress" when message.Step is { } step => $"{operation}: {step}",
                "error" => $"{message.Command} [{message.Code}] {message.Message}".Trim(),
                _ => null
            };
            if (line is not null)
                Output.Add(line);
        });

        BuildResult result;
        try
        {
            var execution = _buildService.RunAsync(operation, directory, project, device, output, events, active.Token);
            ProcessCompletion = execution;
            result = await execution;
        }
        catch (OperationCanceledException) when (active.IsCancellationRequested)
        {
            result = new BuildResult(-1, "Operation cancelled.");
        }

        OperationError = result.Error;
        OperationStatus = result.Success ? result.Event?.Status switch
        {
            "already_installed" => "Firmware already installed.",
            "update_requested" => "Firmware update requested. Verify the running image after reboot.",
            _ => $"{operation} succeeded."
        } : null;
        Output.Add(OperationStatus ?? (OperationError is { } error
            ? $"{operation} failed: {error}"
            : $"{operation} failed ({result.ExitCode})."));
        _operation = null;
        _activeOperation = null;
        if (operation == BuildOperation.Download)
            DownloadActivityChanged?.Invoke();
        OperationExited?.Invoke(operation, result.ExitCode);
    }

    public override bool OnClose()
    {
        if (!base.OnClose())
            return false;

        _operation?.Cancel();
        return true;
    }
}

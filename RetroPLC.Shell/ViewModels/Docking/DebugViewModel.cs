// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Mvvm.Controls;
using RetroPLC.BuildHost.Debugging;

namespace RetroPLC.Shell.ViewModels.Docking;

public sealed class DebugViewModel : Tool
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(50);
    private readonly Dictionary<uint, DebugVariableViewModel> _rows = new();
    private CancellationTokenSource? _session;
    private Task _worker = Task.CompletedTask;

    public ObservableCollection<DebugVariableViewModel> Variables { get; } = new();
    public bool IsMonitoring => _session is not null;

    public void Start(string directory, Action connected, Action<string> disconnected)
    {
        if (IsMonitoring)
            return;

        Variables.Clear();
        _rows.Clear();
        var session = new CancellationTokenSource();
        _session = session;
        _worker = Task.Run(() => PollAsync(directory, session, connected, disconnected));
    }

    public Task StopAsync()
    {
        if (_session is not { } session)
            return _worker;

        _session = null;
        MarkOffline();
        return DisposeSessionAsync(session, _worker);
    }

    private static async Task DisposeSessionAsync(CancellationTokenSource session, Task worker)
    {
        using (session)
        {
            await session.CancelAsync().ConfigureAwait(false);
            await worker.ConfigureAwait(false);
        }
    }

    private async Task PollAsync(string directory, CancellationTokenSource session, Action connected,
        Action<string> disconnected)
    {
        var token = session.Token;
        var failure = string.Empty;
        try
        {
            token.ThrowIfCancellationRequested();
            var map = DebugMap.Load(directory);
            var symbols = map.Symbols.Values.Where(symbol => symbol.ArrayIndex == 0)
                .OrderBy(symbol => symbol.ElementIndex).ToArray();
            if (symbols.Length == 0)
                throw new InvalidDataException("The debug map has no symbols in array 0.");

            Post(() =>
            {
                foreach (var symbol in symbols)
                {
                    var row = new DebugVariableViewModel(symbol);
                    _rows.Add(symbol.ElementIndex, row);
                    Variables.Add(row);
                }
            });

            var client = new SmpDebugClient();
            var monitor = new DebugMonitor(map, client.ReadPageAsync);
            var first = true;
            while (true)
            {
                var values = await monitor.ReadValuesAsync(token).ConfigureAwait(false);
                Post(() =>
                {
                    foreach (var (id, value) in values)
                        _rows[id].Value = value;
                });
                if (first)
                {
                    Post(connected);
                    first = false;
                }

                await Task.Delay(RefreshInterval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            failure = error is FileNotFoundException { FileName: { } filename } &&
                      Path.GetFileName(filename) == "debug_map.json" || error is DirectoryNotFoundException
                ? "No debug map found. Build the PLC project first."
                : error.Message;
        }
        finally
        {
            Post(() =>
            {
                _session = null;
                session.Dispose();
                MarkOffline();
                disconnected(failure);
            });
        }

        void Post(Action action) => Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(_session, session))
                action();
        });
    }

    private void MarkOffline()
    {
        foreach (var row in Variables)
            row.Value = "<offline>";
    }
}

public sealed class DebugVariableViewModel(DebugSymbol symbol) : ObservableObject
{
    private string _value = "<pending>";
    public string Parameter => symbol.Path;
    public uint ArrayIndex => symbol.ArrayIndex;
    public uint ElementIndex => symbol.ElementIndex;
    public string DataType => symbol.DataType;

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }
}

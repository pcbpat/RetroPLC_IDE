// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Text.Json.Nodes;
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Client;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace RetroPLC.LanguageServerHost;

internal sealed class OmniSharpLspClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly LanguageClient _client;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Action<string> _errorHandler;
    private readonly Task _errorTask;
    private bool _shutdownStarted;

    private OmniSharpLspClient(
        Process process,
        LanguageClient client,
        Action<string> errorHandler)
    {
        _process = process;
        _client = client;
        _errorHandler = errorHandler;
        _errorTask = Task.Run(() => ReadErrorsAsync(_lifetime.Token));
    }

    public bool IsRunning
    {
        get
        {
            try
            {
                return !_process.HasExited && !_lifetime.IsCancellationRequested;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    public static async Task<OmniSharpLspClient> StartAsync(
        StrucppToolCommand command,
        string workingDirectory,
        Func<string, JsonNode?, CancellationToken, Task<JsonNode?>> requestHandler,
        Action<string, JsonNode?> notificationHandler,
        Action<string> errorHandler,
        CancellationToken cancellationToken)
    {
        var projectDirectory = Path.GetFullPath(workingDirectory);
        var startInfo = new ProcessStartInfo
        {
            FileName = command.ExecutablePath,
            WorkingDirectory = projectDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in command.PrefixArguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add("--stdio");

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Failed to start the STruC++ language server.");
        }

        try
        {
            var projectUri = DocumentUri.FromFileSystemPath(projectDirectory);
            var client = await LanguageClient.From(
                options =>
                {
                    options
                        .WithInput(process.StandardOutput.BaseStream)
                        .WithOutput(process.StandardInput.BaseStream)
                        .WithRootUri(projectUri)
                        .WithWorkspaceFolder(projectUri, Path.GetFileName(projectDirectory))
                        .WithClientInfo(new ClientInfo
                        {
                            Name = "RetroPLC IDE",
                            Version = "1.0.0"
                        })
                        .WithTrace(InitializeTrace.Off)
                        .WithClientCapabilities(BuildClientCapabilities())
                        .WithUnhandledExceptionHandler(
                            exception => errorHandler(
                                $"STruC++ LSP transport failed: {exception.Message}"));

                    options.OnJsonRequest(
                        "workspace/configuration",
                        async (parameters, token) =>
                            ToJToken(await requestHandler(
                                "workspace/configuration",
                                ToJsonNode(parameters),
                                token).ConfigureAwait(false)));
                    options.OnJsonNotification(
                        "textDocument/publishDiagnostics",
                        parameters => notificationHandler(
                            "textDocument/publishDiagnostics",
                            ToJsonNode(parameters)));
                },
                cancellationToken).ConfigureAwait(false);

            return new OmniSharpLspClient(process, client, errorHandler);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            process.Dispose();
            throw;
        }
    }

    public async Task<JsonNode?> RequestAsync(
        string method,
        JsonNode? parameters,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_lifetime.IsCancellationRequested, this);
        var result = await _client
            .SendRequest(method, ToJToken(parameters))
            .Returning<JToken?>(cancellationToken)
            .ConfigureAwait(false);
        return ToJsonNode(result);
    }

    public Task NotifyAsync(
        string method,
        JsonNode? parameters,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_lifetime.IsCancellationRequested, this);
        cancellationToken.ThrowIfCancellationRequested();
        _client.SendNotification(method, ToJToken(parameters));
        return Task.CompletedTask;
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (_shutdownStarted)
            return;
        _shutdownStarted = true;

        if (!_process.HasExited)
        {
            try
            {
                await _client.Shutdown().WaitAsync(cancellationToken).ConfigureAwait(false);
                await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or OperationCanceledException)
            {
                _errorHandler($"Graceful LSP shutdown failed: {exception.Message}");
            }
        }
    }

    private async Task ReadErrorsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await _process.StandardError
                    .ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                    break;
                if (!string.IsNullOrWhiteSpace(line))
                    _errorHandler(line);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException exception)
        {
            _errorHandler($"STruC++ LSP error stream failed: {exception.Message}");
        }
    }

    private static ClientCapabilities BuildClientCapabilities() => new()
    {
        General = new GeneralClientCapabilities
        {
            PositionEncodings = new Container<PositionEncodingKind>(
                PositionEncodingKind.UTF16)
        },
        Workspace = new WorkspaceClientCapabilities
        {
            Configuration = true,
            WorkspaceFolders = true
        },
        TextDocument = new TextDocumentClientCapabilities
        {
            Synchronization = new TextSynchronizationCapability { DidSave = true },
            PublishDiagnostics = new PublishDiagnosticsCapability { VersionSupport = true },
            DocumentSymbol = new DocumentSymbolCapability
            {
                HierarchicalDocumentSymbolSupport = true
            },
            Completion = new CompletionCapability
            {
                CompletionItem = new CompletionItemCapabilityOptions
                {
                    SnippetSupport = true
                }
            },
            Formatting = new DocumentFormattingCapability(),
            Rename = new RenameCapability { PrepareSupport = true }
        }
    };

    private static JToken ToJToken(JsonNode? node) =>
        node is null ? JValue.CreateNull() : JToken.Parse(node.ToJsonString());

    private static JsonNode? ToJsonNode(JToken? token) =>
        token is null || token.Type is JTokenType.Null or JTokenType.Undefined
            ? null
            : JsonNode.Parse(token.ToString(Newtonsoft.Json.Formatting.None));

    public async ValueTask DisposeAsync()
    {
        if (!_shutdownStarted)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await ShutdownAsync(timeout.Token).ConfigureAwait(false);
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await _errorTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is TimeoutException or OperationCanceledException)
        {
        }

        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().ConfigureAwait(false);
        }

        _client.Dispose();
        _process.Dispose();
        _lifetime.Dispose();
    }
}

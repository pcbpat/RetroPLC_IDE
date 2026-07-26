using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RetroPLC.LanguageServerHost;

internal sealed class LspJsonRpcClient : IAsyncDisposable
{
    private const int MaximumHeaderBytes = 16 * 1024;
    private const int MaximumMessageBytes = 64 * 1024 * 1024;

    private readonly Process _process;
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<string, JsonNode?, CancellationToken, Task<JsonNode?>> _requestHandler;
    private readonly Action<string, JsonNode?> _notificationHandler;
    private readonly Action<string> _errorHandler;
    private readonly Task _readTask;
    private readonly Task _errorTask;
    private long _nextRequestId;
    private bool _shutdownStarted;

    private LspJsonRpcClient(
        Process process,
        Func<string, JsonNode?, CancellationToken, Task<JsonNode?>> requestHandler,
        Action<string, JsonNode?> notificationHandler,
        Action<string> errorHandler)
    {
        _process = process;
        _input = process.StandardInput.BaseStream;
        _output = process.StandardOutput.BaseStream;
        _requestHandler = requestHandler;
        _notificationHandler = notificationHandler;
        _errorHandler = errorHandler;
        _readTask = Task.Run(() => ReadLoopAsync(_lifetime.Token));
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

    public static LspJsonRpcClient Start(
        string executablePath,
        string workingDirectory,
        Func<string, JsonNode?, CancellationToken, Task<JsonNode?>> requestHandler,
        Action<string, JsonNode?> notificationHandler,
        Action<string> errorHandler)
    {
        var fullPath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException(
                "The STruC++ language-server executable was not found.", fullPath);

        StrucppToolchain.EnsureExecutable(fullPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = fullPath,
            WorkingDirectory = Path.GetFullPath(workingDirectory),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--stdio");

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Failed to start the STruC++ language server.");
        }

        return new LspJsonRpcClient(
            process, requestHandler, notificationHandler, errorHandler);
    }

    public async Task<JsonNode?> RequestAsync(
        string method,
        JsonNode? parameters,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_lifetime.IsCancellationRequested, this);
        var id = Interlocked.Increment(ref _nextRequestId);
        var key = id.ToString(CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<JsonNode?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(key, completion))
            throw new InvalidOperationException($"Duplicate JSON-RPC request ID {id}.");

        try
        {
            await SendAsync(
                new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id,
                    ["method"] = method,
                    ["params"] = parameters?.DeepClone()
                },
                cancellationToken).ConfigureAwait(false);
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    public Task NotifyAsync(
        string method,
        JsonNode? parameters,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = method,
                ["params"] = parameters?.DeepClone()
            },
            cancellationToken);

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (_shutdownStarted)
            return;
        _shutdownStarted = true;

        if (!_process.HasExited)
        {
            try
            {
                await RequestAsync("shutdown", null, cancellationToken).ConfigureAwait(false);
                await NotifyAsync("exit", null, cancellationToken).ConfigureAwait(false);
                await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or OperationCanceledException or JsonRpcException)
            {
                _errorHandler($"Graceful LSP shutdown failed: {exception.Message}");
            }
        }
    }

    private async Task SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message);
        var header = Encoding.ASCII.GetBytes(
            $"Content-Length: {body.Length.ToString(CultureInfo.InvariantCulture)}\r\n\r\n");

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _input.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await _input.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            await _input.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await ReadMessageAsync(_output, cancellationToken).ConfigureAwait(false);
                if (message is null)
                    break;
                await DispatchAsync(message, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            FailPending(exception);
            _errorHandler($"STruC++ LSP transport failed: {exception.Message}");
        }
        finally
        {
            if (!_lifetime.IsCancellationRequested)
                FailPending(new EndOfStreamException("The language-server output stream closed."));
        }
    }

    private async Task DispatchAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var method = message["method"]?.GetValue<string>();
        var id = message["id"];

        if (method is not null && id is not null)
        {
            await RespondToServerRequestAsync(
                id, method, message["params"], cancellationToken).ConfigureAwait(false);
            return;
        }

        if (method is not null)
        {
            _notificationHandler(method, message["params"]?.DeepClone());
            return;
        }

        if (id is null || !_pending.TryRemove(GetIdKey(id), out var completion))
            return;

        if (message["error"] is { } error)
            completion.TrySetException(new JsonRpcException(error.ToJsonString()));
        else
            completion.TrySetResult(message["result"]?.DeepClone());
    }

    private async Task RespondToServerRequestAsync(
        JsonNode id,
        string method,
        JsonNode? parameters,
        CancellationToken cancellationToken)
    {
        JsonObject response;
        try
        {
            var result = await _requestHandler(
                method, parameters?.DeepClone(), cancellationToken).ConfigureAwait(false);
            response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id.DeepClone(),
                ["result"] = result?.DeepClone()
            };
        }
        catch (Exception exception)
        {
            response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id.DeepClone(),
                ["error"] = new JsonObject
                {
                    ["code"] = -32603,
                    ["message"] = exception.Message
                }
            };
        }

        await SendAsync(response, cancellationToken).ConfigureAwait(false);
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

    private static async Task<JsonObject?> ReadMessageAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var headers = new List<byte>(128);
        var terminatorState = 0;
        while (headers.Count < MaximumHeaderBytes)
        {
            var buffer = new byte[1];
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return headers.Count == 0
                    ? null
                    : throw new EndOfStreamException("Unexpected EOF in an LSP header.");

            var current = buffer[0];
            headers.Add(current);
            terminatorState = (terminatorState, current) switch
            {
                (0, 13) => 1,
                (1, 10) => 2,
                (2, 13) => 3,
                (3, 10) => 4,
                (_, 13) => 1,
                _ => 0
            };
            if (terminatorState == 4)
                break;
        }

        var headerText = Encoding.ASCII.GetString(headers.ToArray());
        var contentLength = ParseContentLength(headerText);
        if (contentLength is < 0 or > MaximumMessageBytes)
            throw new InvalidDataException($"Invalid LSP Content-Length: {contentLength}.");

        var body = new byte[contentLength];
        await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        return JsonNode.Parse(body) as JsonObject
               ?? throw new InvalidDataException("The LSP body was not a JSON object.");
    }

    private static int ParseContentLength(string headers)
    {
        foreach (var line in headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            const string name = "Content-Length:";
            if (line.StartsWith(name, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line[name.Length..].Trim(), CultureInfo.InvariantCulture, out var length))
                return length;
        }

        throw new InvalidDataException("The LSP header has no Content-Length.");
    }

    private static string GetIdKey(JsonNode id) =>
        id is JsonValue value && value.TryGetValue<long>(out var numeric)
            ? numeric.ToString(CultureInfo.InvariantCulture)
            : id.GetValue<string>();

    private void FailPending(Exception exception)
    {
        foreach (var completion in _pending.Values)
            completion.TrySetException(exception);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_shutdownStarted)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await ShutdownAsync(timeout.Token).ConfigureAwait(false);
        }

        _lifetime.Cancel();
        try
        {
            await Task.WhenAll(_readTask, _errorTask)
                .WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
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

        _input.Dispose();
        _output.Dispose();
        _process.Dispose();
        _writeLock.Dispose();
        _lifetime.Dispose();
    }
}

internal sealed class JsonRpcException(string message) : Exception(message);

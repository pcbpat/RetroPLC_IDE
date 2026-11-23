// SPDX-License-Identifier: GPL-3.0-or-later
namespace RetroPLC.BuildHost;

public sealed record ZephyrBoardInfo(string Name, string Target);

public interface IZephyrBoardService
{
    Task<IReadOnlyList<ZephyrBoardInfo>> GetAvailableBoardsAsync(CancellationToken cancellationToken = default);
    Task<bool> ValidateBoardAsync(string boardTarget, CancellationToken cancellationToken = default);
}

public sealed class ZephyrBoardService : IZephyrBoardService
{
    internal const string DefaultTarget = "arduino_opta/stm32h747xx/m7";

    internal ZephyrBoardService() { }

    // Preserve the existing constructor for consumers of the compatibility API.
    public ZephyrBoardService(Ac6Environment environment, string workspaceDirectory,
        string? hostToolsDirectory = null)
    {
    }

    public Task<IReadOnlyList<ZephyrBoardInfo>> GetAvailableBoardsAsync(
        CancellationToken cancellationToken = default) =>
        UnsupportedAsync<IReadOnlyList<ZephyrBoardInfo>>(cancellationToken);

    public Task<bool> ValidateBoardAsync(string boardTarget, CancellationToken cancellationToken = default) =>
        UnsupportedAsync<bool>(cancellationToken);

    private static Task<T> UnsupportedAsync<T>(CancellationToken token) => token.IsCancellationRequested
        ? Task.FromCanceled<T>(token)
        : Task.FromException<T>(new NotSupportedException(
            "Board discovery is unavailable. Set the board target in the project. Zephyr checks it during west build."));
}

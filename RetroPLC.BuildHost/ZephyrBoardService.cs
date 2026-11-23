// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;

namespace RetroPLC.BuildHost;

public sealed record ZephyrBoardInfo(string Name, string Target);

public interface IZephyrBoardService
{
    Task<IReadOnlyList<ZephyrBoardInfo>> GetAvailableBoardsAsync(CancellationToken cancellationToken = default);
    Task<bool> ValidateBoardAsync(string boardTarget, CancellationToken cancellationToken = default);
}

public sealed class ZephyrBoardService : IZephyrBoardService
{
    private readonly Ac6Environment _environment;
    private readonly string _workspace;
    private readonly string? _hostTools;

    public ZephyrBoardService(Ac6Environment environment, string workspaceDirectory,
        string? hostToolsDirectory = null)
    {
        _environment = environment;
        _workspace = ZephyrConfiguration.RequireWorkspace(
            Path.GetFullPath(workspaceDirectory, environment.Installation.ApplicationDirectory));
        _hostTools = hostToolsDirectory;
    }

    public async Task<IReadOnlyList<ZephyrBoardInfo>> GetAvailableBoardsAsync(
        CancellationToken cancellationToken = default)
    {
        var help = await RunAsync(["boards", "--help"], cancellationToken).ConfigureAwait(false);
        help.EnsureSuccess("Zephyr board discovery");
        var allTargets = help.Output.Contains("--all-targets", StringComparison.Ordinal);
        var qualifiers = !allTargets && help.Output.Contains("qualifiers", StringComparison.Ordinal);
        var formatted = help.Output.Contains("--format", StringComparison.Ordinal);
        var result = await RunAsync(allTargets ? ["boards", "--all-targets"] :
            qualifiers ? ["boards", "-f", "{name}|{qualifiers}"] :
            formatted ? ["boards", "-f", "{name}"] : ["boards"], cancellationToken).ConfigureAwait(false);
        result.EnsureSuccess("Zephyr board discovery");

        IEnumerable<string> targets = result.Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (qualifiers)
            targets = targets.SelectMany(ParseQualifiedTargets);

        return targets.Where(IsBoardTarget).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(target => new ZephyrBoardInfo(target.Split('/', '@')[0], target)).ToArray();
    }

    public async Task<bool> ValidateBoardAsync(string boardTarget, CancellationToken cancellationToken = default)
    {
        if (!IsBoardTarget(boardTarget))
            return false;

        var available = await GetAvailableBoardsAsync(cancellationToken).ConfigureAwait(false);
        if (available.Any(board => board.Target == boardTarget))
            return true;

        // Zephyr can infer an omitted SoC/CPU qualifier if only one target matches.
        return available.Count(board => board.Target.StartsWith(boardTarget + "/", StringComparison.Ordinal)) == 1;
    }

    internal static bool IsBoardTarget(string? target) =>
        target is not null && Regex.IsMatch(target, @"\A[\w.][\w.-]*(?:@[\w.-]+)?(?:/[\w.-]+)*\z") &&
        target.Split('/').All(part => part is not "." and not "..");

    private Task<CommandResult> RunAsync(string[] arguments, CancellationToken cancellationToken) =>
        Ac6Environment.RunAsync(_environment.CreateWest(_workspace, _hostTools, null, arguments), cancellationToken);

    private static IEnumerable<string> ParseQualifiedTargets(string line)
    {
        var fields = line.Split('|');
        if (fields.Length != 2)
            return [];

        var qualifiers = fields[1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return qualifiers.Length == 0 ? [fields[0]] : qualifiers.Select(qualifier => $"{fields[0]}/{qualifier}");
    }
}

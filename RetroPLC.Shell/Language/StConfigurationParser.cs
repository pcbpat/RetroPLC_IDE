// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RetroPLC.LanguageServerHost;

namespace RetroPLC.Shell.Language;

/// <summary>
/// A symbol parsed from a CONFIGURATION source file. The STruC++ language
/// server reports no document symbols for CONFIGURATION files, so the IDE
/// builds the configuration project tree from these parsed elements instead.
/// </summary>
public sealed record StConfigurationSymbol(
    string Kind,
    string Name,
    string DisplayName,
    StrucppRange Range,
    IReadOnlyList<StConfigurationSymbol> Children);

public static class StConfigurationSymbolKinds
{
    public const string Configuration = "configuration";
    public const string GlobalVariable = "global-variable";
    public const string Resource = "resource";
    public const string Task = "task";
    public const string ProgramInstance = "program-instance";
}

/// <summary>
/// Parses IEC 61131-3 CONFIGURATION blocks from Structured Text source:
/// CONFIGURATION, VAR_GLOBAL variables, RESOURCE, TASK (INTERVAL / SINGLE /
/// PRIORITY) and PROGRAM instances (name : type WITH task). Comments and
/// single-quoted strings are ignored, and PROGRAM instances are attached to
/// the task they reference with WITH.
/// </summary>
public static class StConfigurationParser
{
    public static IReadOnlyList<StConfigurationSymbol> Parse(string source)
    {
        var lines = SplitLines(StripCommentsAndStrings(source));
        var symbols = new List<StConfigurationSymbol>();
        ConfigurationBuilder? configuration = null;
        ResourceBuilder? resource = null;
        var inGlobalVariables = false;

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;

            if (configuration is null)
            {
                if (TryMatchKeyword(trimmed, "CONFIGURATION", out var configurationName, out _))
                {
                    configuration = new ConfigurationBuilder(
                        configurationName,
                        RangeOf(line, index, configurationName));
                }

                continue;
            }

            if (IsKeyword(trimmed, "END_CONFIGURATION"))
            {
                symbols.Add(configuration.Build());
                configuration = null;
                resource = null;
                inGlobalVariables = false;
                continue;
            }

            if (inGlobalVariables)
            {
                if (IsKeyword(trimmed, "END_VAR"))
                {
                    inGlobalVariables = false;
                    continue;
                }

                AddGlobalVariable(configuration, line, index);
                continue;
            }

            if (IsKeyword(trimmed, "VAR_GLOBAL"))
            {
                inGlobalVariables = true;
                continue;
            }

            if (TryMatchKeyword(trimmed, "RESOURCE", out var resourceName, out var resourceRest))
            {
                // A new RESOURCE closes the previous one.
                if (resource is not null)
                    configuration.AddResource(resource);
                var processor = ParseProcessor(resourceRest);
                resource = new ResourceBuilder(
                    resourceName,
                    processor,
                    RangeOf(line, index, resourceName));
                continue;
            }

            if (resource is null)
                continue;

            if (IsKeyword(trimmed, "END_RESOURCE"))
            {
                configuration.AddResource(resource);
                resource = null;
                continue;
            }

            if (TryMatchKeyword(trimmed, "TASK", out var taskName, out var taskRest))
            {
                resource.AddTask(CreateTaskSymbol(taskName, taskRest, line, index));
                continue;
            }

            if (TryMatchKeyword(trimmed, "PROGRAM", out var programName, out var programRest))
            {
                var (program, withTask) = CreateProgramSymbol(programName, programRest, line, index);
                resource.AddProgram(program, withTask);
            }
        }

        if (configuration is not null)
            symbols.Add(configuration.Build());

        return symbols;
    }

    private static StConfigurationSymbol CreateTaskSymbol(
        string name,
        string rest,
        string line,
        int lineIndex)
    {
        var interval = MatchArgument(rest, "INTERVAL");
        var single = MatchArgument(rest, "SINGLE");
        var priority = MatchArgument(rest, "PRIORITY");
        var schedule = interval is not null
            ? $"cyclic · {interval}"
            : single is not null
                ? $"interrupt · {single}"
                : "cyclic";
        var prioritySuffix = priority is not null ? $" · priority {priority}" : string.Empty;
        var display = $"{name} (TASK · {schedule}{prioritySuffix})";
        return new StConfigurationSymbol(
            StConfigurationSymbolKinds.Task,
            name,
            display,
            RangeOf(line, lineIndex, name),
            []);
    }

    private static (StConfigurationSymbol Symbol, string? WithTask) CreateProgramSymbol(
        string name,
        string rest,
        string line,
        int lineIndex)
    {
        var restSpan = rest.AsSpan().Trim();
        var withTask = (string?)null;
        var type = (string?)null;
        if (restSpan.StartsWith("WITH ", StringComparison.OrdinalIgnoreCase))
        {
            var remainder = restSpan["WITH ".Length..];
            var withEnd = remainder.IndexOf(':');
            withTask = withEnd < 0
                ? remainder.Trim().ToString()
                : remainder[..withEnd].Trim().ToString();
            restSpan = withEnd < 0 ? [] : remainder[(withEnd + 1)..];
        }

        restSpan = restSpan.TrimStart(':');
        if (restSpan.Length > 0)
        {
            var typeEnd = restSpan.IndexOf(';');
            type = (typeEnd < 0 ? restSpan : restSpan[..typeEnd]).Trim().ToString();
        }

        var display = withTask is null
            ? $"{name} : {type ?? string.Empty}".TrimEnd()
            : $"{name} : {type ?? string.Empty} WITH {withTask}".TrimEnd();
        return (
            new StConfigurationSymbol(
                StConfigurationSymbolKinds.ProgramInstance,
                name,
                display,
                RangeOf(line, lineIndex, name),
                []),
            withTask);
    }

    private static void AddGlobalVariable(
        ConfigurationBuilder configuration,
        string line,
        int lineIndex)
    {
        var trimmed = line.Trim();
        var colon = trimmed.IndexOf(':');
        if (colon <= 0)
            return;

        var names = trimmed[..colon];
        foreach (var part in names.Split(','))
        {
            var name = part.Trim();
            if (!IecIdentifier.IsValid(name))
                continue;
            var type = trimmed[(colon + 1)..].Trim().TrimEnd(';').Trim();
            configuration.AddGlobalVariable(new StConfigurationSymbol(
                StConfigurationSymbolKinds.GlobalVariable,
                name,
                $"{name} : {type}",
                RangeOf(line, lineIndex, name),
                []));
        }
    }

    private static string? MatchArgument(string rest, string argument)
    {
        var restUpper = rest.ToUpperInvariant();
        var marker = argument;
        var index = restUpper.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
            return null;

        // Skip the argument name and any whitespace before ':='.
        var cursor = index + marker.Length;
        while (cursor < rest.Length && char.IsWhiteSpace(rest[cursor]))
            cursor++;
        if (cursor >= rest.Length || rest[cursor] != ':')
            return null;
        cursor++;
        if (cursor >= rest.Length || rest[cursor] != '=')
            return null;
        cursor++;
        while (cursor < rest.Length && char.IsWhiteSpace(rest[cursor]))
            cursor++;

        var valueEnd = cursor;
        while (valueEnd < rest.Length &&
               rest[valueEnd] != ',' &&
               rest[valueEnd] != ')' &&
               rest[valueEnd] != ';')
        {
            valueEnd++;
        }

        return rest[cursor..valueEnd].Trim();
    }

    private static string ParseProcessor(string rest)
    {
        var restSpan = rest.Trim();
        if (!restSpan.StartsWith("ON ", StringComparison.OrdinalIgnoreCase))
            return "PLC";
        var remainder = restSpan["ON ".Length..];
        var end = 0;
        while (end < remainder.Length &&
               (char.IsLetterOrDigit(remainder[end]) || remainder[end] == '_'))
        {
            end++;
        }

        return end > 0 ? remainder[..end].ToString() : "PLC";
    }

    private static bool TryMatchKeyword(
        string trimmed,
        string keyword,
        out string name,
        out string rest)
    {
        name = string.Empty;
        rest = string.Empty;
        if (!trimmed.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
            return false;
        var after = trimmed[keyword.Length..];
        if (after.Length == 0 || !char.IsWhiteSpace(after[0]))
            return false;
        var nameStart = 1;
        var nameEnd = nameStart;
        while (nameEnd < after.Length &&
               (char.IsLetterOrDigit(after[nameEnd]) || after[nameEnd] == '_'))
        {
            nameEnd++;
        }

        if (nameEnd == nameStart)
            return false;
        name = after[nameStart..nameEnd].ToString();
        rest = after[nameEnd..].ToString();
        return true;
    }

    private static bool IsKeyword(string trimmed, string keyword) =>
        trimmed.StartsWith(keyword, StringComparison.OrdinalIgnoreCase) &&
        (trimmed.Length == keyword.Length || !char.IsLetterOrDigit(trimmed[keyword.Length]));

    private static StrucppRange RangeOf(string line, int lineIndex, string name)
    {
        var start = line.IndexOf(name, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            start = 0;
        return new StrucppRange(
            new StrucppPosition(lineIndex, start),
            new StrucppPosition(lineIndex, start + name.Length));
    }

    /// <summary>
    /// Replaces comments and single-quoted strings with spaces so that
    /// keywords inside them are never parsed, while preserving line and
    /// column positions for navigation. Handles nested (* *) comments.
    /// </summary>
    internal static string StripCommentsAndStrings(string source)
    {
        var characters = source.ToCharArray();
        var commentDepth = 0;
        var inString = false;
        for (var index = 0; index < characters.Length; index++)
        {
            var current = characters[index];
            if (inString)
            {
                if (current == '\'')
                    inString = false;
                else if (current != '\n' && current != '\r')
                    characters[index] = ' ';
                continue;
            }

            if (current == '\'')
            {
                inString = true;
                characters[index] = ' ';
                continue;
            }

            if (current == '(' && index + 1 < characters.Length && characters[index + 1] == '*')
            {
                commentDepth++;
                characters[index] = ' ';
                characters[index + 1] = ' ';
                index++;
                continue;
            }

            if (current == '*' && commentDepth > 0 &&
                index + 1 < characters.Length && characters[index + 1] == ')')
            {
                commentDepth--;
                characters[index] = ' ';
                characters[index + 1] = ' ';
                index++;
                continue;
            }

            if (commentDepth > 0 && current != '\n' && current != '\r')
                characters[index] = ' ';
        }

        return new string(characters);
    }

    private static List<string> SplitLines(string source)
    {
        var normalized = source.Replace("\r\n", "\n");
        var lines = new List<string>();
        var builder = new StringBuilder();
        foreach (var character in normalized)
        {
            if (character == '\n')
            {
                lines.Add(builder.ToString());
                builder.Clear();
            }
            else
            {
                builder.Append(character);
            }
        }

        if (builder.Length > 0)
            lines.Add(builder.ToString());
        return lines;
    }

    private sealed class ConfigurationBuilder(string name, StrucppRange range)
    {
        private readonly List<StConfigurationSymbol> _globalVariables = [];
        private readonly List<ResourceBuilder> _resources = [];

        public void AddGlobalVariable(StConfigurationSymbol symbol) =>
            _globalVariables.Add(symbol);

        public void AddResource(ResourceBuilder resource) =>
            _resources.Add(resource);

        public StConfigurationSymbol Build() => new(
            StConfigurationSymbolKinds.Configuration,
            name,
            name,
            range,
            [.. _globalVariables, .. _resources.Select(resource => resource.Build())]);
    }

    private sealed class ResourceBuilder(string name, string processor, StrucppRange range)
    {
        private readonly List<TaskBuilder> _tasks = [];
        private readonly List<StConfigurationSymbol> _programs = [];

        public void AddTask(StConfigurationSymbol task) => _tasks.Add(new TaskBuilder(task));

        public void AddProgram(StConfigurationSymbol program, string? withTask)
        {
            if (withTask is null)
            {
                _programs.Add(program);
                return;
            }

            var task = _tasks.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, withTask, StringComparison.OrdinalIgnoreCase));
            if (task is null)
            {
                _programs.Add(program);
                return;
            }

            task.AddProgram(program);
        }

        public StConfigurationSymbol Build() => new(
            StConfigurationSymbolKinds.Resource,
            name,
            $"{name} (RESOURCE on {processor})",
            range,
            [.. _tasks.Select(task => task.Build()), .. _programs]);
    }

    private sealed class TaskBuilder(StConfigurationSymbol task)
    {
        private readonly List<StConfigurationSymbol> _programs = [];

        public string Name => task.Name;

        public void AddProgram(StConfigurationSymbol program) => _programs.Add(program);

        public StConfigurationSymbol Build() => task with
        {
            Children = [.. task.Children, .. _programs]
        };
    }
}

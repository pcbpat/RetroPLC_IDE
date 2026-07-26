using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RetroPLC.LanguageServerHost;

namespace RetroPLC.Shell.Models;

internal static class StructuredTextPouMemberParser
{
    private static readonly Regex SupportedPouDeclaration = new(
        @"^[\t ]*(?:PROGRAM|FUNCTION_BLOCK|FUNCTION|INTERFACE)\b",
        RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static readonly Regex VariableBlockStart = new(
        @"^[\t ]*(?<kind>VAR_INPUT|VAR_OUTPUT|VAR_IN_OUT|VAR)\b[^\r\n]*",
        RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static readonly Regex VariableBlockEnd = new(
        @"^[\t ]*END_VAR\b[^\r\n]*",
        RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static readonly Regex MethodDeclaration = new(
        @"^[\t ]*(?<kind>METHOD|PROPERTY)[\t ]+" +
        @"(?:(?:PUBLIC|PRIVATE|PROTECTED|INTERNAL|FINAL|ABSTRACT|OVERRIDE)[\t ]+)*" +
        @"(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static readonly Regex MethodBody = new(
        @"^[\t ]*(?:METHOD|PROPERTY)\b.*?^[\t ]*END_(?:METHOD|PROPERTY)\b[^\r\n]*",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline);

    private static readonly Regex Identifier = new(
        @"[A-Za-z_][A-Za-z0-9_]*",
        RegexOptions.CultureInvariant);

    private static readonly Regex AtLocation = new(
        @"\bAT\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<StructuredTextMemberGroup> Parse(string source)
    {
        var maskedSource = MaskCommentsAndStrings(source);
        if (!SupportedPouDeclaration.IsMatch(maskedSource))
            return [];

        var fieldsSource = MaskMethodBodies(maskedSource);
        var membersByGroup = GroupNames.ToDictionary(
            group => group,
            _ => new List<StructuredTextMember>(),
            StringComparer.Ordinal);

        foreach (Match blockStart in VariableBlockStart.Matches(fieldsSource))
        {
            var blockEnd = VariableBlockEnd.Match(fieldsSource, blockStart.Index + blockStart.Length);
            if (!blockEnd.Success)
                continue;

            var groupName = GetGroupName(blockStart.Groups["kind"].Value);
            ParseDeclarations(
                source,
                fieldsSource,
                blockStart.Index + blockStart.Length,
                blockEnd.Index,
                membersByGroup[groupName]);
        }

        foreach (Match method in MethodDeclaration.Matches(maskedSource))
        {
            var name = method.Groups["name"];
            membersByGroup["FUNCTIONS"].Add(new StructuredTextMember(
                name.Value,
                method.Groups["kind"].Value.ToUpperInvariant(),
                ToRange(source, name.Index, name.Length)));
        }

        return GroupNames
            .Where(group => membersByGroup[group].Count > 0)
            .Select(group => new StructuredTextMemberGroup(group, membersByGroup[group]))
            .ToList();
    }

    private static readonly string[] GroupNames =
        ["VAR_IN", "VAR_OUT", "VAR_IN_OUT", "FUNCTIONS", "VAR"];

    private static string GetGroupName(string keyword) =>
        keyword.ToUpperInvariant() switch
        {
            "VAR_INPUT" => "VAR_IN",
            "VAR_OUTPUT" => "VAR_OUT",
            "VAR_IN_OUT" => "VAR_IN_OUT",
            _ => "VAR"
        };

    private static void ParseDeclarations(
        string source,
        string maskedSource,
        int bodyStart,
        int bodyEnd,
        ICollection<StructuredTextMember> members)
    {
        var statementStart = bodyStart;
        while (statementStart < bodyEnd)
        {
            var semicolon = maskedSource.IndexOf(';', statementStart, bodyEnd - statementStart);
            if (semicolon < 0)
                break;

            var colon = maskedSource.IndexOf(':', statementStart, semicolon - statementStart);
            if (colon >= 0)
            {
                var declarationEnd = colon;
                var declarationPrefix = maskedSource[statementStart..colon];
                var at = AtLocation.Match(declarationPrefix);
                if (at.Success)
                    declarationEnd = statementStart + at.Index;

                var typeText = source[(colon + 1)..semicolon];
                var initializer = typeText.IndexOf(":=", StringComparison.Ordinal);
                if (initializer >= 0)
                    typeText = typeText[..initializer];
                typeText = typeText.Trim();

                foreach (Match identifier in Identifier.Matches(
                             maskedSource[statementStart..declarationEnd]))
                {
                    var identifierIndex = statementStart + identifier.Index;
                    members.Add(new StructuredTextMember(
                        identifier.Value,
                        typeText,
                        ToRange(source, identifierIndex, identifier.Length)));
                }
            }

            statementStart = semicolon + 1;
        }
    }

    private static string MaskMethodBodies(string source)
    {
        var masked = source.ToCharArray();
        foreach (Match method in MethodBody.Matches(source))
        {
            for (var index = method.Index; index < method.Index + method.Length; index++)
            {
                if (masked[index] is not '\r' and not '\n')
                    masked[index] = ' ';
            }
        }

        return new string(masked);
    }

    private static string MaskCommentsAndStrings(string source)
    {
        var result = new StringBuilder(source);
        var inBlockComment = false;
        var inLineComment = false;
        var inString = false;

        for (var index = 0; index < source.Length; index++)
        {
            var current = source[index];
            var next = index + 1 < source.Length ? source[index + 1] : '\0';

            if (inLineComment)
            {
                if (current is '\r' or '\n')
                    inLineComment = false;
                else
                    result[index] = ' ';
                continue;
            }

            if (inBlockComment)
            {
                if (current == '*' && next == ')')
                {
                    result[index] = ' ';
                    result[++index] = ' ';
                    inBlockComment = false;
                }
                else if (current is not '\r' and not '\n')
                {
                    result[index] = ' ';
                }
                continue;
            }

            if (inString)
            {
                if (current == '\'' && next == '\'')
                {
                    result[index] = ' ';
                    result[++index] = ' ';
                }
                else
                {
                    if (current == '\'')
                        inString = false;
                    if (current is not '\r' and not '\n')
                        result[index] = ' ';
                }
                continue;
            }

            if (current == '/' && next == '/')
            {
                result[index] = ' ';
                result[++index] = ' ';
                inLineComment = true;
            }
            else if (current == '(' && next == '*')
            {
                result[index] = ' ';
                result[++index] = ' ';
                inBlockComment = true;
            }
            else if (current == '\'')
            {
                result[index] = ' ';
                inString = true;
            }
        }

        return result.ToString();
    }

    private static StrucppRange ToRange(string source, int index, int length)
    {
        var start = ToPosition(source, index);
        return new StrucppRange(start, new StrucppPosition(start.Line, start.Character + length));
    }

    private static StrucppPosition ToPosition(string source, int index)
    {
        var line = 0;
        var lineStart = 0;
        for (var current = 0; current < index; current++)
        {
            if (source[current] != '\n')
                continue;
            line++;
            lineStart = current + 1;
        }

        return new StrucppPosition(line, index - lineStart);
    }
}

internal sealed record StructuredTextMemberGroup(
    string Name,
    IReadOnlyList<StructuredTextMember> Members);

internal sealed record StructuredTextMember(
    string Name,
    string Detail,
    StrucppRange Range)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Detail)
        ? Name
        : $"{Name} : {Detail}";
}

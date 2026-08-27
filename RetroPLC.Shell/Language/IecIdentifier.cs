// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;

namespace RetroPLC.Shell.Language;

internal static class IecIdentifier
{
    private static readonly HashSet<string> Keywords = new(
    [
        "IF", "THEN", "ELSE", "ELSIF", "END_IF", "WHILE", "DO", "END_WHILE",
        "FOR", "TO", "BY", "END_FOR", "REPEAT", "UNTIL", "END_REPEAT",
        "CASE", "OF", "END_CASE", "VAR", "VAR_INPUT", "VAR_OUTPUT", "VAR_IN_OUT",
        "VAR_GLOBAL", "VAR_TEMP", "VAR_EXTERNAL", "CONSTANT", "RETAIN", "END_VAR",
        "PROGRAM", "END_PROGRAM", "FUNCTION", "END_FUNCTION", "FUNCTION_BLOCK",
        "END_FUNCTION_BLOCK", "METHOD", "END_METHOD", "TYPE", "END_TYPE",
        "STRUCT", "END_STRUCT", "ARRAY", "STRING", "WSTRING", "TRUE", "FALSE",
        "AND", "OR", "XOR", "NOT", "MOD", "RETURN", "EXIT", "CONTINUE",
        "BOOL", "BYTE", "WORD", "DWORD", "LWORD", "SINT", "INT", "DINT", "LINT",
        "USINT", "UINT", "UDINT", "ULINT", "REAL", "LREAL", "TIME", "DATE",
        "TIME_OF_DAY", "DATE_AND_TIME", "TOD", "DT"
    ], StringComparer.OrdinalIgnoreCase);

    public static bool IsValid(string? value) =>
        IsLexicallyValid(value) && !IsKeyword(value);

    public static bool IsLexicallyValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || !IsAsciiLetterOrUnderscore(value[0]))
            return false;

        for (var index = 1; index < value.Length; index++)
        {
            if (!IsAsciiLetterOrUnderscore(value[index]) && !char.IsAsciiDigit(value[index]))
                return false;
        }

        return true;
    }

    public static bool IsKeyword(string? value) =>
        value is not null && Keywords.Contains(value);

    private static bool IsAsciiLetterOrUnderscore(char value) =>
        char.IsAsciiLetter(value) || value == '_';
}

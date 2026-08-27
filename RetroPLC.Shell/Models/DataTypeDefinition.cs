// SPDX-License-Identifier: GPL-3.0-or-later
namespace RetroPLC.Shell.Models;

public enum DataTypeKind
{
    Structure,
    Enumeration,
    Alias,
    Subrange,
    Array
}

/// <summary>
/// Describes one IEC derived type declaration. Definition contains either the
/// STRUCT fields, the comma-separated enumeration members, or the complete
/// type expression for aliases, subranges, and arrays.
/// </summary>
public sealed record NewDataTypeDefinition(
    string Name,
    DataTypeKind Kind,
    string Definition);

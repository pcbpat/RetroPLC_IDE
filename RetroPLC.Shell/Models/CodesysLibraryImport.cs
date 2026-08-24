// SPDX-License-Identifier: GPL-3.0-or-later
namespace RetroPLC.Shell.Models;

public sealed record CodesysLibraryImport(
    string SourcePath,
    string LibraryName,
    string Version,
    string? Namespace,
    bool IncludeSource);

// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dock.Model.Mvvm.Controls;

namespace RetroPLC.Shell.ViewModels.Docking;

public sealed record LibrarySymbolInfo(string Name, string Signature, string Documentation);

public sealed class LibraryDetailsViewModel : Document
{
    private LibraryDetailsViewModel(string libraryPath)
    {
        LibraryPath = libraryPath;
        FileName = Path.GetFileName(libraryPath);
        Id = $"Library:{libraryPath}";
        CanClose = true;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(libraryPath));
            var root = document.RootElement;
            var manifest = root.GetProperty("manifest");

            DisplayName = GetString(manifest, "displayName")
                          ?? GetString(manifest, "name")
                          ?? Path.GetFileNameWithoutExtension(libraryPath);
            Title = DisplayName;
            Version = GetString(manifest, "version") ?? "Unknown";
            Namespace = GetString(manifest, "namespace") ?? "None";
            Description = GetString(manifest, "description") ?? "No description is available.";
            IsBuiltIn = GetBoolean(manifest, "isBuiltin") ? "Yes" : "No";
            FormatVersion = GetInt32(root, "formatVersion").ToString();
            ArchiveSize = FormatFileSize(new FileInfo(libraryPath).Length);

            Functions = ReadFunctions(manifest);
            FunctionBlocks = ReadFunctionBlocks(manifest);
            DataTypes = ReadDataTypes(manifest);
            Globals = ReadGlobals(manifest);
            Dependencies = ReadDependencies(root);
            SourceCount = GetCollectionLength(root, "sources");
        }
        catch (Exception exception) when (exception is IOException or JsonException or KeyNotFoundException)
        {
            DisplayName = Path.GetFileNameWithoutExtension(libraryPath);
            Title = DisplayName;
            Version = "Unknown";
            Namespace = "Unknown";
            Description = $"The library manifest could not be read: {exception.Message}";
            IsBuiltIn = "Unknown";
            FormatVersion = "Unknown";
            ArchiveSize = File.Exists(libraryPath)
                ? FormatFileSize(new FileInfo(libraryPath).Length)
                : "Missing";
            Functions = [];
            FunctionBlocks = [];
            DataTypes = [];
            Globals = [];
            Dependencies = [];
        }

        FunctionsHeader = $"Functions ({Functions.Count})";
        FunctionBlocksHeader = $"Function Blocks ({FunctionBlocks.Count})";
        DataTypesHeader = $"Data Types ({DataTypes.Count})";
        GlobalsHeader = $"Globals ({Globals.Count})";
        DependenciesHeader = $"Dependencies ({Dependencies.Count})";
    }

    public string LibraryPath { get; }
    public string FileName { get; }
    public string DisplayName { get; }
    public string Version { get; }
    public string Namespace { get; }
    public string Description { get; }
    public string IsBuiltIn { get; }
    public string FormatVersion { get; }
    public string ArchiveSize { get; }
    public int SourceCount { get; }
    public IReadOnlyList<LibrarySymbolInfo> Functions { get; }
    public IReadOnlyList<LibrarySymbolInfo> FunctionBlocks { get; }
    public IReadOnlyList<LibrarySymbolInfo> DataTypes { get; }
    public IReadOnlyList<LibrarySymbolInfo> Globals { get; }
    public IReadOnlyList<LibrarySymbolInfo> Dependencies { get; }
    public string FunctionsHeader { get; }
    public string FunctionBlocksHeader { get; }
    public string DataTypesHeader { get; }
    public string GlobalsHeader { get; }
    public string DependenciesHeader { get; }

    public static LibraryDetailsViewModel Load(string libraryPath) => new(libraryPath);

    private static IReadOnlyList<LibrarySymbolInfo> ReadFunctions(JsonElement manifest) =>
        GetArray(manifest, "functions")
            .Select(function =>
            {
                var name = GetString(function, "name") ?? "Unnamed function";
                var parameters = GetArray(function, "parameters")
                    .Select(parameter =>
                        $"{GetString(parameter, "name") ?? "?"} : {GetString(parameter, "type") ?? "ANY"}");
                var returnType = GetString(function, "returnType") ?? "VOID";
                var category = GetString(function, "category");
                var signature = $"{name}({string.Join(", ", parameters)}) : {returnType}";
                if (!string.IsNullOrWhiteSpace(category))
                    signature += $"  ·  {category}";
                return new LibrarySymbolInfo(
                    name,
                    signature,
                    GetString(function, "documentation") ?? string.Empty);
            })
            .OrderBy(symbol => symbol.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IReadOnlyList<LibrarySymbolInfo> ReadFunctionBlocks(JsonElement manifest) =>
        GetArray(manifest, "functionBlocks")
            .Select(functionBlock =>
            {
                var name = GetString(functionBlock, "name") ?? "Unnamed function block";
                var inputs = GetArray(functionBlock, "inputs").Count;
                var outputs = GetArray(functionBlock, "outputs").Count;
                var inouts = GetArray(functionBlock, "inouts").Count;
                return new LibrarySymbolInfo(
                    name,
                    $"FUNCTION_BLOCK {name}  ·  {inputs} inputs, {outputs} outputs, {inouts} in-outs",
                    GetString(functionBlock, "documentation") ?? string.Empty);
            })
            .OrderBy(symbol => symbol.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IReadOnlyList<LibrarySymbolInfo> ReadDataTypes(JsonElement manifest) =>
        GetArray(manifest, "types")
            .Select(type =>
            {
                var name = GetString(type, "name") ?? "Unnamed type";
                var kind = GetString(type, "kind") ?? "type";
                var fields = GetArray(type, "fields").Count;
                var fieldDetails = fields == 0 ? string.Empty : $"  ·  {fields} fields";
                return new LibrarySymbolInfo(
                    name,
                    $"{kind.ToUpperInvariant()} {name}{fieldDetails}",
                    GetString(type, "documentation") ?? string.Empty);
            })
            .OrderBy(symbol => symbol.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IReadOnlyList<LibrarySymbolInfo> ReadGlobals(JsonElement manifest) =>
        GetArray(manifest, "globals")
            .Select(global =>
            {
                var name = GetString(global, "name") ?? "Unnamed global";
                var type = GetString(global, "type") ?? "VAR_GLOBAL";
                return new LibrarySymbolInfo(
                    name,
                    $"{name} : {type}",
                    GetString(global, "documentation") ?? string.Empty);
            })
            .OrderBy(symbol => symbol.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IReadOnlyList<LibrarySymbolInfo> ReadDependencies(JsonElement root) =>
        GetArray(root, "dependencies")
            .Select(dependency =>
            {
                var name = GetString(dependency, "name") ?? "Unnamed dependency";
                var version = GetString(dependency, "version") ?? "Any version";
                return new LibrarySymbolInfo(name, $"{name} · {version}", string.Empty);
            })
            .OrderBy(symbol => symbol.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IReadOnlyList<JsonElement> GetArray(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Array
            ? property.EnumerateArray().ToList()
            : [];

    private static int GetCollectionLength(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return 0;
        return property.ValueKind switch
        {
            JsonValueKind.Array => property.GetArrayLength(),
            JsonValueKind.Object => property.EnumerateObject().Count(),
            _ => 0
        };
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool GetBoolean(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        property.GetBoolean();

    private static int GetInt32(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : 0;

    private static string FormatFileSize(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / (1024d * 1024d):0.##} MB"
        : $"{bytes / 1024d:0.##} KB";
}

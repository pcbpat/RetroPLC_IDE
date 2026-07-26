namespace RetroPLC.Shell.Models;

public enum PouKind
{
    Program,
    FunctionBlock,
    Function
}

public sealed record NewPouDefinition(
    string Name,
    PouKind Kind,
    string ReturnType = "BOOL",
    string? Extends = null,
    string? Implements = null,
    bool IsFinal = false,
    bool IsAbstract = false);

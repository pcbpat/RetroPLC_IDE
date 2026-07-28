# STruC++ integration

This project owns the IDE-facing STruC++ integration:

- `IStrucppLanguageService` is the typed boundary used by editor/view-model code.
- `StrucppLanguageService` maps that API to LSP over stdio using OmniSharp's
  C# Language Server Protocol client.
- `StrucppToolchain` resolves the packaged compiler, language server, and libraries.
- `Tools/compiler/strucpp-linux` is the original STruC++ CLI used by Build and
  library import.
- `Tools/lsp/strucpp-lsp-linux` is the standalone language-server executable.

Consumers should not construct JSON-RPC payloads or hard-code tool paths. They
subscribe to `DiagnosticsPublished` and `ServerError`, and call the typed
document, completion, and rename methods on `IStrucppLanguageService`.

The tool content is copied transitively into a consuming application's
`StrucppTools` output directory.

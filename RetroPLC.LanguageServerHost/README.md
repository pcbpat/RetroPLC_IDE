# STruC++ integration

This project owns the IDE-facing STruC++ integration:

- `IStrucppLanguageService` is the typed boundary used by editor/view-model code.
- `StrucppLanguageService` maps that API to LSP over stdio using OmniSharp's
  C# Language Server Protocol client.
- `StrucppToolchain` resolves the packaged compiler, language server, and libraries.
- `Tools/compiler/strucpp-*` is the generated STruC++ CLI used by Build and
  library import.
- `Tools/lsp/strucpp-lsp-*` is the generated standalone language-server
  executable.

Consumers should not construct JSON-RPC payloads or hard-code tool paths. They
subscribe to `DiagnosticsPublished` and `ServerError`, and call the typed
document, completion, formatting, and rename methods on
`IStrucppLanguageService`.

The tool content is copied transitively into a consuming application's
`StrucppTools` output directory.

The executables are not stored in Git. From the repository root, run
`./setup.sh` to initialize the pinned STruC++ submodule and build both tools for
the current platform. The build requires Node.js 22 or newer.

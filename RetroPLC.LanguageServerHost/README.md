# STruC++ integration

This project owns the IDE-facing STruC++ integration:

- `IStrucppLanguageService` is the typed boundary used by editor/view-model code.
- `StrucppLanguageService` maps that API to LSP over stdio using OmniSharp's
  C# Language Server Protocol client.
- `StrucppToolchain` resolves the setup-managed Node runtime, compiler,
  language server, and libraries from the solution-level `Tools` directory.
- `Tools/strucpp/dist/node/cli.js` is the generated STruC++ CLI used by Build
  and library import.
- `Tools/strucpp/vscode-extension/out/server/src/server.js` is the generated
  language server.

Consumers should not construct JSON-RPC payloads or hard-code tool paths. They
subscribe to `DiagnosticsPublished` and `ServerError`, and call the typed
document, completion, formatting, and rename methods on
`IStrucppLanguageService`.

The generated tools are not copied into application output. From the repository
root, run `./setup.sh` to install the private Node runtime and build the pinned
STruC++ tools under `Tools`. Set `RETROPLC_TOOLS_DIRECTORY` only when the tools
directory is intentionally located outside the solution.

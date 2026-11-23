# RetroPLC — strict west Python API backend

This is a *restricted* replacement for the uploaded zephyr-backend.zip. It calls
only APIs explicitly documented on the west API reference page:
https://docs.zephyrproject.org/latest/develop/west/west-apis.html

Supported gRPC requests (all messages are google.protobuf.Struct):

- `GetWorkspaceInfo`: `{ "workspaceDirectory": ".zephyr/workspace" }` -> workspace root, config file, manifest repository, Zephyr checkout.
- `ValidateWorkspace`: same response, API-driven, not a `.west/config` existence check.
- `GetProjects`: `{ "workspaceDirectory": ".zephyr/workspace", "names": ["zephyr"] }` -> project objects. Empty names lists all including manifest repository.
- `GetWestConfig`: `{ "workspaceDirectory": ".zephyr/workspace", "options": ["manifest.path", "manifest.file"] }` -> structured values.

For compatibility with the existing generated C# client, the following RPCs remain
present but **return gRPC UNIMPLEMENTED**: `ResolveConfiguration`, `ListBoards`,
`ValidateBoard`, `ValidateSdk`, `ResolveTerminal`.

They have no equivalent in the documented west public API. In particular,
`west boards` and `west sdk` are Zephyr-specific extension commands; invoking
internal command classes or decoding their terminal output is outside this scope.
BuildHost merges IDE and project settings locally. It forwards the configured
board and SDK to west and CMake for validation.

Start inside the repository:

```bash
.zinstaller/.venv/bin/python -m pip install -r tools/zephyr-backend/requirements.txt
cd tools/zephyr-backend
../../.zinstaller/.venv/bin/python -m retroplc_zephyr --root ../.. --endpoint-file /tmp/retroplc-west-endpoint.json --exit-on-stdin-close
```

`west.configuration.Configuration`, `west.util.west_topdir`, `west.util.west_dir`,
`west.manifest.Manifest.from_topdir`, `Manifest.get_projects`,
`ManifestProject.abspath`, and `Project.is_cloned` are all documented.

No regex, shell output parsing, `.west/config` parsing, SDK directory walking,
Zephyr internal board APIs, or splitting CMake option strings is used.

`Path` operations, JSON serialization, and gRPC address creation are transport
and integration mechanics, not undocumented west metadata parsing.

Run the tests from this directory with
`../../.zinstaller/.venv/bin/python -B -m unittest discover -s tests -v`.
The fixtures use real west APIs with temporary workspaces.

# RetroPLC IDE

RetroPLC IDE is a cross-platform Structured Text development environment built
with .NET and Avalonia. It integrates the STruC++ compiler and language server
for editing and building IEC 61131-3 projects.

https://github.com/user-attachments/assets/962fade2-2978-4fd7-b382-b99ae306dd80

The example project shown in the video can be found in the `TestProj` folder of the repository.

## Requirements

- .NET 10 SDK
- Git
- Linux x86_64 or macOS Apple Silicon for the current `setup.sh`
- On Linux/macOS: `curl`, `tar`, standard POSIX shell utilities, and either `sha256sum` or `shasum`

Node.js, npm, Python, west, CMake, Ninja, DTC and the Zephyr SDK do not need to
be installed system-wide. The setup script manages the required versions.

Windows remains a supported target for the Avalonia application, but the
Windows bootstrap script is not included yet.

## Project motivation

After working with PLCs for some time, I wanted to dive deeper into automation technology and better understand what happens under the hood. RetroPLC started as a project to understand and recreate the complete workflow of developing, deploying, monitoring, and debugging PLC applications.

The main goal was to create a cross-platform, open-source alternative to the Arduino PLC IDE for developing PLC applications for the Arduino Opta. The design and implementation are guided by the concepts described in PLCopen's [IEC 61131-3: a standard programming resource](https://www.plcopen.org/application/files/7117/3868/2055/intro_iec_oct2016.pdf). The project structure and programming workflow follow concepts defined by IEC 61131-3.

RetroPLC differs from currently available solutions in its architecture, runtime environment, debugging approach, and device-management workflow. To help determine which development environment best fits your use case, the following table compares RetroPLC with two other available solutions: Arduino PLC IDE and OpenPLC Editor.

| Feature                             | Arduino PLC IDE                | OpenPLC Editor v4               | RetroPLC IDE                         |
| ----------------------------------- | ------------------------------ | ------------------------------- | ------------------------------------ |
| **Supported platforms**             | Windows                        | Windows, Linux, macOS           | Windows, Linux, macOS                |
| **License**                         | Closed source / Proprietary    | Open source / GPL-3.0           | Open source / GPL-3.0                |
| **Debugging**                       | Live debug, watch, breakpoints | Monitoring & forcing via serial | Monitoring & forcing via `mcumgrctl` |
| **Ecosystem / technology stack**    | Closed-source                  | TypeScript / Electron, STruC++  | C# / Avalonia, STruC++               |
| **Look and feel**                   | Classic engineering tool       | Modern web-style UI             | Classic engineering tool             |
| **Supported IEC 61131-3 languages** | LD, FBD, ST, SFC, IL           | LD, FBD, ST, SFC, IL            | ST                                   |
| **Bootloader**                      | Arduino MCUboot                | Arduino MCUboot                 | Upstream MCUboot                     |
| **Build system**                    | `arduino-cli`                  | `arduino-cli`                   | `west` / CMake                       |
| **Device management and flashing**  | `arduino-cli`                  | `arduino-cli`                   | `mcumgrctl` / SMP protocol           |
| **Runtime**                         | Arduino / Mbed                 | Arduino / Mbed, STruC++ runtime | Zephyr-native, STruC++ runtime       |

> [!CAUTION]
> RetroPLC requires replacing the Arduino Opta's factory bootloader with upstream MCUboot.
>
> Flashing via Arduino IDE, Arduino PLC IDE, and OpenPLC Editor v4 is no longer possible unless the original bootloader is restored. Double-tapping the RESET button will also no longer enter the Arduino DFU mode.
>
> Firmware updates and device management are performed exclusively through the SMP protocol using mcumgr-compatible tools.
> You are strongly advised to back up the factory Arduino Opta bootloader before flashing the upstream MCUboot bootloader. Prebuilt images of Arduino's Opta bootloader are available [here](https://github.com/arduino/ArduinoCore-mbed/tree/main/bootloaders/OPTA).

## Getting started

Clone the repository:

```shell
git clone https://github.com/pcbpat/RetroPLC_IDE.git
cd RetroPLC_IDE
```

The setup script initializes the required Git submodules automatically, so a
manual `git submodule update` is not required.

On Linux/macOS:

```shell
./setup.sh
```

The setup script initializes the icon and STruC++ submodules and provisions the
IDE's Node.js, STruC++ compiler, libraries, language server and mcumgrctl under
`tools`. Set up AC6 and Zephyr separately using the steps below.

Restore and build the IDE:

```shell
dotnet restore RetroPLC.sln
dotnet build RetroPLC.sln
```

Run the desktop application:

```shell
dotnet run --project RetroPLC.Shell/RetroPLC.Shell.csproj
```

## Project structure

- `RetroPLC.Shell` — Avalonia desktop application and editor UI.
- `RetroPLC.LanguageServerHost` — STruC++ language-server integration.
- `RetroPLC.BuildHost` — compiler and firmware build orchestration.
- `RetroPLC.Icons` — generated Windows 98 SE-style icon catalog.
- `RetroPLC.Theme` — shared Avalonia theme and controls.
- `tools/strucpp` — pinned STruC++ source submodule used directly by the setup process.
- `tools` — setup-managed Node.js, STruC++ and mcumgrctl.
- `.zinstaller` — AC6-managed Zephyr host tools and Python virtual environment.

`tools/strucpp` is the canonical STruC++ source checkout. `setup.sh` builds
the compiler, libraries and language server directly in that submodule. The Structured Text editor uses the TextMate grammar at
`RetroPLC.Shell/Assets/Syntax/st.tmLanguage.json`.

## Zephyr host tools and firmware configuration

Install AC6 hosttools into `.zinstaller` at the solution root for development.
For a published application, install hosttools into `.zinstaller` next to the
application. A published installation needs AC6 files generated for its actual
location because the AC6 manifest and venv can contain absolute paths. Use
`RETROPLC_AC6_DIRECTORY` to select an existing installation elsewhere.

Run `setup.sh` to provision the Runtime workspace and its SDK and install
the Zephyr backend's Python dependencies into `.zinstaller/.venv`. The IDE
starts the local backend when it first needs west workspace or manifest
metadata and stops it at shutdown. Zephyr checks the configured board and SDK
compatibility during CMake configuration. The backend always uses Python from the
installation's `.zinstaller/.venv`. The IDE builds
the central application at `.zephyr/workspace/retroplc-runtime/app`, using
the workspace at `.zephyr/workspace`, the SDK directly in `.zephyr/sdk`, and
the `arduino_opta/stm32h747xx/m7` board. These paths are relative to the
application root. On Linux, the IDE uses AppImage extract-and-run mode when
AC6 Python is an AppImage and the FUSE device is unavailable. Set
`APPIMAGE_EXTRACT_AND_RUN=1` if the device is present but the mount still fails.

A `zephyr` object in
the project's `project.json` can contain:

```json
{
  "zephyr": {
    "workspaceDirectory": "../my-west-workspace",
    "useSysbuild": true,
    "pristineMode": "auto",
    "cMakeOptions": {
      "CMAKE_VERBOSE_MAKEFILE": "ON"
    }
  }
}
```

Set `boardTarget` to the Zephyr target, including any qualifiers. The IDE
passes it unchanged to west. Set `sdkDirectory` to override the provisioned SDK. Other
optional fields are `applicationDirectory`, `buildDirectory` and
`hostToolsDirectory`. The default build directory is below `Build/Zephyr` in
the project. Clean removes the project's `Build` tree.

Settings apply in this order, from highest to lowest precedence.

1. Explicit per-operation BuildHost overrides.
2. The project's `zephyr` settings.
3. IDE defaults, including `ZephyrConfiguration` defaults supplied by the
   caller and the environment variables below.

Project and operation paths are relative to the current project's directory.
IDE paths are relative to the application root. CMake options merge by key.
Rebuild always uses a pristine build.

| IDE variable | Purpose |
| --- | --- |
| `RETROPLC_AC6_DIRECTORY` | Existing AC6 hosttools installation |
| `RETROPLC_ZEPHYR_WORKSPACE` | West workspace directory |
| `RETROPLC_ZEPHYR_APPLICATION` | Zephyr application directory |
| `RETROPLC_ZEPHYR_SDK` | Explicit override for the provisioned SDK |
| `RETROPLC_ZEPHYR_BOARD` | Board target override |

The terminal and BuildHost use the west supplied by AC6. Select hosttools with
`RETROPLC_AC6_DIRECTORY`. Set `RETROPLC_TOOLS_DIRECTORY` to override the
application-managed `tools` directory. Relative overrides start at the
application root.

`GetBoardService` exposes discovery and validation for workspace board targets.
The IDE validates the provisioned environment before building. Rebuild uses
a pristine build, and automatic mode rebuilds when the cached SDK location or
version changes.

Run the BuildHost checks with `dotnet run --project RetroPLC.BuildHost.Tests`.
The Linux tests use isolated fixtures for hosttools, west and the ST and
firmware stages.

## Updating STruC++

STruC++ is versioned through the `tools/strucpp` Git submodule. Updating
RetroPLC to another STruC++ release therefore means updating the submodule
pointer to the desired upstream commit or tag and committing that pointer in
the RetroPLC repository.

For example:

```shell
cd tools/strucpp
git fetch --tags
git checkout v0.6.4
cd ../..
git add tools/strucpp
git commit -m "Update STruC++ to v0.6.4"
```

Run `./setup.sh` afterwards. The script detects the changed STruC++ commit and
rebuilds the compiler, libraries and language server from the local submodule.

## Rebuilding dependencies

Run `./setup.sh` whenever managed dependencies are missing or after changing a
pinned dependency such as the STruC++ submodule.

The `tools/strucpp` directory is the STruC++ submodule checkout. Keep it in place
when rerunning setup.

## License

RetroPLC-authored code is licensed under the
[GNU General Public License v3.0 or later](LICENSE.md)
(`SPDX-License-Identifier: GPL-3.0-or-later`). Third-party components and
assets remain under their respective licenses.

## Acknowledgements

- [SE98 icon theme](https://github.com/nestoris/Win98SE) — the Windows 98
  SE-style icon pack used by the IDE.
- [Avalonia](https://github.com/AvaloniaUI/Avalonia) — the cross-platform .NET
  UI framework.
- [Classic.Avalonia](https://github.com/BAndysc/Classic.Avalonia) — the classic
  Windows theme and controls for Avalonia.
- [AvaloniaEdit](https://github.com/AvaloniaUI/AvaloniaEdit) and
  [TextMateSharp](https://github.com/danipen/TextMateSharp) — the code editor
  and TextMate-based syntax highlighting integration.
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) — MVVM
  infrastructure and source generators.
- [Dock](https://github.com/wieslawsoltes/Dock) — the docking layout framework.
- [Iciclecreek.Avalonia.Terminal](https://github.com/tomlm/Iciclecreek.Avalonia.Terminal)
  — the embedded terminal control.
- [OmniSharp language protocol libraries](https://github.com/OmniSharp/csharp-language-server-protocol)
  — the Language Server Protocol client implementation.
- [Inter](https://github.com/rsms/inter) — the bundled application font.
- [STruC++](https://github.com/Autonomy-Logic/STruCpp) — the Structured Text
  compiler, language server, libraries and syntax resources.
- [Zephyr](https://github.com/zephyrproject-rtos/zephyr) — the embedded firmware
  platform and real-time operating system.
- [mcumgrctl](https://github.com/Finomnis/mcumgr-toolkit/) — the command-line
  tool used to manage the running PLC through Simple Management Protocol (SMP).

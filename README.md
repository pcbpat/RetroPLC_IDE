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

The setup script initializes and configures:

- `RetroPLC.Icons/Win98SE` and `external/STruCpp` as pinned Git submodules
- private Node.js and Python runtimes
- `west`, CMake and Ninja inside the RetroPLC Python environment
- the STruC++ compiler, bundled libraries and language server built directly from the local `external/STruCpp` submodule
- the RetroPLC Runtime checkout and a private Zephyr west workspace
- the required Zephyr modules for the Arduino Opta
- a private Zephyr SDK under `Tools/toolchain/zephyr-sdk`, including the `arm-zephyr-eabi` toolchain and DTC host tool
- `mcumgrctl` for firmware updates and device management

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
- `external/STruCpp` — pinned STruC++ source submodule used directly by the setup process.
- `Tools` — setup-managed runtimes, toolchains and Zephyr workspace.

`external/STruCpp` is the canonical STruC++ source checkout. `setup.sh` builds
the compiler, libraries and language server in that local submodule and creates
`Tools/strucpp` as a symlink to it so the existing RetroPLC host paths continue
to work without maintaining a second STruC++ clone.

The Structured Text editor uses the TextMate grammar stored in
`RetroPLC.Shell/Assets/Syntax/st.tmLanguage.json`. It is not a Tree-sitter
grammar.

On Linux and macOS, downloaded runtimes and toolchains are contained below the
solution-level `Tools` directory:

```text
Tools/
├── mcumgr/
├── strucpp -> ../external/STruCpp
└── toolchain/
    ├── node/
    ├── python/
    ├── venv/
    ├── zephyr-sdk/
    └── zephyr-workspace/
        ├── .west/
        ├── RetroPLC_Runtime/
        ├── zephyr/
        ├── bootloader/
        ├── modules/
        └── tools/
```

The Zephyr SDK is always installed privately into
`Tools/toolchain/zephyr-sdk`. An SDK installed elsewhere on the host is not
discovered or reused.

The private west workspace also lives below `Tools`. A `.west` directory in a
parent directory, for example from an older `~/RetroPLC` workspace, is not used
by the IDE.

## Updating STruC++

STruC++ is versioned through the `external/STruCpp` Git submodule. Updating
RetroPLC to another STruC++ release therefore means updating the submodule
pointer to the desired upstream commit or tag and committing that pointer in
the RetroPLC repository.

For example:

```shell
cd external/STruCpp
git fetch --tags
git checkout v0.6.4
cd ../..
git add external/STruCpp
git commit -m "Update STruC++ to v0.6.4"
```

Run `./setup.sh` afterwards. The script detects the changed STruC++ commit and
rebuilds the compiler, libraries and language server from the local submodule.

## Rebuilding dependencies

Run `./setup.sh` whenever managed dependencies are missing or after changing a
pinned dependency such as the STruC++ submodule.

Removing `Tools` forces a clean recreation of the managed toolchain and Zephyr
workspace:

```shell
rm -rf Tools
./setup.sh
```

This does not remove the Git submodules under `external/STruCpp` or
`RetroPLC.Icons/Win98SE`.

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

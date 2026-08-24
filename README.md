# RetroPLC IDE

RetroPLC IDE is a cross-platform Structured Text development environment built
with .NET and Avalonia. It integrates the STruC++ compiler and language server
for editing and building IEC 61131-3 projects.

## Requirements

- .NET 10 SDK
- Git
- Node.js 22 or newer and npm
- A supported 64-bit Linux, macOS, or Windows environment

Node.js is needed only to build the STruC++ command-line and language-server
tools. Their generated executables are intentionally not stored in Git.

## Project motivation

RetroPLC was developed with the goal of providing a cross-platform, open-source alternative to the Arduino PLC IDE for developing PLC applications for the Arduino Opta. The design and implementation of its features are guided by the concepts described in PLCopen's [IEC 61131-3: a standard programming resource](https://www.plcopen.org/application/files/7117/3868/2055/intro_iec_oct2016.pdf). The project structure and programming workflow were designed around concepts defined by IEC 61131-3.

The project was motivated by the lack of existing open-source PLC development environments for the Arduino Opta that combine IEC 61131-3 programming with a Zephyr-native runtime, upstream MCUboot, and standard Zephyr device-management mechanisms.

RetroPLC differs from currently available solutions in its architecture, runtime environment, debugging approach, and device-management workflow. To help determine which development environment best fits your use case, the following table compares RetroPLC with two other available solutions: Arduino PLC IDE and OpenPLC Editor.

| Feature                             | Arduino PLC IDE                | OpenPLC Editor v4                 | RetroPLC IDE                         |
| ----------------------------------- | ------------------------------ | -------------------------------   | ------------------------------------ |
| **Supported platforms**             | Windows                        | Windows, Linux, macOS             | Windows, Linux, macOS                |
| **License**                         | Closed source / Proprietary    | Open source / GPL-3.0             | Open source / GPL-3.0                |
| **Debugging**                       | Live debug, watch, breakpoints | Monitoring & forcing via serial   | Monitoring & forcing via `mcumgrctl` |
| **Ecosystem / technology stack**    | Closed-source                  | TypeScript / Electron, STruC++    | C# / Avalonia, STruC++               |
| **Look and feel**                   | Classic engineering tool       | Modern web-style UI               | Classic engineering tool             |
| **Supported IEC 61131-3 languages** | LD, FBD, ST, SFC, IL           | LD, FBD, ST, SFC, IL              | ST                                   |
| **Bootloader**                      | Arduino MCUboot                | Arduino MCUboot                   | Upstream MCUboot                     |
| **Build system**                    | `arduino-cli`                  | `arduino-cli`                     | `west` / CMake                       |
| **Device management and flashing**  | `arduino-cli`                  | `arduino-cli`                     | `mcumgrctl` / SMP protocol           |
| **Runtime**                         | Arduino / Mbed                 | Arduino / Mbed, STruC++ runtime   | Zephyr-native, STruC++ runtime       |


## Getting started

Clone the repository with its submodules, or initialize them after cloning:

```shell
git submodule update --init --recursive
```

Build the pinned STruC++ 0.6.3 tools:

```shell
./setup.sh
```

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
- `external/STruCpp` — STruC++ source submodule pinned to version 0.6.3.

The generated STruC++ executables are written to the
`RetroPLC.LanguageServerHost/Tools` directory and copied into application build and
publish output by `RetroPLC.LanguageServerHost`.

## Rebuilding dependencies

Run `./setup.sh` whenever the generated STruC++ tools are missing or need to be
initially built.

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
- [STruC++](https://github.com/Autonomy-Logic/STruCpp/tree/development) — the
  Structured Text CLI compiler, language server, and syntax resources.
- [Zephyr](https://github.com/zephyrproject-rtos/zephyr) - the embedded firmware
  platform and real time operating system
  [mcumgr](https://github.com/apache/mynewt-mcumgr-cli) — the embedded firmware
  device-management tooling.
- [mcumgrctl](https://github.com/Finomnis/mcumgr-toolkit/) - the command line tool to manage the running PLC
via Simple Management Protocol (SMP) 

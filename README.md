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

RetroPLC was programmed with the intention to have a cross-platform and open source alternative to Arduino PLC IDE for programming the Arduino Opta PLC. It widely differs to currently available solutions. To determine whether the project is suitable for you, see the following table comparing the available options to make a decision:

| Feature | Arduino PLC IDE | OpenPLC Editor v4 | RetroPLC IDE |
| --- | --- | --- | --- |
| **Supported platforms** | Windows 10/11 (64-bit) | Windows, Linux, macOS (x64/ARM64) | Windows, Linux, macOS (64-bit) |
| **Debugging** | Integrated Live Debug Mode, watch windows, breakpoints, step-by-step execution and oscilloscope | Live variable monitoring and forcing on bare-metal Arduino targets over the serial debug transport; the runtime uses Modbus RTU framing and the STruC++ debug table | Custom Zephyr `mcumgr` management group for live monitoring and forcing |
| **Ecosystem / technology stack** | Closed-source Arduino PLC tooling integrated with the Arduino / Mbed ecosystem | Open source; TypeScript / Electron frontend, STruC++ compiler pipeline and the Arduino core/toolchain | Open source; C# / .NET with Avalonia; STruC++ compiler and language-server tooling |
| **Look and feel** | Classic industrial engineering tool | Modern web-style desktop application (Electron) | Native desktop application with a classic / retro engineering-tool design |
| **Supported IEC 61131-3 languages** | LD, FBD, ST, SFC, IL | LD, FBD, ST, SFC, IL | ST (currently) |
| **Bootloader** | Arduino MCUboot on Opta | No OpenPLC-specific bootloader; uses the bootloader and upload mechanism provided by the selected Arduino board/core | Upstream Zephyr MCUboot |
| **Build system** | Arduino toolchain / `arduino-cli` | STruC++ code generation followed by `arduino-cli` compile/link for Arduino targets | Zephyr `west` / CMake |
| **Device management and flashing** | Arduino Opta DFU / PLC IDE manual-download workflow | Arduino core/package and board handling through `arduino-cli`; firmware upload through `arduino-cli upload` using the board-specific Arduino upload/DFU mechanism | Zephyr SMP via `mcumgrctl`; firmware upload and device management through `mcumgr` |
| **Runtime** | Arduino / Mbed-based PLC runtime on the target | OpenPLC bare-metal Arduino runtime, compiled as an Arduino sketch/library and executed directly on the target | Zephyr-native PLC runtime |

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
- [Zephyr](https://github.com/zephyrproject-rtos/zephyr) and
  [mcumgr](https://github.com/apache/mynewt-mcumgr-cli) — the embedded firmware
  platform and device-management tooling.

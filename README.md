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

The generated STruC++ executables are written beneath
`RetroPLC.LanguageServerHost/Tools` and copied into application build and
publish output by `RetroPLC.LanguageServerHost`.

## Rebuilding the toolchain

Run `./setup.sh` whenever the generated STruC++ tools are missing or need to be
rebuilt. The script verifies the pinned source revision before building, so it
will stop rather than silently use a different STruC++ commit.

## Acknowledgements

- [SE98 icon theme](https://github.com/nestoris/Win98SE) — the Windows 98
  SE-style icon pack used by the IDE.
- [Avalonia](https://github.com/AvaloniaUI/Avalonia) — the cross-platform .NET
  UI framework.
- [Classic.Avalonia](https://github.com/BAndysc/Classic.Avalonia) — the classic
  Windows theme and controls for Avalonia.
- [STruC++](https://github.com/Autonomy-Logic/STruCpp/tree/development) — the
  Structured Text CLI compiler, language server, and syntax resources.
- [Zephyr](https://github.com/zephyrproject-rtos/zephyr) and
  [mcumgr](https://github.com/apache/mynewt-mcumgr-cli) — the embedded firmware
  platform and device-management tooling.

Third-party source, assets, and their license terms are retained in the
corresponding submodules and tool directories.

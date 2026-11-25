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
`tools`. Firmware builds use a separately installed RetroPLC_BuildEnvironment.

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

`tools/strucpp` is the canonical STruC++ source checkout. `setup.sh` builds
the compiler, libraries and language server directly in that submodule. The Structured Text editor uses the TextMate grammar at
`RetroPLC.Shell/Assets/Syntax/st.tmLanguage.json`.

## Firmware build environment

Use an installed RetroPLC_BuildEnvironment workspace. Set
`RETROPLC_BUILD_ENVIRONMENT` to its root, or place the workspace in
`RetroPLC_BuildEnvironment` next to the IDE executable. Pixi is resolved from
`PATH` by default. Set `RETROPLC_PIXI_EXECUTABLE` to override it. Relative
environment paths are resolved against the IDE process's working directory.
The IDE uses the existing locked Pixi environment.

BuildHost runs `pixi run --quiet west <command> <absolute-project-path>` from
the BuildEnvironment root. The interactive terminal opens a Pixi shell there.

| IDE action | West command |
| --- | --- |
| Verify | `plc-transpile` |
| Build / Rebuild | `plc-build` |
| Clean | `plc-clean` |
| Download | `plc-build`, then `plc-upload` after a successful build and artifact validation |

Download uses the project's `device` object in `project.json`. For example:

```json
{
  "device": {
    "serialPort": "/dev/ttyACM0",
    "baudRate": 115200,
    "timeoutMilliseconds": 10000
  }
}
```

Set the serial port explicitly. UDP configuration uses `udpHostname` and
`udpPort` (default 1337) and requires firmware with SMP over UDP support.
`FirmwareBuildService.VerifyFirmwareAsync` invokes `plc-verify`. Pass
`confirm: true` only when explicitly requesting firmware confirmation.
`already_installed` means the matching image is active. `update_requested`
means the update has started and the running image still needs verification.

Build and Rebuild both use the pristine build from BuildEnvironment. Generated
sources and the validated debug map live in `Build/Generated`. The ELF, signed
BIN and HEX, and linker map live in `Build/Zephyr/app/zephyr`. Clean removes
`Build/Generated` and `Build/Zephyr`. Older `Build/Artifacts` directories may
still exist on disk.

The installed commands target `arduino_opta/stm32h747xx/m7` using sysbuild.
BuildHost reports unsupported project `zephyr` overrides and legacy
`RETROPLC_AC6_DIRECTORY` / `RETROPLC_ZEPHYR_*` overrides when compiling
firmware. BuildEnvironment needs to expose custom board, workspace, SDK,
application, build-directory and CMake settings to support those overrides.
It also needs to pass the project `Libraries` directory to its transpiler
before library-bearing projects can build. The IDE reports this limitation
when it finds a project `.stlib` file. Use a project path without spaces for
firmware compilation with the tested BuildEnvironment revision. Its generated
Zephyr compiler command passes an unquoted project path to `-imacros`.

Run the simulated checks with
`dotnet run --project RetroPLC.BuildHost.Tests`. With a configured
BuildEnvironment, append `-- --live /path/to/a/disposable/project-copy` to
exercise Verify, Build and Clean without accessing a device.

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

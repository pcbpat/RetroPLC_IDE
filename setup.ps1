# SPDX-License-Identifier: GPL-3.0-or-later

$ErrorActionPreference = "Stop"

$STRUCPP_COMMIT = "80481d1c4c14c58da3a08f2fa00e7990f20a35ce"
$MCUMGR_VERSION = "0.16.0"
$MCUMGR_REPOSITORY = "https://github.com/Finomnis/mcumgr-toolkit"

$SCRIPT_DIR = $PSScriptRoot
$STRUCPP_DIR = Join-Path $SCRIPT_DIR "external\STruCpp"
$TOOLS_DIR = Join-Path $SCRIPT_DIR "Tools"

function Resolve-RequiredCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if (-not $command) {
        throw "Required command not found: $Name"
    }

    return $command.Source
}

function Invoke-NativeCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Command,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $Command $($Arguments -join ' ')"
    }
}

if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [System.Runtime.InteropServices.OSPlatform]::Windows
)) {
    throw "setup.ps1 is intended for Windows. Use ./setup.sh on Linux or macOS."
}

$git = Resolve-RequiredCommand "git.exe"
$node = Resolve-RequiredCommand "node.exe"
$npm = Resolve-RequiredCommand "npm.cmd"

$architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
if ($architecture -ne "X64") {
    throw "No supported RetroPLC toolchain is currently configured for Windows $architecture. Expected X64."
}

$nodeMajorText = (& $node --print "process.versions.node.split('.')[0]").Trim()
if ($LASTEXITCODE -ne 0) {
    throw "Failed to determine the installed Node.js version."
}

[int]$nodeMajor = 0
if (-not [int]::TryParse($nodeMajorText, [ref]$nodeMajor)) {
    throw "Could not parse Node.js major version: $nodeMajorText"
}

if ($nodeMajor -lt 22) {
    $nodeVersion = (& $node --version).Trim()
    throw "STruC++ requires Node.js 22 or newer (found $nodeVersion)."
}

Write-Host "Initializing STruC++ $STRUCPP_COMMIT..."
Invoke-NativeCommand -Command $git -Arguments @(
    "-C", $SCRIPT_DIR,
    "submodule", "update", "--init", "--checkout", "--",
    "external/STruCpp"
)

$actualCommit = (& $git -C $STRUCPP_DIR rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) {
    throw "Failed to determine the checked-out STruC++ revision."
}

if ($actualCommit -ne $STRUCPP_COMMIT) {
    throw "Unexpected STruC++ revision: $actualCommit`nExpected the superproject to pin $STRUCPP_COMMIT."
}

$pkgTarget = "node22-win-x64"
$compilerName = "strucpp-win.exe"
$lspName = "strucpp-lsp-win.exe"
$mcumgrAsset = "mcumgrctl-windows.exe"
$mcumgrName = "mcumgrctl.exe"

$compilerDir = Join-Path $TOOLS_DIR "compiler"
$lspDir = Join-Path $TOOLS_DIR "lsp"
$mcumgrDir = Join-Path $TOOLS_DIR "mcumgr"

$compilerOutput = Join-Path $compilerDir $compilerName
$lspOutput = Join-Path $lspDir $lspName
$mcumgrOutput = Join-Path $mcumgrDir $mcumgrName

New-Item -ItemType Directory -Force -Path $compilerDir, $lspDir, $mcumgrDir | Out-Null

$mcumgrUrl = "$MCUMGR_REPOSITORY/releases/download/$MCUMGR_VERSION/$mcumgrAsset"
$mcumgrTmp = "$mcumgrOutput.tmp"

Write-Host "Downloading mcumgrctl $MCUMGR_VERSION..."
Remove-Item -Force -ErrorAction SilentlyContinue $mcumgrTmp

try {
    # GitHub requires TLS 1.2. Windows PowerShell 5.1 may not enable it by default.
    if ($PSVersionTable.PSVersion.Major -lt 7) {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    }

    Invoke-WebRequest `
        -Uri $mcumgrUrl `
        -OutFile $mcumgrTmp `
        -UseBasicParsing

    Move-Item -Force $mcumgrTmp $mcumgrOutput
}
finally {
    Remove-Item -Force -ErrorAction SilentlyContinue $mcumgrTmp
}

Write-Host "Installing STruC++ dependencies and building the CLI bundle..."
Push-Location $STRUCPP_DIR
try {
    Invoke-NativeCommand -Command $npm -Arguments @("ci")
    Invoke-NativeCommand -Command $npm -Arguments @("run", "build:bundle")
}
finally {
    Pop-Location
}

$pkg = Join-Path $STRUCPP_DIR "node_modules\.bin\pkg.cmd"
if (-not (Test-Path -LiteralPath $pkg)) {
    throw "STruC++ pkg executable was not found after npm install: $pkg"
}

Write-Host "Building $compilerName..."
Push-Location $STRUCPP_DIR
try {
    Invoke-NativeCommand -Command $pkg -Arguments @(
        "dist/strucpp-bundle.cjs",
        "--no-bytecode",
        "--public-packages", "*",
        "--public",
        "--target", $pkgTarget,
        "--output", $compilerOutput,
        "--compress", "GZip"
    )
}
finally {
    Pop-Location
}

Write-Host "Installing language-server dependencies and bundling the server..."
Push-Location (Join-Path $STRUCPP_DIR "vscode-extension")
try {
    Invoke-NativeCommand -Command $npm -Arguments @("ci")
    Invoke-NativeCommand -Command $npm -Arguments @("run", "vscode:prepublish")
}
finally {
    Pop-Location
}

Write-Host "Building $lspName..."
Push-Location $STRUCPP_DIR
try {
    Invoke-NativeCommand -Command $pkg -Arguments @(
        "vscode-extension/out/server.js",
        "--no-bytecode",
        "--public-packages", "*",
        "--public",
        "--target", $pkgTarget,
        "--output", $lspOutput,
        "--compress", "GZip"
    )
}
finally {
    Pop-Location
}

Write-Host "RetroPLC tools are ready:"
Write-Host "  Compiler:  $compilerOutput"
Write-Host "  LSP:       $lspOutput"
Write-Host "  mcumgrctl: $mcumgrOutput"

#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
#
# RetroPLC toolchain bootstrap for Linux and macOS.
#
# Source dependencies initialized by this script:
#   - RetroPLC.Icons/Win98SE Git submodule
#   - external/STruCpp Git submodule
#
# Managed under RetroPLC_IDE/Tools:
#   - private Node.js runtime
#   - private CPython runtime + venv
#   - west, CMake and Ninja
#   - built STruC++ compiler/LSP access via Tools/strucpp -> external/STruCpp
#   - RetroPLC Runtime + private Zephyr west workspace
#   - private Zephyr SDK with arm-zephyr-eabi toolchain and host tools/DTC
#   - mcumgrctl
#
# Remaining host dependencies:
#   - git
#   - curl
#   - tar
#   - basic POSIX shell utilities
#   - sha256sum or shasum
#
# Windows bootstrap is not implemented yet.

set -euo pipefail

# -----------------------------------------------------------------------------
# Pinned versions
# -----------------------------------------------------------------------------

readonly RETROPLC_RUNTIME_REVISION="${RETROPLC_RUNTIME_REVISION:-main}"
readonly RETROPLC_RUNTIME_REPOSITORY="${RETROPLC_RUNTIME_REPOSITORY:-https://github.com/pcbpat/RetroPLC_Runtime.git}"

readonly MCUMGR_VERSION="0.16.0"
readonly MCUMGR_REPOSITORY="Finomnis/mcumgr-toolkit"

# STruC++ currently requires Node.js >= 22.
readonly NODE_VERSION="22.23.2"

readonly PYTHON_VERSION="3.12.14"
readonly PYTHON_BUILD_RELEASE="20260825"
readonly PYTHON_BUILD_REPOSITORY="astral-sh/python-build-standalone"

readonly WEST_VERSION="1.5.0"
readonly CMAKE_VERSION="3.31.10"
readonly NINJA_VERSION="1.13.0"

# -----------------------------------------------------------------------------
# Paths
# -----------------------------------------------------------------------------

readonly SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
readonly TOOLS_DIR="${SCRIPT_DIR}/Tools"

readonly STRUCPP_SOURCE_DIR="${SCRIPT_DIR}/external/STruCpp"
readonly STRUCPP_TOOL_DIR="${TOOLS_DIR}/strucpp"
readonly STRUCPP_MARKER="${TOOLS_DIR}/.retroplc-strucpp-build"

readonly MCUMGR_DIR="${TOOLS_DIR}/mcumgr"

readonly TOOLCHAIN_DIR="${TOOLS_DIR}/toolchain"
readonly NODE_DIR="${TOOLCHAIN_DIR}/node"
readonly PYTHON_DIR="${TOOLCHAIN_DIR}/python"
readonly PYTHON_VENV_DIR="${TOOLCHAIN_DIR}/venv"
readonly ZEPHYR_WORKSPACE_DIR="${TOOLCHAIN_DIR}/zephyr-workspace"
readonly RETROPLC_RUNTIME_DIR="${ZEPHYR_WORKSPACE_DIR}/RetroPLC_Runtime"
readonly ZEPHYR_DIR="${ZEPHYR_WORKSPACE_DIR}/zephyr"
readonly ZEPHYR_SDK_DIR="${TOOLCHAIN_DIR}/zephyr-sdk"

readonly TMP_DIR="$(mktemp -d "${TMPDIR:-/tmp}/retroplc-setup.XXXXXX")"

cleanup() {
    rm -rf "${TMP_DIR}"
}
trap cleanup EXIT

readonly TOTAL_STEPS=11

if [[ -t 1 ]]; then
    readonly COLOR_CYAN=$'\033[1;36m'
    readonly COLOR_GREEN=$'\033[1;32m'
    readonly COLOR_RED=$'\033[1;31m'
    readonly COLOR_RESET=$'\033[0m'
else
    readonly COLOR_CYAN=''
    readonly COLOR_GREEN=''
    readonly COLOR_RED=''
    readonly COLOR_RESET=''
fi

heading() {
    local step="$1"
    shift
    printf '\n%s==== (%d/%d) [ %s ] ====%s\n' \
        "${COLOR_CYAN}" "${step}" "${TOTAL_STEPS}" "$*" "${COLOR_RESET}"
}

success() {
    printf '\n%s%s%s\n' "${COLOR_GREEN}" "$*" "${COLOR_RESET}"
}

die() {
    printf '%sERROR: %s%s\n' "${COLOR_RED}" "$*" "${COLOR_RESET}" >&2
    exit 1
}

info() {
    printf '  %s\n' "$*"
}

# -----------------------------------------------------------------------------
# Host dependencies
# -----------------------------------------------------------------------------

heading 1 "Checking host dependencies"

for command in git curl tar find awk sed head tr cp chmod mkdir mktemp rm ln cat uname; do
    if ! command -v "${command}" >/dev/null 2>&1; then
        die "Required host command not found: ${command}"
    fi
done

if command -v sha256sum >/dev/null 2>&1; then
    readonly SHA256_COMMAND="sha256sum"
elif command -v shasum >/dev/null 2>&1; then
    readonly SHA256_COMMAND="shasum"
else
    die "Neither sha256sum nor shasum is available."
fi

sha256_file() {
    local file="$1"

    if [[ "${SHA256_COMMAND}" == "sha256sum" ]]; then
        sha256sum "${file}" | awk '{print $1}'
    else
        shasum -a 256 "${file}" | awk '{print $1}'
    fi
}

verify_sha256() {
    local file="$1"
    local expected="$2"
    local actual

    actual="$(sha256_file "${file}")"

    if [[ "${actual}" != "${expected}" ]]; then
        die "SHA-256 mismatch for ${file}: expected ${expected}, got ${actual}"
    fi
}

download() {
    local url="$1"
    local output="$2"

    curl \
        --fail \
        --location \
        --retry 3 \
        --retry-delay 1 \
        --connect-timeout 30 \
        --output "${output}" \
        "${url}"
}

# -----------------------------------------------------------------------------
# Platform selection
# -----------------------------------------------------------------------------

platform="$(uname -s)"
architecture="$(uname -m)"

case "${platform}:${architecture}" in
    Linux:x86_64|Linux:amd64)
        node_platform="linux-x64"
        python_target="x86_64-unknown-linux-gnu"
        zephyr_sdk_platform="linux-x86_64"
        mcumgr_asset="mcumgrctl-linux"
        mcumgr_name="mcumgrctl"
        mcumgr_sha256="d2af9bd8843e108e7dbba43c03387c005e69f303d3cf9267aa5d2c796dcf7aeb"
        ;;
    Darwin:arm64|Darwin:aarch64)
        node_platform="darwin-arm64"
        python_target="aarch64-apple-darwin"
        zephyr_sdk_platform="macos-aarch64"
        mcumgr_asset="mcumgrctl-macos"
        mcumgr_name="mcumgrctl"
        mcumgr_sha256="a9cbfb44cfc0852db8c2713b1255116db5524d02b912fe0ab98aa02e2dccff0"
        ;;
    Linux:aarch64|Linux:arm64)
        die "Linux ARM64 is not enabled because mcumgr-toolkit ${MCUMGR_VERSION} has no matching prebuilt release asset."
        ;;
    Darwin:x86_64|Darwin:amd64)
        die "Intel macOS is not enabled because the validated Zephyr SDK and mcumgrctl assets are unavailable for this host."
        ;;
    MINGW*:*|MSYS*:*|CYGWIN*:*)
        die "Windows bootstrap is not implemented yet."
        ;;
    *)
        die "Unsupported host platform: ${platform} ${architecture}"
        ;;
esac

mkdir -p "${TOOLS_DIR}" "${TOOLCHAIN_DIR}" "${MCUMGR_DIR}"

# Ignore externally configured Zephyr locations. RetroPLC uses only its private
# workspace and private SDK below Tools.
unset ZEPHYR_BASE || true
unset ZEPHYR_SDK_INSTALL_DIR || true

# -----------------------------------------------------------------------------
# Source submodules
# -----------------------------------------------------------------------------

heading 2 "Initializing source submodules"

git -C "${SCRIPT_DIR}" rev-parse --show-toplevel >/dev/null 2>&1 || \
    die "RetroPLC_IDE must be a Git checkout."

git -C "${SCRIPT_DIR}" submodule sync --recursive
git -C "${SCRIPT_DIR}" submodule update --init --recursive \
    RetroPLC.Icons/Win98SE \
    external/STruCpp

[[ -f "${STRUCPP_SOURCE_DIR}/package.json" ]] || \
    die "The STruC++ submodule was not initialized."

# Preserve the existing RetroPLC tool lookup paths without maintaining a second
# STruC++ checkout. The compiler, LSP, libs and runtime are used directly from
# the pinned local submodule.
rm -rf "${STRUCPP_TOOL_DIR}"
ln -s "../external/STruCpp" "${STRUCPP_TOOL_DIR}"

# -----------------------------------------------------------------------------
# Private Node.js runtime
# -----------------------------------------------------------------------------

heading 3 "Node.js ${NODE_VERSION}"

readonly NODE_ARCHIVE="node-v${NODE_VERSION}-${node_platform}.tar.gz"
readonly NODE_URL="https://nodejs.org/dist/v${NODE_VERSION}/${NODE_ARCHIVE}"
readonly NODE_SHASUMS_URL="https://nodejs.org/dist/v${NODE_VERSION}/SHASUMS256.txt"
readonly NODE_BIN="${NODE_DIR}/bin/node"
readonly NPM_CLI="${NODE_DIR}/lib/node_modules/npm/bin/npm-cli.js"

node_is_current=false
if [[ -x "${NODE_BIN}" ]] && \
   [[ "$("${NODE_BIN}" --version 2>/dev/null || true)" == "v${NODE_VERSION}" ]]; then
    node_is_current=true
fi

if [[ "${node_is_current}" == false ]]; then
    info "Installing private Node.js ${NODE_VERSION}"

    node_archive_path="${TMP_DIR}/${NODE_ARCHIVE}"
    node_shasums_path="${TMP_DIR}/node-SHASUMS256.txt"

    download "${NODE_SHASUMS_URL}" "${node_shasums_path}"
    download "${NODE_URL}" "${node_archive_path}"

    node_expected_sha="$(
        awk -v archive="${NODE_ARCHIVE}" '$2 == archive { print $1 }' \
            "${node_shasums_path}"
    )"

    [[ -n "${node_expected_sha}" ]] || \
        die "Could not find ${NODE_ARCHIVE} in Node.js SHASUMS256.txt."

    verify_sha256 "${node_archive_path}" "${node_expected_sha}"

    rm -rf "${NODE_DIR}"
    mkdir -p "${NODE_DIR}"
    tar -xzf "${node_archive_path}" -C "${NODE_DIR}" --strip-components=1
fi

[[ -x "${NODE_BIN}" ]] || die "Private Node.js executable was not installed."
[[ "$("${NODE_BIN}" --version)" == "v${NODE_VERSION}" ]] || \
    die "Unexpected private Node.js version: $("${NODE_BIN}" --version)"
[[ -f "${NPM_CLI}" ]] || die "Bundled npm CLI was not installed with Node.js."

# -----------------------------------------------------------------------------
# GitHub release helpers
# -----------------------------------------------------------------------------

github_release_json() {
    local repository="$1"
    local tag="$2"
    local output="$3"

    curl \
        --fail \
        --location \
        --retry 3 \
        --retry-delay 1 \
        --connect-timeout 30 \
        --header "Accept: application/vnd.github+json" \
        --header "User-Agent: RetroPLC-setup" \
        --output "${output}" \
        "https://api.github.com/repos/${repository}/releases/tags/${tag}"
}

download_github_release_asset() {
    local repository="$1"
    local tag="$2"
    local asset_name="$3"
    local output="$4"

    local safe_repo="${repository//\//-}"
    local release_json="${TMP_DIR}/${safe_repo}-${tag}.json"

    github_release_json "${repository}" "${tag}" "${release_json}"

    local metadata
    metadata="$(
        "${NODE_BIN}" -e '
const fs = require("fs");
const release = JSON.parse(fs.readFileSync(process.argv[1], "utf8"));
const name = process.argv[2];
const asset = release.assets.find(candidate => candidate.name === name);
if (!asset) {
  console.error(`GitHub release asset not found: ${name}`);
  process.exit(1);
}
const digest = typeof asset.digest === "string" ? asset.digest : "";
process.stdout.write(`${asset.browser_download_url}|${digest}`);
' "${release_json}" "${asset_name}"
    )"

    local asset_url="${metadata%%|*}"
    local asset_digest="${metadata#*|}"

    download "${asset_url}" "${output}"

    if [[ "${asset_digest}" == sha256:* ]]; then
        verify_sha256 "${output}" "${asset_digest#sha256:}"
    fi
}

# -----------------------------------------------------------------------------
# Private CPython
# -----------------------------------------------------------------------------

heading 4 "Python ${PYTHON_VERSION}"

python_is_current=false
if [[ -x "${PYTHON_DIR}/bin/python3" ]] && \
   [[ -f "${PYTHON_DIR}/.retroplc-python-build" ]] && \
   [[ "$(cat "${PYTHON_DIR}/.retroplc-python-build")" == "${PYTHON_BUILD_RELEASE}:${PYTHON_VERSION}" ]]; then
    python_existing_version="$(
        "${PYTHON_DIR}/bin/python3" -c \
            'import platform; print(platform.python_version())' \
            2>/dev/null || true
    )"
    if [[ "${python_existing_version}" == "${PYTHON_VERSION}" ]]; then
        python_is_current=true
    fi
fi

if [[ "${python_is_current}" == false ]]; then
    info "Installing private Python ${PYTHON_VERSION}"

    python_asset="cpython-${PYTHON_VERSION}+${PYTHON_BUILD_RELEASE}-${python_target}-install_only_stripped.tar.gz"
    python_archive_path="${TMP_DIR}/${python_asset}"

    download_github_release_asset \
        "${PYTHON_BUILD_REPOSITORY}" \
        "${PYTHON_BUILD_RELEASE}" \
        "${python_asset}" \
        "${python_archive_path}"

    rm -rf "${PYTHON_DIR}"
    mkdir -p "${PYTHON_DIR}"
    tar -xzf "${python_archive_path}" -C "${PYTHON_DIR}" --strip-components=1

    printf '%s\n' "${PYTHON_BUILD_RELEASE}:${PYTHON_VERSION}" > \
        "${PYTHON_DIR}/.retroplc-python-build"
fi

readonly PYTHON_BIN="${PYTHON_DIR}/bin/python3"
[[ -x "${PYTHON_BIN}" ]] || die "Private Python executable was not installed."

python_full_version="$("${PYTHON_BIN}" -c 'import platform; print(platform.python_version())')"
[[ "${python_full_version}" == "${PYTHON_VERSION}" ]] || \
    die "Expected Python ${PYTHON_VERSION}, found ${python_full_version}."

# -----------------------------------------------------------------------------
# Private Python venv + west + CMake + Ninja
# -----------------------------------------------------------------------------

heading 5 "west ${WEST_VERSION} + CMake ${CMAKE_VERSION} + Ninja ${NINJA_VERSION}"

readonly VENV_MARKER_VALUE="${PYTHON_BUILD_RELEASE}:${PYTHON_VERSION}:${WEST_VERSION}:${CMAKE_VERSION}:${NINJA_VERSION}"
readonly VENV_MARKER="${PYTHON_VENV_DIR}/.retroplc-toolchain-version"

venv_recreate=false
if [[ ! -x "${PYTHON_VENV_DIR}/bin/python" ]] || \
   [[ ! -f "${VENV_MARKER}" ]] || \
   [[ "$(cat "${VENV_MARKER}" 2>/dev/null || true)" != "${VENV_MARKER_VALUE}" ]]; then
    venv_recreate=true
fi

if [[ "${venv_recreate}" == true ]]; then
    info "Creating private RetroPLC Python environment"

    rm -rf "${PYTHON_VENV_DIR}"
    "${PYTHON_BIN}" -m venv "${PYTHON_VENV_DIR}"

    "${PYTHON_VENV_DIR}/bin/python" -m pip install \
        --disable-pip-version-check \
        "west==${WEST_VERSION}" \
        "cmake==${CMAKE_VERSION}" \
        "ninja==${NINJA_VERSION}"

    printf '%s\n' "${VENV_MARKER_VALUE}" > "${VENV_MARKER}"
fi

readonly WEST="${PYTHON_VENV_DIR}/bin/west"
readonly CMAKE="${PYTHON_VENV_DIR}/bin/cmake"
readonly NINJA="${PYTHON_VENV_DIR}/bin/ninja"

[[ -x "${WEST}" ]] || die "west was not installed into the private venv."
[[ -x "${CMAKE}" ]] || die "CMake was not installed into the private venv."
[[ -x "${NINJA}" ]] || die "Ninja was not installed into the private venv."

export VIRTUAL_ENV="${PYTHON_VENV_DIR}"
export PATH="${NODE_DIR}/bin:${PYTHON_VENV_DIR}/bin:${PATH}"

# -----------------------------------------------------------------------------
# Build STruC++ from the pinned local submodule
# -----------------------------------------------------------------------------

heading 6 "Building STruC++ from external/STruCpp"

readonly STRUCPP_COMPILER="${STRUCPP_SOURCE_DIR}/dist/node/cli.js"
readonly STRUCPP_SERVER="${STRUCPP_SOURCE_DIR}/vscode-extension/out/server/src/server.js"

strucpp_commit="$(git -C "${STRUCPP_SOURCE_DIR}" rev-parse HEAD)"
strucpp_version="$(
    "${NODE_BIN}" -e \
        'const fs=require("fs"); const p=JSON.parse(fs.readFileSync(process.argv[1],"utf8")); process.stdout.write(p.version);' \
        "${STRUCPP_SOURCE_DIR}/package.json"
)"
strucpp_marker_value="${strucpp_commit}:${NODE_VERSION}"

run_npm() {
    "${NODE_BIN}" "${NPM_CLI}" "$@"
}

strucpp_is_current=false
if [[ -f "${STRUCPP_COMPILER}" ]] && \
   [[ -f "${STRUCPP_SERVER}" ]] && \
   [[ -f "${STRUCPP_MARKER}" ]] && \
   [[ "$(cat "${STRUCPP_MARKER}" 2>/dev/null || true)" == "${strucpp_marker_value}" ]]; then
    strucpp_is_current=true
fi

if [[ "${strucpp_is_current}" == false ]]; then
    info "Building STruC++ compiler, libraries and language server from the local submodule"

    (
        cd "${STRUCPP_SOURCE_DIR}"
        run_npm ci --ignore-scripts
        run_npm run build
    )

    (
        cd "${STRUCPP_SOURCE_DIR}/vscode-extension"
        run_npm ci --ignore-scripts
        run_npm run build
    )

    printf '%s\n' "${strucpp_marker_value}" > "${STRUCPP_MARKER}"
fi

[[ -f "${STRUCPP_COMPILER}" ]] || die "STruC++ compiler JavaScript was not built."
[[ -f "${STRUCPP_SERVER}" ]] || die "STruC++ language-server JavaScript was not built."
[[ -d "${STRUCPP_SOURCE_DIR}/libs" ]] || die "STruC++ libraries directory is missing."
[[ -d "${STRUCPP_SOURCE_DIR}/src/runtime" ]] || die "STruC++ runtime sources are missing."

strucpp_reported_version="$(
    "${NODE_BIN}" "${STRUCPP_COMPILER}" --version 2>/dev/null | tail -n 1 | tr -d '\r'
)"
[[ "${strucpp_reported_version}" == *"${strucpp_version}"* ]] || \
    die "Unexpected STruC++ compiler version output: ${strucpp_reported_version}"

# Existing RetroPLC hosts can continue resolving:
#   Tools/strucpp/dist/node/cli.js
#   Tools/strucpp/vscode-extension/out/server/src/server.js
#   Tools/strucpp/libs
# because Tools/strucpp is a symlink to external/STruCpp.

# -----------------------------------------------------------------------------
# mcumgrctl
# -----------------------------------------------------------------------------

heading 7 "mcumgrctl ${MCUMGR_VERSION}"

readonly MCUMGR_OUTPUT="${MCUMGR_DIR}/${mcumgr_name}"
readonly MCUMGR_MARKER="${MCUMGR_DIR}/.retroplc-mcumgr-version"

mcumgr_is_current=false
if [[ -x "${MCUMGR_OUTPUT}" ]] && \
   [[ -f "${MCUMGR_MARKER}" ]] && \
   [[ "$(cat "${MCUMGR_MARKER}")" == "${MCUMGR_VERSION}" ]]; then
    mcumgr_is_current=true
fi

if [[ "${mcumgr_is_current}" == false ]]; then
    info "Installing mcumgrctl ${MCUMGR_VERSION}"

    mcumgr_tmp="${TMP_DIR}/${mcumgr_name}"

    download_github_release_asset \
        "${MCUMGR_REPOSITORY}" \
        "${MCUMGR_VERSION}" \
        "${mcumgr_asset}" \
        "${mcumgr_tmp}"

    verify_sha256 "${mcumgr_tmp}" "${mcumgr_sha256}"

    cp "${mcumgr_tmp}" "${MCUMGR_OUTPUT}"
    chmod +x "${MCUMGR_OUTPUT}"
    printf '%s\n' "${MCUMGR_VERSION}" > "${MCUMGR_MARKER}"
fi

# -----------------------------------------------------------------------------
# Managed RetroPLC Runtime + private Zephyr west workspace
# -----------------------------------------------------------------------------

heading 8 "RetroPLC Runtime ${RETROPLC_RUNTIME_REVISION}"

mkdir -p "${ZEPHYR_WORKSPACE_DIR}"

if [[ ! -d "${RETROPLC_RUNTIME_DIR}/.git" ]]; then
    rm -rf "${RETROPLC_RUNTIME_DIR}"
    git clone --filter=blob:none --no-checkout \
        "${RETROPLC_RUNTIME_REPOSITORY}" \
        "${RETROPLC_RUNTIME_DIR}"
else
    git -C "${RETROPLC_RUNTIME_DIR}" remote set-url \
        origin "${RETROPLC_RUNTIME_REPOSITORY}"
fi

git -C "${RETROPLC_RUNTIME_DIR}" fetch \
    --depth 1 origin "${RETROPLC_RUNTIME_REVISION}"
git -C "${RETROPLC_RUNTIME_DIR}" checkout --detach --force FETCH_HEAD

# Explicitly create the private nested west workspace. This prevents an older
# parent workspace (for example ~/RetroPLC/.west) from becoming RetroPLC's
# active west topdir.
mkdir -p "${ZEPHYR_WORKSPACE_DIR}/.west"
cat > "${ZEPHYR_WORKSPACE_DIR}/.west/config" <<'WEST_CONFIG'
[manifest]
path = RetroPLC_Runtime
file = west.yml
WEST_CONFIG

# -----------------------------------------------------------------------------
# Zephyr + required modules + Python packages
# -----------------------------------------------------------------------------

heading 9 "Zephyr workspace + required modules"

(
    cd "${ZEPHYR_WORKSPACE_DIR}"
    "${WEST}" update
)

[[ -d "${ZEPHYR_DIR}/.git" ]] || \
    die "Zephyr was not installed into ${ZEPHYR_WORKSPACE_DIR}."

(
    cd "${ZEPHYR_WORKSPACE_DIR}"
    "${WEST}" packages pip --install
)

# -----------------------------------------------------------------------------
# Private Zephyr SDK
# -----------------------------------------------------------------------------

heading 10 "Private Zephyr SDK"

sdk_expected_version="$(head -n 1 "${ZEPHYR_DIR}/SDK_VERSION" | tr -d '[:space:]')"
[[ -n "${sdk_expected_version}" ]] || \
    die "Could not determine Zephyr SDK version from ${ZEPHYR_DIR}/SDK_VERSION."

readonly ZEPHYR_SDK_ARCHIVE="zephyr-sdk-${sdk_expected_version}_${zephyr_sdk_platform}_minimal.tar.xz"
readonly ZEPHYR_SDK_GCC="${ZEPHYR_SDK_DIR}/gnu/arm-zephyr-eabi/bin/arm-zephyr-eabi-gcc"

sdk_is_current=false
if [[ -f "${ZEPHYR_SDK_DIR}/sdk_version" ]] && \
   [[ "$(head -n 1 "${ZEPHYR_SDK_DIR}/sdk_version" | tr -d '[:space:]')" == "${sdk_expected_version}" ]] && \
   [[ -x "${ZEPHYR_SDK_GCC}" ]]; then
    sdk_is_current=true
fi

if [[ "${sdk_is_current}" == false ]]; then
    info "Installing private Zephyr SDK ${sdk_expected_version} into ${ZEPHYR_SDK_DIR}"

    zephyr_sdk_archive_path="${TMP_DIR}/${ZEPHYR_SDK_ARCHIVE}"

    download_github_release_asset \
        "zephyrproject-rtos/sdk-ng" \
        "v${sdk_expected_version}" \
        "${ZEPHYR_SDK_ARCHIVE}" \
        "${zephyr_sdk_archive_path}"

    rm -rf "${ZEPHYR_SDK_DIR}"
    mkdir -p "${ZEPHYR_SDK_DIR}"

    tar -xJf "${zephyr_sdk_archive_path}" \
        -C "${ZEPHYR_SDK_DIR}" \
        --strip-components=1

    [[ -x "${ZEPHYR_SDK_DIR}/setup.sh" ]] || \
        die "The Zephyr SDK setup executable was not installed."

    # Install only the ARM GNU toolchain required for the Opta plus host tools.
    # Host tools include DTC. No SDK outside Tools is discovered or reused.
    "${ZEPHYR_SDK_DIR}/setup.sh" -t arm-zephyr-eabi -h
fi

[[ -x "${ZEPHYR_SDK_GCC}" ]] || \
    die "The private Zephyr ARM toolchain was not installed."

dtc_bin="$(
    find "${ZEPHYR_SDK_DIR}/hosttools" \
        -type f \
        -path '*/usr/bin/dtc' \
        -print \
        -quit
)"
[[ -x "${dtc_bin}" ]] || \
    die "The private Zephyr SDK DTC executable was not installed."

export ZEPHYR_SDK_INSTALL_DIR="${ZEPHYR_SDK_DIR}"

dtc_version="$("${dtc_bin}" --version 2>&1)"

# -----------------------------------------------------------------------------
# Final verification
# -----------------------------------------------------------------------------

heading 11 "Verifying RetroPLC setup"

echo "  Node:           $("${NODE_BIN}" --version)"
echo "  Python:         ${python_full_version}"
echo "  west:           $("${WEST}" --version)"
echo "  CMake:          $("${CMAKE}" --version | head -n 1)"
echo "  Ninja:          $("${NINJA}" --version)"
echo "  DTC:            ${dtc_version}"
echo "  STruC++:        ${strucpp_version} (${strucpp_commit:0:12})"
echo "  STruC++ source: ${STRUCPP_SOURCE_DIR}"
echo "  Runtime:        $(git -C "${RETROPLC_RUNTIME_DIR}" rev-parse --short=12 HEAD)"
echo "  Zephyr:         $(git -C "${ZEPHYR_DIR}" rev-parse --short=12 HEAD)"
echo "  Zephyr SDK:     ${sdk_expected_version} (${ZEPHYR_SDK_DIR})"
echo "  mcumgrctl:      ${MCUMGR_OUTPUT}"
echo
echo "System dependencies still required: git, curl, tar and basic POSIX utilities."
echo "Node, Python, west, CMake, Ninja, the Zephyr workspace, Zephyr SDK, DTC and mcumgrctl are RetroPLC-managed under Tools."
echo "STruC++ and Win98SE are pinned Git submodules inside the RetroPLC_IDE checkout."
success "RetroPLC setup completed successfully."

#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
#
# RetroPLC toolchain bootstrap for Linux and macOS.
#
# Managed by this script:
#   - private Node.js runtime (for the STruC++ server)
#   - STruC++ VSIX contents, including out/server.js
#   - private CPython 3.12 runtime
#   - private Python virtual environment
#   - west, CMake and Ninja
#   - pinned Zephyr workspace + west modules
#   - Zephyr Python packages
#   - mcumgrctl
#
# Zephyr SDK:
#   - installed/reused through `west sdk install`
#   - may live in the user's normal Zephyr SDK location
#
# Remaining host dependencies:
#   - git
#   - curl
#   - tar
#   - dtc (Device Tree Compiler)
#   - sha256sum (Linux) or shasum (macOS)
#   - basic POSIX shell utilities
#
# Windows should use setup.ps1 instead.

set -euo pipefail

# -----------------------------------------------------------------------------
# Pinned versions
# -----------------------------------------------------------------------------

readonly STRUCPP_VERSION="0.6.3"

readonly MCUMGR_VERSION="0.16.0"

# STruC++ requires Node.js >= 22. RetroPLC uses one exact private runtime.
readonly NODE_VERSION="22.23.2"

# Exact python-build-standalone artifact validated for this RetroPLC toolchain.
readonly PYTHON_VERSION="3.12.14"
readonly PYTHON_BUILD_RELEASE="20260825"

readonly WEST_VERSION="1.5.0"
readonly CMAKE_VERSION="3.31.10"
readonly NINJA_VERSION="1.13.0"

# IMPORTANT: pin this to the Zephyr revision validated by RetroPLC.
# v4.4.0 is a reproducible default. Replace it with the exact tested commit
# before a RetroPLC release if the runtime depends on post-v4.4.0 changes.
readonly ZEPHYR_REVISION="${ZEPHYR_REVISION:-v4.4.0}"
readonly ZEPHYR_REPOSITORY="https://github.com/zephyrproject-rtos/zephyr.git"

# -----------------------------------------------------------------------------
# Paths
# -----------------------------------------------------------------------------

readonly SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
readonly TOOLS_DIR="${SCRIPT_DIR}/Tools"

readonly STRUCPP_DIR="${TOOLS_DIR}/strucpp"
readonly MCUMGR_DIR="${TOOLS_DIR}/mcumgr"

readonly TOOLCHAIN_DIR="${TOOLS_DIR}/toolchain"
readonly NODE_DIR="${TOOLCHAIN_DIR}/node"
readonly PYTHON_DIR="${TOOLCHAIN_DIR}/python"
readonly PYTHON_VENV_DIR="${TOOLCHAIN_DIR}/venv"
readonly ZEPHYR_WORKSPACE_DIR="${TOOLCHAIN_DIR}/zephyr-workspace"
readonly ZEPHYR_DIR="${ZEPHYR_WORKSPACE_DIR}/zephyr"

# Keep setup-time state inside the cloned RetroPLC repository as well.
# Setup-time caches stay inside the cloned repository.
# The Zephyr SDK is the deliberate exception: `west sdk install` may reuse or
# install/register an SDK in the user's normal Zephyr SDK/CMake locations.
readonly SETUP_STATE_DIR="${TOOLS_DIR}/.setup-state"
readonly SETUP_HOME_DIR="${SETUP_STATE_DIR}/home"
readonly SETUP_CACHE_DIR="${SETUP_STATE_DIR}/cache"
readonly SETUP_TMP_ROOT="${SETUP_STATE_DIR}/tmp"

mkdir -p "${SETUP_HOME_DIR}" "${SETUP_CACHE_DIR}" "${SETUP_TMP_ROOT}"
readonly TMP_DIR="$(mktemp -d "${SETUP_TMP_ROOT}/run.XXXXXX")"

cleanup() {
    rm -rf "${SETUP_STATE_DIR}"
}
trap cleanup EXIT

readonly TOTAL_STEPS=10

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

# -----------------------------------------------------------------------------
# Bootstrap dependencies
# -----------------------------------------------------------------------------

heading 1 "Checking host dependencies"

for command in git curl tar dtc awk sed head tr cp chmod mkdir mktemp rm; do
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

        python_asset="cpython-${PYTHON_VERSION}+${PYTHON_BUILD_RELEASE}-x86_64-unknown-linux-gnu-install_only_stripped.tar.gz"
        python_url="https://github.com/astral-sh/python-build-standalone/releases/download/${PYTHON_BUILD_RELEASE}/cpython-${PYTHON_VERSION}%2B${PYTHON_BUILD_RELEASE}-x86_64-unknown-linux-gnu-install_only_stripped.tar.gz"
        python_sha256="7ce4a71285d913955a76053cc7605ea96da8ecada54dba9cf395245961816421"

        mcumgr_asset="mcumgrctl-linux"
        mcumgr_url="https://github.com/Finomnis/mcumgr-toolkit/releases/download/${MCUMGR_VERSION}/${mcumgr_asset}"
        mcumgr_sha256="d2af9bd8843e108e7dbba43c03387c005e69f303d3cf9267aa5d2c796dcf7aeb"
        mcumgr_name="mcumgrctl"
        ;;
    Darwin:arm64|Darwin:aarch64)
        node_platform="darwin-arm64"

        python_asset="cpython-${PYTHON_VERSION}+${PYTHON_BUILD_RELEASE}-aarch64-apple-darwin-install_only_stripped.tar.gz"
        python_url="https://github.com/astral-sh/python-build-standalone/releases/download/${PYTHON_BUILD_RELEASE}/cpython-${PYTHON_VERSION}%2B${PYTHON_BUILD_RELEASE}-aarch64-apple-darwin-install_only_stripped.tar.gz"
        python_sha256="8b0f1fa71eab7ca644e482c631807a1116fa848491051cd1c8d9429491de63a6"

        mcumgr_asset="mcumgrctl-macos"
        mcumgr_url="https://github.com/Finomnis/mcumgr-toolkit/releases/download/${MCUMGR_VERSION}/${mcumgr_asset}"
        mcumgr_sha256="a9cbfb44cfc0852db8c2713b1255116db5524d02b912fe0ab98aa02e2dccff0"
        mcumgr_name="mcumgrctl"
        ;;
    Linux:aarch64|Linux:arm64)
        die "Linux ARM64 is not enabled because mcumgr-toolkit ${MCUMGR_VERSION} has no matching prebuilt asset in the current RetroPLC setup."
        ;;
    Darwin:x86_64|Darwin:amd64)
        die "Intel macOS is not enabled for the managed RetroPLC toolchain. Use Apple Silicon macOS."
        ;;
    MINGW*:*|MSYS*:*|CYGWIN*:*)
        die "Use setup.ps1 on Windows."
        ;;
    *)
        die "Unsupported host platform: ${platform} ${architecture}"
        ;;
esac

mkdir -p "${TOOLS_DIR}" "${TOOLCHAIN_DIR}" "${MCUMGR_DIR}"

# -----------------------------------------------------------------------------
# Private Node.js runtime
# -----------------------------------------------------------------------------

heading 2 "Node.js ${NODE_VERSION}"

readonly NODE_ARCHIVE="node-v${NODE_VERSION}-${node_platform}.tar.gz"
readonly NODE_URL="https://nodejs.org/dist/v${NODE_VERSION}/${NODE_ARCHIVE}"
readonly NODE_SHASUMS_URL="https://nodejs.org/dist/v${NODE_VERSION}/SHASUMS256.txt"
readonly NODE_BIN="${NODE_DIR}/bin/node"

node_is_current=false
if [[ -x "${NODE_BIN}" ]] && \
   [[ "$("${NODE_BIN}" --version 2>/dev/null || true)" == "v${NODE_VERSION}" ]]; then
    node_is_current=true
fi

if [[ "${node_is_current}" == false ]]; then
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

# -----------------------------------------------------------------------------
# Private CPython 3.12
# -----------------------------------------------------------------------------

heading 3 "Python ${PYTHON_VERSION}"

readonly PYTHON_MARKER="${PYTHON_DIR}/.retroplc-python-version"
readonly PYTHON_BIN="${PYTHON_DIR}/bin/python3"

python_is_current=false
if [[ -x "${PYTHON_BIN}" ]] && \
   [[ -f "${PYTHON_MARKER}" ]] && \
   [[ "$(cat "${PYTHON_MARKER}")" == "${PYTHON_VERSION}+${PYTHON_BUILD_RELEASE}" ]] && \
   [[ "$("${PYTHON_BIN}" --version 2>&1 || true)" == "Python ${PYTHON_VERSION}" ]]; then
    python_is_current=true
fi

if [[ "${python_is_current}" == false ]]; then
    python_archive_path="${TMP_DIR}/${python_asset}"

    download "${python_url}" "${python_archive_path}"
    verify_sha256 "${python_archive_path}" "${python_sha256}"

    rm -rf "${PYTHON_DIR}"
    mkdir -p "${PYTHON_DIR}"
    tar -xzf "${python_archive_path}" -C "${PYTHON_DIR}" --strip-components=1

    printf '%s\n' "${PYTHON_VERSION}+${PYTHON_BUILD_RELEASE}" > "${PYTHON_MARKER}"
fi

[[ -x "${PYTHON_BIN}" ]] || die "Private Python executable was not installed."

python_full_version="$("${PYTHON_BIN}" --version 2>&1)"
[[ "${python_full_version}" == "Python ${PYTHON_VERSION}" ]] || \
    die "Expected Python ${PYTHON_VERSION}, found ${python_full_version}."

# -----------------------------------------------------------------------------
# Validate DTC
# -----------------------------------------------------------------------------

version_at_least() {
    local actual="$1"
    local required="$2"

    local a1=0 a2=0 a3=0
    local r1=0 r2=0 r3=0

    IFS=. read -r a1 a2 a3 <<< "${actual}"
    IFS=. read -r r1 r2 r3 <<< "${required}"

    a1="${a1:-0}"
    a2="${a2:-0}"
    a3="${a3:-0}"
    r1="${r1:-0}"
    r2="${r2:-0}"
    r3="${r3:-0}"

    (( 10#${a1} > 10#${r1} )) && return 0
    (( 10#${a1} < 10#${r1} )) && return 1

    (( 10#${a2} > 10#${r2} )) && return 0
    (( 10#${a2} < 10#${r2} )) && return 1

    (( 10#${a3} >= 10#${r3} ))
}

dtc_version_raw="$(dtc --version 2>&1 || true)"
dtc_version="$(
    printf '%s\n' "${dtc_version_raw}" | \
        sed -E 's/.*DTC[[:space:]]+([0-9]+(\.[0-9]+)+).*/\1/'
)"

if [[ -z "${dtc_version}" || "${dtc_version}" == "${dtc_version_raw}" ]]; then
    die "Could not determine DTC version from: ${dtc_version_raw}"
fi

version_at_least "${dtc_version}" "1.4.6" || \
    die "DTC 1.4.6 or newer is required (found ${dtc_version})."

# -----------------------------------------------------------------------------
# Private Python venv + west + CMake + Ninja
# -----------------------------------------------------------------------------

heading 4 "west ${WEST_VERSION} + CMake ${CMAKE_VERSION} + Ninja ${NINJA_VERSION}"

readonly VENV_MARKER_VALUE="${PYTHON_VERSION}+${PYTHON_BUILD_RELEASE}:${WEST_VERSION}:${CMAKE_VERSION}:${NINJA_VERSION}"
readonly VENV_MARKER="${PYTHON_VENV_DIR}/.retroplc-toolchain-version"

venv_recreate=false
if [[ ! -x "${PYTHON_VENV_DIR}/bin/python" ]] || \
   [[ ! -f "${VENV_MARKER}" ]] || \
   [[ "$(cat "${VENV_MARKER}" 2>/dev/null || true)" != "${VENV_MARKER_VALUE}" ]]; then
    venv_recreate=true
fi

if [[ "${venv_recreate}" == true ]]; then
    rm -rf "${PYTHON_VENV_DIR}"
    "${PYTHON_BIN}" -m venv "${PYTHON_VENV_DIR}"

    HOME="${SETUP_HOME_DIR}" \
    XDG_CACHE_HOME="${SETUP_CACHE_DIR}" \
    PIP_CACHE_DIR="${SETUP_CACHE_DIR}/pip" \
    "${PYTHON_VENV_DIR}/bin/python" -m pip install \
        --disable-pip-version-check \
        "west==${WEST_VERSION}" \
        "cmake==${CMAKE_VERSION}" \
        "ninja==${NINJA_VERSION}"

    printf '%s\n' "${VENV_MARKER_VALUE}" > "${VENV_MARKER}"
fi

readonly VENV_PYTHON="${PYTHON_VENV_DIR}/bin/python"
readonly WEST="${PYTHON_VENV_DIR}/bin/west"
readonly CMAKE="${PYTHON_VENV_DIR}/bin/cmake"
readonly NINJA="${PYTHON_VENV_DIR}/bin/ninja"

[[ -x "${WEST}" ]] || die "west was not installed into the private venv."
[[ -x "${CMAKE}" ]] || die "CMake was not installed into the private venv."
[[ -x "${NINJA}" ]] || die "Ninja was not installed into the private venv."

# Keep subsequent Zephyr commands on RetroPLC-managed host tools.
export VIRTUAL_ENV="${PYTHON_VENV_DIR}"
export PATH="${PYTHON_VENV_DIR}/bin:${PATH}"

# -----------------------------------------------------------------------------
# Official STruC++ VSIX
# -----------------------------------------------------------------------------

heading 5 "STruC++ ${STRUCPP_VERSION}"

readonly STRUCPP_MARKER="${STRUCPP_DIR}/.retroplc-strucpp-version"
readonly STRUCPP_SERVER="${STRUCPP_DIR}/out/server.js"

strucpp_is_current=false
if [[ -f "${STRUCPP_SERVER}" ]] && \
   [[ -f "${STRUCPP_MARKER}" ]] && \
   [[ "$(cat "${STRUCPP_MARKER}")" == "${STRUCPP_VERSION}" ]]; then
    strucpp_is_current=true
fi

if [[ "${strucpp_is_current}" == false ]]; then
    strucpp_vsix_asset="strucpp-vscode-${STRUCPP_VERSION}.vsix"
    strucpp_vsix_url="https://github.com/Autonomy-Logic/STruCpp/releases/download/v${STRUCPP_VERSION}/${strucpp_vsix_asset}"
    strucpp_vsix_sha256="5ffcf308763a83602e4a16a67b1c1a55113a05cb3ab95ca2750cfc0fe1d6a6a5"

    strucpp_vsix_path="${TMP_DIR}/${strucpp_vsix_asset}"
    strucpp_extract_dir="${TMP_DIR}/strucpp-vsix"

    download "${strucpp_vsix_url}" "${strucpp_vsix_path}"
    verify_sha256 "${strucpp_vsix_path}" "${strucpp_vsix_sha256}"

    mkdir -p "${strucpp_extract_dir}"

    # VSIX is ZIP. Use private Python so unzip is not another host dependency.
    "${PYTHON_BIN}" -m zipfile -e "${strucpp_vsix_path}" "${strucpp_extract_dir}"

    [[ -f "${strucpp_extract_dir}/extension/out/server.js" ]] || \
        die "The STruC++ VSIX does not contain extension/out/server.js."

    rm -rf "${STRUCPP_DIR}"
    mkdir -p "${STRUCPP_DIR}"

    # Preserve the extension layout because server.js discovers runtime and
    # bundled library assets relative to that layout.
    cp -R "${strucpp_extract_dir}/extension/." "${STRUCPP_DIR}/"

    printf '%s\n' "${STRUCPP_VERSION}" > "${STRUCPP_MARKER}"
fi

[[ -f "${STRUCPP_SERVER}" ]] || die "STruC++ server.js was not installed."

# No npm/node_modules installation is required: server.js is an esbuild bundle.
# RetroPLC launches it with:
#   Tools/toolchain/node/bin/node Tools/strucpp/out/server.js --stdio
#
# Use a persistent instance for IDE/LSP features and a separate short-lived
# instance for build compilation so editor-LSP failure cannot break PLC builds.

# -----------------------------------------------------------------------------
# mcumgrctl
# -----------------------------------------------------------------------------

heading 6 "mcumgrctl ${MCUMGR_VERSION}"

readonly MCUMGR_OUTPUT="${MCUMGR_DIR}/${mcumgr_name}"
readonly MCUMGR_MARKER="${MCUMGR_DIR}/.retroplc-mcumgr-version"

mcumgr_is_current=false
if [[ -x "${MCUMGR_OUTPUT}" ]] && \
   [[ -f "${MCUMGR_MARKER}" ]] && \
   [[ "$(cat "${MCUMGR_MARKER}")" == "${MCUMGR_VERSION}" ]]; then
    mcumgr_is_current=true
fi

if [[ "${mcumgr_is_current}" == false ]]; then
    mcumgr_tmp="${TMP_DIR}/${mcumgr_name}"

    download "${mcumgr_url}" "${mcumgr_tmp}"
    verify_sha256 "${mcumgr_tmp}" "${mcumgr_sha256}"

    mkdir -p "${MCUMGR_DIR}"
    cp "${mcumgr_tmp}" "${MCUMGR_OUTPUT}"
    chmod +x "${MCUMGR_OUTPUT}"
    printf '%s\n' "${MCUMGR_VERSION}" > "${MCUMGR_MARKER}"
fi

# -----------------------------------------------------------------------------
# Pinned Zephyr workspace
# -----------------------------------------------------------------------------

heading 7 "Zephyr ${ZEPHYR_REVISION}"

mkdir -p "${ZEPHYR_WORKSPACE_DIR}"

if [[ ! -d "${ZEPHYR_DIR}/.git" ]]; then
    rm -rf "${ZEPHYR_DIR}"
    git clone --filter=blob:none --no-checkout "${ZEPHYR_REPOSITORY}" "${ZEPHYR_DIR}"
else
    git -C "${ZEPHYR_DIR}" remote set-url origin "${ZEPHYR_REPOSITORY}"
fi

git -C "${ZEPHYR_DIR}" fetch --depth 1 origin "${ZEPHYR_REVISION}"
git -C "${ZEPHYR_DIR}" checkout --detach --force FETCH_HEAD

if [[ ! -d "${ZEPHYR_WORKSPACE_DIR}/.west" ]]; then
    (
        cd "${ZEPHYR_WORKSPACE_DIR}"
        "${WEST}" init -l zephyr
    )
fi

heading 8 "Opta Zephyr modules + Python packages"

# Keep the workspace intentionally small for Arduino Opta:
# STM32H747 + Infineon HAL retained for future connectivity, MCUboot/mcumgr,
# and the crypto dependencies used by the firmware-update path.
(
    cd "${ZEPHYR_WORKSPACE_DIR}"
    "${WEST}" update \
        cmsis_6 \
        hal_stm32 \
        hal_infineon \
        mcuboot \
        mbedtls \
        tf-psa-crypto \
        zcbor
)

# Install the Python package set declared by this Zephyr workspace into the
# RetroPLC-owned venv. No global Python packages are touched.
(
    cd "${ZEPHYR_WORKSPACE_DIR}"

    HOME="${SETUP_HOME_DIR}" \
    XDG_CACHE_HOME="${SETUP_CACHE_DIR}" \
    PIP_CACHE_DIR="${SETUP_CACHE_DIR}/pip" \
    "${WEST}" packages pip --install
)

# -----------------------------------------------------------------------------
# Zephyr SDK: reuse an existing installation or install through west
# -----------------------------------------------------------------------------

sdk_expected_version="$(head -n 1 "${ZEPHYR_DIR}/SDK_VERSION" | tr -d '[:space:]')"
[[ -n "${sdk_expected_version}" ]] || \
    die "Could not determine Zephyr SDK version from ${ZEPHYR_DIR}/SDK_VERSION."

heading 9 "Zephyr SDK ${sdk_expected_version} (arm-zephyr-eabi)"

# Deliberately do not force --install-dir here:
# - if the required SDK is already discoverable, west reuses it;
# - otherwise west installs it in its normal user-level SDK location.
(
    cd "${ZEPHYR_DIR}"
    "${WEST}" sdk install \
        --gnu-toolchains arm-zephyr-eabi
)

# -----------------------------------------------------------------------------
# Final verification
# -----------------------------------------------------------------------------

heading 10 "Verifying RetroPLC setup"

echo "  Node:           $("${NODE_BIN}" --version)"
echo "  Python:         ${python_full_version}"
echo "  west:           $("${WEST}" --version)"
echo "  CMake:          $("${CMAKE}" --version | head -n 1)"
echo "  Ninja:          $("${NINJA}" --version)"
echo "  DTC:            ${dtc_version}"
echo "  STruC++:        ${STRUCPP_VERSION}"
echo "  STruC++ server: ${STRUCPP_SERVER}"
echo "  Zephyr:         $(git -C "${ZEPHYR_DIR}" rev-parse --short=12 HEAD)"
echo "  Zephyr SDK:     ${sdk_expected_version}"
echo "  mcumgrctl:      ${MCUMGR_OUTPUT}"

echo
echo "System dependencies still required: git, curl, tar, dtc, and sha256sum/shasum."
echo "RetroPLC-managed Node, Python, west, CMake, Ninja, Zephyr sources, STruC++,"
echo "and mcumgrctl live below:"
echo "  ${TOOLS_DIR}"
echo "The Zephyr SDK is reused or installed by west in its normal user-level location."

success "==== [ RetroPLC setup complete ] ===="

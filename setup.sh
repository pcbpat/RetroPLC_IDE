#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
#
# RetroPLC toolchain bootstrap for Linux and macOS.
#
# Managed by this script:
#   - private Node.js runtime (for STruC++ compiler + language server)
#   - STruC++ source checkout and compiled JavaScript tools
#   - private CPython 3.12.14 runtime
#   - private Python virtual environment
#   - west, CMake and Ninja
#   - pinned Zephyr workspace + west modules
#   - Zephyr Python packages
#   - Zephyr SDK arm-zephyr-eabi toolchain via west (reuse/install in user environment)
#   - mcumgrctl
#
# Remaining host dependencies:
#   - git
#   - curl
#   - tar
#   - dtc (Device Tree Compiler)
#   - basic POSIX shell utilities
#
# Windows should use setup.ps1 instead.

set -euo pipefail

# -----------------------------------------------------------------------------
# Pinned versions
# -----------------------------------------------------------------------------

readonly STRUCPP_VERSION="0.6.3"
readonly STRUCPP_REPOSITORY="https://github.com/Autonomy-Logic/STruCpp.git"

readonly MCUMGR_VERSION="0.16.0"
readonly MCUMGR_REPOSITORY="Finomnis/mcumgr-toolkit"

# STruC++ requires Node.js >= 22. RetroPLC uses one exact private runtime.
readonly NODE_VERSION="22.23.2"

# python-build-standalone release. RetroPLC pins the exact CPython 3.12.14 asset
# from this immutable release rather than depending on a system Python.
readonly PYTHON_VERSION="3.12.14"
readonly PYTHON_BUILD_RELEASE="20260825"
readonly PYTHON_BUILD_REPOSITORY="astral-sh/python-build-standalone"

readonly WEST_VERSION="1.5.0"
readonly CMAKE_VERSION="3.31.10"
readonly NINJA_VERSION="1.13.0"

# Exact Zephyr revision validated with the RetroPLC Opta runtime. The v4.4.0
# tag predates an STM32H7 devicetree fix required by the runtime's two-bank
# flash overlay and fails while configuring MCUboot.
readonly ZEPHYR_REVISION="${ZEPHYR_REVISION:-da0718ca0d52d4f3e3653ead4f8d3a907778ae0b}"
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

readonly TMP_DIR="$(mktemp -d "${TMPDIR:-/tmp}/retroplc-setup.XXXXXX")"

cleanup() {
    rm -rf "${TMP_DIR}"
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

info() {
    printf '  %s\n' "$*"
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
        python_target="x86_64-unknown-linux-gnu"
        mcumgr_asset="mcumgrctl-linux"
        mcumgr_name="mcumgrctl"
        mcumgr_sha256="d2af9bd8843e108e7dbba43c03387c005e69f303d3cf9267aa5d2c796dcf7aeb"
        ;;
    Darwin:arm64|Darwin:aarch64)
        node_platform="darwin-arm64"
        python_target="aarch64-apple-darwin"
        mcumgr_asset="mcumgrctl-macos"
        mcumgr_name="mcumgrctl"
        mcumgr_sha256="a9cbfb44cfc0852db8c2713b1255116db5524d02b912fe0ab98aa02e2dccff0"
        ;;
    Linux:aarch64|Linux:arm64)
        die "Linux ARM64 is not enabled because mcumgr-toolkit ${MCUMGR_VERSION} has no matching prebuilt asset in the existing RetroPLC setup."
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
# GitHub release helpers (using private Node for JSON parsing)
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

github_asset_name_by_prefix_suffix() {
    local release_json="$1"
    local prefix="$2"
    local suffix="$3"

    "${NODE_BIN}" -e '
const fs = require("fs");
const release = JSON.parse(fs.readFileSync(process.argv[1], "utf8"));
const prefix = process.argv[2];
const suffix = process.argv[3];
const matches = release.assets.filter(
  asset => asset.name.startsWith(prefix) && asset.name.endsWith(suffix)
);
if (matches.length !== 1) {
  console.error(`Expected exactly one GitHub release asset matching ${prefix}*${suffix}; found ${matches.length}`);
  process.exit(1);
}
process.stdout.write(matches[0].name);
' "${release_json}" "${prefix}" "${suffix}"
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
    else
        echo "WARNING: GitHub did not provide a SHA-256 digest for ${asset_name}; downloaded asset could not be digest-verified." >&2
    fi
}

# -----------------------------------------------------------------------------
# Private CPython 3.12.14
# -----------------------------------------------------------------------------

heading 3 "Python ${PYTHON_VERSION}"

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

    printf '%s\n' "${PYTHON_BUILD_RELEASE}:${PYTHON_VERSION}" > "${PYTHON_DIR}/.retroplc-python-build"
fi

readonly PYTHON_BIN="${PYTHON_DIR}/bin/python3"
[[ -x "${PYTHON_BIN}" ]] || die "Private Python executable was not installed."

python_full_version="$("${PYTHON_BIN}" -c 'import platform; print(platform.python_version())')"
[[ "${python_full_version}" == "${PYTHON_VERSION}" ]] || \
    die "Expected Python ${PYTHON_VERSION}, found ${python_full_version}."

# -----------------------------------------------------------------------------
# Validate DTC after private Python is available
# -----------------------------------------------------------------------------

dtc_version_raw="$(dtc --version 2>&1 || true)"
dtc_version="$(
    printf '%s\n' "${dtc_version_raw}" | \
        sed -E 's/.*DTC[[:space:]]+([0-9]+(\.[0-9]+)+).*/\1/'
)"

if [[ -z "${dtc_version}" || "${dtc_version}" == "${dtc_version_raw}" ]]; then
    die "Could not determine DTC version from: ${dtc_version_raw}"
fi

if ! "${PYTHON_BIN}" - "${dtc_version}" <<'PY'
import sys

def as_tuple(value: str):
    return tuple(int(part) for part in value.split("."))

actual = as_tuple(sys.argv[1])
required = as_tuple("1.4.6")
raise SystemExit(0 if actual >= required else 1)
PY
then
    die "DTC 1.4.6 or newer is required (found ${dtc_version})."
fi

# -----------------------------------------------------------------------------
# Private Python venv + west + CMake + Ninja
# -----------------------------------------------------------------------------

heading 4 "west ${WEST_VERSION} + CMake ${CMAKE_VERSION} + Ninja ${NINJA_VERSION}"

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

readonly VENV_PYTHON="${PYTHON_VENV_DIR}/bin/python"
readonly WEST="${PYTHON_VENV_DIR}/bin/west"
readonly CMAKE="${PYTHON_VENV_DIR}/bin/cmake"
readonly NINJA="${PYTHON_VENV_DIR}/bin/ninja"

[[ -x "${WEST}" ]] || die "west was not installed into the private venv."
[[ -x "${CMAKE}" ]] || die "CMake was not installed into the private venv."
[[ -x "${NINJA}" ]] || die "Ninja was not installed into the private venv."

# Keep subsequent Zephyr commands on RetroPLC-managed host tools.
export VIRTUAL_ENV="${PYTHON_VENV_DIR}"
export PATH="${NODE_DIR}/bin:${PYTHON_VENV_DIR}/bin:${PATH}"

# -----------------------------------------------------------------------------
# STruC++ source checkout + JavaScript compiler/LSP
# -----------------------------------------------------------------------------

heading 5 "STruC++ ${STRUCPP_VERSION}"

readonly STRUCPP_MARKER="${STRUCPP_DIR}/.retroplc-build-version"
readonly STRUCPP_COMPILER="${STRUCPP_DIR}/dist/node/cli.js"
readonly STRUCPP_SERVER="${STRUCPP_DIR}/vscode-extension/out/server/src/server.js"
readonly STRUCPP_EXPECTED_COMMIT="80481d1c4c14c58da3a08f2fa00e7990f20a35ce"

if [[ ! -d "${STRUCPP_DIR}/.git" ]]; then
    rm -rf "${STRUCPP_DIR}"
    git clone --filter=blob:none --depth 1 --branch "v${STRUCPP_VERSION}" \
        "${STRUCPP_REPOSITORY}" "${STRUCPP_DIR}"
else
    git -C "${STRUCPP_DIR}" remote set-url origin "${STRUCPP_REPOSITORY}"
    git -C "${STRUCPP_DIR}" fetch --force --depth 1 origin \
        "refs/tags/v${STRUCPP_VERSION}:refs/tags/v${STRUCPP_VERSION}"
    git -C "${STRUCPP_DIR}" checkout --detach --force "v${STRUCPP_VERSION}^{commit}"
fi

strucpp_commit="$(git -C "${STRUCPP_DIR}" rev-parse HEAD)"
[[ "${strucpp_commit}" == "${STRUCPP_EXPECTED_COMMIT}" ]] || \
    die "Unexpected STruC++ v${STRUCPP_VERSION} commit: ${strucpp_commit}"

strucpp_marker_value="${STRUCPP_VERSION}:${STRUCPP_EXPECTED_COMMIT}:${NODE_VERSION}"
strucpp_is_current=false
if [[ -f "${STRUCPP_COMPILER}" ]] && \
   [[ -f "${STRUCPP_SERVER}" ]] && \
   [[ -f "${STRUCPP_MARKER}" ]] && \
   [[ "$(cat "${STRUCPP_MARKER}" 2>/dev/null || true)" == "${strucpp_marker_value}" ]]; then
    strucpp_is_current=true
fi

run_npm() {
    "${NODE_BIN}" "${NPM_CLI}" "$@"
}

if [[ "${strucpp_is_current}" == false ]]; then
    info "Building STruC++ compiler and language server from source"

    # STruC++ root: compile TypeScript and rebuild bundled .stlib libraries.
    (
        cd "${STRUCPP_DIR}"
        run_npm ci --ignore-scripts
        run_npm run build
    )

    # The language-server source lives in upstream's vscode-extension tree,
    # but RetroPLC does not build, download or extract a VS Code extension.
    (
        cd "${STRUCPP_DIR}/vscode-extension"
        run_npm ci --ignore-scripts
        run_npm run build
    )

    printf '%s\n' "${strucpp_marker_value}" > "${STRUCPP_MARKER}"
fi

[[ -f "${STRUCPP_COMPILER}" ]] || die "STruC++ compiler JavaScript was not built."
[[ -f "${STRUCPP_SERVER}" ]] || die "STruC++ language-server JavaScript was not built."

# Verify the compiler with RetroPLC's bundled Node.js.
strucpp_reported_version="$("${NODE_BIN}" "${STRUCPP_COMPILER}" --version 2>/dev/null | tail -n 1 | tr -d '\r')"
[[ "${strucpp_reported_version}" == *"${STRUCPP_VERSION}"* ]] || \
    die "Unexpected STruC++ compiler version output: ${strucpp_reported_version}"

# RetroPLC runtime commands:
#   Compiler: Tools/toolchain/node/bin/node Tools/strucpp/dist/node/cli.js ...
#   LSP:      Tools/toolchain/node/bin/node Tools/strucpp/vscode-extension/out/server/src/server.js --stdio

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
    info "Installing mcumgrctl ${MCUMGR_VERSION}"

    mcumgr_tmp="${TMP_DIR}/${mcumgr_name}"

    download_github_release_asset \
        "${MCUMGR_REPOSITORY}" \
        "${MCUMGR_VERSION}" \
        "${mcumgr_asset}" \
        "${mcumgr_tmp}"

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
    "${WEST}" packages pip --install
)

# -----------------------------------------------------------------------------
# Zephyr SDK: reuse an existing matching installation or install for this user
# -----------------------------------------------------------------------------

heading 9 "Zephyr SDK (arm-zephyr-eabi)"

sdk_expected_version="$(head -n 1 "${ZEPHYR_DIR}/SDK_VERSION" | tr -d '[:space:]')"
[[ -n "${sdk_expected_version}" ]] || \
    die "Could not determine Zephyr SDK version from ${ZEPHYR_DIR}/SDK_VERSION."

# Deliberately do not force an install directory. `west sdk install` may reuse
# a matching SDK already registered for the user; otherwise it installs to the
# normal per-user Zephyr SDK location and registers the CMake package.
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
echo "  STruC++ CLI:    ${STRUCPP_COMPILER}"
echo "  STruC++ LSP:    ${STRUCPP_SERVER}"
echo "  Zephyr:         $(git -C "${ZEPHYR_DIR}" rev-parse --short=12 HEAD)"
echo "  Zephyr SDK:     ${sdk_expected_version}"
echo "  mcumgrctl:      ${MCUMGR_OUTPUT}"

echo
echo "System dependencies still required: git, curl, tar, dtc."
echo "Node, npm, Python, west, CMake, Ninja and Zephyr are RetroPLC-managed; the Zephyr SDK is reused or installed per-user by west."
success "RetroPLC setup completed successfully."

#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
#
# RetroPLC toolchain bootstrap for Linux and macOS.
#
# Source dependencies initialized by this script:
#   - RetroPLC.Icons/Win98SE Git submodule
#   - tools/strucpp Git submodule
#
# Managed under RetroPLC_IDE/tools:
#   - private Node.js runtime
#   - built STruC++ compiler and LSP in tools/strucpp (Git submodule)
#   - mcumgrctl
#
# Remaining host dependencies:
#   - git
#   - curl
#   - tar
#   - basic POSIX shell utilities
#   - sha256sum or shasum
#
# AC6 hosttools must already be installed in sibling .zinstaller/.
# Zephyr workspace and matching SDK are provisioned after application tools.
# Windows bootstrap is not implemented yet.

set -euo pipefail

# -----------------------------------------------------------------------------
# Pinned versions
# -----------------------------------------------------------------------------

readonly MCUMGR_VERSION="0.16.0"
readonly MCUMGR_REPOSITORY="Finomnis/mcumgr-toolkit"

# STruC++ currently requires Node.js >= 22.
readonly NODE_VERSION="22.23.2"

# -----------------------------------------------------------------------------
# Paths
# -----------------------------------------------------------------------------

readonly SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
readonly TOOLS_DIR="${SCRIPT_DIR}/tools"
readonly AC6_DIR="${SCRIPT_DIR}/.zinstaller"
readonly AC6_VENV="${AC6_DIR}/.venv"
readonly AC6_ENV="${AC6_DIR}/env.sh"

readonly STRUCPP_SOURCE_DIR="${TOOLS_DIR}/strucpp"
readonly STRUCPP_MARKER="${TOOLS_DIR}/.retroplc-strucpp-build"

readonly MCUMGR_DIR="${TOOLS_DIR}/mcumgr"

readonly NODE_DIR="${TOOLS_DIR}/node"
readonly TMP_DIR="$(mktemp -d "${TMPDIR:-/tmp}/retroplc-setup.XXXXXX")"

readonly ZEPHYR_DIR="${SCRIPT_DIR}/.zephyr"
readonly ZEPHYR_WORKSPACE_DIR="${ZEPHYR_DIR}/workspace"
readonly RETROPLC_RUNTIME_DIR="${ZEPHYR_WORKSPACE_DIR}/retroplc-runtime"
readonly RETROPLC_RUNTIME_URL="${RETROPLC_RUNTIME_URL:-https://github.com/pcbpat/RetroPLC_Runtime.git}"
readonly RETROPLC_SDK_TOOLCHAINS="${RETROPLC_SDK_TOOLCHAINS:-arm-zephyr-eabi}"
readonly ZEPHYR_SDK_DIR="${ZEPHYR_DIR}/sdk"

mkdir -p \
    "${ZEPHYR_WORKSPACE_DIR}" \
    "${ZEPHYR_DIR}"

cleanup() {
    rm -rf "${TMP_DIR}"
}
trap cleanup EXIT

readonly TOTAL_STEPS=8

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
        mcumgr_asset="mcumgrctl-linux"
        mcumgr_name="mcumgrctl"
        mcumgr_sha256="d2af9bd8843e108e7dbba43c03387c005e69f303d3cf9267aa5d2c796dcf7aeb"
        ;;
    Darwin:arm64|Darwin:aarch64)
        node_platform="darwin-arm64"
        mcumgr_asset="mcumgrctl-macos"
        mcumgr_name="mcumgrctl"
        mcumgr_sha256="a9cbfb44cfc0852db8c2713b1255116db5524d02b912fe0ab98aa02e2dccff0"
        ;;
    Linux:aarch64|Linux:arm64)
        die "Linux ARM64 is not enabled because mcumgr-toolkit ${MCUMGR_VERSION} has no matching prebuilt release asset."
        ;;
    Darwin:x86_64|Darwin:amd64)
        die "Intel macOS is not enabled because the validated mcumgrctl asset is unavailable for this host."
        ;;
    MINGW*:*|MSYS*:*|CYGWIN*:*)
        die "Windows bootstrap is not implemented yet."
        ;;
    *)
        die "Unsupported host platform: ${platform} ${architecture}"
        ;;
esac

# No managed dependency is created until the AC6 environment is validated.


# -----------------------------------------------------------------------------
# AC6 hosttools prerequisite (installed separately at repository level)
# -----------------------------------------------------------------------------

heading 2 "Checking AC6 .zinstaller environment"

[[ -d "${AC6_DIR}" ]] || die "AC6 hosttools not found at ${AC6_DIR}. Install AC6 into the RetroPLC_IDE root first."
[[ -f "${AC6_ENV}" ]] || die "AC6 environment file missing: ${AC6_ENV}"
[[ -f "${AC6_DIR}/env.yml" ]] || die "AC6 environment metadata missing: ${AC6_DIR}/env.yml"
[[ -f "${AC6_DIR}/env.py" ]] || die "AC6 environment loader missing: ${AC6_DIR}/env.py"
[[ -x "${AC6_VENV}/bin/python" ]] || die "AC6 Python venv missing: ${AC6_VENV}/bin/python"
[[ -x "${AC6_VENV}/bin/west" ]] || die "AC6 west missing: ${AC6_VENV}/bin/west"

# Validate the configured AC6 tools inside a subshell.
(
    set +u
    unset PYTHONHOME PYTHONPATH VIRTUAL_ENV
    export PYTHON_VENV_PATH="${AC6_VENV}"
    export PATH="${AC6_VENV}/bin:${PATH}"
    if [[ "$(uname -s)" == Linux && ! -w /dev/fuse && -z "${APPIMAGE_EXTRACT_AND_RUN+x}" ]]; then
        export APPIMAGE_EXTRACT_AND_RUN=1
    fi

    "${AC6_VENV}/bin/python" --version || die "AC6 Python failed to start at ${AC6_VENV}/bin/python"
    source "${AC6_ENV}" || die "Could not load ${AC6_ENV}"
    if [[ "${VIRTUAL_ENV:-}" != "${AC6_VENV}" ]]; then
        source "${AC6_VENV}/bin/activate" || die "Could not activate ${AC6_VENV}"
    fi
    [[ "${VIRTUAL_ENV:-}" == "${AC6_VENV}" ]] || die "AC6 virtual environment points elsewhere. Check ${AC6_DIR}."

    export PATH="${AC6_VENV}/bin:${PATH}"
    for executable in cmake ninja; do
        resolved="$(type -P "${executable}")" || die "Configure ${executable} in ${AC6_DIR}/env.yml"
        resolved="$("${AC6_VENV}/bin/python" -c 'import os, sys; print(os.path.realpath(sys.argv[1]))' "${resolved}")" ||
            die "Could not resolve AC6 ${executable}"
        case "${resolved}" in
            "${AC6_DIR}"/tools/*) ;;
            *) die "${executable} resolves outside AC6 tools: ${resolved}" ;;
        esac
        info "${executable}: ${resolved}"
        "${resolved}" --version || die "AC6 ${executable} failed to start"
    done

    "${AC6_VENV}/bin/west" --version || die "AC6 west failed to start"
) || die "AC6 environment verification failed"

mkdir -p "${TOOLS_DIR}" "${MCUMGR_DIR}"

# -----------------------------------------------------------------------------
# Source submodules
# -----------------------------------------------------------------------------

heading 3 "Initializing source submodules"

git -C "${SCRIPT_DIR}" rev-parse --show-toplevel >/dev/null 2>&1 || \
    die "RetroPLC_IDE must be a Git checkout."

if ! git -C "${SCRIPT_DIR}" ls-files --stage -- tools/strucpp | grep -q '^160000 '; then
    die "tools/strucpp is not registered as a Git submodule (mode 160000). Changing .gitmodules alone is insufficient; migrate the gitlink with git mv."
fi

if [[ -L "${STRUCPP_SOURCE_DIR}" ]]; then
    die "tools/strucpp is still a symbolic link. Migrate the submodule to a real directory before running setup."
fi

# Ensure the .gitmodules path matches the tracked gitlink.
if ! git -C "${SCRIPT_DIR}" config -f .gitmodules --get-regexp '^submodule\..*\.path$' \
    | grep -Eq '[[:space:]]tools/strucpp$'; then
    die "The .gitmodules configuration must contain a submodule with path = tools/strucpp."
fi

for submodule in RetroPLC.Icons/Win98SE tools/strucpp; do
    checkout="${SCRIPT_DIR}/${submodule}"
    if [[ ! -e "${checkout}/.git" ]]; then
        git -C "${SCRIPT_DIR}" submodule update --init --recursive -- "${submodule}" ||
            die "Could not initialize ${submodule}"
    fi
done

[[ -f "${STRUCPP_SOURCE_DIR}/package.json" ]] || \
    die "The tools/strucpp submodule was not initialized."
[[ -d "${STRUCPP_SOURCE_DIR}/.git" || -f "${STRUCPP_SOURCE_DIR}/.git" ]] || \
    die "tools/strucpp is not a Git submodule checkout."

# -----------------------------------------------------------------------------
# Private Node.js runtime
# -----------------------------------------------------------------------------

heading 4 "Node.js ${NODE_VERSION}"

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
export PATH="${NODE_DIR}/bin:${PATH}"

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
# Build STruC++ from the pinned local submodule
# -----------------------------------------------------------------------------

heading 5 "Building STruC++ from tools/strucpp"

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
#   tools/strucpp/dist/node/cli.js
#   tools/strucpp/vscode-extension/out/server/src/server.js
#   tools/strucpp/libs
# The actual Git submodule checkout is directly in tools/strucpp.

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

    cp "${mcumgr_tmp}" "${MCUMGR_OUTPUT}"
    chmod +x "${MCUMGR_OUTPUT}"
    printf '%s\n' "${MCUMGR_VERSION}" > "${MCUMGR_MARKER}"
fi

# -----------------------------------------------------------------------------
# Managed Zephyr workspace + SDK (RetroPLC Runtime manifest)
# -----------------------------------------------------------------------------

heading 7 "Provisioning RetroPLC Zephyr workspace and SDK"

# The prerequisite check above runs in a subshell intentionally. Initialize an
# AC6 environment for west here without modifying the caller's global shell.
# AC6's generated env.sh can leave system cmake/ninja first on PATH; explicitly
# prioritize the actual managed executables without assuming a CMake version.
configure_zephyr_environment() {
    unset PYTHONHOME PYTHONPATH VIRTUAL_ENV ZEPHYR_BASE ZEPHYR_SDK_INSTALL_DIR || true
    export PYTHON_VENV_PATH="${AC6_VENV}"
    export PATH="${AC6_VENV}/bin:${PATH}"
    if [[ "$(uname -s)" == Linux && ! -w /dev/fuse && -z "${APPIMAGE_EXTRACT_AND_RUN+x}" ]]; then
        export APPIMAGE_EXTRACT_AND_RUN=1
    fi

    # Some generated AC6 scripts refer to unset variables. Disable nounset
    # only while sourcing their environment, then restore its prior state.
    local nounset_was_on=false
    case "$-" in *u*) nounset_was_on=true ;; esac
    set +u
    if ! source "${AC6_ENV}"; then
        [[ "${nounset_was_on}" == true ]] && set -u
        die "Could not load AC6 environment: ${AC6_ENV}"
    fi
    [[ "${nounset_was_on}" == true ]] && set -u

    local -a cmake_binaries=("${AC6_DIR}"/tools/cmake-*/bin/cmake)
    local cmake_binary=""
    local candidate
    for candidate in "${cmake_binaries[@]}"; do
        [[ -x "${candidate}" ]] || continue
        if [[ -n "${cmake_binary}" ]]; then
            die "Multiple AC6 CMake installations found. Select one in env.yml before provisioning."
        fi
        cmake_binary="${candidate}"
    done
    [[ -n "${cmake_binary}" ]] || die "No managed CMake found under ${AC6_DIR}/tools"
    [[ -x "${AC6_DIR}/tools/ninja/ninja" ]] || die "Managed Ninja missing under ${AC6_DIR}/tools/ninja"

    export VIRTUAL_ENV="${AC6_VENV}"
    export PATH="${AC6_VENV}/bin:$(dirname "${cmake_binary}"):${AC6_DIR}/tools/ninja:${PATH}"
    hash -r

    [[ "$(type -P west)" == "${AC6_VENV}/bin/west" ]] || die "west is not the AC6-managed executable"
    [[ "$(type -P cmake)" == "${cmake_binary}" ]] || die "CMake does not resolve to the managed executable"
    [[ "$(type -P ninja)" == "${AC6_DIR}/tools/ninja/ninja" ]] || die "Ninja does not resolve to the managed executable"
}

provision_zephyr() (
    configure_zephyr_environment
    local west="${AC6_VENV}/bin/west"
    local python="${AC6_VENV}/bin/python"
    local sdk_version sdk_gcc sdk_installed_version

    mkdir -p "${ZEPHYR_WORKSPACE_DIR}"

    # Never reset, force-checkout, or overwrite a local Runtime repository.
    if [[ ! -d "${RETROPLC_RUNTIME_DIR}/.git" && ! -f "${RETROPLC_RUNTIME_DIR}/.git" ]]; then
        if [[ -e "${RETROPLC_RUNTIME_DIR}" ]]; then
            die "Runtime path exists but is not a Git checkout: ${RETROPLC_RUNTIME_DIR}"
        fi
        info "Cloning RetroPLC Runtime manifest repository"
        git clone "${RETROPLC_RUNTIME_URL}" "${RETROPLC_RUNTIME_DIR}"
    else
        info "Reusing existing RetroPLC Runtime checkout"
    fi

    [[ -f "${RETROPLC_RUNTIME_DIR}/west.yml" ]] || die "Runtime manifest missing: ${RETROPLC_RUNTIME_DIR}/west.yml"
    cd "${ZEPHYR_WORKSPACE_DIR}"
    if [[ ! -f .west/config ]]; then
        if [[ -e .west ]]; then
            die "Found incomplete west metadata in ${ZEPHYR_WORKSPACE_DIR}/.west; repair manually"
        fi
        info "Initializing west workspace from retroplc-runtime/west.yml"
        "${west}" init -l "${RETROPLC_RUNTIME_DIR}" --mf west.yml
    else
        local manifest_path manifest_file
        manifest_path="$("${west}" config --local manifest.path 2>/dev/null || true)"
        manifest_file="$("${west}" config --local manifest.file 2>/dev/null || true)"
        [[ -n "${manifest_file}" ]] || manifest_file=west.yml
        [[ -n "${manifest_path}" ]] || die "Existing workspace has no manifest.path: ${ZEPHYR_WORKSPACE_DIR}"
        # Preserve existing workspace if and only if it is tied to our Runtime.
        local actual_manifest
        actual_manifest="$(cd "${ZEPHYR_WORKSPACE_DIR}/${manifest_path}" 2>/dev/null && pwd -P)" || die "Invalid west manifest.path: ${manifest_path}"
        [[ "${actual_manifest}" == "$(cd "${RETROPLC_RUNTIME_DIR}" && pwd -P)" && "${manifest_file}" == west.yml ]] ||
            die "Existing workspace points to a different manifest (${manifest_path}/${manifest_file}); refusing to rewrite"
        info "Reusing initialized west workspace"
    fi

    info "Synchronizing Zephyr modules from the Runtime manifest"
    "${west}" update
    [[ -f zephyr/SDK_VERSION ]] || die "Missing zephyr/SDK_VERSION after west update"
    info "Installing workspace-specific Python requirements into AC6 venv"
    "${west}" packages pip --install
    "${west}" zephyr-export

    sdk_version="$(head -n 1 zephyr/SDK_VERSION | tr -d '[:space:]')"
    [[ "${sdk_version}" =~ ^[0-9]+\.[0-9]+\.[0-9]+([.-][[:alnum:]._-]+)?$ ]] ||
        die "Invalid Zephyr SDK version in zephyr/SDK_VERSION: ${sdk_version}"
    sdk_gcc="${ZEPHYR_SDK_DIR}/gnu/arm-zephyr-eabi/bin/arm-zephyr-eabi-gcc"

    info "Zephyr SDK required by Runtime: ${sdk_version}"
    if [[ -e "${ZEPHYR_SDK_DIR}" ]]; then
        [[ -d "${ZEPHYR_SDK_DIR}" && ! -L "${ZEPHYR_SDK_DIR}" ]] ||
            die "SDK destination is not a regular directory: ${ZEPHYR_SDK_DIR}"
        [[ -f "${ZEPHYR_SDK_DIR}/sdk_version" ]] ||
            die "SDK destination is incomplete: ${ZEPHYR_SDK_DIR}. Move it aside and rerun setup."
        sdk_installed_version="$(head -n 1 "${ZEPHYR_SDK_DIR}/sdk_version" | tr -d '[:space:]')"
        [[ "${sdk_installed_version}" == "${sdk_version}" ]] ||
            die "Installed SDK ${sdk_installed_version} differs from Runtime-required ${sdk_version}. Move ${ZEPHYR_SDK_DIR} aside and rerun setup."
        [[ -x "${sdk_gcc}" ]] ||
            die "SDK at ${ZEPHYR_SDK_DIR} lacks ARM compiler. Repair the installation manually."
        info "Reusing installed SDK: ${ZEPHYR_SDK_DIR}"
    else
        info "Installing Zephyr SDK ${sdk_version} directly into ${ZEPHYR_SDK_DIR}"
        # The Runtime-pinned Zephyr's 'west sdk install --install-dir' option
        # extracts the SDK into this exact directory (no versioned child).
        local -a toolchains=()
        read -r -a toolchains <<< "${RETROPLC_SDK_TOOLCHAINS}"
        (( ${#toolchains[@]} > 0 )) || die "RETROPLC_SDK_TOOLCHAINS cannot be empty"
        "${west}" sdk install --version "${sdk_version}" \
            --install-dir "${ZEPHYR_SDK_DIR}" --toolchains "${toolchains[@]}"
    fi

    [[ -f "${ZEPHYR_SDK_DIR}/sdk_version" ]] || die "Installed SDK missing sdk_version: ${ZEPHYR_SDK_DIR}"
    sdk_installed_version="$(head -n 1 "${ZEPHYR_SDK_DIR}/sdk_version" | tr -d '[:space:]')"
    [[ "${sdk_installed_version}" == "${sdk_version}" ]] ||
        die "Installed SDK version mismatch: expected ${sdk_version}, got ${sdk_installed_version}"
    [[ -x "${sdk_gcc}" ]] || die "Installed SDK missing ARM compiler: ${sdk_gcc}"
    export ZEPHYR_SDK_INSTALL_DIR="${ZEPHYR_SDK_DIR}"
    export ZEPHYR_TOOLCHAIN_VARIANT=zephyr
    "${sdk_gcc}" --version >/dev/null || die "ARM compiler failed to start"
    info "west workspace: ${ZEPHYR_WORKSPACE_DIR}"
    info "Zephyr revision: $(git -C zephyr rev-parse --short HEAD)"
    info "SDK: ${ZEPHYR_SDK_DIR} (version ${sdk_version})"
)

provision_zephyr

# -----------------------------------------------------------------------------
# Final verification
# -----------------------------------------------------------------------------

heading 8 "Verifying RetroPLC setup"

echo "  Node:           $("${NODE_BIN}" --version)"
echo "  STruC++:        ${strucpp_version} (${strucpp_commit:0:12})"
echo "  STruC++ source: ${STRUCPP_SOURCE_DIR}"
echo "  mcumgrctl:      ${MCUMGR_OUTPUT}"
echo
echo "System dependencies still required: git, curl, tar and basic POSIX utilities."
echo "Node.js, STruC++ and mcumgrctl are managed under tools/."
echo "AC6 hosttools:    ${AC6_DIR}"
echo "Zephyr workspace: ${ZEPHYR_WORKSPACE_DIR}"
echo "Zephyr SDK:       ${ZEPHYR_SDK_DIR}"
echo "STruC++ and Win98SE are pinned Git submodules inside the RetroPLC_IDE checkout."
success "RetroPLC setup completed successfully."

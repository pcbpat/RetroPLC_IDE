#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later

set -euo pipefail

readonly STRUCPP_COMMIT="80481d1c4c14c58da3a08f2fa00e7990f20a35ce"
readonly MCUMGR_VERSION="0.16.0"
readonly MCUMGR_REPOSITORY="https://github.com/Finomnis/mcumgr-toolkit"

readonly SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
readonly STRUCPP_DIR="${SCRIPT_DIR}/external/STruCpp"
readonly TOOLS_DIR="${SCRIPT_DIR}/Tools"

for command in git node npm curl; do
    if ! command -v "${command}" >/dev/null 2>&1; then
        echo "Required command not found: ${command}" >&2
        exit 1
    fi
done

node_major="$(node --print "process.versions.node.split('.')[0]")"
if (( node_major < 22 )); then
    echo "STruC++ requires Node.js 22 or newer (found $(node --version))." >&2
    exit 1
fi

echo "Initializing STruC++ ${STRUCPP_COMMIT}..."
git -C "${SCRIPT_DIR}" submodule update --init --checkout -- external/STruCpp

actual_commit="$(git -C "${STRUCPP_DIR}" rev-parse HEAD)"
if [[ "${actual_commit}" != "${STRUCPP_COMMIT}" ]]; then
    echo "Unexpected STruC++ revision: ${actual_commit}" >&2
    echo "Expected the superproject to pin ${STRUCPP_COMMIT}." >&2
    exit 1
fi

platform="$(uname -s)"
architecture="$(uname -m)"

# STruC++ standalone executable target.
case "${platform}:${architecture}" in
    Linux:x86_64|Linux:amd64)
        pkg_target="node22-linux-x64"
        compiler_name="strucpp-linux"
        lsp_name="strucpp-lsp-linux"
        ;;
    Linux:aarch64|Linux:arm64)
        pkg_target="node22-linux-arm64"
        compiler_name="strucpp-linux"
        lsp_name="strucpp-lsp-linux"
        ;;
    Darwin:x86_64|Darwin:amd64)
        pkg_target="node22-macos-x64"
        compiler_name="strucpp-macos"
        lsp_name="strucpp-lsp-macos"
        ;;
    Darwin:arm64|Darwin:aarch64)
        pkg_target="node22-macos-arm64"
        compiler_name="strucpp-macos"
        lsp_name="strucpp-lsp-macos"
        ;;
    MINGW*:x86_64|MSYS*:x86_64|CYGWIN*:x86_64)
        pkg_target="node22-win-x64"
        compiler_name="strucpp-win.exe"
        lsp_name="strucpp-lsp-win.exe"
        ;;
    *)
        echo "Unsupported STruC++ build platform: ${platform} ${architecture}" >&2
        exit 1
        ;;
esac

# mcumgr-toolkit currently publishes these prebuilt mcumgrctl release assets.
case "${platform}:${architecture}" in
    Linux:x86_64|Linux:amd64)
        mcumgr_asset="mcumgrctl-linux"
        mcumgr_name="mcumgrctl"
        ;;
    Darwin:arm64|Darwin:aarch64)
        mcumgr_asset="mcumgrctl-macos"
        mcumgr_name="mcumgrctl"
        ;;
    MINGW*:x86_64|MSYS*:x86_64|CYGWIN*:x86_64)
        mcumgr_asset="mcumgrctl-windows.exe"
        mcumgr_name="mcumgrctl.exe"
        ;;
    *)
        echo "No prebuilt mcumgrctl ${MCUMGR_VERSION} binary is published for ${platform} ${architecture}." >&2
        exit 1
        ;;
esac

compiler_dir="${TOOLS_DIR}/compiler"
lsp_dir="${TOOLS_DIR}/lsp"
mcumgr_dir="${TOOLS_DIR}/mcumgr"

compiler_output="${compiler_dir}/${compiler_name}"
lsp_output="${lsp_dir}/${lsp_name}"
mcumgr_output="${mcumgr_dir}/${mcumgr_name}"

mkdir -p "${compiler_dir}" "${lsp_dir}" "${mcumgr_dir}"

mcumgr_url="${MCUMGR_REPOSITORY}/releases/download/${MCUMGR_VERSION}/${mcumgr_asset}"
mcumgr_tmp="${mcumgr_output}.tmp"

cleanup_mcumgr_download() {
    rm -f "${mcumgr_tmp}"
}
trap cleanup_mcumgr_download EXIT

echo "Downloading mcumgrctl ${MCUMGR_VERSION}..."
rm -f "${mcumgr_tmp}"
curl \
    --fail \
    --location \
    --retry 3 \
    --output "${mcumgr_tmp}" \
    "${mcumgr_url}"

mv "${mcumgr_tmp}" "${mcumgr_output}"
chmod +x "${mcumgr_output}"
trap - EXIT

echo "Installing STruC++ dependencies and building the CLI bundle..."
(
    cd "${STRUCPP_DIR}"
    npm ci
    npm run build:bundle
)

readonly PKG="${STRUCPP_DIR}/node_modules/.bin/pkg"

echo "Building ${compiler_name}..."
(
    cd "${STRUCPP_DIR}"
    "${PKG}" dist/strucpp-bundle.cjs \
        --no-bytecode \
        --public-packages "*" \
        --public \
        --target "${pkg_target}" \
        --output "${compiler_output}" \
        --compress GZip
)

echo "Installing language-server dependencies and bundling the server..."
(
    cd "${STRUCPP_DIR}/vscode-extension"
    npm ci
    npm run vscode:prepublish
)

echo "Building ${lsp_name}..."
(
    cd "${STRUCPP_DIR}"
    "${PKG}" vscode-extension/out/server.js \
        --no-bytecode \
        --public-packages "*" \
        --public \
        --target "${pkg_target}" \
        --output "${lsp_output}" \
        --compress GZip
)

chmod +x "${compiler_output}" "${lsp_output}"

echo "RetroPLC tools are ready:"
echo "  Compiler:  ${compiler_output}"
echo "  LSP:       ${lsp_output}"
echo "  mcumgrctl: ${mcumgr_output}"

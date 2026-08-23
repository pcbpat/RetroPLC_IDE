#!/usr/bin/env bash

set -euo pipefail

readonly STRUCPP_COMMIT="80481d1c4c14c58da3a08f2fa00e7990f20a35ce"
readonly SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
readonly STRUCPP_DIR="${SCRIPT_DIR}/external/STruCpp"
readonly TOOLS_DIR="${SCRIPT_DIR}/RetroPLC.LanguageServerHost/Tools"

for command in git node npm; do
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
        echo "Unsupported build platform: ${platform} ${architecture}" >&2
        exit 1
        ;;
esac

compiler_dir="${TOOLS_DIR}/compiler"
lsp_dir="${TOOLS_DIR}/lsp"
compiler_output="${compiler_dir}/${compiler_name}"
lsp_output="${lsp_dir}/${lsp_name}"

mkdir -p "${compiler_dir}" "${lsp_dir}"

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

echo "STruC++ tools are ready:"
echo "  ${compiler_output}"
echo "  ${lsp_output}"

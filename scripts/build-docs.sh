#!/usr/bin/env bash
# Build the tckit.org site for Cloudflare Pages.
#
# Pipeline:
#   1. Install the .NET 8 SDK (dotnet-install.sh) and mkdocs-material.
#   2. Clone TcUnit at a pinned tag.
#   3. Run the C# doc generator (TcKit.Cli) against TcUnit into a staging dir.
#   4. Build the MkDocs site.
#   5. Splice the generated TcUnit docs into the built site under
#      ``examples/tcunit/`` so it is reachable at /examples/tcunit/.
#
# Cloudflare Pages configuration that pairs with this script:
#   Root directory:   (blank, i.e. repo root)
#   Build command:    bash scripts/build-docs.sh
#   Build output:     docs/site
#   PYTHON_VERSION:   3.11

set -euo pipefail

TCUNIT_REF="1.3.1"
TCUNIT_REPO="https://github.com/tcunit/TcUnit.git"
TCUNIT_DIR="/tmp/TcUnit"
TCUNIT_DOCS_DIR="/tmp/tcunit-docs"
DOTNET_DIR="${HOME}/.dotnet"

echo "==> Installing .NET 8 SDK"
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 --install-dir "${DOTNET_DIR}"
export PATH="${DOTNET_DIR}:${PATH}"
export DOTNET_NOLOGO=true DOTNET_CLI_TELEMETRY_OPTOUT=true
# The projects target net8.0-windows; the SDK needs this to restore the
# Windows reference assemblies on Linux. The doc generator itself is pure
# managed code and runs fine here.
export EnableWindowsTargeting=true

echo "==> Installing mkdocs-material"
pip install mkdocs-material

echo "==> Cloning TcUnit @ ${TCUNIT_REF}"
rm -rf "${TCUNIT_DIR}"
git clone --depth 1 --branch "${TCUNIT_REF}" "${TCUNIT_REPO}" "${TCUNIT_DIR}"

echo "==> Generating TcUnit doc tree"
rm -rf "${TCUNIT_DOCS_DIR}"
dotnet run --project dotnet/src/TcKit.Cli -c Release -- generate-docs "${TCUNIT_DIR}" "${TCUNIT_DOCS_DIR}"

echo "==> Building MkDocs site"
cd docs
mkdocs build

echo "==> Splicing TcUnit example into site/examples/tcunit/"
mkdir -p site/examples
cp -r "${TCUNIT_DOCS_DIR}" site/examples/tcunit

echo "==> Done. Output at docs/site/."

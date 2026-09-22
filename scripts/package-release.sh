#!/usr/bin/env bash
set -euo pipefail

# =================================================================
# Document RAG System - Linux Server Release Packaging Script
# =================================================================

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"
DIST_DIR="${ROOT_DIR}/dist"
PAYLOAD_DIR="${DIST_DIR}/payload"
ARCHIVE_NAME="document-rag-system-linux-x64"

echo "========================================================"
echo " Packaging Document RAG System Release for Linux x64     "
echo "========================================================"
echo "Root directory: ${ROOT_DIR}"
echo "Output directory: ${DIST_DIR}"

# 1. Clean previous packaging artifacts
rm -rf "${DIST_DIR}"
mkdir -p "${PAYLOAD_DIR}/webapi"
mkdir -p "${PAYLOAD_DIR}/worker"
mkdir -p "${PAYLOAD_DIR}/data"

# 2. Publish WebApi (Self-Contained Linux x64)
echo ""
echo "--> Publishing DocumentRagSystem.WebApi (linux-x64)..."
dotnet publish "${ROOT_DIR}/src/DocumentRagSystem.WebApi/DocumentRagSystem.WebApi.csproj" \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -o "${PAYLOAD_DIR}/webapi"

# 3. Publish Worker (Self-Contained Linux x64)
echo ""
echo "--> Publishing DocumentRagSystem.Worker (linux-x64)..."
dotnet publish "${ROOT_DIR}/src/DocumentRagSystem.Worker/DocumentRagSystem.Worker.csproj" \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -o "${PAYLOAD_DIR}/worker"

# 4. Copy configuration, scripts, documentation and sample data
echo ""
echo "--> Assembling release payload..."
if [ -d "${ROOT_DIR}/data/Generelt" ]; then
    cp -r "${ROOT_DIR}/data/Generelt" "${PAYLOAD_DIR}/data/"
fi

# Copy release-specific runtime files
cp "${ROOT_DIR}/scripts/release-templates/docker-compose.yml" "${PAYLOAD_DIR}/docker-compose.yml"
cp "${ROOT_DIR}/scripts/release-templates/Dockerfile.webapi" "${PAYLOAD_DIR}/Dockerfile.webapi"
cp "${ROOT_DIR}/scripts/release-templates/Dockerfile.worker" "${PAYLOAD_DIR}/Dockerfile.worker"
cp "${ROOT_DIR}/scripts/release-templates/document-rag-webapi.service" "${PAYLOAD_DIR}/"
cp "${ROOT_DIR}/scripts/release-templates/document-rag-worker.service" "${PAYLOAD_DIR}/"
cp "${ROOT_DIR}/.env.example" "${PAYLOAD_DIR}/"
cp "${ROOT_DIR}/deploy.sh" "${PAYLOAD_DIR}/"
cp "${ROOT_DIR}/README.md" "${PAYLOAD_DIR}/"

if [ -d "${ROOT_DIR}/docs" ]; then
    cp -r "${ROOT_DIR}/docs" "${PAYLOAD_DIR}/docs"
fi

# Ensure executable permissions on binaries and scripts
chmod +x "${PAYLOAD_DIR}/deploy.sh" || true
chmod +x "${PAYLOAD_DIR}/webapi/DocumentRagSystem.WebApi" || true
chmod +x "${PAYLOAD_DIR}/worker/DocumentRagSystem.Worker" || true

# 5. Create Tarball (.tar.gz)
echo ""
echo "--> Creating release archive (${ARCHIVE_NAME}.tar.gz)..."
tar -czvf "${DIST_DIR}/${ARCHIVE_NAME}.tar.gz" -C "${PAYLOAD_DIR}" .

# 6. Generate SHA256 checksum
cd "${DIST_DIR}"
if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "${ARCHIVE_NAME}.tar.gz" > "${ARCHIVE_NAME}.tar.gz.sha256"
elif command -v shasum >/dev/null 2>&1; then
    shasum -a 256 "${ARCHIVE_NAME}.tar.gz" > "${ARCHIVE_NAME}.tar.gz.sha256"
fi

echo ""
echo "========================================================"
echo " Release Package Created Successfully!                  "
echo " Location: ${DIST_DIR}/${ARCHIVE_NAME}.tar.gz           "
echo " Size    : $(du -h "${DIST_DIR}/${ARCHIVE_NAME}.tar.gz" 2>/dev/null | cut -f1 || echo 'N/A') "
echo "========================================================"

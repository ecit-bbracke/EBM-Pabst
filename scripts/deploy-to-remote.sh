#!/usr/bin/env bash
set -euo pipefail

# =================================================================
# Document RAG System - Automated 1-Command Remote Deploy
# =================================================================

HOST="${1:-185.158.62.152}"
PORT="${2:-5665}"
USER="${3:-ubuntu}"
REMOTE_DIR="${4:-/opt/document-rag-system}"
KEY_FILE="${5:-}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"
ARCHIVE_NAME="document-rag-system-linux-x64.tar.gz"
LOCAL_ARCHIVE="${ROOT_DIR}/dist/${ARCHIVE_NAME}"

echo "========================================================"
echo " Automated Document RAG System Remote Deployment        "
echo " Target: ${USER}@${HOST}:${PORT} -> ${REMOTE_DIR}       "
echo "========================================================"

# 1. Package release
echo ""
echo "[1/4] Packaging release..."
bash "${SCRIPT_DIR}/package-release.sh"

SSH_OPTS=("-p" "${PORT}")
SCP_OPTS=("-P" "${PORT}")

if [ -n "${KEY_FILE}" ]; then
    SSH_OPTS+=("-i" "${KEY_FILE}")
    SCP_OPTS+=("-i" "${KEY_FILE}")
fi

# 2. Upload archive
echo ""
echo "[2/4] Uploading release bundle via SCP..."
scp "${SCP_OPTS[@]}" "${LOCAL_ARCHIVE}" "${USER}@${HOST}:/tmp/${ARCHIVE_NAME}"

# 3. Unpack and Deploy remotely
echo ""
echo "[3/4] Extracting and deploying remotely over SSH..."
ssh "${SSH_OPTS[@]}" "${USER}@${HOST}" bash -s <<EOF
set -e
echo '--> Ensuring target directory exists...'
sudo mkdir -p ${REMOTE_DIR}
sudo chown -R ${USER}:${USER} ${REMOTE_DIR}

echo '--> Unpacking release...'
tar -xzvf /tmp/${ARCHIVE_NAME} -C ${REMOTE_DIR}

cd ${REMOTE_DIR}
chmod +x deploy.sh webapi/DocumentRagSystem.WebApi worker/DocumentRagSystem.Worker 2>/dev/null || true

echo '--> Executing deploy.sh...'
./deploy.sh

rm -f /tmp/${ARCHIVE_NAME}
EOF

# 4. Status
echo ""
echo "[4/4] Verifying container status..."
ssh "${SSH_OPTS[@]}" "${USER}@${HOST}" "docker compose -f ${REMOTE_DIR}/docker-compose.yml ps"

echo ""
echo "========================================================"
echo " Deployment Complete!                                   "
echo " Web UI: http://${HOST}:8080/index.html                 "
echo "========================================================"

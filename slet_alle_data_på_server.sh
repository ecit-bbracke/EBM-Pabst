#!/usr/bin/env bash
set -euo pipefail

# =================================================================
# Document RAG System - Nulstil alle data og frigør diskplads
# =================================================================

REMOTE_DIR="${1:-/opt/document-rag-system}"

echo "========================================================"
echo " ADVARSEL: Nulstilling af alle data på serveren"
echo " Mappe: $REMOTE_DIR"
echo "========================================================"
echo "Dette vil:"
echo "  - Slette alle uploadede PDF-dokumenter"
echo "  - Tømme Qdrant vektordatabasen fuldstændigt"
echo "  - Nulstille SQLite databasen"
echo "  - Frigøre diskplads via docker system prune -af"
echo "  - Tømme store Docker container logfiler"
echo "  - Genstarte containerne i ren tilstand"
echo "========================================================"
echo ""

read -rp "Er du sikker på, at du vil fortsætte? Skriv 'JA' for at bekræfte: " CONFIRMATION
if [[ "$CONFIRMATION" != "JA" ]]; then
    echo "Handlingen blev afbrudt. Ingen ændringer foretaget."
    exit 0
fi

echo ""
echo "=== DISKPLADS FØR OPRYDNING ==="
df -h /

echo ""
echo "=== STOPPER CONTAINERE OG SLETTER VOLUMES ==="
cd "$REMOTE_DIR"
docker compose down -v

echo ""
echo "=== FRIGØR DOCKER DISKPLADS (PRUNE IMAGES OG CACHE) ==="
docker system prune -af

echo ""
echo "=== TØMMER DOCKER LOGFILER ==="
sudo sh -c 'truncate -s 0 /var/lib/docker/containers/*/*-json.log' 2>/dev/null || true

echo ""
echo "=== DISKPLADS EFTER OPRYDNING ==="
df -h /

echo ""
echo "=== STARTER SYSTEMET I REN TILSTAND ==="
docker compose up -d

echo ""
echo "=== CONTAINER STATUS ==="
docker compose ps

echo ""
echo "========================================================"
echo " Oprydning fuldført! Serveren har nu frigjort plads."
echo "========================================================"

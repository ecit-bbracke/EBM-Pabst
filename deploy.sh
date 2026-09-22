#!/usr/bin/env bash
set -euo pipefail

# =================================================================
# Document RAG System - Linux Server Deployment Script
# =================================================================

PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "${PROJECT_DIR}"

echo "========================================================"
echo " Starting Document RAG System Deployment (Linux Server)  "
echo "========================================================"

# 1. Check Docker & Docker Compose installation
if ! command -v docker >/dev/null 2>&1; then
    echo "ERROR: Docker is not installed. Please install Docker first." >&2
    exit 1
fi

if ! docker compose version >/dev/null 2>&1; then
    echo "ERROR: Docker Compose V2 is not available. Please install docker-compose-plugin." >&2
    exit 1
fi

# 2. Check for .env configuration file
if [ ! -f .env ]; then
    if [ -f .env.example ]; then
        echo "WARNING: .env file not found. Creating from .env.example..."
        cp .env.example .env
        echo "Please edit .env and set your GEMINI_API_KEY before running this script."
        exit 1
    else
        echo "ERROR: Neither .env nor .env.example was found." >&2
        exit 1
    fi
fi

# 3. Create required runtime directories and set binary permissions
mkdir -p uploads
mkdir -p data/Generelt

# Ensure executables have proper execute permissions
if [ -f "webapi/DocumentRagSystem.WebApi" ]; then
    chmod +x webapi/DocumentRagSystem.WebApi || true
fi
if [ -f "worker/DocumentRagSystem.Worker" ]; then
    chmod +x worker/DocumentRagSystem.Worker || true
fi

# 4. Build and start containers in detached mode
echo "Building and starting self-contained Linux Docker containers..."
docker compose up --build -d --remove-orphans

# 5. Wait for health / status check
echo "Waiting for services to become healthy..."
sleep 5

docker compose ps

echo "========================================================"
echo " Deployment Complete!                                   "
echo " Web API: http://localhost:8080                         "
echo " Web UI : http://localhost:8080/index.html              "
echo " Qdrant : http://localhost:6333/dashboard               "
echo " Logs   : docker compose logs -f                        "
echo "========================================================"

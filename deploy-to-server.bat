@echo off
REM =========================================================
REM Document RAG System - Deploy to Linux Server
REM Host: 185.158.62.152 | Port: 5665 | User: ubuntu
REM =========================================================

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\deploy-to-remote.ps1" -HostName "185.158.62.152" -Port 5665 -User "ubuntu" -RemoteDir "/opt/document-rag-system" %*

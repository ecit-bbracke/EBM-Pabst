<#
.SYNOPSIS
    Fjerner alle uploadede dokumenter, tømmer Qdrant og SQLite databaserne og rydder op i Docker på produktionsserveren for at frigøre diskplads.
.DESCRIPTION
    Forbinder til Linux-serveren via SSH og udfører:
    1. docker compose down -v (sletter volumes: uploads_data, qdrant_data, database_data)
    2. docker system prune -af (fjerner ubrugte images og build cache for at frigøre maksimal diskplads)
    3. Tømmer store Docker container logfiler
    4. Tjekker fri diskplads (df -h /)
    5. docker compose up -d (starter et helt frisk system op)
#>

[CmdletBinding()]
param (
    [string]$HostName = "185.158.62.152",
    [int]$Port = 5665,
    [string]$User = "ubuntu",
    [string]$RemoteDir = "/opt/document-rag-system",
    [string]$KeyFile = "",
    [switch]$Force
)

$ErrorActionPreference = "Stop"

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

Write-Host "========================================================" -ForegroundColor Red
Write-Host " ADVARSEL: Nulstilling af alle data på serveren         " -ForegroundColor Yellow
Write-Host " Server: $User@$HostName`:$Port ($RemoteDir)            " -ForegroundColor Yellow
Write-Host "========================================================" -ForegroundColor Red
Write-Host "Dette vil:" -ForegroundColor White
Write-Host "  - Slette alle uploadede PDF-dokumenter" -ForegroundColor Gray
Write-Host "  - Tømme Qdrant vektordatabasen fuldstændigt" -ForegroundColor Gray
Write-Host "  - Nulstille SQLite databasen (opretter frisk admin bruger)" -ForegroundColor Gray
Write-Host "  - Frigøre diskplads ved at rense Docker build cache og gamle images" -ForegroundColor Gray
Write-Host "  - Genstarte containerne i en ren tilstand" -ForegroundColor Gray
Write-Host ""

if (-not $Force) {
    $confirmation = Read-Host "Er du sikker på, at du vil slette ALT og frigøre plads? Skriv 'JA' for at bekræfte"
    if ($confirmation -ne "JA") {
        Write-Host "Handlingen blev afbrudt. Ingen ændringer foretaget." -ForegroundColor Green
        exit 0
    }
}

$sshTarget = "$User@$HostName"

Write-Host "`n--> Forbinder til serveren og udfører oprydning..." -ForegroundColor Cyan

$remoteCommands = @"
set -e
echo '=== DISKPLADS FOER OPRYDNING ==='
df -h /

echo ''
echo '=== STOPPER CONTAINERE OG SLETTER VOLUMES ==='
cd $RemoteDir
docker compose down -v

echo ''
echo '=== FRIGOER DOCKER DISKPLADS (PRUNE IMAGES OG CACHE) ==='
docker system prune -af

echo ''
echo '=== TOEMMER DOCKER LOGFILER ==='
sudo sh -c 'truncate -s 0 /var/lib/docker/containers/*/*-json.log' 2>/dev/null || true

echo ''
echo '=== DISKPLADS EFTER OPRYDNING ==='
df -h /

echo ''
echo '=== STARTER SYSTEMET I REN TILSTAND ==='
docker compose up -d

echo ''
echo '=== CONTAINER STATUS ==='
docker compose ps
"@

$remoteScript = $remoteCommands.Replace("`r`n", "`n")
$bytes = [System.Text.Encoding]::UTF8.GetBytes($remoteScript)
$base64 = [Convert]::ToBase64String($bytes)
$cmd = "echo $base64 | base64 -d | bash"

$sshArgs = @("-p", $Port)
if ($KeyFile -and (Test-Path $KeyFile)) {
    $sshArgs += @("-i", $KeyFile)
}
$sshArgs += @($sshTarget, $cmd)

& ssh @sshArgs

if ($LASTEXITCODE -ne 0) {
    Write-Error "Der opstod en fejl under udførelsen af oprydningskommandoerne på serveren."
}

Write-Host "`n========================================================" -ForegroundColor Green
Write-Host " Oprydning og nulstilling er fuldført!                 " -ForegroundColor Green
Write-Host " Systemet kører nu med tomme databaser og frisk plads.  " -ForegroundColor Green
Write-Host " Web UI: http://$HostName`:8080/index.html              " -ForegroundColor Green
Write-Host "========================================================" -ForegroundColor Cyan

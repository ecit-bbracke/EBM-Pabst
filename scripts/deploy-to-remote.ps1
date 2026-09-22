<#
.SYNOPSIS
    One-command automated deployment script to Linux server.
.DESCRIPTION
    Packages the release bundle, transfers it via SCP, extracts it on the remote
    host while preserving existing .env secrets, and restarts Docker containers.
.EXAMPLE
    .\scripts\deploy-to-remote.ps1 -HostName "185.158.62.152" -Port 5665 -User "ubuntu"
#>

[CmdletBinding()]
param (
    [string]$HostName = "185.158.62.152",
    [int]$Port = 5665,
    [string]$User = "ubuntu",
    [string]$RemoteDir = "/opt/document-rag-system",
    [string]$KeyFile = "",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$RootDir = (Resolve-Path "$PSScriptRoot\..").Path
$ArchiveName = "document-rag-system-linux-x64.tar.gz"
$LocalArchive = Join-Path $RootDir "dist\$ArchiveName"

Write-Host "========================================================" -ForegroundColor Cyan
Write-Host " Automated Document RAG System Remote Deployment        " -ForegroundColor Cyan
Write-Host " Target Server: $User@$HostName`:$Port -> $RemoteDir" -ForegroundColor Cyan
Write-Host "========================================================" -ForegroundColor Cyan

# 1. Package release if needed
if (-not $SkipBuild -or -not (Test-Path $LocalArchive)) {
    Write-Host "`n[1/4] Packaging Linux release..." -ForegroundColor Yellow
    & "$PSScriptRoot\package-release.ps1"
} else {
    Write-Host "`n[1/4] Using existing release package ($LocalArchive)..." -ForegroundColor Yellow
}

if (-not (Test-Path $LocalArchive)) {
    Write-Error "Release package not found at: $LocalArchive"
    exit 1
}

# 2. Prepare SSH & SCP parameters
$KeyParam = if ($KeyFile) { "-i `"$KeyFile`"" } else { "" }

# 3. Transfer package via SCP
Write-Host "`n[2/4] Uploading release archive to $HostName..." -ForegroundColor Yellow
$ScpCmd = "scp -P $Port $KeyParam `"$LocalArchive`" $User@$HostName`:/tmp/$ArchiveName"
Write-Host "Executing: $ScpCmd" -ForegroundColor DarkGray
Invoke-Expression $ScpCmd

# 4. Extract and Deploy on remote server via SSH
Write-Host "`n[3/4] Extracting payload and restarting containers on remote server..." -ForegroundColor Yellow

$RemoteCommands = @"
set -e
echo '--> Creating remote directory if missing...'
sudo mkdir -p $RemoteDir
sudo chown -R $User:$User $RemoteDir

echo '--> Extracting new release bundle...'
tar -xzvf /tmp/$ArchiveName -C $RemoteDir

cd $RemoteDir
chmod +x deploy.sh webapi/DocumentRagSystem.WebApi worker/DocumentRagSystem.Worker 2>/dev/null || true

echo '--> Executing deploy.sh...'
./deploy.sh

echo '--> Cleaning up uploaded archive...'
rm -f /tmp/$ArchiveName
"@

$EscapedCommands = $RemoteCommands.Replace("`r`n", "`n")
$SshCmd = "ssh -p $Port $KeyParam $User@$HostName `"$EscapedCommands`""
Write-Host "Executing deployment over SSH..." -ForegroundColor DarkGray
Invoke-Expression $SshCmd

Write-Host "`n[4/4] Verifying remote service status..." -ForegroundColor Yellow
$VerifyCmd = "ssh -p $Port $KeyParam $User@$HostName `"docker compose -f $RemoteDir/docker-compose.yml ps`""
Invoke-Expression $VerifyCmd

Write-Host "`n========================================================" -ForegroundColor Cyan
Write-Host " Deployment to $HostName completed successfully!         " -ForegroundColor Green
Write-Host " Web UI: http://$HostName`:8080/index.html              " -ForegroundColor Green
Write-Host "========================================================" -ForegroundColor Cyan

<#
.SYNOPSIS
    Packages the Document RAG System for Linux x64 server deployment.
.DESCRIPTION
    Builds self-contained Linux x64 binaries for WebApi and Worker,
    gathers configuration, scripts, documentation and sample data,
    and creates a .tar.gz and .zip archive with SHA256 checksums.
#>

[CmdletBinding()]
param (
    [string]$Configuration = "Release",
    [string]$Runtime = "linux-x64"
)

$ErrorActionPreference = "Stop"

$RootDir = (Resolve-Path "$PSScriptRoot\..").Path
$DistDir = Join-Path $RootDir "dist"
$PayloadDir = Join-Path $DistDir "payload"
$ArchiveName = "document-rag-system-linux-x64"

Write-Host "========================================================" -ForegroundColor Cyan
Write-Host " Packaging Document RAG System Release for Linux ($Runtime)" -ForegroundColor Cyan
Write-Host "========================================================" -ForegroundColor Cyan
Write-Host "Root directory  : $RootDir"
Write-Host "Output directory: $DistDir"

# 1. Clean previous packaging artifacts
if (Test-Path $DistDir) {
    Remove-Item -Path $DistDir -Recurse -Force
}
New-Item -ItemType Directory -Path "$PayloadDir\webapi" -Force | Out-Null
New-Item -ItemType Directory -Path "$PayloadDir\worker" -Force | Out-Null
New-Item -ItemType Directory -Path "$PayloadDir\data" -Force | Out-Null

# 2. Publish WebApi (Self-Contained Linux x64)
Write-Host "`n--> Publishing DocumentRagSystem.WebApi ($Runtime)..." -ForegroundColor Yellow
dotnet publish "$RootDir\src\DocumentRagSystem.WebApi\DocumentRagSystem.WebApi.csproj" `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -o "$PayloadDir\webapi"

# 3. Publish Worker (Self-Contained Linux x64)
Write-Host "`n--> Publishing DocumentRagSystem.Worker ($Runtime)..." -ForegroundColor Yellow
dotnet publish "$RootDir\src\DocumentRagSystem.Worker\DocumentRagSystem.Worker.csproj" `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -o "$PayloadDir\worker"

# 4. Copy configuration, scripts, documentation, and data
Write-Host "`n--> Assembling release payload..." -ForegroundColor Yellow
if (Test-Path "$RootDir\data\Generelt") {
    Copy-Item -Path "$RootDir\data\Generelt" -Destination "$PayloadDir\data\" -Recurse -Force
}

# Copy release-specific runtime files
Copy-Item -Path "$RootDir\scripts\release-templates\docker-compose.yml" -Destination "$PayloadDir\docker-compose.yml" -Force
Copy-Item -Path "$RootDir\scripts\release-templates\Dockerfile.webapi" -Destination "$PayloadDir\Dockerfile.webapi" -Force
Copy-Item -Path "$RootDir\scripts\release-templates\Dockerfile.worker" -Destination "$PayloadDir\Dockerfile.worker" -Force
Copy-Item -Path "$RootDir\scripts\release-templates\document-rag-webapi.service" -Destination "$PayloadDir\" -Force
Copy-Item -Path "$RootDir\scripts\release-templates\document-rag-worker.service" -Destination "$PayloadDir\" -Force
Copy-Item -Path "$RootDir\.env.example" -Destination "$PayloadDir\" -Force
Copy-Item -Path "$RootDir\deploy.sh" -Destination "$PayloadDir\" -Force
Copy-Item -Path "$RootDir\README.md" -Destination "$PayloadDir\" -Force

if (Test-Path "$RootDir\docs") {
    Copy-Item -Path "$RootDir\docs" -Destination "$PayloadDir\docs" -Recurse -Force
}

# 5. Create Archives
Write-Host "`n--> Creating release archives..." -ForegroundColor Yellow
$TarGzPath = Join-Path $DistDir "$ArchiveName.tar.gz"
$ZipPath = Join-Path $DistDir "$ArchiveName.zip"

# Create .tar.gz using tar CLI
Push-Location $PayloadDir
try {
    tar.exe -czf "..\$ArchiveName.tar.gz" .
    Write-Host "Created: $TarGzPath" -ForegroundColor Green
}
catch {
    Write-Warning "tar.exe failed or unavailable: $_"
}
finally {
    Pop-Location
}

# Create .zip
Compress-Archive -Path "$PayloadDir\*" -DestinationPath "$ZipPath" -Force
Write-Host "Created: $ZipPath" -ForegroundColor Green

# 6. Generate SHA256 Checksums
Write-Host "`n--> Generating SHA256 checksums..." -ForegroundColor Yellow
$ChecksumFile = Join-Path $DistDir "SHA256SUMS.txt"
$Checksums = @()

if (Test-Path $TarGzPath) {
    $hashTar = (Get-FileHash -Path $TarGzPath -Algorithm SHA256).Hash.ToLower()
    $Checksums += "$hashTar  $ArchiveName.tar.gz"
}

if (Test-Path $ZipPath) {
    $hashZip = (Get-FileHash -Path $ZipPath -Algorithm SHA256).Hash.ToLower()
    $Checksums += "$hashZip  $ArchiveName.zip"
}

$Checksums | Out-File -FilePath $ChecksumFile -Encoding utf8
Get-Content $ChecksumFile | Write-Host -ForegroundColor Gray

Write-Host "`n========================================================" -ForegroundColor Cyan
Write-Host " Release Package Created Successfully!" -ForegroundColor Green
Write-Host " Artifacts available in: $DistDir" -ForegroundColor Green
Write-Host "========================================================" -ForegroundColor Cyan

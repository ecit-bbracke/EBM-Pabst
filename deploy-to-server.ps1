<#
.SYNOPSIS
    One-click deployment script to the Document RAG Linux Server.
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

& "$PSScriptRoot\scripts\deploy-to-remote.ps1" `
    -HostName $HostName `
    -Port $Port `
    -User $User `
    -RemoteDir $RemoteDir `
    -KeyFile $KeyFile `
    -SkipBuild:$SkipBuild

#Requires -Version 7.0
<#
.SYNOPSIS
    Starts the SmtpGateway Windows Service.

.PARAMETER ServiceName
    Windows service name to start. Defaults to "SmtpGateway".

.EXAMPLE
    ./start-service.ps1
#>
[CmdletBinding()]
param(
    [string]$ServiceName = "SmtpGateway"
)

$ErrorActionPreference = "Stop"

$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "start-service.ps1 must be run from an elevated (Administrator) PowerShell session."
    exit 1
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $service) {
    Write-Error "No service named '$ServiceName' is registered. Run install-service.ps1 first."
    exit 1
}

Start-Service -Name $ServiceName

Write-Host "Service '$ServiceName' started."

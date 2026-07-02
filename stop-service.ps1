#Requires -Version 7.0
<#
.SYNOPSIS
    Stops the SmtpGateway Windows Service.

.PARAMETER ServiceName
    Windows service name to stop. Defaults to "SmtpGateway".

.EXAMPLE
    ./stop-service.ps1
#>
[CmdletBinding()]
param(
    [string]$ServiceName = "SmtpGateway"
)

$ErrorActionPreference = "Stop"

$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "stop-service.ps1 must be run from an elevated (Administrator) PowerShell session."
    exit 1
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $service) {
    Write-Error "No service named '$ServiceName' is registered."
    exit 1
}

Stop-Service -Name $ServiceName -Force

Write-Host "Service '$ServiceName' stopped."

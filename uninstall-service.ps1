#Requires -Version 7.0
<#
.SYNOPSIS
    Unregisters the SmtpGateway Windows Service.

.DESCRIPTION
    Stops the service if running, then removes its registration via Remove-Service.

.PARAMETER ServiceName
    Windows service name to unregister. Defaults to "SmtpGateway".

.EXAMPLE
    ./uninstall-service.ps1
#>
[CmdletBinding()]
param(
    [string]$ServiceName = "SmtpGateway"
)

$ErrorActionPreference = "Stop"

$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "uninstall-service.ps1 must be run from an elevated (Administrator) PowerShell session."
    exit 1
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $service) {
    Write-Error "No service named '$ServiceName' is registered."
    exit 1
}

if ($service.Status -ne "Stopped") {
    Stop-Service -Name $ServiceName -Force -Confirm:$false
}

Remove-Service -Name $ServiceName

Write-Host "Service '$ServiceName' unregistered."

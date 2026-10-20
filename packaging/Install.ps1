<#
.SYNOPSIS
  Installs Revit Model Importer for the current user.

.DESCRIPTION
  Copies RevitModelImporter.addin and the RevitModelImporter folder (add-in + converter) into
  %AppData%\Autodesk\Revit\Addins\<year>\. No administrator rights are needed. Run it from the extracted zip.
  Restart Revit afterwards.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\Install.ps1
  powershell -ExecutionPolicy Bypass -File .\Install.ps1 -RevitVersion 2026
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('2025', '2026', '2027')]
    [string] $RevitVersion,

    # Override for testing; defaults to %AppData%\Autodesk\Revit\Addins.
    [string] $AddinsRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$source = $PSScriptRoot
$marker = Join-Path $source 'revit-version.txt'
$packageVersion = if (Test-Path $marker) { (Get-Content $marker -Raw).Trim() } else { $null }
if (-not $RevitVersion) {
    if (-not $packageVersion) { throw 'Pass -RevitVersion 2025, 2026 or 2027 (revit-version.txt was not found next to this script).' }
    $RevitVersion = $packageVersion
}
elseif ($packageVersion -and $packageVersion -ne $RevitVersion) {
    throw "This package is built for Revit $packageVersion. Download the Revit $RevitVersion package instead."
}

if (-not $AddinsRoot) {
    if (-not $env:APPDATA) { throw 'APPDATA is not set; pass -AddinsRoot.' }
    $AddinsRoot = Join-Path $env:APPDATA 'Autodesk\Revit\Addins'
}

$manifest = Join-Path $source 'RevitModelImporter.addin'
$payload = Join-Path $source 'RevitModelImporter'
if (-not (Test-Path $manifest) -or -not (Test-Path (Join-Path $payload 'RevitModelImporter.Revit.dll'))) {
    throw "Run this script from the extracted package folder (RevitModelImporter.addin and the RevitModelImporter folder must be next to it)."
}

$target = Join-Path $AddinsRoot $RevitVersion
$targetFolder = Join-Path $target 'RevitModelImporter'

if (Get-Process -Name 'Revit' -ErrorAction SilentlyContinue) {
    Write-Warning 'Revit is running. If it has the add-in loaded, close Revit first or the files may be locked; restart Revit after installing.'
}

if ($PSCmdlet.ShouldProcess($target, "Install Revit Model Importer for Revit $RevitVersion")) {
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    if (Test-Path $targetFolder) {
        # Replace a previous version completely so no stale files remain.
        Remove-Item $targetFolder -Recurse -Force
    }

    Copy-Item $manifest $target -Force
    Copy-Item $payload $target -Recurse -Force

    # Downloaded zips mark every file as coming from the internet; clear that so Windows does not block the
    # converter executable or the add-in assemblies.
    $onWindows = [System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT
    if ($onWindows -and (Get-Command Unblock-File -ErrorAction SilentlyContinue)) {
        Get-ChildItem $targetFolder -Recurse -File | Unblock-File
        Unblock-File (Join-Path $target 'RevitModelImporter.addin')
    }

    Write-Host "Revit Model Importer installed for Revit $RevitVersion in $target" -ForegroundColor Green
    Write-Host 'Start Revit; the "Model Importer" tab appears on the ribbon. If Revit asks whether to load the add-in, choose "Always Load".'
}

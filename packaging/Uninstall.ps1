<#
.SYNOPSIS
  Removes Revit Model Importer for the current user.

.DESCRIPTION
  Deletes RevitModelImporter.addin and the RevitModelImporter folder from %AppData%\Autodesk\Revit\Addins\<year>\
  for the given (default: all supported) Revit versions. Texture folders next to projects are never touched:
  projects reference them. Use -RemoveSettings to also delete %AppData%\RevitModelImporter (settings) and
  -RemoveTextureCache to delete %LocalAppData%\RevitModelImporter (textures of unsaved projects).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\Uninstall.ps1
  powershell -ExecutionPolicy Bypass -File .\Uninstall.ps1 -RevitVersion 2026 -RemoveSettings
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('2025', '2026', '2027')]
    [string[]] $RevitVersion = @('2025', '2026', '2027'),

    [string] $AddinsRoot,

    [switch] $RemoveSettings,

    [switch] $RemoveTextureCache
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $AddinsRoot) {
    if (-not $env:APPDATA) { throw 'APPDATA is not set; pass -AddinsRoot.' }
    $AddinsRoot = Join-Path $env:APPDATA 'Autodesk\Revit\Addins'
}

if (Get-Process -Name 'Revit' -ErrorAction SilentlyContinue) {
    Write-Warning 'Revit is running; close it first if the add-in is loaded, otherwise its files are locked.'
}

$removed = 0
foreach ($year in $RevitVersion) {
    $target = Join-Path $AddinsRoot $year
    foreach ($item in (Join-Path $target 'RevitModelImporter.addin'), (Join-Path $target 'RevitModelImporter')) {
        if ((Test-Path $item) -and $PSCmdlet.ShouldProcess($item, 'Remove')) {
            Remove-Item $item -Recurse -Force
            $removed++
        }
    }
}

if ($RemoveSettings -and $env:APPDATA) {
    $settings = Join-Path $env:APPDATA 'RevitModelImporter'
    if ((Test-Path $settings) -and $PSCmdlet.ShouldProcess($settings, 'Remove settings')) { Remove-Item $settings -Recurse -Force }
}

if ($RemoveTextureCache -and $env:LOCALAPPDATA) {
    $cache = Join-Path $env:LOCALAPPDATA 'RevitModelImporter'
    if ((Test-Path $cache) -and $PSCmdlet.ShouldProcess($cache, 'Remove texture cache')) { Remove-Item $cache -Recurse -Force }
}

if ($removed -eq 0) {
    Write-Host 'Revit Model Importer was not installed for the selected Revit versions.'
} else {
    Write-Host "Revit Model Importer removed ($removed item(s)). Restart Revit." -ForegroundColor Green
}

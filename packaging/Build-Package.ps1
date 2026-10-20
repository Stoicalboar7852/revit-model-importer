<#
.SYNOPSIS
  Builds the Revit Model Importer packages: one zip per Revit version.

.DESCRIPTION
  For each Revit version: builds the add-in against that year's Revit API reference assemblies, publishes the
  worker (framework-dependent, win-x64, RollForward=LatestMajor), and lays out

    RevitModelImporter-<version>-Revit<year>.zip
      RevitModelImporter.addin
      RevitModelImporter\                       add-in (RevitModelImporter.Revit.dll + RevitModelImporter.Core.dll)
      RevitModelImporter\Worker\                converter process (+ native Assimp)
      Install.ps1, Uninstall.ps1, README.md, LICENSE, THIRD_PARTY_NOTICES.md, revit-version.txt

  Fails if any Autodesk assembly ended up in a package.

.EXAMPLE
  ./packaging/Build-Package.ps1
  ./packaging/Build-Package.ps1 -RevitVersion 2026 -Configuration Debug
#>
[CmdletBinding()]
param(
    [ValidateSet('2025', '2026', '2027')]
    [string[]] $RevitVersion = @('2025', '2026', '2027'),

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [string] $OutputDirectory,

    [switch] $SkipZip
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'dist' }
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
[xml] $props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

# A simple function (no param block) so switches like -c and -o reach dotnet untouched.
function Invoke-DotNet {
    Write-Host "dotnet $($args -join ' ')" -ForegroundColor DarkGray
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($args[0]) failed with exit code $LASTEXITCODE" }
}

if (Test-Path $OutputDirectory) { Remove-Item $OutputDirectory -Recurse -Force }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null

# The worker is identical for every Revit version (net8.0 + RollForward=LatestMajor runs on .NET 8 and .NET 10).
$workerOut = Join-Path $OutputDirectory 'staging/Worker'
Invoke-DotNet publish (Join-Path $root 'src/RevitModelImporter.Worker/RevitModelImporter.Worker.csproj') `
    -c $Configuration -r win-x64 --self-contained false "-p:PublishSingleFile=false" -o $workerOut

foreach ($year in $RevitVersion) {
    $tfm = if ($year -eq '2027') { 'net10.0-windows' } else { 'net8.0-windows' }
    Write-Host "`n=== Revit $year ($tfm) ===" -ForegroundColor Cyan

    Invoke-DotNet build (Join-Path $root 'src/RevitModelImporter.Revit/RevitModelImporter.Revit.csproj') `
        -c $Configuration -f $tfm "-p:RevitVersion=$year"

    $built = Join-Path $root "src/RevitModelImporter.Revit/bin/$Configuration/R$year/$tfm"
    $stage = Join-Path $OutputDirectory "Revit$year"
    $addinDir = Join-Path $stage 'RevitModelImporter'
    New-Item -ItemType Directory -Path $addinDir -Force | Out-Null

    foreach ($name in 'RevitModelImporter.Revit', 'RevitModelImporter.Core') {
        foreach ($ext in '.dll', '.pdb') {
            $file = Join-Path $built "$name$ext"
            if (Test-Path $file) { Copy-Item $file $addinDir }
            elseif ($ext -eq '.dll') { throw "Missing build output: $file" }
        }
    }

    Copy-Item $workerOut (Join-Path $addinDir 'Worker') -Recurse
    Copy-Item (Join-Path $root "packaging/manifests/Revit$year/RevitModelImporter.addin") $stage
    Copy-Item (Join-Path $root 'packaging/Install.ps1'), (Join-Path $root 'packaging/Uninstall.ps1') $stage
    foreach ($doc in 'README.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md') {
        $path = Join-Path $root $doc
        if (Test-Path $path) { Copy-Item $path $stage }
    }
    Set-Content -Path (Join-Path $stage 'revit-version.txt') -Value $year -NoNewline

    # Never ship Autodesk binaries.
    $autodesk = Get-ChildItem $stage -Recurse -File | Where-Object { $_.Name -match '^(RevitAPI.*|AdWindows|UIFramework.*|RevitNET)\.dll$' }
    if ($autodesk) { throw "Autodesk assemblies found in the package: $($autodesk.FullName -join ', ')" }

    $required = @(
        'RevitModelImporter.addin',
        'RevitModelImporter/RevitModelImporter.Revit.dll',
        'RevitModelImporter/RevitModelImporter.Core.dll',
        'RevitModelImporter/Worker/RevitModelImporter.Worker.dll',
        'RevitModelImporter/Worker/RevitModelImporter.Worker.exe',
        'RevitModelImporter/Worker/RevitModelImporter.Worker.runtimeconfig.json',
        'RevitModelImporter/Worker/assimp.dll'
    )
    foreach ($r in $required) {
        if (-not (Test-Path (Join-Path $stage $r))) { throw "Package for Revit $year is missing $r" }
    }

    if (-not $SkipZip) {
        $zip = Join-Path $OutputDirectory "RevitModelImporter-$version-Revit$year.zip"
        Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
        Write-Host "Created $zip" -ForegroundColor Green
    }
}

Remove-Item (Join-Path $OutputDirectory 'staging') -Recurse -Force

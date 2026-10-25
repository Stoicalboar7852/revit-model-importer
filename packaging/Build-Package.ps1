<#
.SYNOPSIS
  Builds the Revit Model Importer packages: one zip per Revit version and one .exe installer for all of them.

.DESCRIPTION
  For each Revit version: builds the add-in against that year's Revit API reference assemblies, publishes the
  worker (framework-dependent, win-x64, RollForward=LatestMajor), and lays out

    RevitModelImporter-<version>-Revit<year>.zip
      RevitModelImporter.addin
      RevitModelImporter\                       add-in (RevitModelImporter.Revit.dll + RevitModelImporter.Core.dll)
      RevitModelImporter\Worker\                converter process (+ native Assimp)
      INSTALL.txt, Install.ps1, Uninstall.ps1, README.md, LICENSE, THIRD_PARTY_NOTICES.md, revit-version.txt

  Then, when NSIS 3 (makensis) is available, builds RevitModelImporter-<version>-Setup.exe from the same files:
  a per-user installer for every built Revit version (packaging/installer/RevitModelImporter.nsi). -Installer Always
  fails when makensis is missing; Never skips it.

  Fails if any Autodesk assembly ended up in a package.

.EXAMPLE
  ./packaging/Build-Package.ps1
  ./packaging/Build-Package.ps1 -RevitVersion 2026 -Configuration Debug -Installer Never
#>
[CmdletBinding()]
param(
    [ValidateSet('2025', '2026', '2027')]
    [string[]] $RevitVersion = @('2025', '2026', '2027'),

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [string] $OutputDirectory,

    [switch] $SkipZip,

    [ValidateSet('Auto', 'Always', 'Never')]
    [string] $Installer = 'Auto'
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

    # Installation instructions for this Revit year, with Windows line endings so Notepad shows them properly.
    $install = (Get-Content (Join-Path $root 'packaging/INSTALL.txt') -Raw).Replace('{YEAR}', $year).Replace('{VERSION}', $version)
    $install = ($install -replace "`r`n", "`n") -replace "`n", "`r`n"
    [System.IO.File]::WriteAllText((Join-Path $stage 'INSTALL.txt'), $install, [System.Text.UTF8Encoding]::new($false))

    # Never ship Autodesk binaries.
    $autodesk = Get-ChildItem $stage -Recurse -File | Where-Object { $_.Name -match '^(RevitAPI.*|AdWindows|UIFramework.*|RevitNET)\.dll$' }
    if ($autodesk) { throw "Autodesk assemblies found in the package: $($autodesk.FullName -join ', ')" }

    $required = @(
        'INSTALL.txt',
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

function Find-MakeNsis {
    $command = Get-Command makensis -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    foreach ($dir in ${env:ProgramFiles(x86)}, $env:ProgramFiles) {
        if ($dir -and (Test-Path (Join-Path $dir 'NSIS/makensis.exe'))) { return Join-Path $dir 'NSIS/makensis.exe' }
    }
    return $null
}

if ($Installer -ne 'Never') {
    $makensis = Find-MakeNsis
    if (-not $makensis) {
        $hint = 'Install NSIS 3 (https://nsis.sourceforge.io, "choco install nsis" or "apt install nsis").'
        if ($Installer -eq 'Always') { throw "makensis was not found, so the .exe installer cannot be built. $hint" }
        Write-Warning "makensis was not found, so the .exe installer was not built. $hint"
    }
    else {
        $setup = Join-Path $OutputDirectory "RevitModelImporter-$version-Setup.exe"
        $nsisArgs = @('-V2', '-WX', "-DVERSION=$version", "-DSTAGE=$OutputDirectory", "-DROOT=$root", "-DOUTFILE=$setup")
        $nsisArgs += $RevitVersion | ForEach-Object { "-DREVIT$_" }
        # Native separators throughout (GetFullPath turns / into \ on Windows); the script's own paths use \.
        $nsisArgs += [System.IO.Path]::GetFullPath((Join-Path $root 'packaging/installer/RevitModelImporter.nsi'))
        Write-Host "`n=== Installer ===" -ForegroundColor Cyan
        & $makensis @nsisArgs
        if ($LASTEXITCODE -ne 0) { throw "makensis failed with exit code $LASTEXITCODE" }
        Write-Host "Created $setup" -ForegroundColor Green
    }
}

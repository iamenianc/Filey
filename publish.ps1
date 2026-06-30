#Requires -Version 5
<#
.SYNOPSIS
    Publishes Filey as a framework-dependent 64-bit Windows app.

.DESCRIPTION
    Produces bin\publish\win-x64\Filey.exe plus its dependency DLLs.
    This is a .NET Framework 4.8 app, so the build is framework-dependent:
    the target machine needs the .NET Framework 4.8 runtime (present by
    default on Windows 10 1903+ and Windows 11). It is NOT a single
    self-contained exe -- ship the whole publish folder.

    Uses MSBuild when available (most reliable for this classic project
    style) and falls back to `dotnet build`. Either way you need the
    .NET Framework 4.8 targeting pack installed (ships with Visual Studio
    or the "Developer Pack" from https://dotnet.microsoft.com/download).

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -Configuration Debug -OutDir bin\publish\dev
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutDir = 'bin\publish\win-x64'
)

$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'Filey.csproj'

function Get-MSBuild {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $path = & $vswhere -latest -requires Microsoft.Component.MSBuild `
            -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
        if ($path) { return $path }
    }
    $cmd = Get-Command msbuild -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

Write-Host "Publishing Filey ($Configuration|x64) -> $OutDir" -ForegroundColor Cyan

$msbuild = Get-MSBuild
if ($msbuild) {
    Write-Host "Using MSBuild: $msbuild" -ForegroundColor DarkGray
    & $msbuild $proj /restore /nologo /v:m `
        /p:Configuration=$Configuration /p:Platform=x64 `
        /p:OutDir=$OutDir\
}
else {
    Write-Host "MSBuild not found; using 'dotnet build'." -ForegroundColor DarkGray
    dotnet build $proj -c $Configuration -p:Platform=x64 -o $OutDir
}

$exe = Join-Path $OutDir 'Filey.exe'
if (Test-Path $exe) {
    Write-Host "Published: $exe" -ForegroundColor Green
}
else {
    throw "Build completed but $exe was not produced. Check the build output above."
}

#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Restore, build, test (1:1 regression) and publish the VDK Toolkit as a
    single-file, self-contained Windows x64 executable.

.DESCRIPTION
    Pipeline:
      1. dotnet restore   (whole solution)
      2. dotnet build     (-c Release, whole solution)
      3. dotnet test      (runs the byte-exact VDK/CT regression in
                           tests/VDKTool.Tests; this is a plain console exe, so
                           it is invoked with `dotnet run`)
      4. dotnet publish   (src/VDKTool as a single self-contained file)

    The publish step bundles Photino's native DLLs (Photino.Native.dll,
    WebView2Loader.dll) INSIDE the executable via
    IncludeNativeLibrariesForSelfExtract; they are extracted to %TEMP% on first
    run. Output goes to <repo>\publish\win-x64\VDK_Tool.exe.

    On success the final exe path, its size and SHA256 are printed.

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.PARAMETER SkipTests
    Skip the regression test step (not recommended; the test needs the RO2
    client data paths to be present).

.EXAMPLE
    ./build.ps1
.EXAMPLE
    ./build.ps1 -SkipTests
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$RepoRoot   = $PSScriptRoot
$Solution   = Join-Path $RepoRoot 'VDKTool.sln'
$ExeProject = Join-Path $RepoRoot 'src\VDKTool\VDKTool.csproj'
$TestProject= Join-Path $RepoRoot 'tests\VDKTool.Tests\VDKTool.Tests.csproj'
$Runtime    = 'win-x64'
$PublishDir = Join-Path $RepoRoot "publish\$Runtime"

function Invoke-Step {
    param([string]$Title, [scriptblock]$Action)
    Write-Host ''
    Write-Host "==> $Title" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) {
        throw "Step failed (exit $LASTEXITCODE): $Title"
    }
}

Write-Host "VDK Toolkit build" -ForegroundColor Green
Write-Host "Repo:          $RepoRoot"
Write-Host "Configuration: $Configuration"
Write-Host "Runtime:       $Runtime"

# 1. Restore -----------------------------------------------------------------
Invoke-Step 'dotnet restore' { dotnet restore $Solution }

# 2. Build -------------------------------------------------------------------
Invoke-Step "dotnet build -c $Configuration" {
    dotnet build $Solution -c $Configuration --no-restore
}

# 3. Test (1:1 regression) ---------------------------------------------------
# VDKTool.Tests is a console app (not a unit-test runner project), so run it
# with `dotnet run`. A zero exit code means every VDK/CT round-trip was
# byte-identical.
if ($SkipTests) {
    Write-Host ''
    Write-Host '==> SKIPPING regression tests (-SkipTests)' -ForegroundColor Yellow
} else {
    Invoke-Step 'dotnet run (regression: VDKTool.Tests)' {
        dotnet run --project $TestProject -c $Configuration --no-restore
    }
}

# 4. Publish (single-file, self-contained) -----------------------------------
Invoke-Step "dotnet publish ($Runtime, single-file self-contained)" {
    dotnet publish $ExeProject `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -o $PublishDir
}

# Report ---------------------------------------------------------------------
$ExePath = Join-Path $PublishDir 'VDK_Tool.exe'
if (-not (Test-Path $ExePath)) {
    throw "Publish reported success but $ExePath was not produced."
}

$Item   = Get-Item $ExePath
$SizeMB = [math]::Round($Item.Length / 1MB, 2)
$Sha    = (Get-FileHash -Algorithm SHA256 -Path $ExePath).Hash

Write-Host ''
Write-Host '====================================================================' -ForegroundColor Green
Write-Host ' BUILD COMPLETE' -ForegroundColor Green
Write-Host '====================================================================' -ForegroundColor Green
Write-Host "Executable : $ExePath"
Write-Host "Size       : $SizeMB MB ($($Item.Length) bytes)"
Write-Host "SHA256     : $Sha"
Write-Host '--------------------------------------------------------------------'
Write-Host 'Single-file, self-contained: run on Windows x64 without a .NET install.'
Write-Host 'First GUI launch self-extracts native DLLs to %TEMP%.'

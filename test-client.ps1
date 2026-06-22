<#
.SYNOPSIS
  LOCAL non-committed runner: exercises VDK_Tool against the REAL RO2 client,
  round-tripping every VDK archive and every ASSET CT table and checking the
  rebuilt bytes are SHA256-identical to the originals.

.DESCRIPTION
  NON-DESTRUCTIVE: originals are read in place, never written. All work happens
  in a scratch dir (-Out, default <repo>\_clienttest) which is wiped at the end.

  VDK round-trip per file (manifest-free: pure reconstruction from the folder):
    extract <orig.vdk> <scratch\extracted>  (files + empty dirs, no sidecar)
    pack    <scratch\extracted> <scratch\repacked.vdk>
    bytes(repacked) == bytes(orig) IGNORING header u32@8 (offset 8-11) -> identical | diff
    (u32@8 is a packer-specific field the game ignores and is not derivable from
     content; our builder emits 0. Everything else must be byte-identical.)

  CT round-trip per file:
    ct2xlsx <orig.ct> -o <scratch\x.xlsx>
    xlsx2ct <scratch\x.xlsx> <scratch\rebuilt.ct>
    SHA256(rebuilt) == SHA256(orig)  -> identical | diff

  Sources:
    VDK : every *.vdk (recursive) under the client Data dir + ASSET.VDK patch
    CT  : every *.ct (recursive) under RO2-Patches\ASSET_VDK\ASSET

.PARAMETER VdkOnly   Only run the VDK sweep.
.PARAMETER CtOnly    Only run the CT sweep.
.PARAMETER MaxSizeMB Skip source files larger than this many MB (0 = no cap).
                     Applies to both VDK and CT.
.PARAMETER MaxCt     Cap the number of CT files processed (0 = all).
.PARAMETER MaxVdk    Cap the number of VDK files processed (0 = all).
.PARAMETER Out       Scratch directory (created, then wiped). Default _clienttest.
.PARAMETER Exe       Path to VDK_Tool.exe. Auto-detected if omitted; falls back
                     to `dotnet run --project src/VDKTool`.
.PARAMETER KeepTemp  Do not delete the scratch dir at the end (debugging).

.EXAMPLE
  # Quick validation sweep (small files, subset of CT):
  ./test-client.ps1 -MaxSizeMB 20 -MaxCt 60

.EXAMPLE
  # FULL sweep, no caps (slow: includes 1GB+ VDKs, all 207 CT):
  ./test-client.ps1
#>
[CmdletBinding()]
param(
    [switch] $VdkOnly,
    [switch] $CtOnly,
    [double] $MaxSizeMB = 0,
    [int]    $MaxCt      = 0,
    [int]    $MaxVdk     = 0,
    [string] $Out,
    [string] $Exe,
    [switch] $KeepTemp
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot

# --- Fixed client paths -----------------------------------------------------
$ClientData = 'C:\Users\Darkr\Proyectos\ReverseEngineering\ragnarok-online-2\clients\ClientFiles\Ragnarok Online 2\Data'
$AssetVdk   = 'C:\Users\Darkr\Proyectos\ReverseEngineering\ragnarok-online-2\client-patches\RO2-Patches\ASSET.VDK'
$AssetCtDir = 'C:\Users\Darkr\Proyectos\ReverseEngineering\ragnarok-online-2\client-patches\RO2-Patches\ASSET_VDK\ASSET'

if (-not $Out) { $Out = Join-Path $repoRoot '_clienttest' }

# --- Resolve the tool invocation -------------------------------------------
$useDotnet = $false
if (-not $Exe) {
    $cand = Join-Path $repoRoot 'publish\win-x64\VDK_Tool.exe'
    if (Test-Path $cand) { $Exe = $cand }
}
if (-not $Exe -or -not (Test-Path $Exe)) {
    $useDotnet = $true
    Write-Host "VDK_Tool.exe not found; falling back to 'dotnet run --project src/VDKTool'." -ForegroundColor Yellow
}

function Invoke-Tool {
    param([string[]] $ToolArgs)
    if ($useDotnet) {
        $full = @('run', '--project', (Join-Path $repoRoot 'src\VDKTool'), '-c', 'Release', '--') + $ToolArgs
        & dotnet @full 2>&1 | Out-Null
    } else {
        & $Exe @ToolArgs 2>&1 | Out-Null
    }
    return $LASTEXITCODE
}

function Get-Sha {
    param([string] $Path)
    (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash
}

# Compare two VDK files for byte-equality IGNORING the header field u32@8
# (offset 8-11): a packer-specific, game-ignored value not derivable from content.
function Test-VdkEqual {
    param([string] $A, [string] $B)
    $ba = [System.IO.File]::ReadAllBytes($A)
    $bb = [System.IO.File]::ReadAllBytes($B)
    if ($ba.Length -ne $bb.Length) { return $false }
    for ($k = 0; $k -lt $ba.Length; $k++) {
        if ($k -ge 8 -and $k -le 11) { continue }   # skip u32@8
        if ($ba[$k] -ne $bb[$k]) { return $false }
    }
    return $true
}

# --- Prepare scratch --------------------------------------------------------
if (Test-Path $Out) { Remove-Item -Recurse -Force -LiteralPath $Out }
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$capBytes = if ($MaxSizeMB -gt 0) { [long]($MaxSizeMB * 1MB) } else { 0 }

Write-Host "=== VDK Toolkit client round-trip runner ===" -ForegroundColor Cyan
Write-Host ("Tool   : {0}" -f ($(if ($useDotnet) {'dotnet run (src/VDKTool)'} else {$Exe})))
Write-Host ("Scratch: {0}" -f $Out)
if ($capBytes -gt 0) { Write-Host ("Size cap: {0} MB" -f $MaxSizeMB) }
Write-Host ""

$vdkResults = New-Object System.Collections.Generic.List[object]
$ctResults  = New-Object System.Collections.Generic.List[object]

# ===========================================================================
# VDK SWEEP
# ===========================================================================
if (-not $CtOnly) {
    Write-Host "--- VDK sweep ---" -ForegroundColor Cyan
    $vdkFiles = @()
    if (Test-Path -LiteralPath $ClientData) {
        $vdkFiles += Get-ChildItem -LiteralPath $ClientData -Recurse -Filter *.vdk -File
    } else {
        Write-Host "  WARN: client Data dir not found: $ClientData" -ForegroundColor Yellow
    }
    if (Test-Path -LiteralPath $AssetVdk) {
        $vdkFiles += Get-Item -LiteralPath $AssetVdk
    }

    # Stable order, smallest-first so caps hit cheap files first.
    $vdkFiles = $vdkFiles | Sort-Object Length
    if ($capBytes -gt 0) { $vdkFiles = $vdkFiles | Where-Object { $_.Length -le $capBytes } }
    if ($MaxVdk -gt 0)   { $vdkFiles = $vdkFiles | Select-Object -First $MaxVdk }

    $total = $vdkFiles.Count
    $i = 0
    foreach ($f in $vdkFiles) {
        $i++
        $tag = "[{0}/{1}]" -f $i, $total
        $extDir   = Join-Path $Out 'vdk_extract'
        $repacked = Join-Path $Out 'repacked.vdk'
        if (Test-Path $extDir)   { Remove-Item -Recurse -Force $extDir }
        if (Test-Path $repacked) { Remove-Item -Force $repacked }

        $status = 'identical'; $note = ''
        try {
            $rc = Invoke-Tool @('extract', $f.FullName, $extDir, '--quiet')
            if ($rc -ne 0) { throw "extract rc=$rc" }
            $rc = Invoke-Tool @('pack', $extDir, $repacked, '--quiet')
            if ($rc -ne 0) { throw "pack rc=$rc" }
            if (-not (Test-Path $repacked)) { throw "no repacked output" }
            if (-not (Test-VdkEqual $repacked $f.FullName)) { $status = 'diff' }
        } catch {
            $status = 'error'; $note = $_.Exception.Message
        }
        $color = switch ($status) { 'identical' {'Green'} 'diff' {'Yellow'} default {'Red'} }
        Write-Host ("  {0} {1,-28} {2}MB -> {3} {4}" -f $tag, $f.Name, [math]::Round($f.Length/1MB,1), $status, $note) -ForegroundColor $color
        $vdkResults.Add([pscustomobject]@{ Name=$f.Name; Path=$f.FullName; Status=$status; Note=$note })
    }
    if (Test-Path (Join-Path $Out 'vdk_extract'))  { Remove-Item -Recurse -Force (Join-Path $Out 'vdk_extract') }
    if (Test-Path (Join-Path $Out 'repacked.vdk')) { Remove-Item -Force (Join-Path $Out 'repacked.vdk') }
    Write-Host ""
}

# ===========================================================================
# CT SWEEP
# ===========================================================================
if (-not $VdkOnly) {
    Write-Host "--- CT sweep ---" -ForegroundColor Cyan
    $ctFiles = @()
    if (Test-Path -LiteralPath $AssetCtDir) {
        $ctFiles = Get-ChildItem -LiteralPath $AssetCtDir -Recurse -Filter *.ct -File | Sort-Object FullName
    } else {
        Write-Host "  WARN: CT dir not found: $AssetCtDir" -ForegroundColor Yellow
    }
    if ($capBytes -gt 0) { $ctFiles = $ctFiles | Where-Object { $_.Length -le $capBytes } }
    if ($MaxCt -gt 0)    { $ctFiles = $ctFiles | Select-Object -First $MaxCt }

    $total = $ctFiles.Count
    $i = 0
    foreach ($f in $ctFiles) {
        $i++
        $tag = "[{0}/{1}]" -f $i, $total
        $xlsx    = Join-Path $Out 'ct.xlsx'
        $rebuilt = Join-Path $Out 'rebuilt.ct'
        if (Test-Path $xlsx)    { Remove-Item -Force $xlsx }
        if (Test-Path $rebuilt) { Remove-Item -Force $rebuilt }

        $status = 'identical'; $note = ''
        try {
            $rc = Invoke-Tool @('ct2xlsx', $f.FullName, '-o', $xlsx, '--quiet')
            if ($rc -ne 0) { throw "ct2xlsx rc=$rc" }
            $rc = Invoke-Tool @('xlsx2ct', $xlsx, $rebuilt, '--quiet')
            if ($rc -ne 0) { throw "xlsx2ct rc=$rc" }
            if (-not (Test-Path $rebuilt)) { throw "no rebuilt output" }
            if ((Get-Sha $rebuilt) -ne (Get-Sha $f.FullName)) { $status = 'diff' }
        } catch {
            $status = 'error'; $note = $_.Exception.Message
        }
        $color = switch ($status) { 'identical' {'Green'} 'diff' {'Yellow'} default {'Red'} }
        Write-Host ("  {0} {1,-34} -> {2} {3}" -f $tag, $f.Name, $status, $note) -ForegroundColor $color
        $ctResults.Add([pscustomobject]@{ Name=$f.Name; Path=$f.FullName; Status=$status; Note=$note })
    }
    if (Test-Path (Join-Path $Out 'ct.xlsx'))    { Remove-Item -Force (Join-Path $Out 'ct.xlsx') }
    if (Test-Path (Join-Path $Out 'rebuilt.ct')) { Remove-Item -Force (Join-Path $Out 'rebuilt.ct') }
    Write-Host ""
}

# ===========================================================================
# SUMMARY
# ===========================================================================
Write-Host "=== SUMMARY ===" -ForegroundColor Cyan
$failures = 0

if (-not $CtOnly) {
    $vTot  = $vdkResults.Count
    $vId   = ($vdkResults | Where-Object Status -eq 'identical').Count
    $vDiff = ($vdkResults | Where-Object Status -eq 'diff').Count
    $vErr  = ($vdkResults | Where-Object Status -eq 'error').Count
    Write-Host ("VDK: {0}/{1} identical ({2} diff, {3} error)" -f $vId, $vTot, $vDiff, $vErr)
    foreach ($r in ($vdkResults | Where-Object Status -ne 'identical')) {
        Write-Host ("     ! {0} [{1}] {2}" -f $r.Name, $r.Status, $r.Note) -ForegroundColor Yellow
    }
    $failures += $vDiff + $vErr
}

if (-not $VdkOnly) {
    $cTot  = $ctResults.Count
    $cId   = ($ctResults | Where-Object Status -eq 'identical').Count
    $cDiff = ($ctResults | Where-Object Status -eq 'diff').Count
    $cErr  = ($ctResults | Where-Object Status -eq 'error').Count
    Write-Host ("CT:  {0}/{1} identical ({2} diff, {3} error)" -f $cId, $cTot, $cDiff, $cErr)
    foreach ($r in ($ctResults | Where-Object Status -ne 'identical')) {
        Write-Host ("     ! {0} [{1}] {2}" -f $r.Name, $r.Status, $r.Note) -ForegroundColor Yellow
    }
    $failures += $cDiff + $cErr
}

# --- Cleanup ----------------------------------------------------------------
if ($KeepTemp) {
    Write-Host ("Scratch kept at: {0}" -f $Out) -ForegroundColor DarkGray
} else {
    if (Test-Path $Out) { Remove-Item -Recurse -Force -LiteralPath $Out }
}

exit ([int]($failures -gt 0))

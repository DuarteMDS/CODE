<#
.SYNOPSIS
  Installs CableTray Void Cutter into Revit 2025's Addins folder.

.DESCRIPTION
  Copies the compiled DLL and .addin manifest to the current-user Revit 2025
  addins directory so Revit loads the plugin on next startup.

.NOTES
  Run from the repository root or the bin\Release folder.
  Requires PowerShell 5+ on Windows.
#>

param(
    [string]$Configuration = "Release",
    [string]$RevitVersion  = "2025"
)

# ── Paths ─────────────────────────────────────────────────────────────────────
$scriptDir    = Split-Path -Parent $MyInvocation.MyCommand.Path
$binDir       = Join-Path $scriptDir "bin\$Configuration"
$addinDir     = "$env:APPDATA\Autodesk\Revit\Addins\$RevitVersion"

# ── Pre-flight ────────────────────────────────────────────────────────────────
if (-not (Test-Path $binDir)) {
    Write-Error "Build output not found at: $binDir`nPlease build the project first."
    exit 1
}

$dll   = Join-Path $binDir "CableTrayVoidCutter.dll"
$addin = Join-Path $scriptDir "CableTrayVoidCutter.addin"

if (-not (Test-Path $dll))   { Write-Error "DLL not found: $dll";   exit 1 }
if (-not (Test-Path $addin)) { Write-Error ".addin not found: $addin"; exit 1 }

# ── Create addins folder if needed ───────────────────────────────────────────
if (-not (Test-Path $addinDir)) {
    New-Item -ItemType Directory -Path $addinDir -Force | Out-Null
    Write-Host "Created: $addinDir"
}

# ── Copy files ────────────────────────────────────────────────────────────────
Write-Host "Installing to: $addinDir"

# Copy all DLLs / PDBs from bin directory
Get-ChildItem -Path $binDir -File | ForEach-Object {
    Copy-Item $_.FullName -Destination $addinDir -Force
    Write-Host "  Copied: $($_.Name)"
}

# Copy .addin manifest
Copy-Item $addin -Destination $addinDir -Force
Write-Host "  Copied: CableTrayVoidCutter.addin"

Write-Host ""
Write-Host "Installation complete. Restart Revit 2025 to load the plugin." -ForegroundColor Green

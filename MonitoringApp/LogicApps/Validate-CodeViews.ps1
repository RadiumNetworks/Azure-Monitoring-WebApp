[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$generator = Join-Path $PSScriptRoot 'Build-CodeViews.ps1'
& $generator -Check

if ($LASTEXITCODE -ne 0) {
    throw "Logic App Code View validation failed with exit code $LASTEXITCODE."
}

Write-Host 'All generated Logic App Code View files are current and valid.'
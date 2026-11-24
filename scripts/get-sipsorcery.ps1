# Windows version of get-sipsorcery.sh (run in PowerShell from anywhere).
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")
$Commit = "6f379fdba939398a51b567064613e1772538a1c8"
$Dir = "third_party/sipsorcery"
if (-not (Test-Path "$Dir/.git")) {
  git clone --filter=blob:none --no-checkout https://github.com/sipsorcery-org/sipsorcery.git $Dir
}
git -C $Dir fetch --depth 1 origin $Commit
git -C $Dir checkout -q --force $Commit
git -C $Dir clean -fdq -- src/SIPSorcery
git -C $Dir apply (Resolve-Path "patches/sipsorcery-vexel.patch")
Write-Host "SIPSorcery $Commit patched in $Dir. Rebuild XStream to use it."

<#
.SYNOPSIS
    Builds Soul Remote end to end: tests, publishes the single-file exe and wraps it
    in a per-user MSI.

.DESCRIPTION
    One command produces everything a release needs, so the artefacts handed round
    are the ones CI would have produced rather than whatever was last left in bin/.

    Steps: restore, build, test, publish (self-contained win-x64, single file),
    regenerate installer/License.rtf from LICENSE, build the MSI, and write a
    SHA-256 next to each artefact.

    Requires the .NET 8 SDK and the WiX 5 CLI:
        dotnet tool install --global wix --version 5.0.2
        wix extension add -g WixToolset.UI.wixext/5.0.2
        wix extension add -g WixToolset.Util.wixext/5.0.2

.PARAMETER Version
    Product version stamped into the exe and the MSI. Must be x.y.z - Windows
    Installer ignores a fourth field when it compares versions.

.PARAMETER PublishDir
    Where the single-file exe is published to. Defaults to publish\ in the
    repository; point it elsewhere when a copy of the app is running from there and
    holding the file open.

.PARAMETER SkipTests
    Skips the test run. For iterating on the installer only.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1 -Version 1.0.0
#>
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '1.0.0',
    [string]$RepoRoot,
    [string]$PublishDir,
    [switch]$SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }

$publishDir   = if ($PublishDir) { $PublishDir } else { Join-Path $RepoRoot 'publish' }
$installerDir = Join-Path $RepoRoot 'installer'
$distDir      = Join-Path $RepoRoot 'dist'
$solution     = Join-Path $RepoRoot 'SoulRemote.sln'
$appProject   = Join-Path $RepoRoot 'src\SoulRemote\SoulRemote.csproj'

function Invoke-Step {
    param([string]$Name, [scriptblock]$Body)
    Write-Host ''
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Body
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE." }
}

function Write-Checksum {
    param([string]$Path)
    $hash = (Get-FileHash $Path -Algorithm SHA256).Hash.ToLower()
    $name = Split-Path -Leaf $Path
    "$hash  $name" | Out-File -Encoding ascii "$Path.sha256"
    $size = [Math]::Round((Get-Item $Path).Length / 1MB, 1)
    Write-Host ("    {0}  {1} MB" -f $name, $size)
    Write-Host "    sha256 $hash"
}

foreach ($tool in @('dotnet', 'wix')) {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "'$tool' is not on PATH. See the notes at the top of this script."
    }
}

Write-Host "Soul Remote $Version" -ForegroundColor Green

Invoke-Step 'Restore' { dotnet restore $solution }
Invoke-Step 'Build'   { dotnet build $solution -c Release --no-restore "-p:Version=$Version" }

if ($SkipTests) {
    Write-Host ''
    Write-Host '==> Test (skipped)' -ForegroundColor Yellow
}
else {
    # --no-build would use assemblies stamped with the version above; the test project
    # does not care about the version, so reusing them keeps this to one compile.
    Invoke-Step 'Test' { dotnet test $solution -c Release --no-build }
}

# The brand assets are generated rather than committed as opaque binaries. Rebuild
# them so the icon in the installer always matches the palette in the repository.
Invoke-Step 'Brand assets' {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'make-brand.ps1') -RepoRoot $RepoRoot
}

Invoke-Step 'Publish (self-contained win-x64, single file)' {
    if (Test-Path $publishDir) {
        # A running copy of the app holds its own exe open, and publishing over it
        # fails halfway through with a bare access-denied. Say which process to close.
        try { Remove-Item $publishDir -Recurse -Force -ErrorAction Stop }
        catch {
            $holder = Get-Process -ErrorAction SilentlyContinue |
                      Where-Object { $_.Path -and $_.Path.StartsWith($publishDir, 'OrdinalIgnoreCase') }
            if ($holder) {
                throw ("Cannot clear $publishDir - SoulRemote is running from it (PID {0}). " +
                       'Close it, or pass -PublishDir to publish somewhere else.') -f $holder[0].Id
            }
            throw
        }
    }
    dotnet publish $appProject -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true "-p:Version=$Version" -o $publishDir
}

$exe = Join-Path $publishDir 'SoulRemote.exe'
if (-not (Test-Path $exe)) { throw "Publish did not produce $exe." }
Write-Checksum $exe

# The licence shown on the installer's second page is derived from LICENSE, so the
# two can never disagree about what people are agreeing to.
Invoke-Step 'License page' {
    $licensePath = Join-Path $RepoRoot 'LICENSE'
    $lines = Get-Content $licensePath
    $escaped = foreach ($line in $lines) {
        if ($line.Trim() -eq '') { '\par' }
        else { ($line -replace '\\', '\\\\' -replace '([{}])', '\$1') + ' ' }
    }
    $rtf = "{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fnil\fcharset0 Segoe UI;}}`r`n" +
           "\viewkind4\uc1\pard\f0\fs18`r`n" + ($escaped -join "`r`n") + "`r`n\par}`r`n"
    [System.IO.File]::WriteAllText((Join-Path $installerDir 'License.rtf'), $rtf, [System.Text.Encoding]::ASCII)
    Write-Host '    installer\License.rtf regenerated from LICENSE'
    $global:LASTEXITCODE = 0
}

$msi = Join-Path $distDir "SoulRemote-$Version-x64.msi"
Invoke-Step 'Installer (per-user MSI)' {
    if (-not (Test-Path $distDir)) { New-Item -ItemType Directory -Path $distDir | Out-Null }
    wix build (Join-Path $installerDir 'SoulRemote.wxs') `
        -arch x64 `
        -define "Version=$Version" `
        -define "PublishDir=$publishDir" `
        -define "IconFile=$(Join-Path $RepoRoot 'src\SoulRemote\Assets\app.ico')" `
        -bindpath $installerDir `
        -ext WixToolset.UI.wixext `
        -ext WixToolset.Util.wixext `
        -out $msi
}

if (-not (Test-Path $msi)) { throw "WiX did not produce $msi." }
Write-Checksum $msi

Write-Host ''
Write-Host 'Artefacts:' -ForegroundColor Green
Write-Host "    $exe"
Write-Host "    $msi"

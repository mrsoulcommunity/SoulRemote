<#
.SYNOPSIS
    Proves that upgrading or removing Soul Remote leaves everything under %APPDATA%
    exactly where it was.

.DESCRIPTION
    Settings, the DPAPI-encrypted tokens, the paired chat list and the logs all live in
    %APPDATA%\SoulRemote. The installer has never managed that folder, and it must stay
    that way: an upgrade that resets someone's pairing costs them the whole setup, and
    they would find out about it from a PC that had stopped answering.

    "Must stay that way" is worth checking rather than asserting, so this does both.

    Static (always)
        Reads the MSI's own tables and fails if the package can reach roaming AppData
        at all - no directory rooted there, no RemoveFile aimed outside the program
        folder, no registry value outside the app's own HKCU key. It also checks that
        the pieces the in-app updater depends on are in the package, because a silent
        install that does not start the app again is an app that never comes back.

    End to end (-Install)
        Installs the package, writes sentinel files into %APPDATA%\SoulRemote, installs
        it again over the top - the same RemoveExistingProducts path a real upgrade
        takes - and compares the sentinels byte for byte. Then uninstalls and checks
        they are still there. This runs Windows Installer for real, so it catches what
        reading tables cannot.

    The second mode changes the machine it runs on. It refuses to start when Soul Remote
    is already installed for the current user unless -Force is given, and it moves any
    existing %APPDATA%\SoulRemote aside and puts it back afterwards. CI runs it on a
    clean runner; on a development machine, prefer the static mode.

.PARAMETER Msi
    The package to inspect. Defaults to the newest SoulRemote-*.msi in dist\.

.PARAMETER Install
    Also run the install/upgrade/uninstall cycle.

.PARAMETER Force
    Run that cycle even though Soul Remote is already installed for this user.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\check-data-survives.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\check-data-survives.ps1 -Install
#>
[CmdletBinding()]
param(
    [string]$RepoRoot,
    [string]$Msi,
    [switch]$Install,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }

$dataDir  = Join-Path $env:APPDATA 'SoulRemote'
$problems = New-Object System.Collections.Generic.List[string]
$checks   = 0

function Assert-That {
    param([string]$What, [bool]$Condition, [string]$Detail = '')
    $script:checks++
    if ($Condition) {
        Write-Host "  ok    $What" -ForegroundColor DarkGray
    }
    else {
        Write-Host "  FAIL  $What" -ForegroundColor Red
        $problems.Add($(if ($Detail) { "$What - $Detail" } else { $What }))
    }
}

# Reads named columns out of one MSI table, one object per row with the columns as
# properties. Objects rather than arrays on purpose: PowerShell unrolls a single-row
# result, and an array row would then arrive as its own first column with nothing to
# say it had.
function Read-Table {
    param($Database, [string]$Table, [string[]]$Columns)

    $select = ($Columns | ForEach-Object { '`' + $_ + '`' }) -join ','
    $view = $Database.OpenView("SELECT $select FROM ``$Table``")
    # Execute and Close hand back a null through COM interop, and a bare call would put
    # it on the pipeline - two phantom rows in front of the real ones.
    [void]$view.Execute()
    while ($true) {
        $record = $view.Fetch()
        if ($null -eq $record) { break }
        $fields = [ordered]@{}
        for ($i = 1; $i -le $Columns.Count; $i++) {
            # StringData is a parameterised COM property, which Windows PowerShell will
            # not call with ordinary syntax - $record.StringData($i) silently evaluates
            # to nothing. Late binding is the only way through.
            $fields[$Columns[$i - 1]] = [string]$record.GetType().InvokeMember(
                'StringData', 'GetProperty', $null, $record, @($i))
        }
        [pscustomobject]$fields
    }
    [void]$view.Close()
}

function Test-HasTable {
    param($Database, [string]$Name)
    try {
        $view = $Database.OpenView("SELECT * FROM ``$Name``")
        [void]$view.Execute()
        [void]$view.Close()
        return $true
    }
    catch { return $false }
}

# msiexec returns immediately unless waited on, and a non-zero exit is the only sign of
# trouble a silent install gives.
function Invoke-Msi {
    param([string[]]$Arguments)
    $process = Start-Process -FilePath 'msiexec.exe' -ArgumentList $Arguments -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "msiexec $($Arguments -join ' ') exited with $($process.ExitCode)."
    }
}

# Every file under a folder, by relative path and content hash. Comparing this before
# and after says more than "the folder is still there": it catches a file that was
# rewritten, truncated or replaced, which is what a careless installer actually does.
function Get-Fingerprint {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return '(missing)' }
    $lines = Get-ChildItem $Path -Recurse -File | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($Path.Length).TrimStart([char]92)
        "$relative  $((Get-FileHash $_.FullName -Algorithm SHA256).Hash)"
    }
    return ($lines -join "`n")
}

if (-not $Msi) {
    $newest = Get-ChildItem (Join-Path $RepoRoot 'dist') -Filter 'SoulRemote-*.msi' -ErrorAction SilentlyContinue |
              Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $newest) {
        throw 'No MSI found in dist. Run tools\build-installer.ps1 first, or pass -Msi.'
    }
    $Msi = $newest.FullName
}
if (-not (Test-Path $Msi)) { throw "No such package: $Msi" }

Write-Host 'Soul Remote - data safety check' -ForegroundColor Green
Write-Host "  package $Msi"

# ---------------------------------------------------------------- static ----

Write-Host ''
Write-Host '==> The package cannot reach %APPDATA%' -ForegroundColor Cyan

$installerCom = New-Object -ComObject WindowsInstaller.Installer
$database = $installerCom.OpenDatabase($Msi, 0)   # 0 = read only

# Every directory in the package, and what each one is rooted at. A row whose chain
# ends at AppDataFolder is a row that writes into the folder this check protects.
$directories = @(Read-Table $database 'Directory' @('Directory', 'Directory_Parent', 'DefaultDir'))
$parentOf = @{}
foreach ($row in $directories) { $parentOf[$row.Directory] = $row.Directory_Parent }

function Get-Root {
    param([string]$Directory)
    $seen = @{}
    $current = $Directory
    while ($current -and $parentOf.ContainsKey($current) -and $parentOf[$current]) {
        if ($seen.ContainsKey($current)) { break }   # a malformed package, not a loop of ours
        $seen[$current] = $true
        $current = $parentOf[$current]
    }
    return $current
}

# LocalAppDataFolder is where the program itself goes and is not the folder at issue.
# AppDataFolder - roaming - is the one holding settings.json.
$forbiddenRoots = @('AppDataFolder', 'PersonalFolder', 'CommonAppDataFolder', 'MyPicturesFolder')

$rootedInAppData = @()
foreach ($row in $directories) {
    if ($forbiddenRoots -contains $row.Directory -or $forbiddenRoots -contains (Get-Root $row.Directory)) {
        $rootedInAppData += $row.Directory
    }
}
Assert-That 'no directory in the package is rooted in roaming AppData' `
    ($rootedInAppData.Count -eq 0) ($rootedInAppData -join ', ')

# A package that never names the folder cannot empty it.
$mentions = @()
$mentions += $directories | ForEach-Object { "$($_.Directory)|$($_.Directory_Parent)|$($_.DefaultDir)" }
if (Test-HasTable $database 'CustomAction') {
    $mentions += @(Read-Table $database 'CustomAction' @('Action', 'Target')) |
                 ForEach-Object { "$($_.Action)|$($_.Target)" }
}
if (Test-HasTable $database 'Property') {
    $mentions += @(Read-Table $database 'Property' @('Property', 'Value')) |
                 ForEach-Object { "$($_.Property)|$($_.Value)" }
}
#  matters: LocalAppDataFolder is where the program itself goes and is fine, while
# a bare AppDataFolder is the roaming folder this check exists to keep out.
$named = @($mentions | Where-Object { $_ -match '(?i)AppDataFolder|settings\.json' })
Assert-That 'nothing in the package names roaming AppData or the settings file' `
    ($named.Count -eq 0) ($named -join '; ')

# RemoveFile is the table that empties folders on uninstall. Every row must be aimed at
# the program folder or a shortcut folder, never at anything the user owns.
if (Test-HasTable $database 'RemoveFile') {
    $allowed = @('INSTALLFOLDER', 'ProgramMenuFolder', 'DesktopFolder')
    $strays = @()
    foreach ($row in @(Read-Table $database 'RemoveFile' @('FileKey', 'DirProperty'))) {
        if ($allowed -notcontains $row.DirProperty) { $strays += "$($row.FileKey) -> $($row.DirProperty)" }
    }
    Assert-That 'every RemoveFile row targets the program folder or a shortcut' `
        ($strays.Count -eq 0) ($strays -join ', ')
}
else {
    Assert-That 'the package has no RemoveFile table at all' $true
}

# The only registry the package owns is its own key under HKCU.
if (Test-HasTable $database 'Registry') {
    $strays = @()
    foreach ($row in @(Read-Table $database 'Registry' @('Registry', 'Key'))) {
        if ($row.Key -notmatch '(?i)^Software\\MrSoul\\SoulRemote') { $strays += "$($row.Registry): $($row.Key)" }
    }
    Assert-That 'every registry value is under HKCU\Software\MrSoul\SoulRemote' `
        ($strays.Count -eq 0) ($strays -join ', ')
}

Write-Host ''
Write-Host '==> A silent install can still bring the app back' -ForegroundColor Cyan

# The in-app updater installs with LAUNCHAFTERINSTALL=1 and then exits so its own exe
# can be replaced. If these three rows go missing, the update lands and the app never
# starts again - on a machine whose entire purpose is to be reachable.
# It is declared with no value, so there is no Property row for it. A property that
# exists only to be passed in shows up in SecureCustomProperties, which is also what
# lets it through from a command line in the first place.
$secure = @(Read-Table $database 'Property' @('Property', 'Value') |
            Where-Object { $_.Property -eq 'SecureCustomProperties' })
Assert-That 'LAUNCHAFTERINSTALL may be passed in on the command line' `
    ($secure.Count -gt 0 -and $secure[0].Value -match 'LAUNCHAFTERINSTALL')

$customActions = @(Read-Table $database 'CustomAction' @('Action'))
Assert-That 'the package carries the after-install launch action' `
    (@($customActions | Where-Object { $_.Action -eq 'LaunchAfterSilentInstall' }).Count -gt 0)

$sequenced = @(Read-Table $database 'InstallExecuteSequence' @('Action', 'Condition') |
               Where-Object { $_.Action -eq 'LaunchAfterSilentInstall' })
Assert-That 'the launch action is sequenced, and only when it is asked for' `
    ($sequenced.Count -gt 0 -and $sequenced[0].Condition -match '(?i)LAUNCHAFTERINSTALL')

[void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($database)
[void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($installerCom)

# ----------------------------------------------------------- end to end ----

if ($Install) {
    Write-Host ''
    Write-Host '==> Install, upgrade, uninstall - with real files in the way' -ForegroundColor Cyan

    $installed = Get-ItemProperty -Path 'HKCU:\Software\MrSoul\SoulRemote' -ErrorAction SilentlyContinue
    if ($installed -and -not $Force) {
        throw ('Soul Remote is already installed for this user. This mode installs and ' +
               'uninstalls it, and would take that copy with it. Pass -Force if that is ' +
               'what you want.')
    }

    $stash = $null
    if (Test-Path $dataDir) {
        $stash = "$dataDir.check-$(Get-Date -Format yyyyMMdd-HHmmss)"
        Write-Host "  moving your existing data aside: $stash"
        Move-Item $dataDir $stash
    }

    $programDir = Join-Path $env:LOCALAPPDATA 'Programs\Soul Remote'

    try {
        Write-Host '  installing'
        Invoke-Msi @('/i', $Msi, '/qn', '/norestart')
        Assert-That 'the app is installed' (Test-Path (Join-Path $programDir 'SoulRemote.exe'))

        # Stand-ins for the things a user cannot afford to lose.
        New-Item -ItemType Directory -Path (Join-Path $dataDir 'logs') -Force | Out-Null
        Set-Content -Path (Join-Path $dataDir 'settings.json') -Encoding utf8 -NoNewline `
            -Value '{"TelegramBotToken":"sentinel","AuthorizedChatIds":[4242]}'
        Set-Content -Path (Join-Path $dataDir 'logs\soulremote-test.log') -Encoding utf8 -NoNewline `
            -Value 'a log line that has to survive'
        $fingerprint = Get-Fingerprint $dataDir

        # AllowSameVersionUpgrades means installing the same package again takes the
        # RemoveExistingProducts path, which is exactly the path a real upgrade takes.
        Write-Host '  upgrading over it'
        Invoke-Msi @('/i', $Msi, '/qn', '/norestart')
        Assert-That 'the app is still installed after the upgrade' `
            (Test-Path (Join-Path $programDir 'SoulRemote.exe'))
        Assert-That 'every file under %APPDATA% survived the upgrade byte for byte' `
            ((Get-Fingerprint $dataDir) -eq $fingerprint) 'the upgrade changed or removed user data'

        Write-Host '  uninstalling'
        Invoke-Msi @('/x', $Msi, '/qn', '/norestart')
        Assert-That 'the program folder is gone' `
            (-not (Test-Path (Join-Path $programDir 'SoulRemote.exe')))
        Assert-That 'every file under %APPDATA% survived the uninstall too' `
            ((Get-Fingerprint $dataDir) -eq $fingerprint) 'uninstalling took user data with it'
    }
    finally {
        if ($stash) {
            Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue
            Move-Item $stash $dataDir
            Write-Host "  your data is back at $dataDir"
        }
    }
}

# ------------------------------------------------------------------ done ----

Write-Host ''
if ($problems.Count -gt 0) {
    Write-Host "$($problems.Count) of $checks checks failed:" -ForegroundColor Red
    foreach ($problem in $problems) { Write-Host "  - $problem" -ForegroundColor Red }
    exit 1
}

Write-Host "$checks checks passed - nothing in this package can touch %APPDATA%\SoulRemote." -ForegroundColor Green
exit 0

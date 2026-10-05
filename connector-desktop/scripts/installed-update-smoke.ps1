param(
    [Parameter(Mandatory = $true)][string]$PackageA,
    [Parameter(Mandatory = $true)][string]$PackageB,
    [string]$TestRoot,
    [ValidatePattern('^Structura\.Connector\.UpdateSmoke$')][string]$TestAppId = 'Structura.Connector.UpdateSmoke',
    [switch]$NoRestart
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $root 'artifacts\installed-update-smoke'))
if (-not $TestRoot) { $TestRoot = Join-Path $artifactsRoot ([Guid]::NewGuid().ToString('N')) }
$testRootFull = [IO.Path]::GetFullPath($TestRoot)
if (-not $testRootFull.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "TestRoot must be below $artifactsRoot"
}
if (Test-Path -LiteralPath $testRootFull) { throw "TestRoot already exists and will not be removed: $testRootFull" }

function Require-File([string]$Path, [string]$Name) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "$Name was not found: $Path" }
    return (Resolve-Path -LiteralPath $Path).Path
}
function Assert-SameUserState($Before, $After, [string]$Name) {
    foreach ($field in @('Exists', 'Files', 'Directories', 'Bytes', 'RootLastWriteUtcTicks', 'MetadataSha256')) {
        if ($null -eq $Before.$field -or $null -eq $After.$field -or $Before.$field -cne $After.$field) {
            throw "$Name user-state fingerprint differs or is missing: $field"
        }
    }
}
function Assert-NativeComposition([string]$OutputDirectory, [string]$Name, [string]$ExpectedVersion) {
    $ui = Get-Content -Raw -LiteralPath (Require-File (Join-Path $OutputDirectory 'native-webview-smoke.json') "$Name native UI evidence") | ConvertFrom-Json
    if ($ui.noForeground -ne $true -or $ui.blockedExternalNavigation -ne $true -or $ui.snapshots.Count -ne 3 -or $ui.commands -ne 1) {
        throw "$Name did not complete the native WPF/WebView2 scenario."
    }
    $composition = Get-Content -Raw -LiteralPath (Require-File (Join-Path $OutputDirectory 'main-window-composition.json') "$Name MainWindow evidence") | ConvertFrom-Json
    foreach ($field in @('actualMainWindow', 'actualGraphiteController', 'seededSettingsHydrated', 'settingsRpcPersisted',
                         'autoStartRedirectedToInjectedService', 'applicationWindowRegistered', 'applicationWindowsRestored')) {
        if ($composition.$field -ne $true) { throw "$Name MainWindow proof failed: $field" }
    }
    foreach ($field in @('windowShown', 'windowLoadedHooksRun', 'trayVisible', 'runtimeStarted', 'userSettingsTouched')) {
        if ($composition.$field -ne $false) { throw "$Name MainWindow isolation proof failed: $field" }
    }
    Assert-SameUserState $composition.userStateBefore $composition.userStateAfter "$Name MainWindow"
    foreach ($field in @('expectedApplicationVersion', 'snapshotApplicationVersion', 'installedApplicationVersion', 'releaseNotesApplicationVersion')) {
        if ($composition.$field -cne $ExpectedVersion) { throw "$Name displayed an incorrect application version: $field" }
    }
    $isolation = Get-Content -Raw -LiteralPath (Require-File (Join-Path $OutputDirectory 'native-user-state-isolation.json') "$Name user-state isolation evidence") | ConvertFrom-Json
    if ($isolation.userStateUnchanged -ne $true) { throw "$Name modified real user state." }
    Assert-SameUserState $isolation.before $isolation.after "$Name native smoke"
    $png = Require-File (Join-Path $OutputDirectory 'main-window-composition.png') "$Name MainWindow screenshot"
    $stream = [IO.File]::OpenRead($png)
    try {
        $signature = [byte[]]::new(8)
        if ($stream.Length -le 8 -or $stream.Read($signature, 0, 8) -ne 8 -or
            [Convert]::ToHexString($signature) -ne '89504E470D0A1A0A') {
            throw "$Name MainWindow screenshot is not a nonempty PNG."
        }
    } finally { $stream.Dispose() }
}
function Invoke-Checked([string]$FilePath, [string[]]$Arguments, [string]$Name) {
    $process = Start-Process -FilePath $FilePath -ArgumentList $Arguments -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -ne 0) { throw "$Name failed with exit code $($process.ExitCode)" }
    return $process.ExitCode
}
function Get-Release([string]$PackageDirectory, [string]$Name) {
    $directory = (Resolve-Path -LiteralPath $PackageDirectory).Path
    $metadataPath = Require-File (Join-Path $directory 'local-package.json') "$Name metadata"
    $metadata = Get-Content -Raw -LiteralPath $metadataPath | ConvertFrom-Json
    if ($metadata.applicationId -ne $TestAppId) { throw "$Name is not an isolated $TestAppId package." }
    if ($metadata.packTitle -ne 'Structura Connector Update Smoke' -or $metadata.shortcuts -ne 'None') {
        throw "$Name must explicitly disable shortcuts and use the isolated title."
    }
    $setup = Require-File (Join-Path $directory ($TestAppId + '-' + $metadata.channel + '-Setup.exe')) "$Name setup"
    $package = Require-File (Join-Path $directory ($TestAppId + '-' + $metadata.version + '-' + $metadata.channel + '-full.nupkg')) "$Name versioned full package"
    return [pscustomobject]@{ Directory = $directory; Metadata = $metadata; Setup = $setup; Package = $package }
}

$a = Get-Release $PackageA 'PackageA'
$b = Get-Release $PackageB 'PackageB'
if ($a.Metadata.version -notmatch '^1\.1\.0-smoke\.(\d+)$' -or $b.Metadata.version -notmatch '^1\.1\.0-smoke\.(\d+)$') {
    throw 'Installed smoke accepts only explicit 1.1.0-smoke.<number> package versions.'
}
if ([int]$Matches[1] -le [int]([regex]::Match($a.Metadata.version, '^1\.1\.0-smoke\.(\d+)$').Groups[1].Value)) {
    throw 'PackageB version must be newer than PackageA.'
}
$vpk = Require-File (Join-Path $root '.tools\vpk-1.2.161\vpk.exe') 'Pinned vpk'

New-Item -ItemType Directory -Path $testRootFull -Force | Out-Null
$installRoot = Join-Path $testRootFull 'installed'
$feedRoot = Join-Path $testRootFull 'feed'
$downloadRoot = Join-Path $testRootFull 'downloaded'
$profileRoot = Join-Path $testRootFull 'profile'
New-Item -ItemType Directory -Path $feedRoot,$downloadRoot,(Join-Path $profileRoot 'ConnectorAgentDesktop') -Force | Out-Null

$fixtureToken = 'update-smoke-' + [Guid]::NewGuid().ToString('N')
$fixtureBytes = [Text.Encoding]::UTF8.GetBytes($fixtureToken)
$fixtureCipher = [Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect($fixtureBytes, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser))
$settingsPath = Join-Path $profileRoot 'ConnectorAgentDesktop\settings.json'
@{ DeviceId = 'update-smoke-device'; TokenCipherBase64 = $fixtureCipher; AutoStart = $false; HeartbeatSeconds = 61 } |
    ConvertTo-Json | Set-Content -LiteralPath $settingsPath -Encoding utf8
$settingsHashBefore = (Get-FileHash -LiteralPath $settingsPath -Algorithm SHA256).Hash.ToLowerInvariant()

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runValueBefore = (Get-ItemProperty -Path $runKey -Name 'ConnectorAgentDesktop' -ErrorAction SilentlyContinue).ConnectorAgentDesktop

Copy-Item -LiteralPath (Join-Path $b.Directory ('RELEASES-' + $b.Metadata.channel)) -Destination $feedRoot
Copy-Item -LiteralPath (Join-Path $b.Directory ('releases.' + $b.Metadata.channel + '.json')) -Destination $feedRoot
Copy-Item -LiteralPath $b.Package -Destination $feedRoot
$downloadExit = Invoke-Checked $vpk @('download', 'local', '--path', $feedRoot, '--outputDir', $downloadRoot, '--channel', $b.Metadata.channel) 'Local feed download'
$downloadedPackage = Require-File (Join-Path $downloadRoot (Split-Path -Leaf $b.Package)) 'Downloaded full package'
if ((Get-FileHash $downloadedPackage -Algorithm SHA256).Hash -ne (Get-FileHash $b.Package -Algorithm SHA256).Hash) { throw 'Downloaded package hash mismatch.' }

$missingFeedExit = -1
& $vpk download local --path (Join-Path $testRootFull 'missing-feed') --outputDir (Join-Path $testRootFull 'missing-download') --channel $b.Metadata.channel 2>$null
$missingFeedExit = $LASTEXITCODE
if ($missingFeedExit -eq 0) { throw 'Missing feed unexpectedly downloaded.' }

$tampered = Join-Path $testRootFull 'tampered-feed'
New-Item -ItemType Directory -Path $tampered | Out-Null
Copy-Item -LiteralPath (Join-Path $b.Directory ('RELEASES-' + $b.Metadata.channel)) -Destination $tampered
Copy-Item -LiteralPath (Join-Path $b.Directory ('releases.' + $b.Metadata.channel + '.json')) -Destination $tampered
Copy-Item -LiteralPath $b.Package -Destination $tampered
$tamperedPackage = Join-Path $tampered (Split-Path -Leaf $b.Package)
$stream = [IO.File]::Open($tamperedPackage, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try { $stream.Position = 128; $byte = $stream.ReadByte(); $stream.Position = 128; $stream.WriteByte(($byte -bxor 0x01)) } finally { $stream.Dispose() }
$tamperedLog = (& $vpk download local --path $tampered --outputDir (Join-Path $testRootFull 'tampered-download') --channel $b.Metadata.channel 2>&1 | Out-String)
$tamperedExit = $LASTEXITCODE
$tamperedDownloaded = Join-Path $testRootFull ('tampered-download\' + (Split-Path -Leaf $b.Package))
$tamperedAccepted = (Test-Path -LiteralPath $tamperedDownloaded -PathType Leaf) -and
    ((Get-FileHash -LiteralPath $tamperedDownloaded -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $b.Package -Algorithm SHA256).Hash)
if ($tamperedLog -notmatch 'Checksum mismatch' -or $tamperedAccepted) { throw 'Tampered feed was not rejected.' }

$registrationPath = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $TestAppId
$registrationKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($registrationPath)
$registrationExisted = $null -ne $registrationKey
$registrationBefore = @{}
if ($registrationKey) {
    foreach ($name in $registrationKey.GetValueNames()) {
        $registrationBefore[$name] = @{ value = $registrationKey.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames); kind = $registrationKey.GetValueKind($name) }
    }
    $registrationKey.Dispose()
}
$launcherPaths = @(
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Structura Connector.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Programs')) 'Structura Connector.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Programs')) 'Structura Connector\Structura Connector.lnk')
)
$launcherHashes = @{}
foreach ($launcherPath in $launcherPaths) {
    $launcherHashes[$launcherPath] = if (Test-Path -LiteralPath $launcherPath) { (Get-FileHash -LiteralPath $launcherPath -Algorithm SHA256).Hash } else { $null }
}
try {
$setupExit = Invoke-Checked $a.Setup @('--silent', '--installto', $installRoot) 'Isolated Setup A'
$updateExe = Require-File (Join-Path $installRoot 'Update.exe') 'Installed updater'
$probeA = Require-File (Join-Path $installRoot 'current\UpdateSmokeProbe.exe') 'Installed probe A'
$probeWriteExit = Invoke-Checked $probeA @('--write', $testRootFull, $fixtureToken) 'Probe A persisted settings'
$settingsHashA = (Get-FileHash -LiteralPath $settingsPath -Algorithm SHA256).Hash.ToLowerInvariant()
$runtimeEvidencePath = Require-File (Join-Path $testRootFull 'runtime-evidence.json') 'Phase A execution evidence'
$runtimeA = Get-Content -Raw -LiteralPath $runtimeEvidencePath | ConvertFrom-Json
if ($runtimeA.phase -ne 'A' -or $runtimeA.phaseAVersion -ne $a.Metadata.version -or $runtimeA.drainBefore -ne 'Draining' -or $runtimeA.activeOperationsBefore -ne 1 -or
    $runtimeA.drainAfter -ne 'ReadyToApply' -or -not $runtimeA.noInterruption -or $runtimeA.terminalStatus -ne 'Success') {
    throw 'Real Agent execution was not drained before applying the update.'
}
Assert-NativeComposition (Join-Path $testRootFull 'native-ui-a') 'Installed A' $a.Metadata.version
$busy = Start-Process -FilePath $env:SystemRoot\System32\cmd.exe -ArgumentList '/c', 'timeout /t 3 /nobreak >nul' -PassThru -WindowStyle Hidden
$busyApply = Start-Process -FilePath $updateExe -ArgumentList @('--rootDir', $installRoot, 'apply', '--silent', '--norestart', '--waitPid', $busy.Id, '--package', $downloadedPackage) -PassThru -WindowStyle Hidden
$busyApply.WaitForExit()
if ($busyApply.ExitCode -ne 0) { throw "Busy wait/apply failed with exit code $($busyApply.ExitCode)" }
if (-not $busy.HasExited) { throw 'Updater did not wait for the isolated busy sentinel.' }
if (-not $NoRestart) {
    if (-not ('IsolatedUpdateEvidenceWatcher' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Threading;
public sealed class IsolatedUpdateEvidenceWatcher : IDisposable {
    private readonly AutoResetEvent signal = new AutoResetEvent(false);
    private readonly FileSystemWatcher watcher;
    public IsolatedUpdateEvidenceWatcher(string root) {
        watcher = new FileSystemWatcher(root, "runtime-evidence.json");
        watcher.Changed += (_, e) => signal.Set();
        watcher.Created += (_, e) => signal.Set();
        watcher.Renamed += (_, e) => signal.Set();
        watcher.EnableRaisingEvents = true;
    }
    public bool Wait(int milliseconds) => signal.WaitOne(milliseconds);
    public void Dispose() { watcher.Dispose(); signal.Dispose(); }
}
'@
    }
    $completionWatcher = [IsolatedUpdateEvidenceWatcher]::new($testRootFull)
    try {
    $restartExit = Invoke-Checked $updateExe @('--rootDir', $installRoot, 'start', 'UpdateSmokeProbe.exe', '--', '--read', $testRootFull, 'ignored') 'Restart installed B probe'
    $completionTimer = [Diagnostics.Stopwatch]::StartNew()
    $runtimeB = Get-Content -Raw -LiteralPath $runtimeEvidencePath | ConvertFrom-Json
    while ($runtimeB.phase -ne 'B') {
        $remainingMs = 45000 - [int]$completionTimer.ElapsedMilliseconds
        if ($remainingMs -le 0 -or -not $completionWatcher.Wait($remainingMs)) { throw 'Installed B did not finish its native restart scenario.' }
        $runtimeB = Get-Content -Raw -LiteralPath $runtimeEvidencePath | ConvertFrom-Json
    }
    if ($runtimeB.phase -ne 'B' -or $runtimeB.phaseBVersion -ne $b.Metadata.version -or $runtimeB.phaseAVersion -ne $a.Metadata.version -or
        -not $runtimeB.immutableHistoryRead -or $runtimeB.executorRerunCount -ne 0 -or -not $runtimeB.dpapiMarkerValidated) {
        throw 'Installed B did not retain the immutable Agent result and DPAPI settings.'
    }
    Assert-NativeComposition (Join-Path $testRootFull 'native-ui-b') 'Installed B' $b.Metadata.version
    } finally { $completionWatcher.Dispose() }
} else { $restartExit = $null }

$settingsHashAfter = (Get-FileHash -LiteralPath $settingsPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($settingsHashAfter -ne $settingsHashA) { throw 'Application settings changed during A to B update.' }
$roundTrip = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($fixtureCipher), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser))
if ($roundTrip -ne $fixtureToken) { throw 'Isolated DPAPI token did not round-trip.' }
$runValueAfter = (Get-ItemProperty -Path $runKey -Name 'ConnectorAgentDesktop' -ErrorAction SilentlyContinue).ConnectorAgentDesktop
if ($runValueBefore -ne $runValueAfter) { throw 'Production autostart identity changed during isolated smoke.' }

} finally {
    if ($registrationExisted) {
        $restoreKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($registrationPath)
        try {
            foreach ($name in $restoreKey.GetValueNames()) { if (-not $registrationBefore.ContainsKey($name)) { $restoreKey.DeleteValue($name) } }
            foreach ($name in $registrationBefore.Keys) { $restoreKey.SetValue($name, $registrationBefore[$name].value, $registrationBefore[$name].kind) }
        } finally { $restoreKey.Dispose() }
    } else { [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKey($registrationPath, $false) }
    $restoredKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($registrationPath)
    try {
        if (($null -ne $restoredKey) -ne $registrationExisted) { throw 'Isolated test registration was not restored.' }
        if ($restoredKey) {
            if ($restoredKey.GetValueNames().Count -ne $registrationBefore.Count) { throw 'Isolated test registration value count changed.' }
            foreach ($name in $registrationBefore.Keys) {
                $restoredValue = $restoredKey.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                if ($restoredKey.GetValueKind($name) -ne $registrationBefore[$name].kind -or
                    (ConvertTo-Json -InputObject $restoredValue -Compress) -cne (ConvertTo-Json -InputObject $registrationBefore[$name].value -Compress)) {
                    throw 'Isolated test registration value was not restored.'
                }
            }
        }
    } finally { if ($restoredKey) { $restoredKey.Dispose() } }
    foreach ($launcherPath in $launcherPaths) {
        $afterHash = if (Test-Path -LiteralPath $launcherPath) { (Get-FileHash -LiteralPath $launcherPath -Algorithm SHA256).Hash } else { $null }
        if ($afterHash -ne $launcherHashes[$launcherPath]) { throw "Production launcher changed: $launcherPath" }
    }
}
@{ schemaVersion = 1; outcome = 'passed'; appId = $TestAppId; packageA = $a.Metadata.version; packageB = $b.Metadata.version;
   executingVersionA = $runtimeA.phaseAVersion; executingVersionB = $(if ($NoRestart) { $null } else { $runtimeB.phaseBVersion });
   installRoot = $installRoot; downloadExit = $downloadExit; setupExit = $setupExit; probeWriteExit = $probeWriteExit; busyApplyExit = $busyApply.ExitCode;
   missingFeedExit = $missingFeedExit; tamperedExit = $tamperedExit; restartExit = $restartExit;
   settingsSha256 = $settingsHashAfter; autostartProductionValuePreserved = $true; dpapiRoundTrip = $true;
   productionLaunchersPreserved = $true; testRegistrationRestored = $true;
   realAgentDrainedBeforeApply = $true; nativeUiA = $true; nativeUiB = -not $NoRestart;
   actualMainWindowA = $true; actualMainWindowB = -not $NoRestart; realUserStatePreserved = $true;
   immutableHistoryRead = -not $NoRestart; executorRerunCount = $(if ($NoRestart) { $null } else { $runtimeB.executorRerunCount }) } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $testRootFull 'results.json') -Encoding utf8
Write-Host "PASS: isolated installed $($a.Metadata.version) -> $($b.Metadata.version); results: $(Join-Path $testRootFull 'results.json')"

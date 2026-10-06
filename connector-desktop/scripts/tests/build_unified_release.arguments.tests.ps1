$ErrorActionPreference = 'Stop'

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$parentPath = Join-Path $root 'scripts\build_unified_release.ps1'
$childPath = Join-Path $root 'scripts\build_package_update.ps1'
$tokens = $null
$errors = $null
$parentAst = [Management.Automation.Language.Parser]::ParseFile($parentPath, [ref]$tokens, [ref]$errors)
if ($errors.Count -or !$parentAst) { throw 'PARENT_PARSE_FAILED' }
$tokens = $null
$errors = $null
$childAst = [Management.Automation.Language.Parser]::ParseFile($childPath, [ref]$tokens, [ref]$errors)
if ($errors.Count -or !$childAst.ParamBlock) { throw 'CHILD_PARAMBLOCK_PARSE_FAILED' }

$invoke = @($parentAst.FindAll({
    param($node)
    $node -is [Management.Automation.Language.CommandAst] -and
    $node.Extent.Text -match '^&\s+\$script:BuildPackageUpdate\s+@packageParameters$'
}, $true))
if ($invoke.Count -ne 1) { throw 'CHILD_INVOKE_MUST_USE_PARAMETER_HASHTABLE' }
$assignment = @($parentAst.FindAll({
    param($node)
    $node -is [Management.Automation.Language.AssignmentStatementAst] -and
    $node.Left.Extent.Text -ceq '$packageParameters'
}, $true))
if ($assignment.Count -ne 1 -or $assignment[0].Right.Expression -isnot [Management.Automation.Language.HashtableAst]) {
    throw 'PACKAGE_PARAMETERS_MUST_BE_A_HASHTABLE'
}
$keys = @($assignment[0].Right.Expression.KeyValuePairs | ForEach-Object { $_.Item1.Value.ToString() } | Sort-Object)
$expectedKeys = @('Channel','FeedUrl','IfcWorkerPath','PackVersion','PackageOutputDir','UpdateSigningKeyPemPath') | Sort-Object
if ((Compare-Object $expectedKeys $keys).Count) { throw 'PACKAGE_PARAMETER_KEYS_MISMATCH' }

# Use the actual child script's ParamBlock so PowerShell performs the same script-parameter binding.
$fixturePath = Join-Path ([IO.Path]::GetTempPath()) ('build-package-update-args-' + [Guid]::NewGuid().ToString('N') + '.ps1')
$fixtureTail = @'
[ordered]@{FeedUrl=$FeedUrl;PackVersion=$PackVersion;Channel=$Channel;IfcWorkerPath=$IfcWorkerPath;UpdateSigningKeyPemPath=$UpdateSigningKeyPemPath;PackageOutputDir=$PackageOutputDir}|ConvertTo-Json -Compress
'@
[IO.File]::WriteAllText($fixturePath, $childAst.ParamBlock.Extent.Text + "`r`n" + $fixtureTail, [Text.UTF8Encoding]::new($false))
try {
    $sample = @{
        PackVersion = '1.1.0-preview.2'
        Channel = 'preview'
        FeedUrl = 'https://feed.example.invalid/updates'
        IfcWorkerPath = 'C:\fixture inputs\worker.exe'
        UpdateSigningKeyPemPath = 'C:\fixture keys\update.pem'
        PackageOutputDir = 'C:\fixture output\release package'
    }
    $legacy = @('-PackVersion',$sample.PackVersion,'-Channel',$sample.Channel,'-FeedUrl',$sample.FeedUrl,
        '-IfcWorkerPath',$sample.IfcWorkerPath,'-UpdateSigningKeyPemPath',$sample.UpdateSigningKeyPemPath,
        '-PackageOutputDir',$sample.PackageOutputDir)
    $legacyFailure = $null
    try { & $fixturePath @legacy | Out-Null } catch { $legacyFailure = $_.Exception.Message }
    if (!$legacyFailure -or $legacyFailure -notmatch "parameter 'FeedUrl'" -or $legacyFailure -notmatch '-PackVersion') {
        throw 'LEGACY_ARRAY_BINDING_FAILURE_NOT_REPRODUCED'
    }

    $actual = (& $fixturePath @sample) | ConvertFrom-Json -ErrorAction Stop
    foreach ($name in $expectedKeys) {
        if ([string]$actual.$name -cne [string]$sample[$name]) { throw ('HASHTABLE_BINDING_MISMATCH_' + $name) }
    }
    'BUILD_UNIFIED_RELEASE_ARGUMENTS_TESTS_PASSED'
} finally {
    Remove-Item -LiteralPath $fixturePath -Force -ErrorAction SilentlyContinue
}

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\connector_local_build_policy.ps1')
$assertions = 0
foreach ($case in @(
    @{Free=[ulong](8.5GB)-1; Commit=[ulong](6GB); Admitted=$false},
    @{Free=[ulong](8.5GB); Commit=[ulong](6GB)-1; Admitted=$false},
    @{Free=[ulong](8.5GB); Commit=[ulong](6GB); Admitted=$true},
    @{Free=[ulong](24GB); Commit=[ulong](24GB); Admitted=$true},
    @{Free=[ulong](0); Commit=[ulong](24GB); Admitted=$false}
)) {
    $actual = Get-ConnectorLocalBuildPolicy $case.Free $case.Commit
    if ($actual.CanStart -ne $case.Admitted) { throw 'Incorrect local build admission boundary.' }
    if ($actual.JobMemoryBytes -ne 2GB -or $actual.ReserveBytes -ne 6GB -or
        $actual.EarlyStopFreeBytes -ne 6.5GB -or $actual.CpuPercent -ne 25 -or $actual.WaitSeconds -ne 0) {
        throw 'Local build limits weakened or grew with available memory.'
    }
    $assertions += 2
}
"CONNECTOR_LOCAL_BUILD_POLICY_PASS: $assertions assertions; no allocation pressure or build."

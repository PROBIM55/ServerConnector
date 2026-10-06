$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptPath = Join-Path $PSScriptRoot '../SmbAclReconcile.ps1'
$functionNames = @('Assert-SmbSshDenyPolicy', 'Assert-SmbSshCanonicalCommandLine', 'Get-SmbSshEffectivePolicy', 'Get-SmbSshServicePolicy', 'Invoke-SmbLsaRightsOperation', 'Ensure-SmbServiceIdentityRights')
$tokens = $null
$parseErrors = $null
$sourceAst = [System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path $scriptPath), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw 'SMB reconciliation script has PowerShell parse errors.' }
$definitions = @($sourceAst.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $functionNames -contains $node.Name }, $true))
if ($definitions.Count -ne $functionNames.Count) { throw 'SMB reconciliation script is missing service identity functions.' }
Invoke-Expression (($definitions | ForEach-Object { $_.Extent.Text }) -join "`n")

$oldProgramData = $env:ProgramData
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('smb-service-identity-' + [guid]::NewGuid().ToString('N'))
try {
    $env:ProgramData = $fixtureRoot
    $sshDirectory = Join-Path $fixtureRoot 'ssh'
    [IO.Directory]::CreateDirectory($sshDirectory) | Out-Null
    $configPath = Join-Path $sshDirectory 'sshd_config'
    [IO.File]::WriteAllText($configPath, "DenyUsers scn_test`r`n")
    $binary = Join-Path $env:WINDIR 'System32/OpenSSH/sshd.exe'
    $now = [datetime]::Now.AddMinutes(2)

    Assert-SmbSshDenyPolicy -UserName 'scn_test' -SshdPath $binary -ConfigPath $configPath -ServiceStartTime $now -EffectiveOutput "port 22`ndenyusers scn_test"
    Assert-SmbSshDenyPolicy -UserName 'scn_test' -SshdPath $binary -ConfigPath $configPath -ServiceStartTime $now -EffectiveOutput "denyusers scn_*"
    $missingPolicyDenied = $false
    try { Assert-SmbSshDenyPolicy -UserName 'scn_test' -SshdPath $binary -ConfigPath $configPath -ServiceStartTime $now -EffectiveOutput "port 22`ndenyusers other_user" }
    catch { $missingPolicyDenied = $true }
    if (-not $missingPolicyDenied) { throw 'Missing effective DenyUsers policy was accepted.' }

    # Exercise the deployed, SHA-pinned script definitions with mocked OS boundaries.
    [IO.File]::WriteAllText($configPath, "DenyUsers scn_test`r`n")
    $binary = [IO.Path]::GetFullPath($binary)
    $script:fixtureService = [pscustomobject]@{ ProcessId = 1234; PathName = $binary }
    $script:fixtureProcess = [pscustomobject]@{ ProcessId = 1234; ExecutablePath = $binary; CommandLine = '"' + $binary + '"' }
    $script:effectiveCallCount = 0
    function Get-CimInstance {
        param([string]$ClassName, [string]$Filter, [string]$ErrorAction)
        if ($ClassName -eq 'Win32_Service') { return $script:fixtureService }
        if ($ClassName -eq 'Win32_Process') { return $script:fixtureProcess }
        throw 'Unexpected CIM class in fixture.'
    }
    function Get-Process {
        param([int]$Id, [string]$ErrorAction)
        return [pscustomobject]@{ StartTime = [datetime]::Now.AddMinutes(5) }
    }
    function Get-SmbSshEffectivePolicy {
        param([string]$SshdPath, [string]$UserName)
        $script:effectiveCallCount++
        if ($SshdPath -ne $binary -or $UserName -ne 'scn_test') { throw 'Unexpected fixture policy arguments.' }
        return 'denyusers scn_test'
    }
    Get-SmbSshServicePolicy -UserName 'scn_test'
    if ($script:effectiveCallCount -ne 1) { throw 'Canonical service command line was not accepted exactly once.' }

    foreach ($serviceArguments in @('-o DenyUsers=other', '-f C:\custom_sshd_config')) {
        $script:fixtureService.PathName = '"' + $binary + '" ' + $serviceArguments
        $rejected = $false
        try { Get-SmbSshServicePolicy -UserName 'scn_test' } catch { $rejected = $true }
        if (-not $rejected -or $script:effectiveCallCount -ne 1) { throw "Unsafe service arguments were not rejected before policy evaluation: $serviceArguments" }
    }
    $script:fixtureService.PathName = $binary
    $script:fixtureProcess.CommandLine = '"' + $binary + '" -o DenyUsers=other'
    $processOverrideDenied = $false
    try { Get-SmbSshServicePolicy -UserName 'scn_test' } catch { $processOverrideDenied = $true }
    if (-not $processOverrideDenied -or $script:effectiveCallCount -ne 1) { throw 'An active sshd process command-line override was accepted.' }

    [IO.File]::WriteAllText($configPath, "Include sshd_config.d/*.conf`r`nDenyUsers scn_test`r`n")
    $includeDenied = $false
    try { Assert-SmbSshDenyPolicy -UserName 'scn_test' -SshdPath $binary -ConfigPath $configPath -ServiceStartTime $now -EffectiveOutput "denyusers scn_test" }
    catch { $includeDenied = $true }
    if (-not $includeDenied) { throw 'Included OpenSSH configuration was accepted.' }

    $source = [IO.File]::ReadAllText((Resolve-Path $scriptPath))
    $ownerCheck = $source.IndexOf('Managed local user ownership marker does not match.', [StringComparison]::Ordinal)
    $policyCheck = $source.IndexOf('Get-SmbSshServicePolicy -UserName $userName', [StringComparison]::Ordinal)
    $passwordChange = $source.IndexOf('Set-LocalUser -Name $userName -Password', [StringComparison]::Ordinal)
    $newDisabled = $source.IndexOf('New-LocalUser -Name $userName -Password $secure', [StringComparison]::Ordinal)
    $rightsCheck = $source.LastIndexOf('Ensure-SmbServiceIdentityRights', [StringComparison]::Ordinal)
    $aclGrant = $source.IndexOf('Add-OwnedAcl $resource $account $sid', [StringComparison]::Ordinal)
    $enable = $source.IndexOf('Enable-LocalUser -Name $userName', [StringComparison]::Ordinal)
    if ($ownerCheck -lt 0 -or $policyCheck -lt $ownerCheck -or $passwordChange -lt $policyCheck) { throw 'Foreign identity or missing SSH policy is not rejected before password mutation.' }
    if ($newDisabled -lt $policyCheck -or $rightsCheck -lt $newDisabled -or $aclGrant -lt $rightsCheck -or $enable -lt $aclGrant) {
        throw 'New managed account must stay disabled until policy, rights, and ACL setup complete.'
    }

    $script:fixtureRights = @()
    $script:fixtureAddCount = 0
    $script:fixtureReadFails = $false
    function Invoke-SmbLsaRightsOperation {
        param([string]$Operation, [Security.Principal.SecurityIdentifier]$Sid, [string[]]$Rights)
        if ($script:fixtureReadFails -and $Operation -eq 'Read') { throw 'Fixture LSA read failure.' }
        if ($Operation -eq 'Add') { $script:fixtureAddCount++; $script:fixtureRights += $Rights; return }
        return $script:fixtureRights
    }
    $sid = [Security.Principal.SecurityIdentifier]::new('S-1-5-21-10-20-30-1001')
    Ensure-SmbServiceIdentityRights $sid
    if ($script:fixtureAddCount -ne 1 -or $script:fixtureRights.Count -ne 2) { throw 'Required deny logon rights were not added and read back.' }
    $script:fixtureReadFails = $true
    $rightsReadDenied = $false
    try { Ensure-SmbServiceIdentityRights $sid } catch { $rightsReadDenied = $true }
    if (-not $rightsReadDenied) { throw 'Unreadable LSA rights were accepted.' }
} finally {
    $env:ProgramData = $oldProgramData
    if (Test-Path -LiteralPath $fixtureRoot) { Remove-Item -LiteralPath $fixtureRoot -Recurse -Force }
}

Write-Output 'PASS: canonical running sshd executable and argument rejection; effective SSH deny policy and Include rejection; identity/policy/password order; disabled-new-user sequence; LSA right add/readback and fail-closed read.'

[CmdletBinding()]
param(
    [string]$MsiPath,
    [ValidateSet('Json', 'Object')]
    [string]$OutputFormat = 'Json'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Normalize-GuidText {
    param([Parameter(Mandatory = $true)][string]$Value)
    $parsed = [guid]::Empty
    if (![guid]::TryParse($Value, [ref]$parsed)) { throw "Invalid GUID: $Value" }
    return '{' + $parsed.ToString('D').ToUpperInvariant() + '}'
}

function ConvertTo-PackedMsiGuid {
    param([Parameter(Mandatory = $true)][string]$Value)
    $text = (Normalize-GuidText $Value).Trim('{}').Replace('-', '')
    $packed = -join $text.Substring(0, 8).ToCharArray()[7..0]
    $packed += -join $text.Substring(8, 4).ToCharArray()[3..0]
    $packed += -join $text.Substring(12, 4).ToCharArray()[3..0]
    for ($index = 16; $index -lt 32; $index += 2) {
        $packed += $text[$index + 1]
        $packed += $text[$index]
    }
    return $packed.ToUpperInvariant()
}

function Get-MsiProperties {
    param([Parameter(Mandatory = $true)][string]$Path)
    $installer = $null
    $database = $null
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $database = $installer.OpenDatabase($Path, 0)
        $properties = [ordered]@{}
        foreach ($name in @('ProductCode', 'UpgradeCode', 'ProductVersion', 'Manufacturer', 'ProductName')) {
            $view = $null
            $record = $null
            try {
                $view = $database.OpenView("SELECT Value FROM Property WHERE Property = '$name'")
                [void]$view.Execute()
                $record = $view.Fetch()
                if ($null -eq $record -or [string]::IsNullOrWhiteSpace($record.StringData(1))) {
                    throw "MSI property $name is missing."
                }
                $properties[$name] = [string]$record.StringData(1)
            }
            finally {
                if ($null -ne $record) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) }
                if ($null -ne $view) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) }
            }
        }
        return [pscustomobject]$properties
    }
    finally {
        if ($null -ne $database) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) }
        if ($null -ne $installer) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer) }
    }
}

function Get-NetBirdExpectedIdentity {
    param([Parameter(Mandatory = $true)][string]$Path)
    $scriptsRoot = $PSScriptRoot
    if ([string]::IsNullOrWhiteSpace($scriptsRoot)) {
        $scriptsRoot = Split-Path -Parent $MyInvocation.ScriptName
    }
    $projectRoot = Split-Path -Parent $scriptsRoot
    $verifierPath = Join-Path $scriptsRoot 'verify_netbird_installer.ps1'
    $lockPath = Join-Path $projectRoot 'infra/netbird/v0.79.0-windows-x64.lock.json'
    # The COM-based verifier currently emits a leading null before its result.
    # Select the one typed result explicitly and fail closed on any other output.
    $verificationOutput = @(& $verifierPath -MsiPath $Path)
    $verifiedCandidates = @($verificationOutput | Where-Object {
        $null -ne $_ -and $_.PSObject.Properties.Name -contains 'verified'
    })
    $unexpectedOutput = @($verificationOutput | Where-Object {
        $null -ne $_ -and $_.PSObject.Properties.Name -notcontains 'verified'
    })
    if ($verifiedCandidates.Count -ne 1 -or $unexpectedOutput.Count -ne 0) {
        throw 'Pinned NetBird MSI verifier returned an unexpected result.'
    }
    $verified = $verifiedCandidates[0]
    if (!$verified.verified) { throw 'Pinned NetBird MSI verification failed.' }
    $lock = Get-Content -Raw -LiteralPath $lockPath | ConvertFrom-Json
    $resolved = (Resolve-Path -LiteralPath $Path -ErrorAction Stop).Path
    $msi = Get-MsiProperties -Path $resolved
    if ($msi.ProductVersion -cne $verified.version) { throw 'Verified MSI identity changed during inspection.' }
    $programFiles = if ([string]::IsNullOrWhiteSpace($env:ProgramW6432)) { $env:ProgramFiles } else { $env:ProgramW6432 }
    $cliPath = [IO.Path]::GetFullPath((Join-Path $programFiles 'NetBird\netbird.exe'))
    $programData = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
    $markerPath = [IO.Path]::GetFullPath((Join-Path $programData 'Structura Connector\Network\netbird-install-owner.json'))
    [pscustomobject]@{
        version = [string]$verified.version
        productCode = Normalize-GuidText $msi.ProductCode
        upgradeCode = Normalize-GuidText $msi.UpgradeCode
        manufacturer = [string]$msi.Manufacturer
        productName = [string]$msi.ProductName
        installerSha256 = [string]$verified.sha256
        signerSubjectPrefix = [string]$lock.signerSubjectPrefix
        signerThumbprint = ([string]$lock.signerThumbprint).ToUpperInvariant()
        cliPath = $cliPath
        serviceName = 'NetBird'
        serviceStartType = 2
        markerPath = $markerPath
    }
}

function Test-ProtectedWindowsPath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$TrustedRoot
    )
    $reasons = [Collections.Generic.List[string]]::new()
    try {
        $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\')
        $fullRoot = [IO.Path]::GetFullPath($TrustedRoot).TrimEnd('\')
        if (!$fullPath.StartsWith($fullRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
            $reasons.Add('outside-trusted-root')
        }
        $trustedSids = @(
            'S-1-5-18',
            'S-1-5-32-544',
            'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464'
        )
        $writeMask = [Security.AccessControl.FileSystemRights]::Write -bor
            [Security.AccessControl.FileSystemRights]::Modify -bor
            [Security.AccessControl.FileSystemRights]::FullControl -bor
            [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
            [Security.AccessControl.FileSystemRights]::TakeOwnership -bor
            [Security.AccessControl.FileSystemRights]::Delete -bor
            [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles
        $relative = $fullPath.Substring([Math]::Min($fullRoot.Length, $fullPath.Length)).TrimStart('\')
        $current = $fullRoot
        foreach ($part in @($relative.Split('\', [StringSplitOptions]::RemoveEmptyEntries))) {
            $current = Join-Path $current $part
            $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                $reasons.Add("reparse:$current")
            }
            $acl = Get-Acl -LiteralPath $current -ErrorAction Stop
            $ownerSid = [Security.Principal.NTAccount]::new([string]$acl.Owner).Translate([Security.Principal.SecurityIdentifier]).Value
            if ($trustedSids -notcontains $ownerSid) { $reasons.Add("untrusted-owner:$current") }
            $rules = $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])
            foreach ($rule in $rules) {
                if ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
                    (($rule.FileSystemRights -band $writeMask) -ne 0) -and
                    $trustedSids -notcontains $rule.IdentityReference.Value) {
                    $reasons.Add("untrusted-writer:$current")
                    break
                }
            }
        }
    }
    catch {
        $reasons.Add('acl-inspection-failed')
    }
    [pscustomobject]@{ secure = ($reasons.Count -eq 0); reasons = @($reasons) }
}

function Get-NetBirdRegistrySnapshot {
    param([Parameter(Mandatory = $true)]$Expected)
    $entries = [Collections.Generic.List[object]]::new()
    $errors = [Collections.Generic.List[string]]::new()
    $upgradeMembers = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $productKeyPresent = $false
    $packedProduct = ConvertTo-PackedMsiGuid $Expected.productCode
    $packedUpgrade = ConvertTo-PackedMsiGuid $Expected.upgradeCode
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
        $base = $null
        try {
            $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, $view)
            $uninstall = $base.OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', $false)
            if ($null -eq $uninstall) { throw "HKLM uninstall key missing in $view." }
            try {
                foreach ($subName in $uninstall.GetSubKeyNames()) {
                    $sub = $uninstall.OpenSubKey($subName, $false)
                    if ($null -eq $sub) { continue }
                    try {
                        $displayName = [string]$sub.GetValue('DisplayName', '')
                        $normalizedCode = $null
                        try { $normalizedCode = Normalize-GuidText $subName } catch { }
                        if (($normalizedCode -eq $Expected.productCode) -or $displayName.StartsWith('NetBird', [StringComparison]::OrdinalIgnoreCase)) {
                            $entries.Add([pscustomobject]@{
                                view = [string]$view
                                key = $subName
                                productCode = $normalizedCode
                                displayName = $displayName
                                displayVersion = [string]$sub.GetValue('DisplayVersion', '')
                                publisher = [string]$sub.GetValue('Publisher', '')
                                installLocation = [string]$sub.GetValue('InstallLocation', '')
                                windowsInstaller = [int]$sub.GetValue('WindowsInstaller', 0)
                            })
                        }
                    }
                    finally { $sub.Dispose() }
                }
            }
            finally { $uninstall.Dispose() }
            $productKey = $base.OpenSubKey("SOFTWARE\Classes\Installer\Products\$packedProduct", $false)
            if ($null -ne $productKey) { $productKeyPresent = $true; $productKey.Dispose() }
            $upgradeKey = $base.OpenSubKey("SOFTWARE\Classes\Installer\UpgradeCodes\$packedUpgrade", $false)
            if ($null -ne $upgradeKey) {
                try { foreach ($member in $upgradeKey.GetValueNames()) { [void]$upgradeMembers.Add($member.ToUpperInvariant()) } }
                finally { $upgradeKey.Dispose() }
            }
        }
        catch { $errors.Add("registry-$view-unavailable") }
        finally { if ($null -ne $base) { $base.Dispose() } }
    }
    [pscustomobject]@{
        querySucceeded = ($errors.Count -eq 0)
        entries = @($entries)
        productKeyPresent = $productKeyPresent
        upgradeMembers = @($upgradeMembers)
        expectedUpgradeMembership = $upgradeMembers.Contains($packedProduct)
        errors = @($errors)
    }
}

function Get-ExecutableFromImagePath {
    param([string]$ImagePath)
    if ([string]::IsNullOrWhiteSpace($ImagePath)) { return $null }
    $expanded = [Environment]::ExpandEnvironmentVariables($ImagePath).Trim()
    if ($expanded.StartsWith('"')) {
        $end = $expanded.IndexOf('"', 1)
        if ($end -lt 2) { return $null }
        return $expanded.Substring(1, $end - 1)
    }
    $match = [regex]::Match($expanded, '^(?<exe>.+?\.exe)(?:\s|$)', [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (!$match.Success) { return $null }
    return $match.Groups['exe'].Value
}

function Get-NetBirdServiceSnapshot {
    param([Parameter(Mandatory = $true)]$Expected)
    try {
        $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
            [Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
        try { $key = $base.OpenSubKey("SYSTEM\CurrentControlSet\Services\$($Expected.serviceName)", $false) }
        finally { $base.Dispose() }
        if ($null -eq $key) {
            return [pscustomobject]@{ querySucceeded = $true; present = $false; name = $Expected.serviceName; imagePath = $null; executablePath = $null; pathMatch = $false; startType = $null; errors = @() }
        }
        try {
            $imagePath = [string]$key.GetValue('ImagePath', '')
            $startType = [int]$key.GetValue('Start', -1)
        }
        finally { $key.Dispose() }
        $executablePath = Get-ExecutableFromImagePath $imagePath
        $pathMatch = $false
        if (![string]::IsNullOrWhiteSpace($executablePath)) {
            try { $pathMatch = [IO.Path]::GetFullPath($executablePath).Equals($Expected.cliPath, [StringComparison]::OrdinalIgnoreCase) } catch { }
        }
        [pscustomobject]@{ querySucceeded = $true; present = $true; name = $Expected.serviceName; imagePath = $imagePath; executablePath = $executablePath; pathMatch = $pathMatch; startType = $startType; errors = @() }
    }
    catch {
        [pscustomobject]@{ querySucceeded = $false; present = $false; name = $Expected.serviceName; imagePath = $null; executablePath = $null; pathMatch = $false; startType = $null; errors = @('service-registry-unavailable') }
    }
}

function Test-NetBirdVersionText {
    param([string]$Actual, [string]$Expected)
    if ([string]::IsNullOrWhiteSpace($Actual)) { return $false }
    return [regex]::IsMatch($Actual.Trim(), '^' + [regex]::Escape($Expected) + '(?:\.\d+)?$', [Text.RegularExpressions.RegexOptions]::IgnoreCase)
}

function Get-NetBirdCliSnapshot {
    param([Parameter(Mandatory = $true)]$Expected)
    try {
        if (!(Test-Path -LiteralPath $Expected.cliPath -PathType Leaf)) {
            return [pscustomobject]@{ querySucceeded = $true; present = $false; path = $Expected.cliPath; pathMatch = $true; pathSecure = $false; version = $null; versionMatch = $false; signatureStatus = 'NotFound'; signerThumbprint = $null; signatureMatch = $false; errors = @() }
        }
        $file = Get-Item -LiteralPath $Expected.cliPath -Force
        $version = if (![string]::IsNullOrWhiteSpace($file.VersionInfo.ProductVersion)) { $file.VersionInfo.ProductVersion } else { $file.VersionInfo.FileVersion }
        $signature = Get-AuthenticodeSignature -LiteralPath $Expected.cliPath
        $subjectMatch = $null -ne $signature.SignerCertificate -and
            $signature.SignerCertificate.Subject.StartsWith($Expected.signerSubjectPrefix, [StringComparison]::Ordinal)
        $thumbprint = if ($null -eq $signature.SignerCertificate) { $null } else { $signature.SignerCertificate.Thumbprint.ToUpperInvariant() }
        $programFiles = Split-Path -Parent (Split-Path -Parent $Expected.cliPath)
        $pathSecurity = Test-ProtectedWindowsPath -Path $Expected.cliPath -TrustedRoot $programFiles
        [pscustomobject]@{
            querySucceeded = $true; present = $true; path = $file.FullName; pathMatch = $file.FullName.Equals($Expected.cliPath, [StringComparison]::OrdinalIgnoreCase)
            pathSecure = $pathSecurity.secure; pathSecurityReasons = $pathSecurity.reasons
            version = $version; versionMatch = (Test-NetBirdVersionText $version $Expected.version)
            signatureStatus = [string]$signature.Status; signerThumbprint = $thumbprint
            signatureMatch = ($signature.Status -eq [Management.Automation.SignatureStatus]::Valid -and $subjectMatch -and $thumbprint -eq $Expected.signerThumbprint)
            errors = @()
        }
    }
    catch {
        [pscustomobject]@{ querySucceeded = $false; present = $true; path = $Expected.cliPath; pathMatch = $false; pathSecure = $false; version = $null; versionMatch = $false; signatureStatus = 'Unknown'; signerThumbprint = $null; signatureMatch = $false; errors = @('cli-inspection-failed') }
    }
}

function Get-NetBirdMarkerSnapshot {
    param([Parameter(Mandatory = $true)]$Expected)
    if (!(Test-Path -LiteralPath $Expected.markerPath -PathType Leaf)) {
        return [pscustomobject]@{ querySucceeded = $true; present = $false; path = $Expected.markerPath; contentValid = $false; pathSecure = $false; valid = $false; errors = @() }
    }
    try {
        $file = Get-Item -LiteralPath $Expected.markerPath -Force
        if ($file.Length -gt 8192) { throw 'Marker exceeds size limit.' }
        $marker = Get-Content -Raw -LiteralPath $Expected.markerPath | ConvertFrom-Json
        $programData = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
        $pathSecurity = Test-ProtectedWindowsPath -Path $Expected.markerPath -TrustedRoot $programData
        $contentValid = $marker.schemaVersion -eq 1 -and $marker.provisioner -ceq 'StructuraConnector.NetBirdInstaller' -and
            $marker.version -ceq $Expected.version -and (Normalize-GuidText $marker.productCode) -ceq $Expected.productCode -and
            (Normalize-GuidText $marker.upgradeCode) -ceq $Expected.upgradeCode -and
            $marker.installerSha256 -ceq $Expected.installerSha256 -and
            ([IO.Path]::GetFullPath([string]$marker.cliPath)).Equals($Expected.cliPath, [StringComparison]::OrdinalIgnoreCase) -and
            $marker.serviceName -ceq $Expected.serviceName -and [int]$marker.serviceStartType -eq $Expected.serviceStartType
        [pscustomobject]@{
            querySucceeded = $true; present = $true; path = $Expected.markerPath; contentValid = $contentValid
            pathSecure = $pathSecurity.secure; pathSecurityReasons = $pathSecurity.reasons
            valid = ($contentValid -and $pathSecurity.secure); errors = @()
        }
    }
    catch {
        [pscustomobject]@{ querySucceeded = $true; present = $true; path = $Expected.markerPath; contentValid = $false; pathSecure = $false; valid = $false; errors = @('owner-marker-untrusted') }
    }
}

function Get-NetBirdWindowsSnapshot {
    param([Parameter(Mandatory = $true)]$Expected)
    [pscustomobject]@{
        registry = Get-NetBirdRegistrySnapshot $Expected
        service = Get-NetBirdServiceSnapshot $Expected
        cli = Get-NetBirdCliSnapshot $Expected
        marker = Get-NetBirdMarkerSnapshot $Expected
    }
}

function Test-NetBirdPreflightSnapshot {
    param(
        [Parameter(Mandatory = $true)]$Expected,
        [Parameter(Mandatory = $true)]$Snapshot
    )
    $reasons = [Collections.Generic.List[string]]::new()
    $checks = [Collections.Generic.List[object]]::new()
    $entries = @($Snapshot.registry.entries)
    $upgradeMembers = @($Snapshot.registry.upgradeMembers)
    $exactEntries = @($entries | Where-Object { $_.productCode -eq $Expected.productCode })
    $foreignEntries = @($entries | Where-Object { $_.productCode -ne $Expected.productCode })
    $inspectionOk = $Snapshot.registry.querySucceeded -and $Snapshot.service.querySucceeded -and $Snapshot.cli.querySucceeded -and $Snapshot.marker.querySucceeded
    $checks.Add([pscustomobject]@{ id = 'inspection-complete'; ok = $inspectionOk })
    $state = 'unknown'
    $ownership = 'unknown'
    if (!$inspectionOk) {
        $reasons.Add('inspection-incomplete')
    }
    elseif ($entries.Count -gt 1 -or $exactEntries.Count -gt 1 -or $upgradeMembers.Count -gt 1) {
        $state = 'multiple'; $ownership = 'untrusted'; $reasons.Add('multiple-installations')
    }
    elseif ($entries.Count -eq 0) {
        if ($Snapshot.service.present) { $state = 'service-only'; $ownership = 'foreign'; $reasons.Add('service-without-msi') }
        elseif ($Snapshot.cli.present -or $Snapshot.marker.present -or $Snapshot.registry.productKeyPresent -or $upgradeMembers.Count -gt 0) {
            $state = 'mismatch'; $ownership = 'untrusted'; $reasons.Add('orphaned-install-artifact')
        }
        else { $state = 'absent'; $ownership = 'none' }
    }
    elseif ($exactEntries.Count -eq 0 -or $foreignEntries.Count -gt 0) {
        $state = 'foreign'; $ownership = 'foreign'; $reasons.Add('foreign-netbird-installation')
    }
    else {
        $entry = $exactEntries[0]
        $packageOk = $entry.displayVersion -ceq $Expected.version -and $entry.publisher -ceq $Expected.manufacturer -and
            $entry.windowsInstaller -eq 1 -and $Snapshot.registry.productKeyPresent -and
            $Snapshot.registry.expectedUpgradeMembership -and $upgradeMembers.Count -eq 1
        $serviceOk = $Snapshot.service.present -and $Snapshot.service.pathMatch -and $Snapshot.service.startType -eq $Expected.serviceStartType
        $cliOk = $Snapshot.cli.present -and $Snapshot.cli.pathMatch -and $Snapshot.cli.pathSecure -and $Snapshot.cli.versionMatch -and $Snapshot.cli.signatureMatch
        $checks.Add([pscustomobject]@{ id = 'msi-registration'; ok = $packageOk })
        $checks.Add([pscustomobject]@{ id = 'service-identity'; ok = $serviceOk })
        $checks.Add([pscustomobject]@{ id = 'cli-identity'; ok = $cliOk })
        $checks.Add([pscustomobject]@{ id = 'owner-marker'; ok = $Snapshot.marker.valid })
        if (!$Snapshot.marker.present) {
            $state = 'foreign'; $ownership = 'foreign'; $reasons.Add('owner-marker-missing')
        }
        elseif (!$Snapshot.marker.valid) {
            $state = 'untrusted'; $ownership = 'untrusted'; $reasons.Add('owner-marker-untrusted')
        }
        elseif (!$packageOk -or !$serviceOk -or !$cliOk) {
            $state = 'mismatch'; $ownership = 'untrusted'; $reasons.Add('owned-installation-mismatch')
        }
        else { $state = 'owned'; $ownership = 'structura' }
    }
    $safeToInstall = $state -eq 'absent'
    $safeToUse = $state -eq 'owned'
    [pscustomobject]@{
        schemaVersion = 1
        readOnly = $true
        state = $state
        ownership = $ownership
        decision = if ($safeToInstall) { 'safe-to-install-candidate' } elseif ($safeToUse) { 'owned-ready' } else { 'blocked' }
        safeToInstall = $safeToInstall
        safeToUse = $safeToUse
        requiresPrivilegedRecheck = $true
        expected = $Expected
        observed = $Snapshot
        checks = @($checks)
        reasons = @($reasons)
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    if ([string]::IsNullOrWhiteSpace($MsiPath)) { throw 'MsiPath is required for a live preflight.' }
    try {
        $expected = Get-NetBirdExpectedIdentity -Path $MsiPath
        $snapshot = Get-NetBirdWindowsSnapshot -Expected $expected
        $result = Test-NetBirdPreflightSnapshot -Expected $expected -Snapshot $snapshot
    }
    catch {
        $result = [pscustomobject]@{
            schemaVersion = 1; readOnly = $true; state = 'unknown'; ownership = 'unknown'; decision = 'blocked'
            safeToInstall = $false; safeToUse = $false; requiresPrivilegedRecheck = $true
            expected = $null; observed = $null; checks = @([pscustomobject]@{ id = 'installer-verification'; ok = $false })
            reasons = @('installer-verification-or-preflight-failed')
        }
    }
    if ($OutputFormat -eq 'Object') { $result } else { $result | ConvertTo-Json -Depth 10 }
}

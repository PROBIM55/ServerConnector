$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$scriptPath = Join-Path $PSScriptRoot '../../smb-helper/SmbAclReconcile.ps1'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$errors)
if ($errors.Count -ne 0) { throw 'SMB script syntax errors.' }
foreach ($name in @('Get-OwnedSessions', 'Close-OwnedSessions')) {
    $function = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $false)
    if ($null -eq $function) { throw "Missing function $name" }
    Invoke-Expression $function.Extent.Text
}
$userName = 'cnb_0123456789abcdef'
$script:closed = [Collections.Generic.List[long]]::new()
$script:failRead = $false
function Get-SmbSession {
    [CmdletBinding()]param()
    if ($script:failRead) { throw 'Fixture readback unavailable.' }
    @(
        [pscustomobject]@{ SessionId = 1L; ClientUserName = "$env:COMPUTERNAME\$userName" },
        [pscustomobject]@{ SessionId = 2L; ClientUserName = "OTHERDOMAIN\$userName" },
        [pscustomobject]@{ SessionId = 3L; ClientUserName = $userName }
    )
}
function Close-SmbSession {
    [CmdletBinding()]param([long]$SessionId, [switch]$Force)
    $script:closed.Add($SessionId)
}
Close-OwnedSessions $userName
if ($script:closed.Count -ne 1 -or $script:closed[0] -ne 1L) { throw 'Foreign SMB session was selected.' }
if (@(Get-OwnedSessions $userName).Count -ne 1) { throw 'Readback uses a different ownership boundary.' }
$script:failRead = $true
$rejected = $false
try { Get-OwnedSessions $userName | Out-Null } catch { $rejected = $true }
if (-not $rejected) { throw 'Unavailable session readback was treated as empty.' }
Write-Output 'PASS: exact local principal; foreign sessions preserved; unavailable readback rejected.'

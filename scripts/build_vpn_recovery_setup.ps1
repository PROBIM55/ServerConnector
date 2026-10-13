[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$source = Join-Path $repo 'connector-desktop\tools\VpnRecoverySetup'
$payloadRoot = Join-Path $repo 'connector-desktop\scripts\vpn-recovery'
[void](New-Item -ItemType Directory -Path $OutputDirectory -Force)
$output = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) 'Structura.VpnRecovery.Setup.exe'
$compiler = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ "/out:$output" `
    /reference:System.Windows.Forms.dll /reference:System.Core.dll `
    "/win32manifest:$(Join-Path $source 'app.manifest')" `
    "/resource:$(Join-Path $payloadRoot 'connector_vpn_recovery.ps1'),connector_vpn_recovery.ps1" `
    "/resource:$(Join-Path $payloadRoot 'install_connector_vpn_recovery.ps1'),install_connector_vpn_recovery.ps1" `
    (Join-Path $source 'Program.cs')
if ($LASTEXITCODE -ne 0) { throw 'VPN recovery setup build failed.' }
$hash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  Structura.VpnRecovery.Setup.exe" | Set-Content -LiteralPath ($output + '.sha256') -Encoding ASCII
Get-Item -LiteralPath $output | Select-Object FullName, Length

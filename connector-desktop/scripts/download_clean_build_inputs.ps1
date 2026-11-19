#Requires -Version 7.4
# Pinned inputs of build_clean_connector_release.ps1 for the cloud update check (same sources and checks as the
# build-and-install job): protected access configuration, IFC worker, NetBird 0.79 MSI, WebView2 bootstrapper and the
# accepted 1.1.9 predecessor with its image manifest.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') { throw 'Hosted build only.' }
if ([string]::IsNullOrWhiteSpace($env:ACCESS_CONFIGURATION)) { throw 'Missing protected access configuration.' }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'OutputDirectory must be new.' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$access = Join-Path $OutputDirectory 'connector-access.json'
$accessConfig = $env:ACCESS_CONFIGURATION | ConvertFrom-Json
$accessConfig | Add-Member -Force -NotePropertyName smbLegacyEncryptedShareUncs -NotePropertyValue @('\\62.113.36.107\BIM_Models')
[IO.File]::WriteAllText($access, ($accessConfig | ConvertTo-Json -Depth 16), [Text.UTF8Encoding]::new($false))
Remove-Item Env:ACCESS_CONFIGURATION
$client = [Net.Http.HttpClient]::new()
try {
    foreach ($asset in @(@($env:IFC_WORKER_URL,'ifc.exe'), @($env:NETBIRD_MSI_URL,'netbird.msi'),
                         @('https://go.microsoft.com/fwlink/?linkid=2124701','webview2.exe'),
                         @('https://github.com/PROBIM55/ServerConnector/releases/download/platform-connector-v1.1.9/PlatformConnector-1.1.9.exe','accepted-1.1.9.exe'))) {
        $uri = [Uri]$asset[0]; if ($uri.Scheme -cne 'https' -or $uri.UserInfo) { throw 'Dependency must use HTTPS.' }
        $response = $client.GetAsync($uri, [Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        try {
            $response.EnsureSuccessStatusCode() | Out-Null
            if ($response.RequestMessage.RequestUri.Scheme -cne 'https') { throw 'Dependency left HTTPS.' }
            $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            try { $file = [IO.File]::Create((Join-Path $OutputDirectory $asset[1])); try { $stream.CopyTo($file) } finally { $file.Dispose() } } finally { $stream.Dispose() }
        } finally { $response.Dispose() }
    }
} finally { $client.Dispose() }
& (Join-Path $PSScriptRoot 'prepare_accepted_desktop_predecessor.ps1') -SetupExecutable (Join-Path $OutputDirectory 'accepted-1.1.9.exe') -OutputDirectory (Join-Path $OutputDirectory 'accepted-predecessor')

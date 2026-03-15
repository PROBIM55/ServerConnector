param(
    [string]$ServerBaseUrl = "https://server.structura-most.ru",
    [string]$BundledGitRoot = "",
    [string]$AdminApiKey = "",
    [switch]$CheckAdmin
)

$ErrorActionPreference = 'Stop'

function Write-Ok($name, $value) {
    Write-Output ("[OK] {0}: {1}" -f $name, $value)
}

function Write-Fail($name, $value) {
    Write-Output ("[FAIL] {0}: {1}" -f $name, $value)
}

try {
    $manifestUrl = ($ServerBaseUrl.TrimEnd('/')) + "/updates/tekla/firm/latest.json"
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $manifest = Invoke-RestMethod -Method GET -Uri $manifestUrl -TimeoutSec 20
    $sw.Stop()

    $required = @("version", "revision", "published_at", "target_path", "minimum_connector_version", "repo_url", "repo_ref")
    foreach ($k in $required) {
        if (-not $manifest.PSObject.Properties.Name.Contains($k)) {
            throw "manifest missing field '$k'"
        }
    }

    Write-Ok "manifest.version" $manifest.version
    Write-Ok "manifest.revision" $manifest.revision
    Write-Ok "manifest.latency_ms" $sw.ElapsedMilliseconds
}
catch {
    Write-Fail "manifest" $_.Exception.Message
    exit 1
}

if ($BundledGitRoot) {
    $gitBin = Join-Path $BundledGitRoot "bin/git.exe"
    $gitCmd = Join-Path $BundledGitRoot "cmd/git.exe"
    if (Test-Path $gitBin) {
        Write-Ok "bundled_git" $gitBin
    }
    elseif (Test-Path $gitCmd) {
        Write-Ok "bundled_git" $gitCmd
    }
    else {
        Write-Fail "bundled_git" "git.exe not found in bin/cmd"
        exit 1
    }
}

if ($CheckAdmin) {
    if ([string]::IsNullOrWhiteSpace($AdminApiKey)) {
        Write-Fail "admin_check" "AdminApiKey is required when -CheckAdmin is used"
        exit 1
    }

    try {
        $adminUrl = ($ServerBaseUrl.TrimEnd('/')) + "/admin/tekla/clients"
        $headers = @{ "X-Admin-Key" = $AdminApiKey }
        $data = Invoke-RestMethod -Method GET -Uri $adminUrl -Headers $headers -TimeoutSec 20
        $count = @($data.items).Count
        Write-Ok "admin.tekla_clients_count" $count
    }
    catch {
        Write-Fail "admin_check" $_.Exception.Message
        exit 1
    }
}

Write-Output "Smoke check completed successfully."

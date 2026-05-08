# Connector server-side deploy step.
#
# Запускается:
#   - GitHub Actions (.github/workflows/deploy-connector-server.yml) через SSH;
#   - либо вручную с VPS (`opwork_admin`) при необходимости.
#
# Делает:
#   1. git fetch + reset --hard origin/master в C:\Connector\src
#   2. pip install -r requirements.txt в runtime venv
#   3. backup connector.db в C:\Connector\backup\
#   4. python run_migrations.py (применяет pending миграции)
#   5. перезапускает Scheduled Task ConnectorApi
#   6. smoke test: GET /health должен ответить 200 и вернуть нужный SHA
#
# При любой ошибке выше шагов 4–6 — auto-rollback: возвращает src на
# предыдущий SHA и перезапускает Task. БД из бэкапа НЕ восстанавливается
# автоматически (большинство миграций forward-only ALTER ADD; для
# destructive — восстанавливать руками из C:\Connector\backup\).

param(
    [Parameter(Mandatory = $true)]
    [string]$CommitSha
)

$ErrorActionPreference = 'Stop'

$src        = 'C:\Connector\src'
$runtime    = 'C:\Connector\runtime'
$backup     = 'C:\Connector\backup'
$venvPython = Join-Path $runtime '.venv\Scripts\python.exe'
$serverDir  = Join-Path $src 'connector\server'
$reqsFile   = Join-Path $serverDir 'requirements.txt'

function Invoke-Step([string]$Name, [scriptblock]$Block) {
    Write-Host "==> $Name"

    # Native-command stderr merged via 2>&1 в PowerShell с $ErrorActionPreference='Stop'
    # триггерит выкидывание исключения даже при успешном exit code (git fetch пишет
    # progress в stderr). Поэтому внутри блока временно ослабляем EAP и опираемся
    # ТОЛЬКО на $LASTEXITCODE для определения провала native-команд.
    $oldEAP = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $Block
    }
    finally {
        $ErrorActionPreference = $oldEAP
    }

    if ($LASTEXITCODE -ne $null -and $LASTEXITCODE -ne 0) {
        throw "Step '$Name' exited with code $LASTEXITCODE"
    }
}

# Запомнить текущий SHA для rollback
$prevSha = (& git -C $src rev-parse HEAD).Trim()
Write-Host "PREV_SHA=$prevSha"
Write-Host "TARGET_SHA=$CommitSha"

try {
    Invoke-Step 'git fetch' { & git -C $src fetch origin master 2>&1 | Out-Host }
    Invoke-Step 'git reset --hard' { & git -C $src reset --hard origin/master 2>&1 | Out-Host }

    $newSha = (& git -C $src rev-parse HEAD).Trim()
    Write-Host "NEW_SHA=$newSha"

    if ($CommitSha -and ($newSha -ne $CommitSha)) {
        throw "Pulled SHA $newSha doesn't match expected $CommitSha"
    }

    Invoke-Step 'pip install requirements' {
        & $venvPython -m pip install --quiet -r $reqsFile 2>&1 | Out-Host
    }

    $ts = Get-Date -Format 'yyyy-MM-ddTHH-mm-ss'
    $backupFile = Join-Path $backup "connector.db.$prevSha-$ts"
    Invoke-Step "backup db -> $backupFile" {
        Copy-Item (Join-Path $runtime 'connector.db') $backupFile -Force
    }

    Invoke-Step 'run migrations' {
        $env:CONNECTOR_DB_PATH = Join-Path $runtime 'connector.db'
        & $venvPython (Join-Path $serverDir 'run_migrations.py') 2>&1 | Out-Host
    }

    Invoke-Step 'restart ConnectorApi' {
        schtasks /End /TN ConnectorApi 2>&1 | Out-Host
        Start-Sleep -Seconds 2
        schtasks /Run /TN ConnectorApi 2>&1 | Out-Host
        Start-Sleep -Seconds 5
    }

    Invoke-Step 'smoke test /health' {
        $resp = Invoke-RestMethod -Uri 'http://127.0.0.1:8080/health' -TimeoutSec 10
        if (-not $resp.ok) { throw "Health returned ok=false" }
        $expected = $newSha.Substring(0, 8)
        if ($resp.version -ne $expected) {
            throw "Deployed version mismatch: got '$($resp.version)', expected '$expected'"
        }
        Write-Host "HEALTH_OK version=$($resp.version)"
    }

    Write-Host "DEPLOY_OK $newSha"
}
catch {
    $err = $_
    Write-Warning "DEPLOY_FAIL: $err"
    Write-Warning "Rolling back to $prevSha"

    try {
        & git -C $src reset --hard $prevSha 2>&1 | Out-Host
        schtasks /End /TN ConnectorApi 2>&1 | Out-Host
        Start-Sleep -Seconds 2
        schtasks /Run /TN ConnectorApi 2>&1 | Out-Host
        Start-Sleep -Seconds 5
        $resp = Invoke-RestMethod -Uri 'http://127.0.0.1:8080/health' -TimeoutSec 10
        if ($resp.ok) { Write-Warning "ROLLBACK_OK to $prevSha" }
        else { Write-Warning "ROLLBACK_HEALTH_BAD" }
    }
    catch {
        Write-Warning "ROLLBACK_ITSELF_FAILED: $_"
    }

    throw $err
}

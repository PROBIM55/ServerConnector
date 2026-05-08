# Создание Connector БД в существующем PG-инстансе на VPS.
#
# PG-инстанс `127.0.0.1:5432` исторически принадлежит FamilyBudget,
# data dir `C:\apps\family-budget\postgres-data\`. Platform уже сосуществует
# с ним (`platform_prod`/`platform_user`). Этот скрипт добавляет
# `connector_prod`/`connector_user` с собственным сильным паролем,
# **без прав** на чужие БД.
#
# Метод аутентификации (взято из Platform'овского create_platform_postgres_db.ps1):
# временно добавляет `host all all 127.0.0.1/32 trust` в pg_hba.conf,
# создаёт role и database, в finally восстанавливает оригинальный pg_hba.
#
# Параметры передаются обязательным -DbPassword (не дефолтить пароль в коде).

param(
    [string]$PgDataDir = "C:\apps\family-budget\postgres-data",
    [string]$PgCtlPath = "C:\pgsql\pgsql\bin\pg_ctl.exe",
    [string]$PsqlPath  = "C:\pgsql\pgsql\bin\psql.exe",
    [string]$DbName    = "connector_prod",
    [string]$DbUser    = "connector_user",
    [Parameter(Mandatory = $true)][string]$DbPassword
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $PgDataDir)) { throw "Postgres data dir not found: $PgDataDir" }
if (-not (Test-Path $PgCtlPath))  { throw "pg_ctl not found: $PgCtlPath" }
if (-not (Test-Path $PsqlPath))   { throw "psql not found: $PsqlPath" }

$hbaPath = Join-Path $PgDataDir "pg_hba.conf"
if (-not (Test-Path $hbaPath)) { throw "pg_hba.conf not found: $hbaPath" }

$backupPath   = "$hbaPath.connector.bak"
$trustLine    = "host    all             all             127.0.0.1/32            trust"
$restoreNeeded = $false

try {
    Copy-Item $hbaPath $backupPath -Force

    $content = Get-Content $hbaPath -Raw
    if ($content -notmatch [Regex]::Escape($trustLine)) {
        Set-Content -Path $hbaPath -Value ($trustLine + [Environment]::NewLine + $content) -Encoding ASCII
        & $PgCtlPath reload -D $PgDataDir | Out-Null
        $restoreNeeded = $true
    }

    $safePassword = $DbPassword.Replace("'", "''")

    # Idempotent role creation.
    & $PsqlPath -h 127.0.0.1 -U postgres -d postgres -v ON_ERROR_STOP=0 `
        -c "CREATE ROLE $DbUser LOGIN PASSWORD '$safePassword';" | Out-Null
    & $PsqlPath -h 127.0.0.1 -U postgres -d postgres -v ON_ERROR_STOP=1 `
        -c "ALTER ROLE $DbUser WITH LOGIN PASSWORD '$safePassword';" | Out-Null

    # DB.
    $dbExists = & $PsqlPath -h 127.0.0.1 -U postgres -d postgres -Atqc `
        "SELECT 1 FROM pg_database WHERE datname='$DbName';"
    if ($LASTEXITCODE -ne 0) { throw "psql DB existence check failed: $LASTEXITCODE" }

    if (-not $dbExists) {
        & $PsqlPath -h 127.0.0.1 -U postgres -d postgres -v ON_ERROR_STOP=1 `
            -c "CREATE DATABASE $DbName OWNER $DbUser;" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "psql CREATE DATABASE failed: $LASTEXITCODE" }
    } else {
        & $PsqlPath -h 127.0.0.1 -U postgres -d postgres -v ON_ERROR_STOP=1 `
            -c "ALTER DATABASE $DbName OWNER TO $DbUser;" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "psql ALTER DATABASE failed: $LASTEXITCODE" }
    }

    # Изоляция: connector_user не должен иметь доступа к чужим БД.
    # CREATE DATABASE с OWNER уже даёт ему всё на $DbName и ничего на других — это default.
    # Доп. явный REVOKE на всякий случай:
    & $PsqlPath -h 127.0.0.1 -U postgres -d postgres -v ON_ERROR_STOP=0 `
        -c "REVOKE ALL ON DATABASE platform_prod FROM $DbUser;" | Out-Null

    Write-Host "PG_DB_OK $DbName / $DbUser"
}
finally {
    if ($restoreNeeded -and (Test-Path $backupPath)) {
        Move-Item -Path $backupPath -Destination $hbaPath -Force
        & $PgCtlPath reload -D $PgDataDir | Out-Null
        Write-Host "pg_hba.conf restored to original state."
    } elseif (Test-Path $backupPath) {
        Remove-Item $backupPath -Force
    }
}

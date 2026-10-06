# Phase F snapshot diff: сравнивает before/{scenario}.json и
# after/{scenario}.json — оба сохранены parity_snapshot.ps1.
# Выводит numeric diff с tolerance 0.01mm на координаты и exact match
# на материалы/имена. Exit code 0 = match, 1 = mismatch.

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Scenario,
    [string]$FixturesDir = (Join-Path $PSScriptRoot ".." | Join-Path -ChildPath "tests\fixtures\parity"),
    [double]$CoordTolerance = 0.01
)

$ErrorActionPreference = "Stop"

$beforePath = Join-Path $FixturesDir "before\$Scenario.json"
$afterPath = Join-Path $FixturesDir "after\$Scenario.json"

if (-not (Test-Path $beforePath)) { throw "Missing before snapshot: $beforePath. Run parity_snapshot.ps1 -Stage before first." }
if (-not (Test-Path $afterPath))  { throw "Missing after snapshot:  $afterPath. Run parity_snapshot.ps1 -Stage after first." }

$before = Get-Content $beforePath -Raw | ConvertFrom-Json
$after  = Get-Content $afterPath -Raw | ConvertFrom-Json

$mismatches = @()

# Структура ответа /component/read зависит от ComponentReadResult shape;
# обобщённо ожидаем 'component' с полями externalObjectId, teklaComponentGuid,
# parameters{...}, parts[...] (если adapter их выгружает).
function Diff-Field {
    param($name, $beforeVal, $afterVal, [double]$tol = 0)
    if ($beforeVal -eq $null -and $afterVal -eq $null) { return }
    if ($beforeVal -is [double] -or $beforeVal -is [int]) {
        if ([Math]::Abs([double]$beforeVal - [double]$afterVal) -gt $tol) {
            $script:mismatches += "$name`: $beforeVal != $afterVal (diff $([Math]::Abs([double]$beforeVal - [double]$afterVal)))"
        }
    } elseif ($beforeVal -ne $afterVal) {
        $script:mismatches += "$name`: '$beforeVal' != '$afterVal'"
    }
}

Write-Host "[parity_diff] $Scenario"
Write-Host "[parity_diff] before parts: $($before.component.parts.Count)"
Write-Host "[parity_diff] after  parts: $($after.component.parts.Count)"

if ($before.component.parts.Count -ne $after.component.parts.Count) {
    $mismatches += "parts count: $($before.component.parts.Count) != $($after.component.parts.Count)"
}

$count = [Math]::Min($before.component.parts.Count, $after.component.parts.Count)
for ($i = 0; $i -lt $count; $i++) {
    $b = $before.component.parts[$i]
    $a = $after.component.parts[$i]
    Diff-Field "parts[$i].name" $b.name $a.name
    Diff-Field "parts[$i].material" $b.material $a.material
    Diff-Field "parts[$i].profile" $b.profile $a.profile
    Diff-Field "parts[$i].class" $b.class $a.class
    if ($b.startPoint -and $a.startPoint) {
        Diff-Field "parts[$i].startPoint.x" $b.startPoint.x $a.startPoint.x $CoordTolerance
        Diff-Field "parts[$i].startPoint.y" $b.startPoint.y $a.startPoint.y $CoordTolerance
        Diff-Field "parts[$i].startPoint.z" $b.startPoint.z $a.startPoint.z $CoordTolerance
    }
    if ($b.endPoint -and $a.endPoint) {
        Diff-Field "parts[$i].endPoint.x" $b.endPoint.x $a.endPoint.x $CoordTolerance
        Diff-Field "parts[$i].endPoint.y" $b.endPoint.y $a.endPoint.y $CoordTolerance
        Diff-Field "parts[$i].endPoint.z" $b.endPoint.z $a.endPoint.z $CoordTolerance
    }
}

if ($mismatches.Count -eq 0) {
    Write-Host "[parity_diff] PASS — $Scenario matches within tolerance $CoordTolerance mm." -ForegroundColor Green
    exit 0
} else {
    Write-Host "[parity_diff] FAIL — $($mismatches.Count) mismatch(es):" -ForegroundColor Red
    foreach ($m in $mismatches) { Write-Host "  $m" -ForegroundColor Red }
    exit 1
}

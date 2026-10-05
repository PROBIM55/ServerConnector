[CmdletBinding()]
param(
    [string]$WorkspaceId = '9294458915f045ee8aa3a0ba0f6e7ade',
    [string]$GenerationId = 'd137628161c9442fbccc0744070debf5',
    [string]$OutputPath,
    [string]$DatabaseConnectionString = $env:CONNECTOR_CATALOG_DATABASE_CONNECTION_STRING,
    [string]$PsqlExecutable = $env:CONNECTOR_PSQL_EXECUTABLE,
    [double]$LineToleranceMm = 0.25,
    [double]$ArcToleranceMm = 0.5,
    [double]$PathJoinToleranceMm = 1.0,
    [double]$SectionDepthToleranceMm = 1.0
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $PSScriptRoot '..\deploy\component\FachwerkColumnProfiles.json'
}

function Get-ConnectionParts {
    $connection = $DatabaseConnectionString
    if ([string]::IsNullOrWhiteSpace($connection)) {
        throw 'Set CONNECTOR_CATALOG_DATABASE_CONNECTION_STRING for this administrative export. No server configuration file is read.'
    }

    $parts = @{}
    foreach ($pair in $connection -split ';') {
        if ($pair -match '^\s*([^=]+)=(.*)$') {
            $parts[$matches[1].Trim()] = $matches[2].Trim()
        }
    }
    return $parts
}

function Invoke-PlatformSqlJson([string]$Sql, [hashtable]$ConnectionParts) {
    $psql = $PsqlExecutable
    if ([string]::IsNullOrWhiteSpace($psql)) {
        $command = Get-Command psql -ErrorAction SilentlyContinue
        if ($command) { $psql = $command.Source }
    }
    if ([string]::IsNullOrWhiteSpace($psql) -or -not (Test-Path -LiteralPath $psql)) {
        throw 'Install the PostgreSQL client or set CONNECTOR_PSQL_EXECUTABLE to its psql executable.'
    }
    $previousPassword = [Environment]::GetEnvironmentVariable('PGPASSWORD', 'Process')
    $previousEncoding = [Environment]::GetEnvironmentVariable('PGCLIENTENCODING', 'Process')
    $temporaryOutput = [IO.Path]::GetTempFileName()
    try {
        $env:PGPASSWORD = $ConnectionParts['Password']
        $env:PGCLIENTENCODING = 'UTF8'
        & $psql -h $ConnectionParts['Host'] -p $ConnectionParts['Port'] -U $ConnectionParts['Username'] -d $ConnectionParts['Database'] -At -o $temporaryOutput -c $Sql
        if ($LASTEXITCODE -ne 0) { throw "PostgreSQL query failed with exit code $LASTEXITCODE." }
        $content = Get-Content -LiteralPath $temporaryOutput -Raw -Encoding UTF8
        if ([string]::IsNullOrWhiteSpace($content)) { throw 'PostgreSQL query returned no JSON.' }
        return $content | ConvertFrom-Json
    }
    finally {
        Remove-Item -LiteralPath $temporaryOutput -Force -ErrorAction SilentlyContinue
        [Environment]::SetEnvironmentVariable('PGPASSWORD', $previousPassword, 'Process')
        [Environment]::SetEnvironmentVariable('PGCLIENTENCODING', $previousEncoding, 'Process')
    }
}

function New-Point([double]$X, [double]$Y) {
    return [PSCustomObject]@{ X = $X; Y = $Y }
}

function Get-Distance($Left, $Right) {
    $dx = [double]$Right.X - [double]$Left.X
    $dy = [double]$Right.Y - [double]$Left.Y
    return [math]::Sqrt($dx * $dx + $dy * $dy)
}

function Get-PointAtLength($Points, $Lengths, [double]$Distance) {
    for ($index = 1; $index -lt $Points.Count; $index++) {
        if ($Distance -gt $Lengths[$index] + 1e-8) { continue }
        $span = $Lengths[$index] - $Lengths[$index - 1]
        $t = if ($span -lt 1e-9) { 0 } else { ($Distance - $Lengths[$index - 1]) / $span }
        return New-Point `
            ($Points[$index - 1].X + ($Points[$index].X - $Points[$index - 1].X) * $t) `
            ($Points[$index - 1].Y + ($Points[$index].Y - $Points[$index - 1].Y) * $t)
    }
    return $Points[$Points.Count - 1]
}

function Get-SlicedPoints($SourcePoints, [double]$FromT, [double]$ToT) {
    if ($SourcePoints.Count -lt 2) { return @() }
    $lengths = New-Object 'System.Collections.Generic.List[double]'
    $lengths.Add(0)
    for ($index = 1; $index -lt $SourcePoints.Count; $index++) {
        $lengths.Add($lengths[$index - 1] + (Get-Distance $SourcePoints[$index - 1] $SourcePoints[$index]))
    }
    $total = $lengths[$lengths.Count - 1]
    if ($total -lt 1e-9) { return @($SourcePoints) }
    $startDistance = [math]::Max(0, [math]::Min(1, $FromT)) * $total
    $endDistance = [math]::Max(0, [math]::Min(1, $ToT)) * $total
    $reverse = $startDistance -gt $endDistance
    if ($reverse) { $temporary = $startDistance; $startDistance = $endDistance; $endDistance = $temporary }

    $result = New-Object 'System.Collections.Generic.List[object]'
    $result.Add((Get-PointAtLength $SourcePoints $lengths $startDistance))
    for ($index = 1; $index -lt $SourcePoints.Count - 1; $index++) {
        if ($lengths[$index] -gt $startDistance + 1e-7 -and $lengths[$index] -lt $endDistance - 1e-7) {
            $result.Add($SourcePoints[$index])
        }
    }
    $result.Add((Get-PointAtLength $SourcePoints $lengths $endDistance))
    $array = $result.ToArray()
    if ($reverse) { [array]::Reverse($array) }
    return $array
}

function Get-Circle($First, $Middle, $Last) {
    $ax = [double]$First.X; $ay = [double]$First.Y
    $bx = [double]$Middle.X; $by = [double]$Middle.Y
    $cx = [double]$Last.X; $cy = [double]$Last.Y
    $d = 2 * ($ax * ($by - $cy) + $bx * ($cy - $ay) + $cx * ($ay - $by))
    if ([math]::Abs($d) -lt 1e-8) { return $null }
    $a2 = $ax * $ax + $ay * $ay
    $b2 = $bx * $bx + $by * $by
    $c2 = $cx * $cx + $cy * $cy
    $x = ($a2 * ($by - $cy) + $b2 * ($cy - $ay) + $c2 * ($ay - $by)) / $d
    $y = ($a2 * ($cx - $bx) + $b2 * ($ax - $cx) + $c2 * ($bx - $ax)) / $d
    $center = New-Point $x $y
    return [PSCustomObject]@{ Center = $center; Radius = Get-Distance $center $First }
}

function Get-NormalizedAngle([double]$Angle) {
    while ($Angle -le -[math]::PI) { $Angle += 2 * [math]::PI }
    while ($Angle -gt [math]::PI) { $Angle -= 2 * [math]::PI }
    return $Angle
}

function Convert-ToPrimitives($Points, [double]$LineTolerance, [double]$ArcTolerance) {
    if ($Points.Count -lt 2) { return @() }
    $first = $Points[0]
    $last = $Points[$Points.Count - 1]
    $chord = Get-Distance $first $last
    if ($chord -lt 1e-8) { return @() }

    $maxLineResidual = 0.0
    foreach ($point in $Points) {
        $residual = [math]::Abs(($last.X - $first.X) * ($first.Y - $point.Y) - ($first.X - $point.X) * ($last.Y - $first.Y)) / $chord
        $maxLineResidual = [math]::Max($maxLineResidual, $residual)
    }
    if ($maxLineResidual -le $LineTolerance) {
        return @([PSCustomObject]@{ kind = 'line'; start = $first; end = $last })
    }

    $circle = Get-Circle $first $Points[[int][math]::Floor(($Points.Count - 1) / 2)] $last
    if ($null -ne $circle -and $circle.Radius -lt 10000000) {
        $maxCircleResidual = 0.0
        foreach ($point in $Points) {
            $maxCircleResidual = [math]::Max($maxCircleResidual, [math]::Abs((Get-Distance $circle.Center $point) - $circle.Radius))
        }
        if ($maxCircleResidual -le $ArcTolerance) {
            $angles = foreach ($point in $Points) { [math]::Atan2($point.Y - $circle.Center.Y, $point.X - $circle.Center.X) }
            $sweep = 0.0
            for ($index = 1; $index -lt $angles.Count; $index++) { $sweep += Get-NormalizedAngle ($angles[$index] - $angles[$index - 1]) }
            $sweepDeg = $sweep * 180 / [math]::PI
            if ([math]::Abs($sweepDeg) -gt 0.01) {
                return @([PSCustomObject]@{ kind = 'arc'; start = $first; end = $last; center = $circle.Center; sweepDeg = $sweepDeg })
            }
        }
    }

    $segments = New-Object 'System.Collections.Generic.List[object]'
    for ($index = 1; $index -lt $Points.Count; $index++) {
        if ((Get-Distance $Points[$index - 1] $Points[$index]) -gt 1e-6) {
            $segments.Add([PSCustomObject]@{ kind = 'line'; start = $Points[$index - 1]; end = $Points[$index] })
        }
    }
    return @($segments)
}

function Convert-ToLocalPoint($Point, $Anchor) {
    return [ordered]@{
        x = [double]$Point.X - [double]$Anchor.x
        y = [double]$Point.Y - [double]$Anchor.y
    }
}

function Convert-ToPath($Role, $Profile, [double]$NormalOffset, $SemanticEdges, $Curves, $Anchor) {
    $primitives = New-Object 'System.Collections.Generic.List[object]'
    $previousEnd = $null
    foreach ($edge in $SemanticEdges | Sort-Object ordinal) {
        $curve = $Curves[$edge.curveObjectId]
        if ($null -eq $curve -or $curve.Count -lt 2) {
            throw "Semantic curve '$($edge.curveObjectId)' for role '$Role' is missing or degenerate."
        }
        $points = Get-SlicedPoints $curve ([double]$edge.fromT) ([double]$edge.toT)
        $converted = @(Convert-ToPrimitives $points $LineToleranceMm $ArcToleranceMm)
        if ($converted.Count -eq 0) {
            throw "Semantic curve '$($edge.curveObjectId)' for role '$Role' produced no primitives."
        }
        foreach ($primitive in $converted) {
            if ($null -ne $previousEnd) {
                $gap = Get-Distance $previousEnd $primitive.start
                if ($gap -gt $PathJoinToleranceMm) {
                    throw "Semantic chain '$Role' has a $([math]::Round($gap, 3)) mm gap before curve '$($edge.curveObjectId)'."
                }
            }
            $record = [ordered]@{
                kind = $primitive.kind
                start = Convert-ToLocalPoint $primitive.start $Anchor
                end = Convert-ToLocalPoint $primitive.end $Anchor
            }
            if ($primitive.kind -eq 'arc') {
                $record.center = Convert-ToLocalPoint $primitive.center $Anchor
                $record.sweepDeg = [double]$primitive.sweepDeg
            }
            $primitives.Add([PSCustomObject]$record)
            $previousEnd = $primitive.end
        }
    }
    if ($primitives.Count -eq 0) { throw "No usable primitives for role '$Role'." }
    return [ordered]@{
        role = $Role
        profile = $Profile
        material = 'C355-5'
        className = '3'
        normalOffset = $NormalOffset
        primitives = $primitives.ToArray()
    }
}

function Get-PathEndPoint($Path, [bool]$AtStart) {
    if ($AtStart) { return $Path.primitives[0].start }
    return $Path.primitives[$Path.primitives.Count - 1].end
}

function Assert-SectionDepth($InnerPath, $OuterPath, [bool]$AtStart, [double]$Expected, [string]$ProfileMark) {
    $innerPoint = Get-PathEndPoint $InnerPath $AtStart
    $outerPoint = Get-PathEndPoint $OuterPath $AtStart
    $actual = Get-Distance $innerPoint $outerPoint
    if ([math]::Abs($actual - $Expected) -gt $SectionDepthToleranceMm) {
        $position = if ($AtStart) { 'bottom' } else { 'top' }
        throw "Profile '$ProfileMark' has $([math]::Round($actual, 3)) mm $position section depth; expected $Expected mm."
    }
}

function Get-SectionTransition($InnerPath, [string]$ProfileMark) {
    $matches = @($InnerPath.primitives | Where-Object {
        $_.kind -eq 'line' -and
        [math]::Abs((([double]$_.end.y - [double]$_.start.y) - 400.0)) -le $LineToleranceMm
    })
    if ($matches.Count -ne 1) {
        throw "Profile '$ProfileMark' must contain exactly one ascending 400 mm section transition; found $($matches.Count)."
    }
    return [ordered]@{
        startY = [double]$matches[0].start.y
        endY = [double]$matches[0].end.y
        lowerWebProfile = 'PL25*350'
        upperWebProfile = 'PL25*280'
    }
}

$parts = Get-ConnectionParts
$sql = @"
with semantic_rows as (
    select semantic.id, semantic.source_profile_key, semantic.label,
           semantic.insertion_x, semantic.insertion_y, semantic.insertion_z,
           coalesce((
               select jsonb_agg(jsonb_build_object(
                   'role', edge.role,
                   'ordinal', edge.ordinal,
                   'curveObjectId', edge.curve_object_id,
                   'fromT', edge.from_t,
                   'toT', edge.to_t
               ) order by edge.role, edge.ordinal)
               from tekla_kmd_profile_semantic_edges edge
               where edge.profile_semantic_id = semantic.id
           ), '[]'::jsonb) as edges
    from tekla_kmd_profile_semantics semantic
    where semantic.workspace_id = '$WorkspaceId'
), part_profiles as (
    select placement.source_profile_key,
           object.category,
           object.profile,
           avg(
               ((object.center_line[1] + object.center_line[4]) / 2 - placement.origin_x) * placement.normal_x +
               ((object.center_line[2] + object.center_line[5]) / 2 - placement.origin_y) * placement.normal_y +
               ((object.center_line[3] + object.center_line[6]) / 2 - placement.origin_z) * placement.normal_z
           ) as normal_offset,
           count(*) as part_count
    from tekla_kmd_profile_placements placement
    join tekla_kmd_model_objects object
      on object.generation_id = placement.target_generation_id
     and object.assembly_guid = placement.target_assembly_guid
    where placement.workspace_id = '$WorkspaceId'
      and placement.target_generation_id = '$GenerationId'
      and object.category in ('column-web', 'column-flange')
      and cardinality(object.center_line) >= 6
    group by placement.source_profile_key, object.category, object.profile
), library as (
    select library_json::jsonb as value
    from scene_import_library
    where project_id = (select project_id from tekla_kmd_workspaces where id = '$WorkspaceId')
)
select jsonb_build_object(
    'semantics', coalesce(jsonb_agg(jsonb_build_object(
        'key', semantic.source_profile_key,
        'mark', semantic.label,
        'anchor', jsonb_build_object('x', semantic.insertion_x, 'y', semantic.insertion_y, 'z', semantic.insertion_z),
        'edges', semantic.edges
    ) order by semantic.label), '[]'::jsonb),
    'parts', coalesce((select jsonb_agg(jsonb_build_object(
        'key', source_profile_key,
        'category', category,
        'profile', profile,
        'normalOffset', normal_offset,
        'count', part_count
    )) from part_profiles), '[]'::jsonb),
    'objects', (select value -> 'objects' from library)
) from semantic_rows semantic;
"@

$source = Invoke-PlatformSqlJson $sql $parts
$curves = @{}
foreach ($object in $source.objects) {
    if ($object.kind -ne 'curve' -or $null -eq $object.curve.points) { continue }
    $curves[$object.id] = @($object.curve.points | ForEach-Object { New-Point ([double]$_[0]) ([double]$_[1]) })
}

$partRecords = @{}
foreach ($part in $source.parts) {
    if (-not $partRecords.ContainsKey($part.key)) { $partRecords[$part.key] = @() }
    $partRecords[$part.key] += $part
}

$profiles = New-Object 'System.Collections.Generic.List[object]'
foreach ($semantic in $source.semantics) {
    $anchor = $semantic.anchor
    $inner = @($semantic.edges | Where-Object { $_.role -eq 'inner' })
    $outer = @($semantic.edges | Where-Object { $_.role -eq 'outer' })
    if ($inner.Count -eq 0 -or $outer.Count -eq 0) { throw "Profile '$($semantic.mark)' is missing semantic edges." }

    $records = @($partRecords[$semantic.key])
    $web = @($records | Where-Object { $_.category -eq 'column-web' })
    $flange = @($records | Where-Object { $_.category -eq 'column-flange' })
    if ($web.Count -eq 0 -or $flange.Count -eq 0) { throw "Profile '$($semantic.mark)' has no scanned web/flange cross-section data." }

    $webOffset = 77.5
    $innerPath = Convert-ToPath 'inner-flange' 'PL50*180' 0 $inner $curves $anchor
    $outerPath = Convert-ToPath 'outer-flange' 'PL50*180' 0 $outer $curves $anchor
    Assert-SectionDepth $innerPath $outerPath $true 450.0 $semantic.mark
    Assert-SectionDepth $innerPath $outerPath $false 380.0 $semantic.mark
    $transition = Get-SectionTransition $innerPath $semantic.mark
    $profiles.Add([ordered]@{
        key = $semantic.mark
        mark = $semantic.mark
        sectionTransition = $transition
        paths = @(
            (Convert-ToPath 'inner-web' 'PL25*350' (-$webOffset) $inner $curves $anchor),
            (Convert-ToPath 'outer-web' 'PL25*350' $webOffset $outer $curves $anchor),
            $innerPath,
            $outerPath
        )
    })
}

if ($profiles.Count -ne 47) { throw "Expected 47 profiles, exported $($profiles.Count)." }
$catalog = [ordered]@{
    schema = 'fachwerk-column-catalog/v2'
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    source = 'accepted DXF semantic paths + verified Fachwerk KM section contract'
    profiles = $profiles.ToArray()
}

$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
$directory = Split-Path -Parent $resolvedOutput
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$catalog | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $resolvedOutput -Encoding UTF8
Write-Host "Exported $($profiles.Count) Fachwerk column profiles to $resolvedOutput"

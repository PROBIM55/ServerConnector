[CmdletBinding()]
param(
    [string]$TeklaVersion = "2020.0",
    [string]$TeklaExtensionsRoot = $null,
    [string]$TeklaMacroRoot = $null,
    [switch]$SkipBuild,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$teklaBin = "C:\TeklaStructures\$TeklaVersion\nt\bin\plugins"
if (-not (Test-Path $teklaBin)) {
    throw "Tekla Open API plugins directory does not exist: $teklaBin"
}
$teklaDialogsBin = "C:\TeklaStructures\$TeklaVersion\nt\bin\dialogs"
if (-not (Test-Path $teklaDialogsBin)) {
    throw "Tekla Open API dialogs directory does not exist: $teklaDialogsBin"
}
if (-not $TeklaExtensionsRoot) {
    # This exact path is scanned by Tekla 2020 in XS_PLUGIN_DEVELOPER_MODE.
    # The session log must report this path when the DLL is loaded.
    $TeklaExtensionsRoot = "C:\TeklaStructures\$TeklaVersion\Environments\common\extensions\custom\FachwerkKmd"
}
if (-not $TeklaMacroRoot) {
    # Tekla 2020 discovers C# modeling macros only in the standard
    # `modeling` subdirectory of each XS_MACRO_DIRECTORY root.
    $TeklaMacroRoot = "C:\TeklaStructures\$TeklaVersion\Environments\common\macros\modeling"
}

Write-Host "[fachwerk] source: $repoRoot"
Write-Host "[fachwerk] target: $TeklaExtensionsRoot"
Write-Host "[fachwerk] macro:  $TeklaMacroRoot"

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backupRoot = Join-Path $env:LOCALAPPDATA "Platform\TeklaPluginBackups\FachwerkKmd\$stamp"
$environmentRoot = "C:\TeklaStructures\$TeklaVersion\Environments"

function Move-FachwerkArtifactToBackup {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Category
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $categoryRoot = Join-Path $backupRoot $Category
    New-Item -ItemType Directory -Force -Path $categoryRoot | Out-Null
    $destination = Join-Path $categoryRoot ([IO.Path]::GetFileName($Path))
    if (Test-Path -LiteralPath $destination) {
        $destination = Join-Path $categoryRoot ("{0}-{1}{2}" -f `
            [IO.Path]::GetFileNameWithoutExtension($Path),
            [Guid]::NewGuid().ToString("N"),
            [IO.Path]::GetExtension($Path))
    }

    Move-Item -LiteralPath $Path -Destination $destination -Force
    Write-Host "[fachwerk] archived $Path -> $destination"
}

function Copy-FachwerkArtifactToBackup {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Category
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $categoryRoot = Join-Path $backupRoot $Category
    New-Item -ItemType Directory -Force -Path $categoryRoot | Out-Null
    $destination = Join-Path $categoryRoot ([IO.Path]::GetFileName($Path))
    if (Test-Path -LiteralPath $destination) {
        $destination = Join-Path $categoryRoot ("{0}-{1}{2}" -f `
            [IO.Path]::GetFileNameWithoutExtension($Path),
            [Guid]::NewGuid().ToString("N"),
            [IO.Path]::GetExtension($Path))
    }

    Copy-Item -LiteralPath $Path -Destination $destination -Force
    Write-Host "[fachwerk] backed up $Path -> $destination"
}

$sectionGeometrySource = Join-Path $repoRoot "src\FachwerkColumnPlugin\FachwerkColumnSectionGeometry.cs"
$sectionGeometryText = Get-Content -LiteralPath $sectionGeometrySource -Raw
if ($sectionGeometryText -match 'new\s+ContourPlate\s*\(') {
    throw "Fachwerk column wall contract violation: ContourPlate construction is forbidden."
}
if ($sectionGeometryText -notmatch 'new\s+PolyBeam\s*\(') {
    throw "Fachwerk column wall contract violation: PolyBeam construction was not found."
}

if (-not $SkipBuild) {
    Push-Location $repoRoot
    try {
        dotnet build "src\FachwerkColumnPlugin\FachwerkColumnPlugin.csproj" -c Release "/p:TeklaBin=$teklaBin" | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "FachwerkColumnPlugin build failed." }
        dotnet build "src\ControlledNumberingTool\ControlledNumberingTool.csproj" -c Release "/p:TeklaBin=$teklaBin" "/p:TeklaNtBin=C:\TeklaStructures\$TeklaVersion\nt\bin" | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "ControlledNumberingTool build failed." }
    } finally { Pop-Location }
}

$files = @(
    @{ Source = "src\FachwerkColumnPlugin\bin\Release\net48\FachwerkColumnPlugin.dll"; Destination = "FachwerkColumnPlugin.dll" },
    @{ Source = "src\ControlledNumberingTool\bin\Release\net48\ControlledNumberingTool.dll"; Destination = "ControlledNumberingTool.dll" },
    @{ Source = "deploy\component\FachwerkColumnPlugin.inp"; Destination = "FachwerkColumnPlugin.inp" },
    @{ Source = "deploy\component\FachwerkColumnProfiles.json"; Destination = "FachwerkColumnProfiles.json" }
)

$macros = @(
    @{ Source = "deploy\macro\ReloadFachwerkPlugin.cs"; Destination = "ReloadFachwerkPlugin.cs" },
    @{ Source = "deploy\macro\ControlledNumbering.cs"; Destination = "ControlledNumbering.cs" }
)

if ($DryRun) {
    $files | ForEach-Object { Write-Host "[dry-run] $($_.Source) -> $TeklaExtensionsRoot\$($_.Destination)" }
    $macros | ForEach-Object { Write-Host "[dry-run] $($_.Source) -> $TeklaMacroRoot\$($_.Destination)" }
    $global:LASTEXITCODE = 0
    exit 0
}

New-Item -ItemType Directory -Force -Path $TeklaExtensionsRoot | Out-Null

# Tekla scans every active plugin assembly under the extensions tree. Keeping
# backups or an experimental V2 registration next to the canonical DLL creates
# two catalog entries and makes it impossible to know which implementation is
# executing. Archive all such artefacts outside the Tekla scan path first.
$staleExtensionArtifacts = Get-ChildItem -LiteralPath $TeklaExtensionsRoot -File -ErrorAction SilentlyContinue |
    Where-Object {
        $_.Name -like "*.bak-*" -or
        $_.Name -like "FachwerkColumnPluginV2*" -or
        $_.Name -like "FachwerkRigelPlugin*"
    }
foreach ($artifact in $staleExtensionArtifacts) {
    Move-FachwerkArtifactToBackup -Path $artifact.FullName -Category "extensions"
}

# The autonomous rigel is a third Plugin type in FachwerkColumnPlugin.dll.
# Archive every earlier standalone DLL/INP so Tekla cannot discover a stale
# competing assembly from another environment extension directory.
$standaloneRigelArtifacts = Get-ChildItem -LiteralPath $environmentRoot -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^FachwerkRigelPlugin.*\.(dll|inp)$' }
foreach ($artifact in $standaloneRigelArtifacts) {
    Move-FachwerkArtifactToBackup -Path $artifact.FullName -Category "standalone-rigel"
}

foreach ($file in $files) {
    $source = Join-Path $repoRoot $file.Source
    $destination = Join-Path $TeklaExtensionsRoot $file.Destination
    if (-not (Test-Path $source)) { throw "Build artefact is missing: $source" }
    if (Test-Path $destination) {
        Move-FachwerkArtifactToBackup -Path $destination -Category "extensions"
    }
    Copy-Item $source $destination -Force
    Write-Host "[fachwerk] deployed $($file.Destination)"
}

New-Item -ItemType Directory -Force -Path $TeklaMacroRoot | Out-Null

foreach ($macro in $macros) {
    $macroSource = Join-Path $repoRoot $macro.Source
    $macroDestination = Join-Path $TeklaMacroRoot $macro.Destination
    if (-not (Test-Path $macroSource)) { throw "Macro artefact is missing: $macroSource" }

    # Keep exactly one catalog source for each managed macro. The canonical source
    # remains continuously present while Tekla is open; stale compiled files and
    # duplicate sources in other environment folders are archived.
    $macroBaseName = [IO.Path]::GetFileNameWithoutExtension($macro.Destination)
    $macroArtifacts = Get-ChildItem -LiteralPath $environmentRoot -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like "$macroBaseName.*" }
    foreach ($artifact in $macroArtifacts) {
        if ($artifact.FullName -ieq $macroDestination) {
            Copy-FachwerkArtifactToBackup -Path $artifact.FullName -Category "macros"
        } else {
            Move-FachwerkArtifactToBackup -Path $artifact.FullName -Category "macros"
        }
    }

    Copy-Item $macroSource $macroDestination -Force
    Get-Item -LiteralPath $macroDestination | ForEach-Object {
        $_.LastWriteTimeUtc = [DateTime]::UtcNow
    }
    Write-Host "[fachwerk] deployed $($macro.Destination)"
}

$activeColumnDlls = @(Get-ChildItem -LiteralPath $environmentRoot -Recurse -File -ErrorAction Stop |
    Where-Object { $_.Name -match '^FachwerkColumnPlugin.*\.dll$' })
if ($activeColumnDlls.Count -ne 1 -or $activeColumnDlls[0].FullName -ne (Join-Path $TeklaExtensionsRoot "FachwerkColumnPlugin.dll")) {
    $activeNames = ($activeColumnDlls | ForEach-Object Name) -join ", "
    throw "Expected exactly one active Fachwerk column DLL, found: $activeNames"
}

$activeColumnInps = @(Get-ChildItem -LiteralPath $environmentRoot -Recurse -File -ErrorAction Stop |
    Where-Object { $_.Name -match '^FachwerkColumnPlugin.*\.inp$' })
if ($activeColumnInps.Count -ne 1 -or $activeColumnInps[0].FullName -ne (Join-Path $TeklaExtensionsRoot "FachwerkColumnPlugin.inp")) {
    $activeNames = ($activeColumnInps | ForEach-Object Name) -join ", "
    throw "Expected exactly one active Fachwerk column INP, found: $activeNames"
}

$activeRigelDlls = @(Get-ChildItem -LiteralPath $environmentRoot -Recurse -File -ErrorAction Stop |
    Where-Object { $_.Name -match '^FachwerkRigelPlugin.*\.dll$' })
if ($activeRigelDlls.Count -ne 0) {
    $activeNames = ($activeRigelDlls | ForEach-Object FullName) -join ", "
    throw "Standalone Fachwerk rigel DLLs must not remain active: $activeNames"
}

$activeRigelInps = @(Get-ChildItem -LiteralPath $environmentRoot -Recurse -File -ErrorAction Stop |
    Where-Object { $_.Name -match '^FachwerkRigelPlugin.*\.inp$' })
if ($activeRigelInps.Count -ne 0) {
    $activeNames = ($activeRigelInps | ForEach-Object FullName) -join ", "
    throw "Standalone Fachwerk rigel INPs must not remain active: $activeNames"
}

foreach ($macro in $macros) {
    $macroDestination = Join-Path $TeklaMacroRoot $macro.Destination
    $activeMacroSources = @(Get-ChildItem -LiteralPath $environmentRoot -Recurse -File -ErrorAction Stop |
        Where-Object { $_.Name -eq $macro.Destination })
    if ($activeMacroSources.Count -ne 1 -or $activeMacroSources[0].FullName -ne $macroDestination) {
        $activeNames = ($activeMacroSources | ForEach-Object FullName) -join ", "
        throw "Expected exactly one $($macro.Destination) source, found: $activeNames"
    }
}

$sourceDll = Join-Path $repoRoot "src\FachwerkColumnPlugin\bin\Release\net48\FachwerkColumnPlugin.dll"
$deployedDll = Join-Path $TeklaExtensionsRoot "FachwerkColumnPlugin.dll"
$sourceHash = (Get-FileHash -LiteralPath $sourceDll -Algorithm SHA256).Hash
$deployedHash = (Get-FileHash -LiteralPath $deployedDll -Algorithm SHA256).Hash
if ($sourceHash -ne $deployedHash) {
    throw "Deployed FachwerkColumnPlugin.dll hash does not match the Release build."
}

$numberingSourceDll = Join-Path $repoRoot "src\ControlledNumberingTool\bin\Release\net48\ControlledNumberingTool.dll"
$numberingDeployedDll = Join-Path $TeklaExtensionsRoot "ControlledNumberingTool.dll"
$numberingSourceHash = (Get-FileHash -LiteralPath $numberingSourceDll -Algorithm SHA256).Hash
$numberingDeployedHash = (Get-FileHash -LiteralPath $numberingDeployedDll -Algorithm SHA256).Hash
if ($numberingSourceHash -ne $numberingDeployedHash) {
    throw "Deployed ControlledNumberingTool.dll hash does not match the Release build."
}

$requiredRigelConnectionTypes = @(
    "Structura.Tekla.Fachwerk.FachwerkColumnRigelConnectionPlugin",
    "Structura.Tekla.Fachwerk.FachwerkColumnRigelConnectionForm",
    "Structura.Tekla.Fachwerk.FachwerkColumnRigelConnectionPluginData"
)
$teklaAssemblyPaths = @(
    (Join-Path $teklaBin "Tekla.Structures.dll"),
    (Join-Path $teklaBin "Tekla.Structures.Model.dll"),
    (Join-Path $teklaBin "Tekla.Structures.Plugins.dll"),
    (Join-Path $teklaDialogsBin "Tekla.Structures.Dialog.dll")
)
foreach ($assemblyPath in $teklaAssemblyPaths) {
    if (-not (Test-Path -LiteralPath $assemblyPath)) {
        throw "Tekla dependency required for registration verification is missing: $assemblyPath"
    }
    [void][Reflection.Assembly]::LoadFrom($assemblyPath)
}
$deployedAssembly = [Reflection.Assembly]::LoadFrom($deployedDll)
$missingRigelConnectionTypes = @(
    $requiredRigelConnectionTypes |
        Where-Object { $null -eq $deployedAssembly.GetType($_, $false, $false) }
)
if ($missingRigelConnectionTypes.Count -ne 0) {
    throw "Fachwerk rigel connection registration is incomplete: $($missingRigelConnectionTypes -join ', ')"
}

$requiredRigelInsertConnectionTypes = @(
    "Structura.Tekla.Fachwerk.FachwerkColumnRigelInsertConnectionPlugin",
    "Structura.Tekla.Fachwerk.FachwerkColumnRigelInsertConnectionForm",
    "Structura.Tekla.Fachwerk.FachwerkColumnRigelInsertConnectionPluginData",
    "Structura.Tekla.Fachwerk.FachwerkColumnRigelInsertConnectionGeometry",
    "Structura.Tekla.Fachwerk.FachwerkColumnRigelInsertConnectionTeklaAdapter"
)
$missingRigelInsertConnectionTypes = @(
    $requiredRigelInsertConnectionTypes |
        Where-Object { $null -eq $deployedAssembly.GetType($_, $false, $false) }
)
if ($missingRigelInsertConnectionTypes.Count -ne 0) {
    throw "Fachwerk rigel insert connection registration is incomplete: $($missingRigelInsertConnectionTypes -join ', ')"
}

$requiredRigelInsertConnectionFields = @(
    "fkri_side",
    "fkri_up_ctrl",
    "fkri_lo_ctrl",
    "fkri_up_of_dep",
    "fkri_up_if_dep",
    "fkri_up_w_dep",
    "fkri_lo_of_dep",
    "fkri_lo_if_dep",
    "fkri_lo_w_dep",
    "fkri_fl_h",
    "fkri_web_add",
    "fkri_overlap",
    "fkri_insf_ang",
    "fkri_partf_ang",
    "fkri_insw_ang",
    "fkri_partw_ang",
    "fkri_direct_ang",
    "fkri_root"
)
$tooLongRigelInsertConnectionFields = @(
    $requiredRigelInsertConnectionFields |
        Where-Object { $_.Length -gt 19 }
)
if ($tooLongRigelInsertConnectionFields.Count -ne 0) {
    throw "Fachwerk rigel insert connection data contract exceeds Tekla 2020's 19-character attribute limit: $($tooLongRigelInsertConnectionFields -join ', ')"
}
$rigelInsertConnectionDataType = $deployedAssembly.GetType(
    "Structura.Tekla.Fachwerk.FachwerkColumnRigelInsertConnectionPluginData",
    $true,
    $false)
$registeredRigelInsertConnectionFields = @(
    $rigelInsertConnectionDataType.GetFields() |
        ForEach-Object {
            $_.GetCustomAttributes($false) |
                Where-Object { $_.GetType().Name -eq "StructuresFieldAttribute" } |
                ForEach-Object { $_.AttributeName }
        }
)
$missingRigelInsertConnectionFields = @(
    $requiredRigelInsertConnectionFields |
        Where-Object { $_ -notin $registeredRigelInsertConnectionFields }
)
if ($missingRigelInsertConnectionFields.Count -ne 0) {
    throw "Fachwerk rigel insert connection data contract is missing fields: $($missingRigelInsertConnectionFields -join ', ')"
}

$requiredLowerRigelNodeTypes = @(
    "Structura.Tekla.Fachwerk.FachwerkLowerRigelNodePlugin",
    "Structura.Tekla.Fachwerk.FachwerkLowerRigelNodeForm",
    "Structura.Tekla.Fachwerk.FachwerkLowerRigelNodePluginData",
    "Structura.Tekla.Fachwerk.FachwerkLowerRigelNodeGeometry",
    "Structura.Tekla.Fachwerk.FachwerkLowerRigelBottomClosure",
    "Structura.Tekla.Fachwerk.FachwerkLowerRigelNodeAutoLength",
    "Structura.Tekla.Fachwerk.FachwerkLowerRigelNodeTeklaAdapter"
)
$missingLowerRigelNodeTypes = @(
    $requiredLowerRigelNodeTypes |
        Where-Object { $null -eq $deployedAssembly.GetType($_, $false, $false) }
)
if ($missingLowerRigelNodeTypes.Count -ne 0) {
    throw "Fachwerk lower-rigel node registration is incomplete: $($missingLowerRigelNodeTypes -join ', ')"
}

$requiredLowerRigelNodeFields = @(
    "fklr_gap",
    "fklr_diameter",
    "fklr_plate_profile",
    "fklr_material",
    "fklr_class",
    "fklr_plate_width",
    "fklr_left_length",
    "fklr_right_length",
    "fklr_auto_len",
    "fklr_bottom_cap",
    "fklr_axis_corr",
    "fklr_control_line"
)
$tooLongLowerRigelNodeFields = @(
    $requiredLowerRigelNodeFields |
        Where-Object { $_.Length -gt 19 }
)
if ($tooLongLowerRigelNodeFields.Count -ne 0) {
    throw "Fachwerk lower-rigel node data contract exceeds Tekla 2020's 19-character attribute limit: $($tooLongLowerRigelNodeFields -join ', ')"
}

$lowerRigelNodeDataType = $deployedAssembly.GetType(
    "Structura.Tekla.Fachwerk.FachwerkLowerRigelNodePluginData",
    $true,
    $false)
$registeredLowerRigelNodeFields = @(
    $lowerRigelNodeDataType.GetFields() |
        ForEach-Object {
            $_.GetCustomAttributes($false) |
                Where-Object { $_.GetType().Name -eq "StructuresFieldAttribute" } |
                ForEach-Object { $_.AttributeName }
        }
)
$missingLowerRigelNodeFields = @(
    $requiredLowerRigelNodeFields |
        Where-Object { $_ -notin $registeredLowerRigelNodeFields }
)
if ($missingLowerRigelNodeFields.Count -ne 0) {
    throw "Fachwerk lower-rigel node data contract is missing fields: $($missingLowerRigelNodeFields -join ', ')"
}

$requiredRigelTypes = @(
    "Structura.Tekla.Fachwerk.FachwerkRigelPlugin",
    "Structura.Tekla.Fachwerk.FachwerkRigelForm",
    "Structura.Tekla.Fachwerk.FachwerkRigelPluginData",
    "Structura.Tekla.Fachwerk.FachwerkRigelCatalog",
    "Structura.Tekla.Fachwerk.FachwerkRigelGeometry"
)
$missingRigelTypes = @(
    $requiredRigelTypes |
        Where-Object { $null -eq $deployedAssembly.GetType($_, $false, $false) }
)
if ($missingRigelTypes.Count -ne 0) {
    throw "Fachwerk rigel component registration is incomplete: $($missingRigelTypes -join ', ')"
}
if ($deployedAssembly.GetManifestResourceNames() -notcontains "Structura.Tekla.Fachwerk.FachwerkRigelCatalog.json") {
    throw "Fachwerk rigel component does not contain its embedded support-axis catalog."
}

$requiredRigelFields = @(
    "fr_101_offset",
    "fr_103_offset",
    "fr_rs2_offset",
    "fr_rs2_d00",
    "fr_rs2_d01",
    "fr_material",
    "fr_class"
) + @(
    0..15 | ForEach-Object { "fr_101_d{0:D2}" -f $_ }
) + @(
    0..15 | ForEach-Object { "fr_103_d{0:D2}" -f $_ }
) + @(
    0..15 | ForEach-Object { "fr_101_t{0:D2}" -f $_ }
) + @(
    0..15 | ForEach-Object { "fr_103_t{0:D2}" -f $_ }
) + @(
    0..1 | ForEach-Object { "fr_rs2_t{0:D2}" -f $_ }
)
$rigelDataType = $deployedAssembly.GetType(
    "Structura.Tekla.Fachwerk.FachwerkRigelPluginData",
    $true,
    $false)
$registeredRigelFields = @(
    $rigelDataType.GetFields() |
        ForEach-Object {
            $_.GetCustomAttributes($false) |
                Where-Object { $_.GetType().Name -eq "StructuresFieldAttribute" } |
                ForEach-Object { $_.AttributeName }
        }
)
$missingRigelFields = @($requiredRigelFields | Where-Object { $_ -notin $registeredRigelFields })
if ($missingRigelFields.Count -ne 0) {
    throw "Fachwerk rigel data contract is missing fields: $($missingRigelFields -join ', ')"
}

Write-Host "[fachwerk] verified column, both rigel-connection components, lower-rigel-node, and autonomous-rigel registrations in one DLL; SHA256=$deployedHash"
Write-Host "[numbering] verified ControlledNumberingTool.dll and ControlledNumbering.cs; SHA256=$numberingDeployedHash"
Write-Host "[fachwerk] backups: $backupRoot"
Write-Warning "If Tekla was open while duplicate/V2 DLLs were present, close every Tekla 2020 process and start Tekla once. ReloadFachwerkPlugin cannot remove an assembly identity that is already loaded in that process."
Write-Host "[fachwerk] later single-DLL updates can use ReloadFachwerkPlugin while XS_PLUGIN_DEVELOPER_MODE=TRUE."
$global:LASTEXITCODE = 0

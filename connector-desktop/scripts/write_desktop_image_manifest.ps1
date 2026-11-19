[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Directory,
    [Parameter(Mandatory)][string]$OutputFile,
    [Parameter(Mandatory)][string]$Version,
    [string]$AcceptedPredecessorManifest
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') { throw 'Hosted image verification only.' }
$root=[IO.Path]::GetFullPath($Directory).TrimEnd('\','/')
if (!(Test-Path -LiteralPath $root -PathType Container)) { throw 'Image directory missing.' }
$files=@(Get-ChildItem -LiteralPath $root -Recurse -File -Force | Where-Object Extension -ne '.pdb' | Sort-Object FullName | ForEach-Object {
    if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse image file.' }
    $pe=if ($_.Extension -in @('.dll','.exe')) { [Diagnostics.FileVersionInfo]::GetVersionInfo($_.FullName) } else { $null }
    $fileVersion=if ($pe -and $pe.FileMajorPart -ge 0 -and $pe.FileVersion) { '{0}.{1}.{2}.{3}' -f $pe.FileMajorPart,$pe.FileMinorPart,$pe.FileBuildPart,$pe.FilePrivatePart } else { $null }
    @{path=[IO.Path]::GetRelativePath($root,$_.FullName).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant();fileVersion=$fileVersion}
})
if ($files.Count -lt 100 -or @($files|Where-Object path -ceq 'Connector.Desktop.exe').Count -ne 1) { throw 'Incomplete desktop image.' }
$baselineSha=$null
if ($AcceptedPredecessorManifest) {
    $old=Get-Content -LiteralPath $AcceptedPredecessorManifest -Raw|ConvertFrom-Json
    if ($old.version -cne '1.1.9' -or $old.predecessorExeSha256 -cne '2ea234dea9238f59bc4def852d9b0ef72872612706ed70891b53a6b2aecff769') { throw 'Unauthenticated predecessor manifest.' }
    $oldByPath=@{};foreach($file in $old.files){$oldByPath.Add([string]$file.path,$file)}
    foreach($file in $files) {
        $previous=$oldByPath[$file.path]
        if ($previous -and $previous.fileVersion -and !$file.fileVersion) { throw ('Versioned MSI key file became unversioned: '+$file.path) }
        if ($previous -and $previous.fileVersion -and $file.fileVersion -and [Version]$file.fileVersion -lt [Version]$previous.fileVersion) { throw ('MSI key file version regressed: '+$file.path) }
    }
    $runtime=Get-Content -LiteralPath (Join-Path $root 'Connector.Desktop.runtimeconfig.json') -Raw|ConvertFrom-Json
    foreach($framework in @('Microsoft.NETCore.App','Microsoft.WindowsDesktop.App')) {
        $resolved=@($runtime.runtimeOptions.includedFrameworks|Where-Object name -ceq $framework)
        if($resolved.Count -ne 1 -or $resolved[0].version -cne '8.0.32'){throw 'Unexpected self-contained runtime pack.'}
    }
    foreach($critical in @('coreclr.dll','hostfxr.dll','hostpolicy.dll','createdump.exe')) {
        $resolved=@($files|Where-Object path -ceq $critical)
        $previous=$oldByPath[$critical]
        if($resolved.Count -ne 1 -or !$resolved[0].fileVersion -or !$previous -or !$previous.fileVersion -or
            [Version]$resolved[0].fileVersion -lt [Version]$previous.fileVersion){throw 'Runtime key file is missing or below its accepted predecessor.'}
    }
    $baselineSha=(Get-FileHash -LiteralPath $AcceptedPredecessorManifest).Hash.ToLowerInvariant()
}
@{schemaVersion=1;version=$Version;sdkVersion=$(if($AcceptedPredecessorManifest){'8.0.426'}else{$null});runtimeVersion='8.0.32';predecessorManifestSha256=$baselineSha;files=$files}|
    ConvertTo-Json -Depth 6|Set-Content -LiteralPath $OutputFile -Encoding utf8

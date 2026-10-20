[CmdletBinding()]
param([Parameter(Mandatory)][string]$ImageManifest,[Parameter(Mandatory)][string]$InstalledDirectory)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
if($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted'){throw 'Hosted installed-image check only.'}
$root=[IO.Path]::GetFullPath($InstalledDirectory).TrimEnd('\','/')
$prefix=$root+[IO.Path]::DirectorySeparatorChar
$manifest=Get-Content -LiteralPath $ImageManifest -Raw|ConvertFrom-Json
$expected=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($file in $manifest.files){
    $path=[IO.Path]::GetFullPath((Join-Path $root ([string]$file.path)))
    if(!$path.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or !$expected.Add($path)){throw 'Invalid or duplicate image path.'}
    if(!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -ne $file.bytes -or
        (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -cne $file.sha256){throw ('Installed image differs: '+$file.path)}
}
if($expected.Count -lt 100){throw 'Incomplete expected image.'}
foreach($file in Get-ChildItem -LiteralPath $root -Recurse -File -Force){if(!$expected.Contains($file.FullName)){throw 'Predecessor-only or unknown file remained in the installed image.'}}

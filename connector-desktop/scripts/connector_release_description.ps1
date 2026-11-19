#Requires -Version 7.4
# Network-free rules of the signed Platform Connector release description (schema 2, §3.5 of the update design).
# Dot-sourced by new_release_description.ps1, sign_release_description.ps1, publish_connector_release.ps1,
# sign_ci_release_documents.ps1 and tests/publish_connector_release.tests.ps1.
#
# ConvertFrom-ReleaseDescription mirrors ReleaseDescriptionVerifier.Verify of
# connector-desktop/src/Connector.Updates/ReleaseDescription.cs rule for rule; Assert-OwnReleaseDescription adds the
# stricter rules of what this pipeline itself produces (canonical bytes, 1..8 notes of at most 200 characters).
# The only function that ever sees the private key is Invoke-PinnedReleaseSignature: it reads the key from the
# environment of the calling step, clears that variable at once and never returns or prints it.
Set-StrictMode -Version Latest

$script:ConnectorReleaseRepository = 'PROBIM55/ServerConnector'
$script:ConnectorReleaseChannels = @('pilot', 'stable')
$script:ConnectorReleaseApplicationId = 'Structura.PlatformConnector'
# The first version that installs updates by itself; older installations never read a description.
$script:ConnectorReleaseDefaultMinimumVersion = '1.1.15'
$script:ConnectorReleaseRootProperties = @('schemaVersion', 'applicationId', 'channel', 'version', 'publishedAtUtc', 'paused', 'minimumVersion', 'installer', 'notes')
$script:ConnectorReleaseInstallerProperties = @('fileName', 'size', 'sha256')
$script:ConnectorReleaseMaximumDescriptionBytes = 65536
$script:ConnectorReleaseMaximumInstallerBytes = 2L * 1024 * 1024 * 1024
$script:ConnectorReleaseCandidateWorkflow = '.github/workflows/build-unified-connector-candidate.yml'
$script:ConnectorReleaseP256Oid = '1.2.840.10045.3.1.7'
$script:ConnectorReleaseUtcFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'"

function Stop-ConnectorRelease([string]$Code, [string]$Detail) { throw "${Code}: $Detail" }

function Get-PinnedReleaseKeyPath {
    [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Connector.Desktop\Assets\update-public-key.pem'))
}

# ReleaseVersion.TryParse: exactly three decimal parts, 1..5 digits each, no leading zeros, each <= 65535.
function Get-ConnectorReleaseVersion([AllowNull()][AllowEmptyString()][string]$Value) {
    if ([string]::IsNullOrEmpty($Value) -or $Value.Length -gt 17) { return $null }
    $parts = $Value.Split('.')
    if ($parts.Length -ne 3) { return $null }
    $numbers = [int[]]::new(3)
    for ($index = 0; $index -lt 3; $index++) {
        $part = $parts[$index]
        if ($part.Length -eq 0 -or $part.Length -gt 5 -or ($part.Length -gt 1 -and [int]$part[0] -eq 48)) { return $null }
        foreach ($character in $part.ToCharArray()) { if ([int]$character -lt 48 -or [int]$character -gt 57) { return $null } }
        $number = [int]::Parse($part, [Globalization.NumberStyles]::None, [Globalization.CultureInfo]::InvariantCulture)
        if ($number -gt 65535) { return $null }
        $numbers[$index] = $number
    }
    return [version]::new($numbers[0], $numbers[1], $numbers[2])
}

function Format-ConnectorReleaseVersion([version]$Version) { "$($Version.Major).$($Version.Minor).$($Version.Build)" }

# A note shown to the user: no control, format (bidirectional overrides, zero-width), line/paragraph separator,
# private-use or unpaired surrogate characters, no surrounding white space.
function Test-ReleaseNoteText([AllowEmptyString()][string]$Note, [int]$MaximumLength) {
    if ($Note.Length -eq 0 -or $Note.Length -gt $MaximumLength -or $Note.Trim().Length -ne $Note.Length) { return $false }
    for ($index = 0; $index -lt $Note.Length; $index++) {
        $character = $Note[$index]
        if ([char]::IsControl($character)) { return $false }
        $category = [char]::GetUnicodeCategory($character)
        if ($category -in @([Globalization.UnicodeCategory]::Format, [Globalization.UnicodeCategory]::LineSeparator,
                [Globalization.UnicodeCategory]::ParagraphSeparator, [Globalization.UnicodeCategory]::PrivateUse)) { return $false }
        if ([char]::IsHighSurrogate($character)) {
            if ($index + 1 -ge $Note.Length -or -not [char]::IsLowSurrogate($Note[$index + 1])) { return $false }
            $index++
        } elseif ([char]::IsLowSurrogate($character)) { return $false }
    }
    return $true
}

# Workflow input "notes": lines separated by '|', each trimmed; 1..8 lines of 1..200 characters.
function ConvertFrom-ReleaseNotes([AllowNull()][AllowEmptyString()][string]$Text) {
    if ([string]::IsNullOrWhiteSpace($Text)) { Stop-ConnectorRelease 'NOTES_INVALID' 'At least one line for the user is required.' }
    $lines = @($Text.Split('|') | ForEach-Object { $_.Trim() })
    if ($lines.Count -lt 1 -or $lines.Count -gt 8) { Stop-ConnectorRelease 'NOTES_INVALID' 'From 1 to 8 lines separated by | are required.' }
    for ($index = 0; $index -lt $lines.Count; $index++) {
        if (-not (Test-ReleaseNoteText $lines[$index] 200)) {
            Stop-ConnectorRelease 'NOTES_INVALID' "Line $($index + 1) must have 1..200 characters without control or format characters."
        }
    }
    return , [string[]]$lines
}

# Strict reading of the workflow inputs; nothing else reaches the description.
function ConvertTo-ConnectorReleaseRequest {
    param(
        [AllowEmptyString()][string]$Channel, [AllowEmptyString()][string]$Version, [AllowEmptyString()][string]$SourceRunId,
        [AllowEmptyString()][string]$Paused, [AllowEmptyString()][string]$MinimumVersion, [AllowEmptyString()][string]$Notes)
    if ($Channel -cnotin $script:ConnectorReleaseChannels) { Stop-ConnectorRelease 'CHANNEL_INVALID' 'Channel must be pilot or stable.' }
    $versionValue = Get-ConnectorReleaseVersion $Version
    if ($null -eq $versionValue -or (Format-ConnectorReleaseVersion $versionValue) -cne $Version) { Stop-ConnectorRelease 'VERSION_INVALID' 'Version must be x.y.z.' }
    if ($SourceRunId -cnotmatch '^(0|[1-9][0-9]{0,19})\z') { Stop-ConnectorRelease 'SOURCE_RUN_INVALID' 'source_run_id must be 0 or a run id.' }
    if ($Paused -cnotin @('true', 'false')) { Stop-ConnectorRelease 'PAUSED_INVALID' 'paused must be true or false.' }
    $minimumValue = Get-ConnectorReleaseVersion $MinimumVersion
    if ($null -eq $minimumValue -or (Format-ConnectorReleaseVersion $minimumValue) -cne $MinimumVersion -or $minimumValue -gt $versionValue) {
        Stop-ConnectorRelease 'MINIMUM_VERSION_INVALID' 'minimum_version must be x.y.z and not greater than version.'
    }
    [pscustomobject]@{
        Channel = $Channel; Version = $Version; VersionValue = $versionValue; SourceRunId = $SourceRunId
        Paused = ($Paused -ceq 'true'); MinimumVersion = $MinimumVersion; Notes = (ConvertFrom-ReleaseNotes $Notes)
    }
}

# Exact bytes: fields and order of §3.5, compact, UTF-8 without BOM; non-ASCII escaped by the default encoder.
function New-ReleaseDescriptionBytes {
    param(
        [Parameter(Mandatory)][string]$Channel, [Parameter(Mandatory)][string]$Version, [Parameter(Mandatory)][DateTimeOffset]$PublishedAtUtc,
        [Parameter(Mandatory)][bool]$Paused, [Parameter(Mandatory)][string]$MinimumVersion, [Parameter(Mandatory)][string]$InstallerFileName,
        [Parameter(Mandatory)][long]$InstallerSize, [Parameter(Mandatory)][string]$InstallerSha256, [Parameter(Mandatory)][string[]]$Notes)
    $stream = [IO.MemoryStream]::new()
    $writer = [System.Text.Json.Utf8JsonWriter]::new($stream)
    try {
        $writer.WriteStartObject()
        $writer.WriteNumber('schemaVersion', [long]2)
        $writer.WriteString('applicationId', [string]$script:ConnectorReleaseApplicationId)
        $writer.WriteString('channel', [string]$Channel)
        $writer.WriteString('version', [string]$Version)
        $writer.WriteString('publishedAtUtc', [string]$PublishedAtUtc.UtcDateTime.ToString($script:ConnectorReleaseUtcFormat, [Globalization.CultureInfo]::InvariantCulture))
        $writer.WriteBoolean('paused', $Paused)
        $writer.WriteString('minimumVersion', [string]$MinimumVersion)
        $writer.WriteStartObject('installer')
        $writer.WriteString('fileName', [string]$InstallerFileName)
        $writer.WriteNumber('size', [long]$InstallerSize)
        $writer.WriteString('sha256', [string]$InstallerSha256)
        $writer.WriteEndObject()
        $writer.WriteStartArray('notes')
        foreach ($note in $Notes) { $writer.WriteStringValue([string]$note) }
        $writer.WriteEndArray()
        $writer.WriteEndObject()
        $writer.Flush()
        return , $stream.ToArray()
    } finally { $writer.Dispose(); $stream.Dispose() }
}

function Assert-ExactJsonProperties([System.Text.Json.JsonElement]$Element, [string[]]$Expected) {
    if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) { Stop-ConnectorRelease 'DESCRIPTION_INVALID' 'An object is expected.' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($property in $Element.EnumerateObject()) {
        if (-not $seen.Add($property.Name)) { Stop-ConnectorRelease 'DESCRIPTION_INVALID' "Duplicate field $($property.Name)." }
        if ($property.Name -cnotin $Expected) { Stop-ConnectorRelease 'DESCRIPTION_INVALID' "Unknown field $($property.Name)." }
    }
    if ($seen.Count -ne $Expected.Length) { Stop-ConnectorRelease 'DESCRIPTION_INVALID' 'A required field is missing.' }
}

function Get-JsonStringValue([System.Text.Json.JsonElement]$Element, [string]$Name) {
    $value = $Element.GetProperty($Name)
    if ($value.ValueKind -ne [System.Text.Json.JsonValueKind]::String) { Stop-ConnectorRelease 'DESCRIPTION_INVALID' "$Name must be a string." }
    return $value.GetString()
}

function Get-JsonInt64Value([System.Text.Json.JsonElement]$Element, [string]$Name) {
    $value = $Element.GetProperty($Name)
    $number = [long]0
    if ($value.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or -not $value.TryGetInt64([ref]$number)) { Stop-ConnectorRelease 'DESCRIPTION_INVALID' "$Name must be an integer." }
    # 2.0 and 2e0 parse as integers too; only the plain spelling is accepted.
    if ($value.GetRawText() -cnotmatch '^[0-9]+\z') { Stop-ConnectorRelease 'DESCRIPTION_INVALID' "$Name must be spelled as plain digits." }
    return $number
}

function ConvertFrom-ReleaseUtc([string]$Value) {
    $parsed = [DateTimeOffset]::MinValue
    $formats = [string[]]@("yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'")
    $styles = [Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal
    if (-not [DateTimeOffset]::TryParseExact($Value, $formats, [Globalization.CultureInfo]::InvariantCulture, $styles, [ref]$parsed)) {
        Stop-ConnectorRelease 'DESCRIPTION_INVALID' 'publishedAtUtc is not a UTC time.'
    }
    return $parsed
}

# The client's rules (ReleaseDescriptionVerifier.Verify after the signature check), in the client's order.
function ConvertFrom-ReleaseDescription {
    param([Parameter(Mandatory)][AllowEmptyCollection()][byte[]]$Bytes, [Parameter(Mandatory)][AllowEmptyString()][string]$Channel)
    if ($Channel -cnotmatch '^[a-z][a-z0-9-]{0,31}$') { Stop-ConnectorRelease 'CHANNEL_INVALID' 'Requested channel is invalid.' }
    if ($Bytes.Length -eq 0 -or $Bytes.Length -gt $script:ConnectorReleaseMaximumDescriptionBytes) { Stop-ConnectorRelease 'DESCRIPTION_TOO_LARGE' 'Description must have 1..65536 bytes.' }
    $options = [System.Text.Json.JsonDocumentOptions]@{ AllowTrailingCommas = $false; CommentHandling = [System.Text.Json.JsonCommentHandling]::Disallow; MaxDepth = 4 }
    try { $document = [System.Text.Json.JsonDocument]::Parse([ReadOnlyMemory[byte]]::new($Bytes), $options) }
    catch { Stop-ConnectorRelease 'DESCRIPTION_INVALID' 'Description is not strict JSON.' }
    try {
        $root = $document.RootElement
        Assert-ExactJsonProperties $root $script:ConnectorReleaseRootProperties
        if ((Get-JsonInt64Value $root 'schemaVersion') -ne 2 -or (Get-JsonStringValue $root 'applicationId') -cne $script:ConnectorReleaseApplicationId) {
            Stop-ConnectorRelease 'DESCRIPTION_INVALID' 'Schema or application differs.'
        }
        $channelValue = Get-JsonStringValue $root 'channel'
        if ($channelValue -cnotmatch '^[a-z][a-z0-9-]{0,31}$') { Stop-ConnectorRelease 'DESCRIPTION_INVALID' 'Channel is invalid.' }
        if ($channelValue -cne $Channel) { Stop-ConnectorRelease 'CHANNEL_MISMATCH' "Description is for channel $channelValue." }
        $versionText = Get-JsonStringValue $root 'version'
        $minimumText = Get-JsonStringValue $root 'minimumVersion'
        $version = Get-ConnectorReleaseVersion $versionText
        $minimum = Get-ConnectorReleaseVersion $minimumText
        if ($null -eq $version -or $null -eq $minimum -or $minimum -gt $version) { Stop-ConnectorRelease 'DESCRIPTION_INVALID' 'Version or minimum version is invalid.' }
        $published = ConvertFrom-ReleaseUtc (Get-JsonStringValue $root 'publishedAtUtc')
        $pausedElement = $root.GetProperty('paused')
        $paused = switch ($pausedElement.ValueKind) {
            ([System.Text.Json.JsonValueKind]::True) { $true }
            ([System.Text.Json.JsonValueKind]::False) { $false }
            default { Stop-ConnectorRelease 'DESCRIPTION_INVALID' 'paused must be a boolean.' }
        }
        $installer = $root.GetProperty('installer')
        Assert-ExactJsonProperties $installer $script:ConnectorReleaseInstallerProperties
        $fileName = Get-JsonStringValue $installer 'fileName'
        if ($fileName -cne "PlatformConnector-$(Format-ConnectorReleaseVersion $version).exe") { Stop-ConnectorRelease 'DESCRIPTION_INVALID' 'Installer name differs from the version.' }
        $size = Get-JsonInt64Value $installer 'size'
        if ($size -le 0 -or $size -gt $script:ConnectorReleaseMaximumInstallerBytes) { Stop-ConnectorRelease 'DESCRIPTION_INVALID' 'Installer size is out of bounds.' }
        $sha256 = Get-JsonStringValue $installer 'sha256'
        if ($sha256 -cnotmatch '^[0-9a-fA-F]{64}\z') { Stop-ConnectorRelease 'DESCRIPTION_INVALID' 'Installer SHA-256 is invalid.' }
        $notesElement = $root.GetProperty('notes')
        if ($notesElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Array -or $notesElement.GetArrayLength() -gt 20) { Stop-ConnectorRelease 'DESCRIPTION_INVALID' 'notes must be an array of at most 20 strings.' }
        $notes = [Collections.Generic.List[string]]::new()
        foreach ($item in $notesElement.EnumerateArray()) {
            if ($item.ValueKind -ne [System.Text.Json.JsonValueKind]::String) { Stop-ConnectorRelease 'DESCRIPTION_INVALID' 'A note is not a string.' }
            try { $note = $item.GetString() } catch { Stop-ConnectorRelease 'DESCRIPTION_INVALID' 'A note is not valid text.' }
            if ($note.Length -eq 0 -or $note.Length -gt 300 -or @($note.ToCharArray() | Where-Object { [char]::IsControl($_) }).Count -gt 0) {
                Stop-ConnectorRelease 'DESCRIPTION_INVALID' 'A note is empty, too long or has control characters.'
            }
            $notes.Add($note)
        }
        return [pscustomobject]@{
            Channel = $channelValue; Version = (Format-ConnectorReleaseVersion $version); VersionValue = $version
            PublishedAtUtc = $published; Paused = $paused; MinimumVersion = (Format-ConnectorReleaseVersion $minimum)
            Installer = [pscustomobject]@{ FileName = $fileName; Size = $size; Sha256 = $sha256.ToLowerInvariant() }
            Notes = [string[]]$notes.ToArray()
        }
    } finally { $document.Dispose() }
}

# What this pipeline produces: the client's rules plus pilot/stable only, 1..8 notes of at most 200 characters and
# canonical bytes (no BOM, exact field order, second-precision time, lowercase digest, nothing extra anywhere).
function Assert-OwnReleaseDescription {
    param([Parameter(Mandatory)][AllowEmptyCollection()][byte[]]$Bytes, [Parameter(Mandatory)][string]$Channel, [string]$Version)
    if ($Bytes.Length -ge 3 -and $Bytes[0] -eq 0xEF -and $Bytes[1] -eq 0xBB -and $Bytes[2] -eq 0xBF) { Stop-ConnectorRelease 'DESCRIPTION_INVALID' 'Byte order mark is not allowed.' }
    $description = ConvertFrom-ReleaseDescription -Bytes $Bytes -Channel $Channel
    if ($description.Channel -cnotin $script:ConnectorReleaseChannels) { Stop-ConnectorRelease 'DESCRIPTION_INVALID' 'Only pilot and stable are published.' }
    if ($Version -and $description.Version -cne $Version) { Stop-ConnectorRelease 'DESCRIPTION_INVALID' 'Description version differs from the request.' }
    if ($description.Notes.Length -lt 1 -or $description.Notes.Length -gt 8 -or @($description.Notes | Where-Object { -not (Test-ReleaseNoteText $_ 200) }).Count -gt 0) {
        Stop-ConnectorRelease 'DESCRIPTION_INVALID' 'Notes must be 1..8 clean lines of at most 200 characters.'
    }
    $canonical = New-ReleaseDescriptionBytes -Channel $description.Channel -Version $description.Version -PublishedAtUtc $description.PublishedAtUtc `
        -Paused $description.Paused -MinimumVersion $description.MinimumVersion -InstallerFileName $description.Installer.FileName `
        -InstallerSize $description.Installer.Size -InstallerSha256 $description.Installer.Sha256 -Notes $description.Notes
    if ([Convert]::ToHexString($canonical) -cne [Convert]::ToHexString($Bytes)) { Stop-ConnectorRelease 'DESCRIPTION_INVALID' 'Description bytes are not canonical.' }
    $description | Add-Member -NotePropertyName Bytes -NotePropertyValue $Bytes
    return $description
}

# Public half only, ECDSA P-256 (PinnedUpdateKey.RequireP256).
function Import-PinnedReleaseKey([Parameter(Mandatory)][string]$Path) {
    $pem = [IO.File]::ReadAllText($Path)
    if ($pem -notmatch '-----BEGIN PUBLIC KEY-----' -or $pem -match 'PRIVATE') { throw 'The pinned key file must hold a public key only.' }
    $key = [Security.Cryptography.ECDsa]::Create()
    try {
        $key.ImportFromPem($pem)
        if ($key.KeySize -ne 256 -or $key.ExportParameters($false).Curve.Oid.Value -cne $script:ConnectorReleaseP256Oid) { throw 'The pinned key must be ECDSA P-256.' }
        return $key
    } catch { $key.Dispose(); throw }
}

function Test-ReleaseSignature {
    param([Parameter(Mandatory)][AllowEmptyCollection()][byte[]]$Bytes, [Parameter(Mandatory)][AllowEmptyCollection()][byte[]]$Signature, [Parameter(Mandatory)][Security.Cryptography.ECDsa]$PublicKey)
    if ($Bytes.Length -eq 0 -or $Signature.Length -ne 64) { return $false }
    return $PublicKey.VerifyData($Bytes, $Signature, [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
}

# The one signing implementation of the repository. The key comes only from environment variable $KeyVariable of
# the calling step, which is cleared before anything else happens; it must match the pinned public key, every
# signature (ECDSA P-256 / SHA-256, IEEE P1363, 64 bytes) is verified with the pinned key before it is returned.
function Invoke-PinnedReleaseSignature {
    param([Parameter(Mandatory)][object[]]$Documents, [Parameter(Mandatory)][string]$PublicKeyPemPath, [string]$KeyVariable = 'SIGN_KEY_PEM')
    $pem = [Environment]::GetEnvironmentVariable($KeyVariable)
    [Environment]::SetEnvironmentVariable($KeyVariable, [NullString]::Value)
    $private = $null
    $public = $null
    try {
        if ([string]::IsNullOrWhiteSpace($pem)) { throw 'The signing key is not available to this step.' }
        $public = Import-PinnedReleaseKey $PublicKeyPemPath
        $private = [Security.Cryptography.ECDsa]::Create()
        try { $private.ImportFromPem($pem) } catch { throw 'The signing key could not be read.' }
        $pem = $null
        if ($private.KeySize -ne 256 -or $private.ExportParameters($false).Curve.Oid.Value -cne $script:ConnectorReleaseP256Oid -or
            [Convert]::ToBase64String($private.ExportSubjectPublicKeyInfo()) -cne [Convert]::ToBase64String($public.ExportSubjectPublicKeyInfo())) {
            throw 'Signing key does not match the repository pin.'
        }
        $signatures = [Collections.Generic.List[byte[]]]::new()
        foreach ($document in $Documents) {
            $bytes = [byte[]]$document
            if ($bytes.Length -eq 0 -or $bytes.Length -gt $script:ConnectorReleaseMaximumDescriptionBytes) { throw 'Document length is out of bounds.' }
            $signature = $private.SignData($bytes, [Security.Cryptography.HashAlgorithmName]::SHA256,
                [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
            if (-not (Test-ReleaseSignature -Bytes $bytes -Signature $signature -PublicKey $public)) { throw 'Signature verification failed.' }
            $signatures.Add($signature)
        }
        return , $signatures.ToArray()
    } finally {
        $pem = $null
        [Environment]::SetEnvironmentVariable($KeyVariable, [NullString]::Value)
        if ($null -ne $private) { $private.Dispose() }
        if ($null -ne $public) { $public.Dispose() }
    }
}

function Get-ReleaseTimestamp([DateTimeOffset]$Now) { [DateTimeOffset]::FromUnixTimeSeconds($Now.ToUnixTimeSeconds()) }

function Assert-SameReleaseInstaller($Expected, $Actual, [string]$Where) {
    if ($Expected.FileName -cne $Actual.FileName -or [long]$Expected.Size -ne [long]$Actual.Size -or
        ([string]$Expected.Sha256).ToLowerInvariant() -cne ([string]$Actual.Sha256).ToLowerInvariant()) {
        Stop-ConnectorRelease 'INSTALLER_CHANGED' "A version is immutable: the installer differs from $Where."
    }
}

# build-unified-connector-candidate.yml run that produced the installer of a first publication.
function Assert-CandidateRun([Parameter(Mandatory)]$Run, [Parameter(Mandatory)][string]$RunId, [string]$Repository = $script:ConnectorReleaseRepository) {
    if ([string]$Run.id -cne $RunId -or $Run.path -cne $script:ConnectorReleaseCandidateWorkflow -or $Run.status -cne 'completed' -or
        $Run.conclusion -cne 'success' -or $Run.event -cne 'workflow_dispatch' -or $Run.head_branch -cne 'master' -or
        [string]$Run.head_sha -cnotmatch '^[a-f0-9]{40}\z' -or $Run.head_repository.full_name -cne $Repository) {
        Stop-ConnectorRelease 'SOURCE_RUN_INVALID' 'source_run_id must be a successful manual master run of build-unified-connector-candidate.yml.'
    }
}

# compare/{run head}...{this commit}: the build commit is this commit or one of its ancestors.
function Assert-AncestorComparison([Parameter(Mandatory)]$Comparison) {
    if ($Comparison.status -cnotin @('identical', 'ahead') -or [int]$Comparison.behind_by -ne 0) {
        Stop-ConnectorRelease 'SOURCE_RUN_INVALID' 'The build commit is not an ancestor of the publishing commit.'
    }
}

# Artifact platform-connector-<version>-cloud-tested-unsigned: exactly one installer and its release.json.
function Assert-CandidateArtifact {
    param([Parameter(Mandatory)][string]$Directory, [Parameter(Mandatory)][string]$Version, [Parameter(Mandatory)][string]$RunId, [Parameter(Mandatory)][string]$HeadSha)
    $name = "PlatformConnector-$Version.exe"
    $items = @(Get-ChildItem -LiteralPath $Directory -Recurse -Force)
    if (@($items | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }).Count) { Stop-ConnectorRelease 'CANDIDATE_INVALID' 'Artifact contains reparse points.' }
    $installers = @($items | Where-Object { -not $_.PSIsContainer -and $_.Name -like 'PlatformConnector-*' })
    $manifests = @($items | Where-Object { -not $_.PSIsContainer -and $_.Name -ceq 'release.json' })
    if ($installers.Count -ne 1 -or $installers[0].Name -cne $name -or $manifests.Count -ne 1 -or
        $manifests[0].DirectoryName -cne $installers[0].DirectoryName) { Stop-ConnectorRelease 'CANDIDATE_INVALID' "Artifact must hold exactly $name and its release.json." }
    $file = $installers[0]
    if ($file.Length -le 0 -or $file.Length -gt $script:ConnectorReleaseMaximumInstallerBytes) { Stop-ConnectorRelease 'CANDIDATE_INVALID' 'Installer size is out of bounds.' }
    $sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $document = [System.Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($manifests[0].FullName))
    try {
        $root = $document.RootElement
        $read = {
            param([string]$Property)
            $value = [System.Text.Json.JsonElement]::new()
            if ($root.ValueKind -ne [System.Text.Json.JsonValueKind]::Object -or -not $root.TryGetProperty($Property, [ref]$value)) { Stop-ConnectorRelease 'CANDIDATE_INVALID' "release.json lacks $Property." }
            $value
        }
        $text = { param($Element) if ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::String) { $Element.GetString() } else { $Element.GetRawText() } }
        $bytes = [long]0
        $bytesElement = & $read 'bytes'
        $fault = & $read 'ciInjectedInstallFault'
        if ((& $text (& $read 'version')) -cne $Version -or (& $text (& $read 'fileName')) -cne $name -or
            (& $text (& $read 'artifactKind')) -cne 'unsigned-cloud-candidate' -or $fault.ValueKind -ne [System.Text.Json.JsonValueKind]::False -or
            (& $text (& $read 'sourceRun')) -cne $RunId -or (& $text (& $read 'sourceSha')) -cne $HeadSha -or
            $bytesElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or -not $bytesElement.TryGetInt64([ref]$bytes) -or $bytes -ne $file.Length -or
            (& $text (& $read 'sha256')) -cne $sha256) { Stop-ConnectorRelease 'CANDIDATE_INVALID' 'release.json does not describe this exact cloud-tested installer of the source run.' }
    } finally { $document.Dispose() }
    [pscustomobject]@{ FileName = $name; Size = [long]$file.Length; Sha256 = $sha256; Path = $file.FullName; ManifestPath = $manifests[0].FullName }
}

# Channel rules and version immutability; returns the exact bytes of the new description and its parsed form.
#   CurrentChannel      — verified current description of the channel or $null;
#   VersionDescriptions — verified descriptions of this version (any channel) from platform-connector-v<version>;
#   InstallerAsset      — @{Size; Digest} of PlatformConnector-<version>.exe already in that release, or $null.
function New-ConnectorReleasePlan {
    param(
        [Parameter(Mandatory)]$Request, [Parameter(Mandatory)]$Installer, [Parameter(Mandatory)][DateTimeOffset]$Now,
        [AllowNull()]$CurrentChannel, [AllowEmptyCollection()][object[]]$VersionDescriptions = @(), [AllowNull()]$InstallerAsset)
    $name = "PlatformConnector-$($Request.Version).exe"
    if ($Installer.FileName -cne $name -or [long]$Installer.Size -le 0 -or [long]$Installer.Size -gt $script:ConnectorReleaseMaximumInstallerBytes -or
        [string]$Installer.Sha256 -cnotmatch '^[a-f0-9]{64}\z') { Stop-ConnectorRelease 'INSTALLER_INVALID' "Installer must be $name with a size and a lowercase SHA-256." }
    foreach ($description in $VersionDescriptions) {
        if ($description.Version -cne $Request.Version) { Stop-ConnectorRelease 'VERSION_RELEASE_INCONSISTENT' "A description of $($description.Version) lies in the release of $($Request.Version)." }
        Assert-SameReleaseInstaller $description.Installer $Installer "the signed $($description.Channel) description"
    }
    if ($null -ne $InstallerAsset) {
        if ([long]$InstallerAsset.Size -ne [long]$Installer.Size -or ($InstallerAsset.Digest -and [string]$InstallerAsset.Digest -cne "sha256:$($Installer.Sha256)")) {
            Stop-ConnectorRelease 'INSTALLER_CHANGED' 'A version is immutable: the published installer differs.'
        }
    }
    $published = Get-ReleaseTimestamp $Now
    if ($null -ne $CurrentChannel) {
        if ($CurrentChannel.Channel -cne $Request.Channel) { Stop-ConnectorRelease 'CHANNEL_MISMATCH' 'Current description belongs to another channel.' }
        if ($CurrentChannel.VersionValue -gt $Request.VersionValue) { Stop-ConnectorRelease 'VERSION_DOWNGRADE' "Channel $($Request.Channel) already has $($CurrentChannel.Version)." }
        if ($CurrentChannel.Version -ceq $Request.Version) { Assert-SameReleaseInstaller $CurrentChannel.Installer $Installer "the current $($Request.Channel) description" }
        if ($published -le $CurrentChannel.PublishedAtUtc) { Stop-ConnectorRelease 'PUBLISHED_NOT_NEWER' 'publishedAtUtc must be later than the current description of the channel.' }
    }
    # Only a pilot that was actually distributed counts: a pilot description on pause does not open stable.
    if ($Request.Channel -ceq 'stable' -and @($VersionDescriptions | Where-Object { $_.Channel -ceq 'pilot' -and -not $_.Paused }).Count -eq 0) {
        Stop-ConnectorRelease 'STABLE_REQUIRES_PILOT' "Version $($Request.Version) has no signed, unpaused pilot description."
    }
    $bytes = New-ReleaseDescriptionBytes -Channel $Request.Channel -Version $Request.Version -PublishedAtUtc $published -Paused $Request.Paused `
        -MinimumVersion $Request.MinimumVersion -InstallerFileName $Installer.FileName -InstallerSize ([long]$Installer.Size) `
        -InstallerSha256 $Installer.Sha256 -Notes $Request.Notes
    [pscustomobject]@{ Bytes = $bytes; Description = (Assert-OwnReleaseDescription -Bytes $bytes -Channel $Request.Channel -Version $Request.Version) }
}

# Current state of a channel. No release platform-connector-channel-<channel> (404) = the channel is new and has no
# rules to apply. A release that exists but whose connector-release.json + .sig cannot be read as a description of this
# channel signed by the pinned key (missing file, wrong signature, a genuine description of another channel or a
# broken one) is never treated as "no channel": the run stops and the owner decides.
function Resolve-ChannelDescription {
    param([Parameter(Mandatory)][bool]$ReleaseExists, [AllowNull()][byte[]]$Bytes, [AllowNull()][byte[]]$Signature,
        [Parameter(Mandatory)][string]$Channel, [Parameter(Mandatory)][Security.Cryptography.ECDsa]$PublicKey)
    if (-not $ReleaseExists) { return $null }
    $tag = "platform-connector-channel-$Channel"
    $fix = "Канал $Channel не опубликован заново: прежнее состояние нельзя проверить. Если выпуск $tag действительно испорчен, удалите этот выпуск целиком (тег можно оставить) и запустите сценарий снова — канал будет опубликован как новый, без сравнения с прежней версией; выбирайте версию осознанно."
    if ($null -eq $Bytes -or $null -eq $Signature -or $Bytes.Length -eq 0 -or $Signature.Length -eq 0) {
        Stop-ConnectorRelease 'CHANNEL_DESCRIPTION_UNREADABLE' "$tag exists but has no connector-release.json with connector-release.json.sig. $fix"
    }
    if (-not (Test-ReleaseSignature -Bytes $Bytes -Signature $Signature -PublicKey $PublicKey)) {
        Stop-ConnectorRelease 'CHANNEL_DESCRIPTION_UNREADABLE' "$tag/connector-release.json is not signed by the pinned key. $fix"
    }
    try { $description = ConvertFrom-ReleaseDescription -Bytes $Bytes -Channel $Channel }
    catch { Stop-ConnectorRelease 'CHANNEL_DESCRIPTION_UNREADABLE' "$tag/connector-release.json is signed but is not a description of channel $Channel ($($_.Exception.Message)). $fix" }
    $description | Add-Member -NotePropertyName Bytes -NotePropertyValue $Bytes
    return $description
}

# What the owner sees in the summary of job "plan" before approving the signing job, and what "sign" must reproduce.
function Get-ReleasePlanFacts {
    param([Parameter(Mandatory)]$Request, [Parameter(Mandatory)]$Installer, [Parameter(Mandatory)][string]$Source, [AllowNull()]$CurrentChannel)
    $current = if ($null -eq $CurrentChannel) { 'none' } else {
        "$($CurrentChannel.Version) paused=$($CurrentChannel.Paused.ToString().ToLowerInvariant()) publishedAtUtc=$($CurrentChannel.PublishedAtUtc.UtcDateTime.ToString($script:ConnectorReleaseUtcFormat, [Globalization.CultureInfo]::InvariantCulture))"
    }
    [ordered]@{
        channel = $Request.Channel; version = $Request.Version; paused = $Request.Paused.ToString().ToLowerInvariant()
        minimum_version = $Request.MinimumVersion; installer_file = $Installer.FileName; installer_size = [string]([long]$Installer.Size)
        installer_sha256 = $Installer.Sha256; source = $Source; channel_current = $current
    }
}

# "sign" recomputes everything itself; the outputs of "plan" are only compared, never used as a source.
function Assert-ReleasePlanMatch([Parameter(Mandatory)]$Facts, [Parameter(Mandatory)][hashtable]$Planned) {
    foreach ($key in $Facts.Keys) {
        if (-not $Planned.ContainsKey($key) -or [string]$Planned[$key] -cne [string]$Facts[$key]) {
            Stop-ConnectorRelease 'PLAN_MISMATCH' "$key differs from what job plan showed before approval (plan: '$($Planned[$key])', now: '$($Facts[$key])'); run the workflow again."
        }
    }
}

# Reference of an existing tag (GET git/ref/tags/<tag>): exactly that ref; returns the object it points to.
function Get-ReleaseTagObject([Parameter(Mandatory)]$Reference, [Parameter(Mandatory)][string]$Tag) {
    if ($Reference -is [array] -or $Reference.ref -cne "refs/tags/$Tag" -or $null -eq $Reference.object) { Stop-ConnectorRelease 'TAG_INVALID' "Tag $Tag is ambiguous." }
    return $Reference.object
}

function Assert-ReleaseTagCommit([Parameter(Mandatory)]$Object, [Parameter(Mandatory)][string]$Tag) {
    if ($Object.type -cne 'commit' -or [string]$Object.sha -cnotmatch '^[a-f0-9]{40}\z') { Stop-ConnectorRelease 'TAG_INVALID' "Tag $Tag does not point to a commit." }
}

# The installer pins its update key at build time: the build commit must carry the same key file as this commit.
function Assert-SamePinnedKeyBlob([AllowEmptyString()][string]$CandidateBlob, [AllowEmptyString()][string]$CurrentBlob) {
    if ($CandidateBlob -cnotmatch '^[a-f0-9]{40}\z' -or $CurrentBlob -cnotmatch '^[a-f0-9]{40}\z' -or $CandidateBlob -cne $CurrentBlob) {
        Stop-ConnectorRelease 'SOURCE_RUN_INVALID' 'The build commit pins another update-public-key.pem than this commit; such an installer could not verify later updates.'
    }
}

# GET actions/runs/<id>/artifacts?name=<name>: exactly one, not expired; its id and digest (when GitHub reports it).
function Select-CandidateArtifact([Parameter(Mandatory)]$Listing, [Parameter(Mandatory)][string]$Name) {
    $found = @($Listing.artifacts | Where-Object { $_.name -ceq $Name })
    if ($found.Count -ne 1 -or $found[0].expired -or [string]$found[0].id -cnotmatch '^[1-9][0-9]{0,19}\z') {
        Stop-ConnectorRelease 'CANDIDATE_INVALID' "Run must have exactly one unexpired artifact $Name."
    }
    $digest = if ($found[0].PSObject.Properties['digest'] -and $found[0].digest) { [string]$found[0].digest } else { $null }
    if ($null -ne $digest -and $digest -cnotmatch '^sha256:[a-f0-9]{64}\z') { Stop-ConnectorRelease 'CANDIDATE_INVALID' "Artifact $Name reports an unexpected digest." }
    [pscustomobject]@{ Id = [string]$found[0].id; Digest = $digest }
}

# The run must not have changed (re-run, new attempt) between its check and the download of its artifact.
function Assert-SameRunState([Parameter(Mandatory)]$Before, [Parameter(Mandatory)]$After) {
    foreach ($field in 'id', 'run_attempt', 'status', 'conclusion', 'head_sha') {
        if ([string]$Before.$field -cne [string]$After.$field) { Stop-ConnectorRelease 'SOURCE_RUN_CHANGED' "Run $($Before.id) changed ($field) while its artifact was downloaded." }
    }
}

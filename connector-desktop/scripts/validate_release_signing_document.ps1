function Assert-ReleaseSigningDocument([byte[]]$Bytes, [string]$Purpose) {
    $document = [System.Text.Json.JsonDocument]::Parse([ReadOnlyMemory[byte]]::new($Bytes))
    try {
        function ExactProperties($element, [string[]]$expected) {
            $actual = @($element.EnumerateObject() | ForEach-Object Name)
            if ($actual.Count -ne $expected.Count -or @($actual | Select-Object -Unique).Count -ne $actual.Count -or
                @($actual | Where-Object { $_ -cnotin $expected }).Count) { throw 'Unexpected release document fields' }
        }
        function ValidAsset($element) {
            if ($element.GetProperty('size').GetInt64() -le 0 -or $element.GetProperty('size').GetInt64() -gt 1073741824 -or
                $element.GetProperty('sha256').GetString() -cnotmatch '^[a-fA-F0-9]{64}$') { throw 'Invalid release asset pin' }
        }
        $d = $document.RootElement
        # Only the machine-helper manifest is signed through this path (sign-unified-release-document.yml).
        if ($Purpose -ceq 'helper') {
            ExactProperties $d @('schemaVersion','version','helperRelativePath','size','sha256','velopackSetup','callerImage')
            ValidAsset $d
            $version = $d.GetProperty('version').GetString()
            if ($d.GetProperty('schemaVersion').GetInt32() -ne 4 -or $version -cnotmatch '^\d+\.\d+\.\d+$' -or
                $d.GetProperty('helperRelativePath').GetString() -cne 'Connector.Upgrade.MachineHelper.exe') { throw 'Unexpected helper identity' }
            $setup = $d.GetProperty('velopackSetup'); $caller = $d.GetProperty('callerImage')
            ExactProperties $setup @('relativePath','size','sha256','packageId','version'); ValidAsset $setup
            ExactProperties $caller @('relativePath','size','sha256'); ValidAsset $caller
            if ($setup.GetProperty('relativePath').GetString() -cne 'Setup.exe' -or
                $setup.GetProperty('packageId').GetString() -cne 'Structura.Connector.Desktop' -or
                $setup.GetProperty('version').GetString() -cne $version -or
                $caller.GetProperty('relativePath').GetString() -cne 'StructuraConnectorInstaller\Bootstrapper\Connector.Upgrade.Bootstrapper.exe') { throw 'Unexpected protected caller/setup identity' }
        } else { throw 'Unknown release signing purpose' }
    } finally { $document.Dispose() }
}

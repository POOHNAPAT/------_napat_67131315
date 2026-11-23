$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'connect-supabase.ps1'
$source = Get-Content -LiteralPath $scriptPath -Raw
$builderBlock = [regex]::Match($source, '(?s)    # DbConnectionStringBuilder.*?(?=    \$secretsDirectory)').Value
if (!$builderBlock) { throw 'Setup connection builder not found.' }

foreach ($fakePassword in @('fake-test-only', 'fake;with"quotes''and$dollar`tick')) {
    # Pipeline strings reproduce the PSObject wrapper produced by interactive prompts.
    $databaseUser = Write-Output -NoEnumerate 'postgres.testproject'
    $securePassword = ConvertTo-SecureString $fakePassword -AsPlainText -Force
    . ([scriptblock]::Create($builderBlock))
    $parsed = New-Object System.Data.Common.DbConnectionStringBuilder
    $parsed.set_ConnectionString($databaseConnection.get_ConnectionString())
    if ($parsed.get_Item('Username') -cne 'postgres.testproject') { throw 'Username changed.' }
    if ($parsed.get_Item('Password') -cne $fakePassword) { throw 'Password roundtrip failed.' }
    if ($parsed.get_Item('Host') -cne 'aws-0-ap-south-1.pooler.supabase.com') { throw 'Wrong host.' }
    if ($parsed.get_Item('Port') -ne '5432') { throw 'Wrong port.' }
    if ($parsed.get_Item('SSL Mode') -cne 'Require') { throw 'SSL not required.' }
    $securePassword.Dispose()
}
Write-Output 'PASS: actual setup builder handles wrapped prompt values and special-character passwords without saving credentials.'

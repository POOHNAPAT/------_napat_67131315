$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$Host.UI.RawUI.WindowTitle = 'CourtBook - Connect Supabase'

try {
    Write-Host 'CourtBook Supabase setup' -ForegroundColor Cyan
    Write-Host 'Host: aws-0-ap-south-1.pooler.supabase.com:5432'
    Write-Host 'Copy the User from Supabase > Connect > Session pooler.'
    $databaseUser = Read-Host 'Database User (postgres.your-project-ref)'
    if ($databaseUser -notmatch '^postgres\.[a-z0-9]+$') {
        throw 'Copy the complete User value, starting with postgres., from Supabase.'
    }
    $securePassword = Read-Host 'Database password (hidden)' -AsSecureString
    if ($securePassword.Length -eq 0) { throw 'Database password is required.' }

    # DbConnectionStringBuilder quotes passwords containing semicolons or quotes.
    $databaseConnection = New-Object System.Data.Common.DbConnectionStringBuilder
    # Explicit setters unwrap PowerShell prompt values before passing them to ADO.NET.
    $databaseConnection.set_Item('Host', 'aws-0-ap-south-1.pooler.supabase.com')
    $databaseConnection.set_Item('Port', '5432')
    $databaseConnection.set_Item('Database', 'postgres')
    $databaseConnection.set_Item('Username', [string]$databaseUser)
    $databaseConnection.set_Item('Password', [string](New-Object System.Net.NetworkCredential('', $securePassword)).Password)
    $databaseConnection.set_Item('SSL Mode', 'Require')
    $databaseConnection.set_Item('Maximum Pool Size', '10')
    $databaseConnection.set_Item('Timeout', '15')

    $secretsDirectory = Join-Path $env:APPDATA 'Microsoft\UserSecrets\CourtBook-napat-67131315'
    $secretsPath = Join-Path $secretsDirectory 'secrets.json'
    $settings = @{}
    if (Test-Path -LiteralPath $secretsPath) {
        $existing = Get-Content -LiteralPath $secretsPath -Raw | ConvertFrom-Json
        foreach ($property in $existing.PSObject.Properties) { $settings[$property.Name] = $property.Value }
    }
    $settings['Database:Provider'] = 'Supabase'
    $settings['ConnectionStrings:Supabase'] = $databaseConnection.get_ConnectionString()
    New-Item -ItemType Directory -Path $secretsDirectory -Force | Out-Null
    $settings | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $secretsPath -Encoding UTF8
    $databaseConnection.Clear()
    $settings.Clear()
    $securePassword.Dispose()

    # Environment variables override User Secrets, so select the saved settings explicitly.
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:Database__Provider = 'Supabase'
    Remove-Item Env:ConnectionStrings__Supabase -ErrorAction SilentlyContinue
    $dll = Join-Path $PSScriptRoot 'artifacts\supabase-check\CourtBook.dll'
    dotnet build CourtBook.csproj -o artifacts/supabase-check
    if ($LASTEXITCODE -ne 0) { throw 'Build failed. Review the error above.' }
    Write-Host 'Settings saved outside the project. Connecting to Supabase...' -ForegroundColor Green
    Write-Host 'The app initializes cloud tables. Existing SQLite data is not imported.'
    Write-Host 'Keep this window open while using http://localhost:5001'
    New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot 'artifacts') -Force | Out-Null
    dotnet $dll 2>&1 | Tee-Object -FilePath (Join-Path $PSScriptRoot 'artifacts\supabase-connection.log')
    if ($LASTEXITCODE -ne 0) { throw 'Connection or startup failed. Review the error above.' }
}
catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
}
finally {
    Read-Host 'Press Enter to close'
}

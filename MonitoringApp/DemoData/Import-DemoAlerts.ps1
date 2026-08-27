[CmdletBinding()]
param(
    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$BaseUrl = 'http://localhost:5187',

    [Parameter()]
    [switch]$ResetLocalDatabase,

    [Parameter()]
    [string]$AccessToken
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$demoDataDirectory = $PSScriptRoot
$payloadPath = Join-Path $demoDataDirectory 'screenshot-alerts.json'
$developmentSettingsPath = Join-Path (Split-Path $demoDataDirectory -Parent) 'appsettings.Development.json'
$endpoint = [Uri]::new(([Uri]::new($BaseUrl)), 'api/alerts')
$payloads = Get-Content $payloadPath -Raw | ConvertFrom-Json
if ($payloads.Count -eq 0) {
    throw "No alert payloads were found in '$payloadPath'."
}

foreach ($payload in $payloads) {
    if ($null -eq $payload.data -or
        $null -eq $payload.data.essentials -or
        [string]::IsNullOrWhiteSpace([string]$payload.data.essentials.alertId)) {
        throw "Every payload in '$payloadPath' must contain data.essentials.alertId."
    }
}

if ($ResetLocalDatabase) {
    if ($endpoint.Host -notin @('localhost', '127.0.0.1', '::1')) {
        throw '-ResetLocalDatabase requires a loopback BaseUrl.'
    }

    $developmentSettings = Get-Content $developmentSettingsPath -Raw | ConvertFrom-Json
    $connectionString = $developmentSettings.ConnectionStrings.AlertsDatabase
    $connectionBuilder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($connectionString)
    $expectedServer = '(localdb)\mssqllocaldb'
    $expectedDatabase = 'MonitoringApp'

    if ($connectionBuilder.DataSource -ne $expectedServer -or
        $connectionBuilder.InitialCatalog -ne $expectedDatabase -or
        -not $connectionBuilder.IntegratedSecurity) {
        throw "Refusing to reset database '$($connectionBuilder.InitialCatalog)' on '$($connectionBuilder.DataSource)'. Expected local development database '$expectedDatabase' on '$expectedServer' with integrated security."
    }

    $connection = [System.Data.SqlClient.SqlConnection]::new($connectionBuilder.ConnectionString)
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandText = 'DELETE FROM dbo.Alerts;'
        $deleted = $command.ExecuteNonQuery()
        Write-Host "Deleted $deleted alert row(s) from local database $expectedDatabase."
    }
    finally {
        $connection.Dispose()
    }
}

$headers = @{}
if (-not [string]::IsNullOrWhiteSpace($AccessToken)) {
    $headers.Authorization = "Bearer $AccessToken"
}

$createdCount = 0
$duplicateCount = 0
foreach ($payload in $payloads) {
    $alertId = $payload.data.essentials.alertId
    $response = Invoke-RestMethod `
        -Method Post `
        -Uri $endpoint `
        -Headers $headers `
        -ContentType 'application/json' `
        -Body ($payload | ConvertTo-Json -Depth 30)

    if ($response.created) {
        $createdCount++
        Write-Host "Created $alertId."
    }
    else {
        $duplicateCount++
        Write-Host "Already present: $alertId."
    }
}

Write-Host "Imported $($payloads.Count) payload(s): $createdCount created, $duplicateCount already present."

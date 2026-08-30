[CmdletBinding()]
param(
    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$BaseUrl = 'http://localhost:5187',

    [Parameter()]
    [string]$AccessToken
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$endpoint = [Uri]::new(([Uri]::new($BaseUrl)), 'api/alerts')
if ($endpoint.Host -notin @('localhost', '127.0.0.1', '::1')) {
    throw 'This test modifies test data and may only target a loopback BaseUrl.'
}

$subscriptionId = 'inventory-test-subscription'
$computer = 'INVENTORY-TEST-01'
$expectedSite = 'INVENTORY-TEST-SITE'
$alertIds = @('inventory-test-without-site', 'inventory-test-with-site')
$payloadPath = Join-Path $PSScriptRoot 'inventory-enrichment-alerts.json'
$settingsPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'appsettings.Development.json'
$payloads = Get-Content $payloadPath -Raw | ConvertFrom-Json

if ($payloads.Count -ne 2) {
    throw "Expected exactly two payloads in '$payloadPath'."
}

$settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
$connectionBuilder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new(
    [string]$settings.ConnectionStrings.AlertsDatabase)
if ($connectionBuilder.DataSource -ne '(localdb)\mssqllocaldb' -or
    $connectionBuilder.InitialCatalog -ne 'MonitoringApp' -or
    -not $connectionBuilder.IntegratedSecurity) {
    throw "Refusing to modify database '$($connectionBuilder.InitialCatalog)' on '$($connectionBuilder.DataSource)'."
}

$connection = [System.Data.SqlClient.SqlConnection]::new($connectionBuilder.ConnectionString)
try {
    $connection.Open()

    $cleanup = $connection.CreateCommand()
    $cleanup.CommandText = @'
DELETE FROM dbo.Alerts WHERE AlertId IN (@firstAlertId, @secondAlertId);
DELETE FROM dbo.ComputerInventory WHERE SubscriptionId = @subscriptionId AND Computer = @computer;
'@
    [void]$cleanup.Parameters.AddWithValue('@firstAlertId', $alertIds[0])
    [void]$cleanup.Parameters.AddWithValue('@secondAlertId', $alertIds[1])
    [void]$cleanup.Parameters.AddWithValue('@subscriptionId', $subscriptionId)
    [void]$cleanup.Parameters.AddWithValue('@computer', $computer)
    [void]$cleanup.ExecuteNonQuery()

    $headers = @{}
    if (-not [string]::IsNullOrWhiteSpace($AccessToken)) {
        $headers.Authorization = "Bearer $AccessToken"
    }

    foreach ($payload in $payloads) {
        $alertId = [string]$payload.data.essentials.alertId
        $response = Invoke-RestMethod `
            -Method Post `
            -Uri $endpoint `
            -Headers $headers `
            -ContentType 'application/json' `
            -Body ($payload | ConvertTo-Json -Depth 30)

        if (-not $response.created) {
            throw "Expected alert '$alertId' to be created, but the endpoint reported a duplicate."
        }

        Write-Host "Created alert '$alertId'."
    }

    $verify = $connection.CreateCommand()
    $verify.CommandText = @'
SELECT SubscriptionId, Computer, Site, Domain
FROM dbo.ComputerInventory
WHERE SubscriptionId = @subscriptionId AND Computer = @computer;
'@
    [void]$verify.Parameters.AddWithValue('@subscriptionId', $subscriptionId)
    [void]$verify.Parameters.AddWithValue('@computer', $computer)

    $reader = $verify.ExecuteReader()
    try {
        if (-not $reader.Read()) {
            throw 'The expected inventory record was not created.'
        }

        $actualSubscriptionId = $reader.GetString(0)
        $actualComputer = $reader.GetString(1)
        $actualSite = if ($reader.IsDBNull(2)) { $null } else { $reader.GetString(2) }
        $actualDomain = if ($reader.IsDBNull(3)) { $null } else { $reader.GetString(3) }

        if ($reader.Read()) {
            throw 'More than one inventory record was created for the same subscription and computer.'
        }
    }
    finally {
        $reader.Dispose()
    }

    if ($actualSubscriptionId -ne $subscriptionId -or
        $actualComputer -ne $computer -or
        $actualSite -ne $expectedSite -or
        $null -ne $actualDomain) {
        throw "Unexpected inventory values: SubscriptionId='$actualSubscriptionId', Computer='$actualComputer', Site='$actualSite', Domain='$actualDomain'."
    }

    Write-Host 'Inventory ingestion test passed.' -ForegroundColor Green
    Write-Host "SubscriptionId: $actualSubscriptionId"
    Write-Host "Computer:       $actualComputer"
    Write-Host "Site:           $actualSite"
    Write-Host 'Domain:         (not set)'
}
finally {
    $connection.Dispose()
}

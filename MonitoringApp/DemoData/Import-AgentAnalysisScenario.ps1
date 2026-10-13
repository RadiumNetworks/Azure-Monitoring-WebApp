[CmdletBinding()]
param(
    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$BaseUrl = 'http://127.0.0.1:5187',

    [Parameter()]
    [string]$AccessToken
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$endpoint = [Uri]::new(([Uri]::new($BaseUrl)), 'api/alerts')
if ($endpoint.Host -notin @('localhost', '127.0.0.1', '::1')) {
    throw 'The Agent Analysis scenario can only be imported through a loopback BaseUrl.'
}

$settingsPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'appsettings.Development.json'
$settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
$connectionBuilder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new(
    [string]$settings.ConnectionStrings.AlertsDatabase)
$expectedServer = '(localdb)\mssqllocaldb'
$expectedDatabase = 'MonitoringApp'
if ($connectionBuilder.DataSource -ne $expectedServer -or
    $connectionBuilder.InitialCatalog -ne $expectedDatabase -or
    -not $connectionBuilder.IntegratedSecurity) {
    throw "Refusing to seed database '$($connectionBuilder.InitialCatalog)' on '$($connectionBuilder.DataSource)'. Expected '$expectedDatabase' on '$expectedServer' with integrated security."
}

$scenarioNow = [DateTimeOffset]::UtcNow
$subscription = 'demo-subscription-a'
$otherSubscription = 'demo-subscription-b'
$resourceGroup = 'rg-demo-directory'
$headers = @{}
if (-not [string]::IsNullOrWhiteSpace($AccessToken)) {
    $headers.Authorization = "Bearer $AccessToken"
}

function New-AlertPayload {
    param(
        [string]$AlertId,
        [string]$AlertRule,
        [string]$Severity,
        [string]$MonitorCondition,
        [string]$SubscriptionId,
        [string]$Target,
        [string]$Site,
        [string]$QueryType,
        [DateTimeOffset]$OccurredAt,
        [object[]]$Columns,
        [object[]]$Rows,
        [string]$Description
    )

    return [ordered]@{
        schemaId = 'azureMonitorCommonAlertSchema'
        data = [ordered]@{
            essentials = [ordered]@{
                alertId = $AlertId
                alertRule = $AlertRule
                severity = $Severity
                signalType = 'Log'
                monitorCondition = $MonitorCondition
                targetResourceGroup = $resourceGroup
                targetSubscriptionId = $SubscriptionId
                alertTargetIDs = @("/subscriptions/$SubscriptionId/resourceGroups/$resourceGroup/providers/Microsoft.HybridCompute/machines/$Target")
                firedDateTime = $OccurredAt.ToString('o')
                description = $Description
            }
            alertContext = [ordered]@{
                condition = [ordered]@{
                    allOf = @([ordered]@{
                        searchQuery = "$QueryType demo query"
                        dimensions = @(
                            [ordered]@{ name = 'Computer'; value = $Target },
                            [ordered]@{ name = 'Site'; value = $Site }
                        )
                    })
                }
            }
        }
        queryResult = [ordered]@{
            type = $QueryType
            columns = $Columns
            rows = $Rows
            rowCount = $Rows.Count
        }
    }
}

function Get-DcDiagColumns {
    return @(
        [ordered]@{ name = 'Computer'; type = 'string' },
        [ordered]@{ name = 'Site'; type = 'string' },
        [ordered]@{ name = 'TimeGenerated'; type = 'datetime' },
        [ordered]@{ name = 'Status'; type = 'dynamic' }
    )
}

function New-DcDiagPayload {
    param(
        [string]$AlertId,
        [string]$MonitorCondition = 'Fired',
        [string]$SubscriptionId = $subscription,
        [string]$Target = 'DC-ORION-07',
        [string]$Site = 'Northbridge',
        [DateTimeOffset]$OccurredAt,
        [string]$Severity = 'Sev2',
        [string]$FailedTest = 'Connectivity',
        [string]$AlertRule = 'Directory Diagnostics'
    )

    $status = [ordered]@{
        Advertising = 'Passed'
        Connectivity = 'Passed'
        DNS = 'Passed'
        DFSREvent = 'Passed'
        Replications = 'Passed'
        Services = 'Passed'
    }
    if ($MonitorCondition -eq 'Fired') {
        $status[$FailedTest] = 'Failed'
    }

    return New-AlertPayload `
        -AlertId $AlertId `
        -AlertRule $AlertRule `
        -Severity $Severity `
        -MonitorCondition $MonitorCondition `
        -SubscriptionId $SubscriptionId `
        -Target $Target `
        -Site $Site `
        -QueryType 'DCDiag' `
        -OccurredAt $OccurredAt `
        -Columns (Get-DcDiagColumns) `
        -Rows @(,@($Target, $Site, $OccurredAt.ToString('o'), ($status | ConvertTo-Json -Compress))) `
        -Description 'Anonymized directory diagnostic evidence for local Agent Analysis development.'
}

$payloads = [System.Collections.Generic.List[object]]::new()

$dcPortColumns = @(
    [ordered]@{ name = 'LocalSystem'; type = 'string' },
    [ordered]@{ name = 'RemoteSystem'; type = 'string' },
    [ordered]@{ name = 'RemoteSite'; type = 'string' },
    [ordered]@{ name = 'TimeGenerated'; type = 'datetime' },
    [ordered]@{ name = 'Port'; type = 'string' },
    [ordered]@{ name = 'Protocol'; type = 'string' },
    [ordered]@{ name = 'Status'; type = 'string' }
)
$dcPortAt = $scenarioNow.AddMinutes(-8)
$dcPortRows = @()
foreach ($port in @('53/UDP', '88/TCP', '123/UDP', '135/TCP', '389/TCP', '445/TCP', '464/TCP', '636/TCP', '3268/TCP', '3269/TCP', '9389/TCP')) {
    $parts = $port.Split('/')
    $dcPortRows += ,@('DC-ATLAS-12', 'DC-ORION-07', 'Northbridge', $dcPortAt.ToString('o'), $parts[0], $parts[1], 'failed')
}
$payloads.Add((New-AlertPayload `
    -AlertId 'agent-demo-primary-dcport' `
    -AlertRule 'Domain Controller Port Monitoring' `
    -Severity 'Sev1' `
    -MonitorCondition 'Fired' `
    -SubscriptionId $subscription `
    -Target 'DC-ATLAS-12' `
    -Site 'Cedarfield' `
    -QueryType 'DCPort' `
    -OccurredAt $dcPortAt `
    -Columns $dcPortColumns `
    -Rows $dcPortRows `
    -Description 'Eleven required directory service port checks failed against the anonymized target.'))

$replicationAt = $scenarioNow.AddMinutes(-13)
$replicationColumns = @(
    [ordered]@{ name = 'SourceDSA'; type = 'string' },
    [ordered]@{ name = 'TimeGenerated'; type = 'datetime' },
    [ordered]@{ name = 'DestDSASite'; type = 'string' },
    [ordered]@{ name = 'DestDSA'; type = 'string' },
    [ordered]@{ name = 'NC'; type = 'string' },
    [ordered]@{ name = 'SourceDSASite'; type = 'string' },
    [ordered]@{ name = 'Protocol'; type = 'string' },
    [ordered]@{ name = 'NumberOfFailures'; type = 'string' },
    [ordered]@{ name = 'LastFailureTime'; type = 'string' },
    [ordered]@{ name = 'LastSuccessTime'; type = 'datetime' },
    [ordered]@{ name = 'LastErrorStatus'; type = 'string' }
)
$payloads.Add((New-AlertPayload `
    -AlertId 'agent-demo-primary-replication' `
    -AlertRule 'Directory Replication Delay' `
    -Severity 'Sev2' `
    -MonitorCondition 'Fired' `
    -SubscriptionId $subscription `
    -Target 'DC-ATLAS-12' `
    -Site 'Cedarfield' `
    -QueryType 'Replication' `
    -OccurredAt $replicationAt `
    -Columns $replicationColumns `
    -Rows @(,@('DC-ATLAS-12', $replicationAt.ToString('o'), 'Northbridge', 'DC-ORION-07', 'DC=corp,DC=example,DC=test', 'Cedarfield', 'RPC', '4', $replicationAt.AddMinutes(-2).ToString('o'), $replicationAt.AddMinutes(-48).ToString('o'), '1722')) `
    -Description 'Replication to the anonymized destination failed with RPC error 1722.'))

$payloads.Add((New-DcDiagPayload `
    -AlertId 'agent-demo-primary-dcdiag' `
    -OccurredAt $scenarioNow.AddMinutes(-18) `
    -Severity 'Sev1' `
    -FailedTest 'DNS' `
    -AlertRule 'Directory Health'))

$heartbeatColumns = @(
    [ordered]@{ name = 'Computer'; type = 'string' },
    [ordered]@{ name = 'Site'; type = 'string' },
    [ordered]@{ name = 'TimeGenerated'; type = 'datetime' },
    [ordered]@{ name = 'Status'; type = 'string' },
    [ordered]@{ name = 'DelayMinutes'; type = 'long' }
)
$heartbeatAt = $scenarioNow.AddMinutes(-23)
$payloads.Add((New-AlertPayload `
    -AlertId 'agent-demo-primary-heartbeat' `
    -AlertRule 'Directory Server Heartbeat' `
    -Severity 'Sev2' `
    -MonitorCondition 'Fired' `
    -SubscriptionId $subscription `
    -Target 'DC-ORION-07' `
    -Site 'Northbridge' `
    -QueryType 'Heartbeat' `
    -OccurredAt $heartbeatAt `
    -Columns $heartbeatColumns `
    -Rows @(,@('DC-ORION-07', 'Northbridge', $heartbeatAt.ToString('o'), 'Delayed', 23)) `
    -Description 'The anonymized target heartbeat is delayed.'))

$payloads.Add((New-DcDiagPayload `
    -AlertId 'agent-demo-suppressed-dfsr' `
    -Target 'DC-ATLAS-12' `
    -Site 'Cedarfield' `
    -OccurredAt $scenarioNow.AddHours(-2) `
    -Severity 'Sev3' `
    -FailedTest 'DFSREvent' `
    -AlertRule 'DCDiag Isolated DFSR Event'))

for ($index = 1; $index -le 12; $index++) {
    $historyAt = $scenarioNow.AddHours(-8).AddHours(-10 * $index)
    $historyId = 'agent-demo-history-{0:d2}' -f $index
    $payloads.Add((New-DcDiagPayload `
        -AlertId $historyId `
        -OccurredAt $historyAt `
        -Severity 'Sev3' `
        -FailedTest $(if ($index % 2 -eq 0) { 'Replications' } else { 'DNS' }) `
        -AlertRule 'Historical Directory Health'))
    $payloads.Add((New-DcDiagPayload `
        -AlertId $historyId `
        -MonitorCondition 'Resolved' `
        -OccurredAt $historyAt.AddMinutes(35) `
        -Severity 'Sev3' `
        -AlertRule 'Historical Directory Health'))
}

foreach ($offset in @(3, 5, 7, 9, 11)) {
    $payloads.Add((New-DcDiagPayload `
        -AlertId ('agent-demo-history-open-{0:d2}' -f $offset) `
        -OccurredAt $scenarioNow.AddDays(-$offset).AddHours(-2) `
        -Severity 'Sev3' `
        -FailedTest 'Services' `
        -AlertRule 'Historical Directory Health'))
}

$payloads.Add((New-DcDiagPayload `
    -AlertId 'agent-demo-context-neighbor' `
    -Target 'DC-VEGA-03' `
    -Site 'Northbridge' `
    -OccurredAt $scenarioNow.AddHours(-20) `
    -Severity 'Sev3' `
    -FailedTest 'Advertising' `
    -AlertRule 'Neighbor Directory Health'))
$payloads.Add((New-DcDiagPayload `
    -AlertId 'agent-demo-context-too-old' `
    -Target 'DC-VEGA-03' `
    -Site 'Northbridge' `
    -OccurredAt $scenarioNow.AddHours(-26) `
    -Severity 'Sev3' `
    -FailedTest 'Advertising' `
    -AlertRule 'Neighbor Directory Health'))
$payloads.Add((New-DcDiagPayload `
    -AlertId 'agent-demo-cross-subscription' `
    -SubscriptionId $otherSubscription `
    -OccurredAt $scenarioNow.AddMinutes(-11) `
    -Severity 'Sev1' `
    -FailedTest 'Connectivity' `
    -AlertRule 'Isolated Subscription Health'))

$createdCount = 0
$existingCount = 0
foreach ($payload in $payloads) {
    $response = Invoke-RestMethod `
        -Method Post `
        -Uri $endpoint `
        -Headers $headers `
        -ContentType 'application/json' `
        -Body ($payload | ConvertTo-Json -Depth 30)
    if ($response.created) {
        $createdCount++
    }
    else {
        $existingCount++
    }
}

$connection = [System.Data.SqlClient.SqlConnection]::new($connectionBuilder.ConnectionString)
try {
    $connection.Open()
    $transaction = $connection.BeginTransaction()
    try {
        foreach ($payload in $payloads) {
            $essentials = $payload.data.essentials
            $occurredAt = [DateTimeOffset]::Parse([string]$essentials.firedDateTime)
            $rawJson = $payload | ConvertTo-Json -Depth 30 -Compress
            $dimensionsJson = $payload.data.alertContext.condition.allOf[0].dimensions |
                ConvertTo-Json -Depth 10 -Compress
            $queryResultsJson = $payload.queryResult | ConvertTo-Json -Depth 20 -Compress
            $command = $connection.CreateCommand()
            $command.Transaction = $transaction
            $command.CommandText = @'
UPDATE dbo.Alerts
SET ReceivedAt = @OccurredAt,
    FiredAt = @OccurredAt,
    RawJson = @RawJson
WHERE AlertId = @AlertId AND MonitorCondition = @MonitorCondition;

UPDATE dbo.ParsedAlerts
SET FiredDateTime = @OccurredAt,
    Dimensions = @Dimensions,
    QueryResults = @QueryResults
WHERE AlertId = @AlertId AND MonitorCondition = @MonitorCondition;
'@
            $null = $command.Parameters.AddWithValue('@OccurredAt', $occurredAt)
            $null = $command.Parameters.AddWithValue('@AlertId', [string]$essentials.alertId)
            $null = $command.Parameters.AddWithValue('@MonitorCondition', [string]$essentials.monitorCondition)
            $null = $command.Parameters.AddWithValue('@RawJson', $rawJson)
            $null = $command.Parameters.AddWithValue('@Dimensions', $dimensionsJson)
            $null = $command.Parameters.AddWithValue('@QueryResults', $queryResultsJson)
            $null = $command.ExecuteNonQuery()
        }

        $inventory = @(
            @($subscription, 'DC-ORION-07', 'corp.example.test', 'Northbridge', 'Primary Domain Controller', '10.60.10.17', $scenarioNow.AddMinutes(-23)),
            @($subscription, 'DC-ATLAS-12', 'corp.example.test', 'Cedarfield', 'Domain Controller', '10.60.20.12', $scenarioNow.AddMinutes(-4)),
            @($subscription, 'DC-VEGA-03', 'corp.example.test', 'Northbridge', 'Domain Controller', '10.60.10.23', $scenarioNow.AddMinutes(-2)),
            @($otherSubscription, 'DC-ORION-07', 'isolated.example.test', 'Southridge', 'Domain Controller', '10.80.10.17', $scenarioNow.AddMinutes(-3))
        )
        foreach ($entry in $inventory) {
            $command = $connection.CreateCommand()
            $command.Transaction = $transaction
            $command.CommandText = @'
MERGE dbo.ComputerInventory AS target
USING (SELECT @SubscriptionId AS SubscriptionId, @Computer AS Computer) AS source
ON target.SubscriptionId = source.SubscriptionId AND target.Computer = source.Computer
WHEN MATCHED THEN UPDATE SET
    Domain = @Domain, Site = @Site, ResourceGroup = @ResourceGroup,
    Role = @Role, PrivateIP = @PrivateIP, LastHeartbeatAt = @LastHeartbeatAt
WHEN NOT MATCHED THEN INSERT
    (SubscriptionId, Computer, Domain, Site, ResourceGroup, Role, PrivateIP, LastHeartbeatAt)
VALUES
    (@SubscriptionId, @Computer, @Domain, @Site, @ResourceGroup, @Role, @PrivateIP, @LastHeartbeatAt);
'@
            $null = $command.Parameters.AddWithValue('@SubscriptionId', [string]$entry[0])
            $null = $command.Parameters.AddWithValue('@Computer', [string]$entry[1])
            $null = $command.Parameters.AddWithValue('@Domain', [string]$entry[2])
            $null = $command.Parameters.AddWithValue('@Site', [string]$entry[3])
            $null = $command.Parameters.AddWithValue('@ResourceGroup', $resourceGroup)
            $null = $command.Parameters.AddWithValue('@Role', [string]$entry[4])
            $null = $command.Parameters.AddWithValue('@PrivateIP', [string]$entry[5])
            $null = $command.Parameters.AddWithValue('@LastHeartbeatAt', [DateTimeOffset]$entry[6])
            $null = $command.ExecuteNonQuery()
        }

        $lifecycleCommand = $connection.CreateCommand()
        $lifecycleCommand.Transaction = $transaction
        $lifecycleCommand.CommandText = @'
;WITH Lifecycles AS (
    SELECT AlertId,
           MAX(CASE WHEN MonitorCondition = 'Resolved' THEN FiredDateTime END) AS ResolvedAt
    FROM dbo.ParsedAlerts
    WHERE AlertId LIKE 'agent-demo-history-%'
    GROUP BY AlertId
)
UPDATE parsed
SET ResolvedAt = lifecycle.ResolvedAt
FROM dbo.ParsedAlerts AS parsed
INNER JOIN Lifecycles AS lifecycle ON lifecycle.AlertId = parsed.AlertId;
'@
        $null = $lifecycleCommand.ExecuteNonQuery()
        $transaction.Commit()
    }
    catch {
        $transaction.Rollback()
        throw
    }
}
finally {
    $connection.Dispose()
}

Write-Host "Agent Analysis scenario anchored at $($scenarioNow.ToString('u'))."
Write-Host "Imported $($payloads.Count) payload(s): $createdCount created, $existingCount already present."
Write-Host 'Seeded 37 alert events in 25 lifecycles and 4 anonymized inventory targets across 2 subscriptions.'

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$generator = Join-Path $PSScriptRoot 'Build-CodeViews.ps1'
& $generator -Check

if ($LASTEXITCODE -ne 0) {
    throw "Logic App Code View validation failed with exit code $LASTEXITCODE."
}

$inventoryWorkflowPath = Join-Path $PSScriptRoot 'heartbeat-inventory-sync.codeview.json'
$inventoryWorkflow = Get-Content $inventoryWorkflowPath -Raw | ConvertFrom-Json
$inventoryTrigger = $inventoryWorkflow.definition.triggers.Every_five_minutes
$inventoryQuery = $inventoryWorkflow.definition.actions.Run_Log_Analytics_Heartbeat_Query.inputs.body.query
$inventoryPayload = $inventoryWorkflow.definition.actions.Forward_Inventory_Status_To_Webhook.inputs.body

if ($inventoryTrigger.type -ne 'Recurrence' -or
    $inventoryTrigger.recurrence.frequency -ne 'Minute' -or
    $inventoryTrigger.recurrence.interval -ne 5) {
    throw 'Scheduled inventory workflow must run every five minutes.'
}

foreach ($field in @('SubscriptionId', 'Computer', 'Domain', 'PrivateIP', 'LastHeartbeat')) {
    if (-not $inventoryQuery.Contains($field)) {
        throw "Scheduled inventory query does not contain '$field'."
    }
}

if (-not $inventoryQuery.Contains('NetBiosComputer = iff(DotIndex >= 0, substring(ComputerFqdn, 0, DotIndex), ComputerFqdn)') -or
    -not $inventoryQuery.Contains('Domain = iff(DotIndex >= 0, substring(ComputerFqdn, DotIndex + 1), "")') -or
    -not $inventoryQuery.Contains('Computer = NetBiosComputer') -or
    -not $inventoryQuery.Contains('by SubscriptionId, Computer')) {
    throw 'Scheduled inventory query must split the FQDN into the NetBIOS computer name and domain.'
}

if ($inventoryPayload.queryResult.type -ne 'InventoryHeartbeat') {
    throw 'Scheduled inventory payload must use queryResult.type InventoryHeartbeat.'
}

Write-Host 'Valid: heartbeat-inventory-sync.codeview.json'

$casesPath = Join-Path $PSScriptRoot 'query-enrichment-cases.json'
$cases = (Get-Content $casesPath -Raw | ConvertFrom-Json).cases
foreach ($case in $cases) {
    $refreshPath = Join-Path $PSScriptRoot ([string]$case.refreshOutputFile)
    $refresh = Get-Content $refreshPath -Raw | ConvertFrom-Json
    $trigger = $refresh.definition.triggers.Every_five_minutes
    $targets = $refresh.definition.actions.Get_Active_Refresh_Targets
    $loop = $refresh.definition.actions.For_each_active_alert
    $query = $loop.actions.Run_Log_Analytics_Query.inputs.body.query
    $update = $loop.actions.Update_Query_Result

    if ($trigger.type -ne 'Recurrence' -or
        $trigger.recurrence.frequency -ne 'Minute' -or
        $trigger.recurrence.interval -ne 5 -or
        $trigger.runtimeConfiguration.concurrency.runs -ne 1) {
        throw "Scheduled $($case.name) refresh workflow must run every five minutes without overlapping runs."
    }

    if (-not $targets.inputs.uri.Contains('refreshTargetsUrl') -or
        $targets.inputs.method -ne 'GET' -or
        -not $refresh.parameters.refreshTargetsUrl.value.EndsWith("?type=$($case.queryResultType)")) {
        throw "Scheduled $($case.name) refresh workflow must load the matching active targets from the Monitoring API."
    }

    if ($loop.operationOptions -ne 'Sequential' -or
        -not $query.Contains("item()?['dimensionValue']")) {
        throw "Scheduled $($case.name) refresh workflow must query validated targets sequentially."
    }

    if ($update.inputs.method -ne 'PUT' -or
        -not $update.inputs.uri.Contains('/query-result') -or
        $update.inputs.body.type -ne $case.queryResultType -or
        $update.inputs.body.dimensionName -ne $case.dimensionName) {
        throw "Scheduled $($case.name) refresh workflow must update the matching existing query result."
    }

    Write-Host "Valid: $($case.refreshOutputFile)"
}

Write-Host 'All generated Logic App Code View files are current and valid.'
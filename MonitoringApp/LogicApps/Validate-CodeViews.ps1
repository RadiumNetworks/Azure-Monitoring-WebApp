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
Write-Host 'All generated Logic App Code View files are current and valid.'
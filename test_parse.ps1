$tPath = "MonitoringApp\logic-app-dcdiag-query.user-assigned.template.json"
$cPath = "MonitoringApp\logic-app-dcdiag-query.user-assigned.codeview.json"

$tJson = Get-Content -Raw $tPath | ConvertFrom-Json
$cJson = Get-Content -Raw $cPath | ConvertFrom-Json

$tActions = $tJson.resources[0].properties.definition.actions
$cActions = $cJson.definition.actions

Write-Output "Template action keys: $($tActions.PSObject.Properties.Name -join ', ')"
Write-Output "Codeview action keys: $($cActions.PSObject.Properties.Name -join ', ')"

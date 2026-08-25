$tPath = "MonitoringApp\logic-app-dcdiag-query.user-assigned.template.json"
$cPath = "MonitoringApp\logic-app-dcdiag-query.user-assigned.codeview.json"

$tJson = ConvertFrom-Json (Get-Content -Raw $tPath)
$cJson = ConvertFrom-Json (Get-Content -Raw $cPath)

$tDef = $tJson.resources[0].properties.definition
$cDef = $cJson.definition

# Let's inspect the entire text of the actions to find replication queries and authentication
# We'll also normalize the identity strings:
# template uses "[parameters('userAssignedIdentityResourceId')]"
# codeview uses "<user-assigned-managed-identity-resource-id>"
# Let's normalize by replacing both in their respective JSON string representation (for comparison)

$tActionsJson = ConvertTo-Json $tDef.actions -Depth 100
$cActionsJson = ConvertTo-Json $cDef.actions -Depth 100

# Normalize identity strings
$tNorm = $tActionsJson -replace "\[parameters\('userAssignedIdentityResourceId'\)\]", "IDENTITY_PLACEHOLDER"
$cNorm = $cActionsJson -replace "<user-assigned-managed-identity-resource-id>", "IDENTITY_PLACEHOLDER"

# Let's parse them back and check if they match (or compare their hash/string)
# First strip spaces and newlines to avoid formatting differences
$tClean = $tNorm -replace "\s+", ""
$cClean = $cNorm -replace "\s+", ""

$match = $tClean -eq $cClean
Write-Output "Action trees match after normalization: $match"

if (-not $match) {
    # Let's see differences
    Write-Output "tClean length: $($tClean.Length)"
    Write-Output "cClean length: $($cClean.Length)"
}

# Let's find the Replication query text
# Usually in logic apps, queries are in actions like HTTP, Azure Monitor, or Log Analytics. Let's find any query strings or bodies.
$queryMatches = [regex]::Matches($tActionsJson, '"query"\s*:\s*"([^"]+)"')
foreach ($qm in $queryMatches) {
    Write-Output "Found query: $($qm.Groups[1].Value)"
}

# If not "query", let's search for "Replication" or look at the action names
Write-Output "Action names in Template:"
$tDef.actions.PSObject.Properties.Name | ForEach-Object { Write-Output " - $_" }

# Let's print out the exact HTTP request body or query parameters
# For each action, let's look for "queries" or "body"
foreach ($actName in $tDef.actions.PSObject.Properties.Name) {
    $act = $tDef.actions.$actName
    Write-Output "Action: $actName"
    if ($act.inputs) {
        if ($act.inputs.body) {
            Write-Output "  Body: $($act.inputs.body | ConvertTo-Json -Depth 5)"
        }
        if ($act.inputs.queries) {
            Write-Output "  Queries: $($act.inputs.queries | ConvertTo-Json -Depth 5)"
        }
    }
}

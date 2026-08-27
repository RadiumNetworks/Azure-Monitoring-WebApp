$cv_path = "MonitoringApp\logic-app-dcport-query.user-assigned.codeview.json"
$tmpl_path = "MonitoringApp\logic-app-dcport-query.user-assigned.template.json"

$cv_text = Get-Content -Raw -Path $cv_path
$tmpl_text = Get-Content -Raw -Path $tmpl_path

# 1. Parse both as JSON
$cv = ConvertFrom-Json $cv_text
$tmpl = ConvertFrom-Json $tmpl_text

# 2. String replace placeholders in cv text and parse that version for structural comparison
$cv_replaced_text = $cv_text -replace '"<user-assigned-managed-identity-resource-id>"', '"[parameters(''userAssignedIdentityResourceId'')]"'
$cv_replaced = ConvertFrom-Json $cv_replaced_text

# 3. Structural comparison of template resources[0].properties.definition and cv_replaced.definition
$tmpl_def = $tmpl.resources[0].properties.definition
$cv_def = $cv_replaced.definition

# Define helper to deeply compare two objects
function Compare-ObjectsDeep($obj1, $obj2, $path = "") {
    if ($null -eq $obj1 -and $null -eq $obj2) { return }
    if ($null -eq $obj1 -or $null -eq $obj2) {
        Write-Output "Mismatch at $path: $obj1 vs $obj2"
        return $false
    }
    $t1 = $obj1.GetType().Name
    $t2 = $obj2.GetType().Name
    if ($t1 -ne $t2 -and ($t1 -match "Int" -and $t2 -match "Int" -eq $false)) {
        # conversion check
    }
    
    if ($obj1 -is [System.Management.Automation.PSCustomObject] -or $obj1 -is [System.Collections.IDictionary]) {
        $keys1 = $obj1 | Get-Member -MemberType NoteProperty | Select-Object -ExpandProperty Name | Sort-Object
        $keys2 = $obj2 | Get-Member -MemberType NoteProperty | Select-Object -ExpandProperty Name | Sort-Object
        
        if ($keys1.Count -ne $keys2.Count) {
            Write-Output "Mismatch keys count at $path: $($keys1 -join ',') vs $($keys2 -join ',')"
            return $false
        }
        for ($i = 0; $i -lt $keys1.Count; $i++) {
            if ($keys1[$i] -ne $keys2[$i]) {
                Write-Output "Mismatch keys at $path: $($keys1[$i]) vs $($keys2[$i])"
                return $false
            }
            $p = if ($path -eq "") { $keys1[$i] } else { "$path.$($keys1[$i])" }
            $res = Compare-ObjectsDeep $obj1.$($keys1[$i]) $obj2.$($keys2[$i]) $p
            if ($res -eq $false) { return $false }
        }
    } elseif ($obj1 -is [System.Array] -or $obj1 -is [System.Collections.IList]) {
        if ($obj1.Count -ne $obj2.Count) {
            Write-Output "Mismatch array count at $path: $($obj1.Count) vs $($obj2.Count)"
            return $false
        }
        for ($i = 0; $i -lt $obj1.Count; $i++) {
            $res = Compare-ObjectsDeep $obj1[$i] $obj2[$i] "$path[$i]"
            if ($res -eq $false) { return $false }
        }
    } else {
        $v1 = $obj1.ToString()
        $v2 = $obj2.ToString()
        if ($v1 -ne $v2) {
            Write-Output "Value mismatch at $path: '$v1' vs '$v2'"
            return $false
        }
    }
    return $true
}

$def_comparison = Compare-ObjectsDeep $tmpl_def $cv_def "definition"
Write-Output "Definition match: $def_comparison"

# 4. Also verify ARM top-level parameters, workflow UAMI attachment, property parameter bindings, and outputs match the established DCDiag wrapper shape.
# Let's inspect the DCDiag structure or list what we see to ensure it matches:
# - parameters should have: workflowName, location, workspaceId, webhookUrl, webhookAudience, userAssignedIdentityResourceId
# - identity block has UserAssigned and [parameters('userAssignedIdentityResourceId')]
# - properties.parameters should have bindings for workspaceId, webhookUrl, webhookAudience pointing to parameters(...)
# - properties.definition should have these defined as well.
# - Let's see if outputs match too. Let's list template outputs.
Write-Output "Template parameters:"
$tmpl.parameters | Get-Member -MemberType NoteProperty | Select-Object -ExpandProperty Name

Write-Output "Template resources[0].identity:"
$tmpl.resources[0].identity | ConvertTo-Json -Depth 5

Write-Output "Template resources[0].properties.parameters:"
$tmpl.resources[0].properties.parameters | ConvertTo-Json -Depth 5

Write-Output "Template outputs:"
$tmpl.outputs | ConvertTo-Json -Depth 5


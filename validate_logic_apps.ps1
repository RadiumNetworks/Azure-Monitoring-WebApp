# Semantic validation of Logic App JSON files
$manifest = Get-Content "MonitoringApp\LogicApps\query-enrichment-cases.json" -Raw | ConvertFrom-Json
$cases = $manifest.cases

# Checking logic-app*.json in MonitoringApp root directory
Write-Output "Finding logic-app*.json in MonitoringApp root..."
$rootFiles = Get-ChildItem -Path "MonitoringApp\logic-app*.json" | Select-Object -ExpandProperty Name
Write-Output "Files in MonitoringApp root matching logic-app*.json: ($($rootFiles -join ', '))"
$hasOnlyCodeview = $true
$rootFiles | ForEach-Object {
    if ($_ -notlike "*codeview.json") { $hasOnlyCodeview = $false }
}
Write-Output "Only codeview files exist in root: $hasOnlyCodeview"

# Validate each case
foreach ($case in $cases) {
    Write-Output "=========================================="
    Write-Output "Validating Case: $($case.name)"
    $filePath = Join-Path "MonitoringApp" $case.outputFile
    if (-not (Test-Path $filePath)) {
        Write-Error "File not found: $filePath"
        continue
    }
    
    $json = Get-Content $filePath -Raw | ConvertFrom-Json
    $definition = $json.definition
    
    # helper to find actions recursively or from expected nested paths
    $actions = $definition.actions
    
    # 1. verify Filter_<dimension>_Dimension & <dimension>_Dimension_Found exist
    $dim = $case.dimensionName
    $filterName = "Filter_${dim}_Dimension"
    $foundName = "${dim}_Dimension_Found"
    
    $filterAction = $actions."$filterName"
    $foundAction = $actions."$foundName"
    
    if ($null -eq $filterAction) { Write-Error "Missing filter action: $filterName"; continue }
    if ($null -eq $foundAction) { Write-Error "Missing found action: $foundName"; continue }
    Write-Output "✔ Filter and Found actions exist."
    
    # 2. verify filter expression names the lowercase dimension
    $whereExpr = $filterAction.inputs.where
    $lowDim = $dim.ToLower()
    if ($whereExpr -like "*'$lowDim'*") {
        Write-Output "✔ Filter expression names lowercase dimension: $lowDim"
    } else {
        Write-Error "Filter expression mismatch! Expected lowercase '$lowDim' in: $whereExpr"
    }
    
    # 3. parse nested actions inside Found Action
    # Computer_Dimension_Found -> actions -> Computer_Value_Is_Safe -> actions -> Run_Log_Analytics_Query etc.
    $safeIfName = "${dim}_Value_Is_Safe"
    $composeName = "$dim"
    
    $nestedActions1 = $foundAction.actions
    $composeAction = $nestedActions1."$composeName"
    $safeIfAction = $nestedActions1."$safeIfName"
    
    if ($null -eq $composeAction) { Write-Error "Missing compose action: $composeName"; continue }
    if ($null -eq $safeIfAction) { Write-Error "Missing safe IF action: $safeIfName"; continue }
    
    $nestedActions2 = $safeIfAction.actions
    $runLAAction = $nestedActions2.Run_Log_Analytics_Query
    $forwardAction = $nestedActions2.Forward_Alert_To_Webhook
    $responseAction = $nestedActions2.Return_Query_Result
    
    if ($null -eq $runLAAction) { Write-Error "Missing Run_Log_Analytics_Query action"; continue }
    if ($null -eq $forwardAction) { Write-Error "Missing Forward_Alert_To_Webhook action"; continue }
    if ($null -eq $responseAction) { Write-Error "Missing Return_Query_Result action"; continue }
    Write-Output "✔ Found Run_Log_Analytics_Query, Forward_Alert_To_Webhook, Return_Query_Result."
    
    # 4. verify Log Analytics query references outputs('<dimension>')
    $laQuery = $runLAAction.inputs.body.query
    $expectedOutputRef = "outputs('$dim')"
    # Since JSON contains escaped single quotes \u0027 or '
    # In PowerShell, let's normalize both to single quotes
    $normalizedQuery = $laQuery -replace "\\u0027", "'" -replace "[\x27]", "'"
    if ($normalizedQuery -like "*$expectedOutputRef*") {
        Write-Output "✔ Log Analytics query references outputs('$dim')"
    } else {
        Write-Error "Log Analytics query doesn't reference outputs('$dim')! Query is: $laQuery"
    }
    
    # 5. verify KQL reconstruction when {{dimensionValue}} is replaced by sentinel
    # Normalized query looks like @concat('QueryPart1', outputs('dim'), 'QueryPart2')
    # Let's extract static parts
    # If the format is @concat('part1', outputs('dim'), 'part2') we can reconstruct it by replacing outputs with sentinel
    $sentinel = "{{dimensionValue}}"
    # We want to reconstruct: part1 + sentinel + part2
    # Let's see how concat is laid out: "@concat('...', outputs('...'), '...')"
    if ($normalizedQuery -match "^@concat\((.*)\)$") {
        # Split by comma we might have issues with commas inside strings. So let's parse gracefully
        # Since it's a known format: concat('part1', outputs('dim'), 'part2')
        # Let's write a simple extraction of the prefix and suffix string literals.
        # Let's find single-quoted strings:
        # A simple approach: let's replace outputs(...) with sentinel, delete '@concat(' and ')' then join string parts
        $reconstructedExp = $normalizedQuery -replace "outputs\('$dim'\)", "'$sentinel'"
        # Remove '@concat(' and ')'
        if ($reconstructedExp -match "^@concat\('(.*)'\)$") {
            # Let's do a more robust split of single quotes while ignoring escaped/internal single quotes.
            # Usually it is: '@concat('...KQL1...', outputs('Computer'), '...KQL2...')'
            # Let's extract prefix and suffix via regex
            $m = [regex]::Match($normalizedQuery, "^@concat\('(.*)',\s*outputs\('$dim'\),\s*'(.*)'\)$", [System.Text.RegularExpressions.RegexOptions]::Singleline)
            if ($m.Success) {
                # Unescape single quotes inside KQL back
                $prefix = $m.Groups[1].Value
                $suffix = $m.Groups[2].Value
                $reconstructedKql = $prefix + $sentinel + $suffix
                
                # Compare to case KQL. The case KQL might have different carriage returns etc. Normalizing both:
                $normalizedReconstructed = ($reconstructedKql -replace "\r\n", "`n").Trim()
                $normalizedCaseKql = ($case.kql -replace "\r\n", "`n").Trim()
                
                if ($normalizedReconstructed -eq $normalizedCaseKql) {
                    Write-Output "✔ KQL matches the case KQL exactly upon reconstruction!"
                } else {
                    Write-Error "KQL mismatch on reconstruction!"
                    Write-Error "Expected:`n$normalizedCaseKql"
                    Write-Error "Reconstructed:`n$normalizedReconstructed"
                }
            } else {
                Write-Error "Failed to parse @concat structure of: $normalizedQuery"
            }
        } else {
            Write-Error "Does not match expected @concat pattern: $normalizedQuery"
        }
    } else {
        Write-Error "Query not wrapped in concat: $laQuery"
    }
    
    # 6. verify timespan and queryResult.type
    $timespan = $runLAAction.inputs.body.timespan
    if ($timespan -eq $case.timespan) {
        Write-Output "✔ Timespan matches: $timespan"
    } else {
        Write-Error "Timespan mismatch! Expected $($case.timespan), got $timespan"
    }
    
    $qrType = $forwardAction.inputs.body.queryResult.type
    if ($qrType -eq $case.queryResultType) {
        Write-Output "✔ QueryResult type matches: $qrType"
    } else {
        Write-Error "QueryResult type mismatch! Expected $($case.queryResultType), got $qrType"
    }
    
    # 7. verify queryResult context property exists and equals @outputs('<dimension>') in both webhook and response
    # Webhook queryResult context property is e.g. computer
    $ctxProp = $case.contextProperty
    $webhookCtxVal = $forwardAction.inputs.body.queryResult."$ctxProp"
    $expectedRef = "@outputs('$dim')"
    $expectedRefNormalized = $expectedRef -replace "[\x27]", "'"
    
    $normalizedWebhookCtxVal = $webhookCtxVal -replace "[\x27]", "'" -replace "\\u0027", "'"
    if ($normalizedWebhookCtxVal -eq $expectedRefNormalized) {
        Write-Output "✔ Webhook queryResult.$ctxProp equals $expectedRefNormalized"
    } else {
        Write-Error "Webhook queryResult.$ctxProp mismatch! Got '$normalizedWebhookCtxVal', expected '$expectedRefNormalized'"
    }
    
    $respCtxVal = $responseAction.inputs.body."$ctxProp"
    $normalizedRespCtxVal = $respCtxVal -replace "[\x27]", "'" -replace "\\u0027", "'"
    if ($normalizedRespCtxVal -eq $expectedRefNormalized) {
        Write-Output "✔ Response body.$ctxProp equals $expectedRefNormalized"
    } else {
        Write-Error "Response body.$ctxProp mismatch! Got '$normalizedRespCtxVal', expected '$expectedRefNormalized'"
    }
    
    # 8. verify both HTTP authentications use ManagedServiceIdentity and the UAMI placeholder
    $laAuth = $runLAAction.inputs.authentication
    $fwdAuth = $forwardAction.inputs.authentication
    
    $uamiPlaceholder = "<user-assigned-managed-identity-resource-id>"
    if ($laAuth.type -eq "ManagedServiceIdentity" -and $laAuth.identity -eq $uamiPlaceholder) {
        Write-Output "✔ Run_Log_Analytics_Query auth matches ManagedServiceIdentity & placeholder."
    } else {
        Write-Error "Run_Log_Analytics_Query auth invalid: Type=$($laAuth.type), Identity=$($laAuth.identity)"
    }
    if ($fwdAuth.type -eq "ManagedServiceIdentity" -and $fwdAuth.identity -eq $uamiPlaceholder) {
        Write-Output "✔ Forward_Alert_To_Webhook auth matches ManagedServiceIdentity & placeholder."
    } else {
        Write-Error "Forward_Alert_To_Webhook auth invalid: Type=$($fwdAuth.type), Identity=$($fwdAuth.identity)"
    }
    
    # 9. verify no unresolved {{...}} tokens anywhere in file contents
    $rawContent = Get-Content $filePath -Raw
    # Ignore regex check for double curly braces if they contain nothing but word chars / acceptable things, or just check for presence of "{{..."
    if ($rawContent -match "\{\{[a-zA-Z0-9_]+\}\}") {
        Write-Error "Unresolved tokens found in $filePath: $matches"
    } else {
        Write-Output "✔ No unresolved {{...}} tokens found in file!"
    }
}

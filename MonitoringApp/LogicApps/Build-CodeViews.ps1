[CmdletBinding()]
param(
    [Parameter()]
    [switch]$Check
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$templatePath = Join-Path $PSScriptRoot 'query-enrichment.codeview.template.json'
$casesPath = Join-Path $PSScriptRoot 'query-enrichment-cases.json'
$outputDirectory = Split-Path $PSScriptRoot -Parent
$template = Get-Content $templatePath -Raw
$cases = (Get-Content $casesPath -Raw | ConvertFrom-Json).cases
$dimensionToken = '{{dimensionValue}}'

function ConvertTo-JsonLiteral {
    param([AllowEmptyString()][string]$Value)

    return ConvertTo-Json -InputObject $Value -Compress
}

function Set-TemplateValue {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Name,
        [AllowEmptyString()][string]$Value
    )

    $token = '"{{' + $Name + '}}"'
    if (-not $Text.Contains($token)) {
        throw "Template token {{$Name}} was not found."
    }

    return $Text.Replace($token, (ConvertTo-JsonLiteral $Value))
}

foreach ($case in $cases) {
    $outputFile = [string]$case.outputFile
    if ([IO.Path]::GetFileName($outputFile) -ne $outputFile -or
        -not $outputFile.EndsWith('.codeview.json', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Case '$($case.name)' has an invalid outputFile '$outputFile'."
    }

    $dimensionName = [string]$case.dimensionName
    $contextProperty = [string]$case.contextProperty
    $kql = [string]$case.kql
    if ([regex]::Matches($kql, [regex]::Escape($dimensionToken)).Count -ne 1) {
        throw "Case '$($case.name)' KQL must contain exactly one $dimensionToken placeholder."
    }
    $parts = $kql.Split([string[]]@($dimensionToken), 2, [StringSplitOptions]::None)

    $filterAction = "Filter_${dimensionName}_Dimension"
    $dimensionFoundAction = "${dimensionName}_Dimension_Found"
    $safeValueAction = "${dimensionName}_Value_Is_Safe"
    $dimensionOutputExpression = "@outputs('$dimensionName')"
    $queryPrefix = $parts[0].Replace("'", "''")
    $querySuffix = $parts[1].Replace("'", "''")
    $queryExpression = "@concat('$queryPrefix', outputs('$dimensionName'), '$querySuffix')"
    $values = [ordered]@{
        filterAction = $filterAction
        dimensionFoundAction = $dimensionFoundAction
        dimensionName = $dimensionName
        safeValueAction = $safeValueAction
        dimensionFilterExpression = "@equals(toLower(string(item()?['name'])), '$($dimensionName.ToLowerInvariant())')"
        dimensionFoundExpression = "@greater(length(body('$filterAction')), 0)"
        dimensionValueExpression = "@trim(string(first(body('$filterAction'))?['value']))"
        safeValueExpression = "@and(not(empty(outputs('$dimensionName'))), not(equals(toUpper(outputs('$dimensionName')), '<EMPTY_VALUE>')), not(contains(outputs('$dimensionName'), decodeUriComponent('%22'))), not(contains(outputs('$dimensionName'), decodeUriComponent('%5C'))), not(contains(outputs('$dimensionName'), decodeUriComponent('%0A'))), not(contains(outputs('$dimensionName'), decodeUriComponent('%0D'))))"
        queryExpression = $queryExpression
        timespan = [string]$case.timespan
        queryResultType = [string]$case.queryResultType
        contextProperty = $contextProperty
        dimensionOutputExpression = $dimensionOutputExpression
        invalidResponseAction = "Return_Invalid_$dimensionName"
        invalidDimensionError = "The $dimensionName dimension is empty, contains a placeholder, or contains unsupported characters."
        missingResponseAction = "Return_Missing_$dimensionName"
        missingDimensionError = "The alert payload does not contain a $dimensionName dimension at data.alertContext.condition.allOf[0].dimensions."
    }

    $rendered = $template
    foreach ($entry in $values.GetEnumerator()) {
        $rendered = Set-TemplateValue -Text $rendered -Name $entry.Key -Value $entry.Value
    }

    $unresolved = @([regex]::Matches($rendered, '\{\{[^{}]+\}\}') | ForEach-Object Value | Select-Object -Unique)
    if ($unresolved.Count -gt 0) {
        throw "Case '$($case.name)' left unresolved template tokens: $($unresolved -join ', ')."
    }

    $null = $rendered | ConvertFrom-Json
    $rendered = $rendered.TrimEnd() + [Environment]::NewLine
    $outputPath = Join-Path $outputDirectory $outputFile

    if ($Check) {
        if (-not (Test-Path $outputPath)) {
            throw "Generated file '$outputFile' does not exist. Run Build-CodeViews.ps1 first."
        }
        if ((Get-Content $outputPath -Raw) -cne $rendered) {
            throw "Generated file '$outputFile' is out of date. Run Build-CodeViews.ps1."
        }
        Write-Host "Current: $outputFile"
    }
    else {
        [IO.File]::WriteAllText($outputPath, $rendered, [Text.UTF8Encoding]::new($false))
        Write-Host "Generated: $outputFile"
    }
}

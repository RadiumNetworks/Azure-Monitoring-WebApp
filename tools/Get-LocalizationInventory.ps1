[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$OutputDirectory = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $RepositoryRoot 'doc'
}
if (-not (Test-Path -LiteralPath $OutputDirectory -PathType Container)) {
    throw "Inventory output directory does not exist: $OutputDirectory"
}

$application = Join-Path $RepositoryRoot 'MonitoringApp'
$promptTemplateDirectory = Join-Path $application 'Services\AgentAnalysis\Templates'
$scopes = @(
    'Components', 'Domain', 'Services', 'Infrastructure', 'Endpoints',
    'Middleware', 'Presentation', 'wwwroot', 'AlertDefinitions',
    'AnalysisQueries', 'DemoData', 'Deployment'
)
$extensions = @('.cs', '.razor', '.js', '.css', '.json', '.sql', '.ps1')
$files = @(
    foreach ($scope in $scopes) {
        $directory = Join-Path $application $scope
        if (Test-Path -LiteralPath $directory -PathType Container) {
            Get-ChildItem -LiteralPath $directory -Recurse -File |
                Where-Object {
                    $_.Extension -in $extensions -or
                    ($_.DirectoryName -eq $promptTemplateDirectory -and $_.Name.EndsWith('.en.txt'))
                }
        }
    }
    Get-Item -LiteralPath (Join-Path $application 'Program.cs')
) | Sort-Object FullName

$inventory = [System.Collections.Generic.List[object]]::new()
$candidates = [System.Collections.Generic.List[object]]::new()
foreach ($file in $files) {
    $relative = [System.IO.Path]::GetRelativePath($RepositoryRoot, $file.FullName)
    $isEnglishPromptTemplate = $file.DirectoryName -eq $promptTemplateDirectory -and
        $file.Name.EndsWith('.en.txt')
    $category = 'TechnicalAndVisibleText'
    $workPackage = 'LOC-AP6'
    $excluded = $false
    $reason = 'Mixed code; separate visible messages from technical literals.'
    switch -Regex ($relative) {
        '\\Components\\' {
            $category = 'UI'
            $workPackage = 'LOC-AP4'
            $reason = 'Markup, attributes and C# expressions require text review.'
            break
        }
        '\\AlertDefinitions\\' {
            $category = 'ResultDefinition'
            $workPackage = 'LOC-AP5'
            $reason = 'Translate labels only; preserve type, field and comparison values.'
            break
        }
        '\\Services\\KQL\\Templates\\.*\.json$|\\AnalysisQueries\\.*\.json$' {
            $category = 'QueryTemplate'
            $workPackage = 'LOC-AP7'
            $reason = 'Names/descriptions versus stable keys, KQL and persisted user edits.'
            break
        }
        '\\Services\\EFCore\\Migrations\\|\\Services\\EFCore\\Scripts\\' {
            $category = 'HistoricalSchemaAndDeployment'
            $workPackage = 'LOC-AP7'
            $excluded = $true
            $reason = 'Do not translate historical migrations or deployment contracts in place.'
            break
        }
        '\\DemoData\\|\\Deployment\\' {
            $category = 'EvidenceOrDeploymentConfiguration'
            $workPackage = 'Excluded'
            $excluded = $true
            $reason = 'Original evidence/configuration is not a UI translation source.'
            break
        }
        '\\Services\\AgentAnalysis\\|\\Domain\\AgentAnalysis\\|\\Domain\\Incidents\\|\\Services\\Incidents\\' {
            $category = 'AgentPromptEvidenceAndContract'
            $workPackage = 'LOC-AP8'
            $reason = 'Preserve evidence/contracts; prompt extraction requires separate review.'
            break
        }
        '\\Presentation\\' {
            $category = 'Presentation'
            $workPackage = 'LOC-AP5'
            $reason = 'Separate localized display from invariant data and prompt consumers.'
            break
        }
        '\\Domain\\' {
            $category = 'DomainAndContract'
            $reason = 'Keep identifiers stable; review explanations and validation separately.'
            break
        }
        '\\wwwroot\\' {
            $category = 'ClientOrStyle'
            $workPackage = 'LOC-AP4'
            $reason = 'Review client-generated labels; do not translate selectors or CSS values.'
            break
        }
    }

    if ($isEnglishPromptTemplate) {
        $reason = 'Versioned English instructions; preserve wording and prompt contracts, no UI-language coupling.'
    }
    $lines = @(Get-Content -LiteralPath $file.FullName -Encoding utf8)
    $text = $lines -join "`n"
    $dependencies = [System.Collections.Generic.List[string]]::new()
    if ($text -match 'QueryResultPresenter|IncidentDiagnosticEvidence|ToCanonicalJson') {
        $dependencies.Add('PromptBoundaryReview')
    }
    if ($text -match 'DbContext|AlertStore|SettingsService|Registry|SqlAuthenticationService') {
        $dependencies.Add('PersistenceReview')
    }
    if ($text -match 'AddSingleton|static readonly') {
        $dependencies.Add('SharedStateReview')
    }

    $candidateCount = 0
    $insideRawString = $false
    if (-not $excluded) {
        for ($index = 0; $index -lt $lines.Count; $index++) {
            $line = $lines[$index]
            $delimiters = [regex]::Matches($line, '"{3,}').Count
            $quoted = $line -match '["''][^"'']*[A-Za-z][^"'']*["'']'
            $markup = $file.Extension -eq '.razor' -and
                $line -match '>\s*[A-Za-z][^<]*<'
            $cssContent = $file.Extension -eq '.css' -and $line -match '\bcontent\s*:'
            $include = $isEnglishPromptTemplate -or $quoted -or $markup -or $insideRawString -or $delimiters -gt 0 -or $cssContent
            if ($file.Extension -eq '.css') { $include = $cssContent }
            if ($include -and -not [string]::IsNullOrWhiteSpace($line)) {
                $candidateCount++
                $candidates.Add([pscustomobject]@{
                    File = $relative
                    Line = $index + 1
                    Category = $category
                    WorkPackage = $workPackage
                    CandidateLine = $line.Trim()
                    ResourceKey = ''
                    ParametersAndPlural = 'ReviewRequired'
                    Dependencies = $dependencies -join ';'
                    ReviewStatus = 'CandidateNotApprovedForExtraction'
                })
            }
            if ($delimiters % 2 -eq 1) { $insideRawString = -not $insideRawString }
        }
    }

    $inventory.Add([pscustomobject]@{
        File = $relative
        Category = $category
        WorkPackage = $workPackage
        ProposedResourceFamily = if ($excluded) { '' } else {
            [System.IO.Path]::ChangeExtension(
                [System.IO.Path]::GetRelativePath($application, $file.FullName), $null).Replace('\', '.')
        }
        Decision = if ($excluded) { 'ExcludedFromDirectExtraction' } else { 'TextReviewRequired' }
        Reason = $reason
        Lines = $lines.Count
        CandidateLines = $candidateCount
        Dependencies = $dependencies -join ';'
        SourceSha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    })
}

$inventory | Export-Csv -LiteralPath (Join-Path $OutputDirectory 'LOCALIZATION-FILE-INVENTORY.csv') -NoTypeInformation -Encoding utf8
$candidates | Export-Csv -LiteralPath (Join-Path $OutputDirectory 'LOCALIZATION-TEXT-CANDIDATES.csv') -NoTypeInformation -Encoding utf8
Write-Output "Files: $($inventory.Count); candidate lines (not translation units): $($candidates.Count)"
$inventory | Group-Object Category | Sort-Object Name | Select-Object Name, Count

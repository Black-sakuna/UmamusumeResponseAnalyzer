[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SleetConfig,

    [Parameter(Mandatory)]
    [string]$SleetSource
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$configPath = (Resolve-Path -LiteralPath $SleetConfig).Path
$relativeConfigPath = [System.IO.Path]::GetRelativePath($repoRoot, $configPath)
if (-not [System.IO.Path]::IsPathRooted($relativeConfigPath) -and
    -not $relativeConfigPath.StartsWith("..$([System.IO.Path]::DirectorySeparatorChar)", [System.StringComparison]::Ordinal)) {
    throw "Sleet config must be outside the repository: '$configPath'."
}

$packageId = "UmamusumeResponseAnalyzer"
$packageVersion = "2026.9.1"
$versionIndexUri = "https://ura.shuise.net/nuget/flatcontainer/$($packageId.ToLowerInvariant())/index.json"
$versionIndex = $null
try {
    $versionIndex = Invoke-RestMethod -Method Get -Uri $versionIndexUri
} catch {
    if ($_.Exception.Response.StatusCode -ne [System.Net.HttpStatusCode]::NotFound) {
        throw
    }
}
if ($null -ne $versionIndex -and $versionIndex.versions -contains $packageVersion) {
    throw "$packageId $packageVersion already exists in the Sleet feed. Existing packages are never overwritten."
}

$packageOutput = Join-Path $repoRoot "artifacts\nuget"
$project = Join-Path $repoRoot "UmamusumeResponseAnalyzer\UmamusumeResponseAnalyzer.csproj"
dotnet pack $project --configuration Release --output $packageOutput
if ($LASTEXITCODE -ne 0) {
    throw "dotnet pack failed with exit code $LASTEXITCODE."
}

$packagePath = Join-Path $packageOutput "$packageId.$packageVersion.nupkg"
if (-not (Test-Path -LiteralPath $packagePath)) {
    throw "Expected package was not created: '$packagePath'."
}

$toolManifest = Join-Path $repoRoot ".config\dotnet-tools.json"
dotnet tool restore --tool-manifest $toolManifest
if ($LASTEXITCODE -ne 0) {
    throw "dotnet tool restore failed with exit code $LASTEXITCODE."
}

Push-Location $repoRoot
try {
    dotnet tool run sleet -- push --config $configPath --source $SleetSource $packagePath
    if ($LASTEXITCODE -ne 0) {
        throw "sleet push failed with exit code $LASTEXITCODE."
    }
} finally {
    Pop-Location
}

$ErrorActionPreference = "Stop"

$engRoot = Split-Path -Parent $PSScriptRoot
$propsPath = Join-Path $engRoot "URA.Plugin.Build.props"
$targetsPath = Join-Path $engRoot "URA.Plugin.Build.targets"
$projectPath = Join-Path (Split-Path -Parent $engRoot) "UmamusumeResponseAnalyzer\UmamusumeResponseAnalyzer.csproj"
$errors = New-Object System.Collections.Generic.List[string]

[xml]$project = Get-Content -LiteralPath $projectPath -Raw
if ($project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText -ne '1.14.4.0') {
    $errors.Add("$projectPath must keep Host assembly Version 1.14.4.0")
}
if ($project.SelectSingleNode('/Project/PropertyGroup/PackageId').InnerText -ne 'UmamusumeResponseAnalyzer' -or
    $project.SelectSingleNode('/Project/PropertyGroup/PackageVersion').InnerText -ne '2026.9.1') {
    $errors.Add("$projectPath must pack UmamusumeResponseAnalyzer 2026.9.1")
}
if ($project.SelectSingleNode('/Project/PropertyGroup/IncludeBuildOutput').InnerText -ne 'false') {
    $errors.Add("$projectPath must not pack the runtime Host build output")
}
$packedPaths = @($project.SelectNodes('/Project/ItemGroup/None[@Pack="true"]') | ForEach-Object { $_.PackagePath })
foreach ($expectedPath in @('ref\$(TargetFramework)\', 'buildTransitive\UmamusumeResponseAnalyzer.props', 'buildTransitive\UmamusumeResponseAnalyzer.targets')) {
    if ($packedPaths -notcontains $expectedPath) {
        $errors.Add("$projectPath must pack $expectedPath")
    }
}

[xml]$props = Get-Content -LiteralPath $propsPath -Raw
if (@($props.SelectNodes("/Project/PropertyGroup/DefaultItemExcludes") |
    Where-Object { $_.InnerText.Contains('$(MSBuildProjectDirectory)\deps\**') }).Count -ne 1) {
    $errors.Add("$propsPath must exclude pinned dependency source trees from SDK default items")
}
if (@($props.SelectNodes("/Project/PropertyGroup/DefaultItemExcludes") |
    Where-Object { $_.InnerText.Contains('$(MSBuildProjectDirectory)\tests\**') }).Count -ne 1) {
    $errors.Add("$propsPath must exclude repository-owned test source trees from plugin default items")
}

if (-not (Test-Path -LiteralPath $targetsPath)) {
    $errors.Add("$targetsPath must provide URA plugin build targets")
} else {
    [xml]$targets = Get-Content -LiteralPath $targetsPath -Raw

    if (@($targets.SelectNodes("//UraHostProjectPath")).Count -ne 0) {
        $errors.Add("$targetsPath must not define UraHostProjectPath")
    }

    $expectedDefaultCondition = "'`$(IsUraPlugin)' == ''"
    $defaultPluginValues = @($targets.SelectNodes("/Project/PropertyGroup/IsUraPlugin") |
        Where-Object { $_.Condition -eq $expectedDefaultCondition } |
        ForEach-Object { $_.InnerText.Trim() })
    if ($defaultPluginValues -notcontains "false") {
        $errors.Add("$targetsPath must default IsUraPlugin to false; plugin projects opt in from their csproj")
    }

    if (@($targets.SelectNodes("/Project/ItemGroup/ProjectReference")).Count -ne 0) {
        $errors.Add("$targetsPath must not add Host ProjectReference items")
    }

    if (@($targets.SelectNodes("/Project/Target[@Name='ValidateUraPluginHostProject']")).Count -ne 0) {
        $errors.Add("$targetsPath must not validate a Host source checkout")
    }

    foreach ($targetName in @("GenerateUraPluginManifest", "PackageUraPlugin", "DeployUraPluginToLocalAppData")) {
        if (@($targets.SelectNodes("/Project/Target[@Name='$targetName']")).Count -eq 0) {
            $errors.Add("$targetsPath must define target $targetName")
        }
    }

    if (@($targets.SelectNodes("/Project/UsingTask[@TaskName='WriteUraPluginManifestTask']")).Count -eq 0) {
        $errors.Add("$targetsPath must define WriteUraPluginManifestTask")
    }
}

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { [Console]::Error.WriteLine($_) }
    exit 1
}

Write-Host "URA plugin build targets are ready for buildTransitive packaging."

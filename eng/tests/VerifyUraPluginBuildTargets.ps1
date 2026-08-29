$ErrorActionPreference = "Stop"

$engRoot = Split-Path -Parent $PSScriptRoot
$propsPath = Join-Path $engRoot "URA.Plugin.Build.props"
$targetsPath = Join-Path $engRoot "URA.Plugin.Build.targets"
$errors = New-Object System.Collections.Generic.List[string]

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

    $hostPath = @($targets.SelectNodes("/Project/PropertyGroup/UraHostProjectPath") |
        ForEach-Object { $_.InnerText.Trim() })
    if ($hostPath.Count -eq 0) {
        $errors.Add("$targetsPath must define UraHostProjectPath")
    }

    $expectedDefaultCondition = "'`$(IsUraPlugin)' == ''"
    $defaultPluginValues = @($targets.SelectNodes("/Project/PropertyGroup/IsUraPlugin") |
        Where-Object { $_.Condition -eq $expectedDefaultCondition } |
        ForEach-Object { $_.InnerText.Trim() })
    if ($defaultPluginValues -notcontains "false") {
        $errors.Add("$targetsPath must default IsUraPlugin to false; plugin projects opt in from their csproj")
    }

    $hostReferences = @($targets.SelectNodes("/Project/ItemGroup/ProjectReference[@Include='`$(UraHostProjectPath)']"))
    if ($hostReferences.Count -eq 0) {
        $errors.Add("$targetsPath must add a ProjectReference to `$`(UraHostProjectPath)")
    }

    $hostValidation = @($targets.SelectNodes("/Project/Target[@Name='ValidateUraPluginHostProject']/Error"))
    if ($hostValidation.Count -ne 1) {
        $errors.Add("$targetsPath must fail clearly when the pinned Host submodule is unavailable")
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

Write-Host "URA plugin build targets are provided by eng/URA.Plugin.Build.targets."

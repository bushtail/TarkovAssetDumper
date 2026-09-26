[CmdletBinding()]
param(
    [string]$PackageRoot = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Package([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

try {
    $root = (Resolve-Path -LiteralPath $PackageRoot).Path.TrimEnd('\', '/')
    $manifest = Get-Content -LiteralPath (Join-Path $root 'package.json') -Raw | ConvertFrom-Json
    Assert-Package ($manifest.name -match '^[a-z0-9]+(?:\.[a-z0-9][a-z0-9-]*)+$') 'Invalid UPM package identifier.'
    Assert-Package ($manifest.version -match '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$') 'Invalid package version.'
    Assert-Package ($manifest.unity -match '^\d+\.\d+$') 'Missing or invalid Unity version.'
    Assert-Package (-not [string]::IsNullOrWhiteSpace($manifest.displayName)) 'Missing package display name.'
    foreach ($dependency in $manifest.dependencies.PSObject.Properties) {
        Assert-Package ($dependency.Value -match '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') "Dependency '$($dependency.Name)' must use a registry version; install Git prerequisites in the project manifest."
    }
    foreach ($dependency in @('com.unity.scriptablebuildpipeline', 'com.unity.nuget.newtonsoft-json')) {
        Assert-Package ($dependency -in @($manifest.dependencies.PSObject.Properties.Name)) "Missing required dependency: $dependency"
    }

    foreach ($required in @('README.md', 'CHANGELOG.md', 'Third Party Notices.md', 'Documentation~/AssetBundleDumper.md', 'Documentation~/Maintaining.md')) {
        Assert-Package (Test-Path -LiteralPath (Join-Path $root $required) -PathType Leaf) "Missing documentation: $required"
    }
    Assert-Package ((Get-Content -LiteralPath (Join-Path $root 'CHANGELOG.md') -Raw) -match ('(?m)^## ' + [regex]::Escape($manifest.version) + '\s*$')) 'Current version has no changelog heading.'

    $editor = Join-Path $root 'Editor'
    Assert-Package (Test-Path -LiteralPath $editor -PathType Container) 'Missing Editor directory.'
    $definitions = @(Get-ChildItem -LiteralPath $editor -Recurse -Filter '*.asmdef' -File)
    Assert-Package ($definitions.Count -eq 1) 'Expected one root editor assembly definition.'
    Assert-Package ($definitions[0].DirectoryName -eq $editor) 'The assembly definition must cover the entire Editor folder.'
    $assembly = Get-Content -LiteralPath $definitions[0].FullName -Raw | ConvertFrom-Json
    Assert-Package ($assembly.name -eq 'bushtail.AssetBundleDumper.Editor') 'Unexpected dumper assembly name.'
    Assert-Package ($assembly.rootNamespace -eq 'Editor.bushtail') 'Assembly root namespace does not match the dumper.'
    Assert-Package (@($assembly.includePlatforms).Count -eq 1 -and $assembly.includePlatforms[0] -eq 'Editor') 'Dumper assembly must be Editor-only.'
    Assert-Package (-not $assembly.overrideReferences -and -not $assembly.noEngineReferences) 'Dumper requires engine and automatic plugin references.'
    foreach ($reference in @('Unity.AssetBundleBrowser.Editor', 'Unity.ScriptableBuildPipeline', 'Unity.ScriptableBuildPipeline.Editor')) {
        Assert-Package ($reference -in @($assembly.references)) "Missing assembly reference: $reference"
    }

    $items = @(Get-Item -LiteralPath $editor) + @(Get-ChildItem -LiteralPath $editor -Recurse -Force)
    $allowed = @('.cs', '.asmdef', '.dll', '.txt', '.meta')
    foreach ($item in $items) {
        Assert-Package (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) "Package content must be stored directly, not through a link: $($item.FullName)"
        if (-not $item.PSIsContainer) {
            Assert-Package ($item.Extension.ToLowerInvariant() -in $allowed) "Unexpected editor payload: $($item.FullName)"
            if ($item.Extension -eq '.meta') { continue }
        }
        Assert-Package (Test-Path -LiteralPath ($item.FullName + '.meta') -PathType Leaf) "Missing Unity metadata: $($item.FullName).meta"
    }
    Assert-Package (@($items | Where-Object { -not $_.PSIsContainer -and $_.Extension -eq '.cs' }).Count -eq 9) 'Expected the nine dumper scripts.'

    $metaFiles = @(Get-ChildItem -LiteralPath $editor -Recurse -Filter '*.meta' -File) + @(Get-ChildItem -LiteralPath $root -Filter '*.meta' -File)
    $guidOwners = @{}
    foreach ($meta in $metaFiles) {
        $target = $meta.FullName.Substring(0, $meta.FullName.Length - 5)
        Assert-Package (Test-Path -LiteralPath $target) "Orphan metadata: $($meta.FullName)"
        $text = Get-Content -LiteralPath $meta.FullName -Raw
        $match = [regex]::Match($text, '(?m)^guid: ([0-9a-fA-F]{32})\s*$')
        Assert-Package ($match.Success) "Invalid metadata GUID: $($meta.FullName)"
        $guid = $match.Groups[1].Value.ToLowerInvariant()
        Assert-Package (-not $guidOwners.ContainsKey($guid)) "Duplicate metadata GUID $guid in $($meta.FullName)"
        $guidOwners[$guid] = $meta.FullName
        Assert-Package ($text -notmatch '(?m)^[ \t]*assetBundle(?:Name|Variant):[ \t]*\S') "Editor package asset has a bundle label: $($meta.FullName)"
        if (Test-Path -LiteralPath $target -PathType Container) {
            Assert-Package ($text -match '(?m)^folderAsset: yes\s*$') "Folder metadata has no folder marker: $($meta.FullName)"
        }
    }

    $dependencyRoot = Join-Path $editor 'bushtail/Dependencies'
    $binary = Join-Path $dependencyRoot 'BundleDumper.AssetsTools.NET.dll'
    Assert-Package (Test-Path -LiteralPath $binary -PathType Leaf) 'Missing isolated AssetsTools.NET DLL.'
    $plugin = Get-Content -LiteralPath ($binary + '.meta') -Raw
    Assert-Package ($plugin -match '(?s)Any:[ \t]*\r?\n.*?enabled: 0') 'DLL must not be enabled on every platform.'
    Assert-Package ($plugin -match '(?s)Editor: Editor\r?\n.*?enabled: 1') 'DLL must be enabled for the Editor.'
    Assert-Package ($plugin -match '(?m)^  isExplicitlyReferenced: 0\s*$') 'DLL must allow automatic references.'
    $license = Join-Path $dependencyRoot 'AssetsTools.NET.LICENSE.txt'
    Assert-Package ((Get-Content -LiteralPath $license -Raw) -match 'MIT License') 'Bundled AssetsTools.NET license is missing.'
    Assert-Package (Test-Path -LiteralPath (Join-Path $dependencyRoot 'README.txt') -PathType Leaf) 'Missing dependency provenance.'

    if (Test-Path -LiteralPath (Join-Path $root '.git')) {
        $files = @(& git -C $root -c core.quotepath=false ls-files --cached --others --exclude-standard)
        Assert-Package ($LASTEXITCODE -eq 0) 'Unable to inspect Git distribution files.'
    } else {
        $files = @(Get-ChildItem -LiteralPath $root -Recurse -File -Force | ForEach-Object { $_.FullName.Substring($root.Length + 1).Replace('\', '/') })
    }
    foreach ($file in $files) {
        Assert-Package ($file -notmatch '(?i)(?:^|/)(?:Assets|Library|Temp|Logs|ProjectSettings|UserSettings|ValidationProject)(?:/|$)') "Generated project/game content in distribution: $file"
        Assert-Package ($file -notmatch '(?i)\.(?:prefab|unity|asset|mat|shader|cubemap|physicmaterial|fbx|obj|wav|ogg|bundle|unitypackage|resS|resource|csproj|sln|pdb|log)$') "Unexpected game/build payload in distribution: $file"
    }

    Write-Output "PACKAGE_VALIDATION_PASSED: $($manifest.name) $($manifest.version); 9 editor scripts; $($guidOwners.Count) unique GUIDs; $($files.Count) distribution files."
    Write-Output 'Package metadata/content checks passed. Validate compilation and affected extraction/build behavior in a compatible SDK separately.'
    exit 0
} catch {
    Write-Error $_
    exit 1
}

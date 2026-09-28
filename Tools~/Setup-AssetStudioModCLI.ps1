param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'bushtail\AssetStudioModCLI-weapon-dumper')
)

$ErrorActionPreference = 'Stop'
$commit = '6b66ec74674f61d7b331d0766fc38511e9c885f3'
$release = 'v0.19.0'
$destinationPath = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $destinationPath) {
    $marker = Join-Path $destinationPath 'bushtail-patched.txt'
    if ((Test-Path -LiteralPath $marker) -and ((Get-Content -LiteralPath $marker -Raw) -match $commit)) {
        Write-Output "AssetStudioModCLI is ready: $(Join-Path $destinationPath 'AssetStudioModCLI.exe')"
        return
    }
    throw "Destination already exists and is not this patched build: $destinationPath"
}

$stage = Join-Path ([IO.Path]::GetFullPath($env:TEMP)) ('bushtail-assetstudio-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    $binaryArchive = Join-Path $stage 'release.zip'
    $sourceArchive = Join-Path $stage 'source.zip'
    Invoke-WebRequest -Uri "https://github.com/aelurum/AssetStudioMod/releases/download/$release/AssetStudioModCLI_net9_win64.zip" -OutFile $binaryArchive
    Invoke-WebRequest -Uri "https://github.com/aelurum/AssetStudioMod/archive/$commit.zip" -OutFile $sourceArchive
    Expand-Archive -LiteralPath $binaryArchive -DestinationPath (Join-Path $stage 'release')
    Expand-Archive -LiteralPath $sourceArchive -DestinationPath (Join-Path $stage 'source')

    $binary = Get-ChildItem -LiteralPath (Join-Path $stage 'release') -Recurse -Filter 'AssetStudioModCLI.exe' -File | Select-Object -First 1
    $sourceRoot = Get-ChildItem -LiteralPath (Join-Path $stage 'source') -Directory | Select-Object -First 1
    if (-not $binary -or -not $sourceRoot) { throw 'AssetStudioMod archives have an unexpected layout.' }

    $optionsPath = Join-Path $sourceRoot.FullName 'AssetStudioCLI\Options\CLIOptions.cs'
    $options = Get-Content -LiteralPath $optionsPath -Raw
    $pattern = 'ClassIDType\.Animator,\s*ClassIDType\.Mesh,'
    if ([regex]::Matches($options, $pattern).Count -ne 1) { throw 'Could not locate the Animator export type list in AssetStudioModCLI.' }
    $options = [regex]::Replace($options, $pattern, "ClassIDType.Animator,`r`n                            ClassIDType.AnimationClip,`r`n                            ClassIDType.Mesh,")
    Set-Content -LiteralPath $optionsPath -Value $options -NoNewline

    foreach ($name in @('AssetStudioFBXNative', 'Texture2DDecoderNative')) {
        $nativeTarget = Join-Path $sourceRoot.FullName "$name\bin\x64\Release"
        New-Item -ItemType Directory -Force -Path $nativeTarget | Out-Null
        Copy-Item -LiteralPath (Join-Path $binary.DirectoryName "$name.dll") -Destination (Join-Path $nativeTarget "$name.dll")
    }

    $project = Join-Path $sourceRoot.FullName 'AssetStudioCLI\AssetStudioCLI.csproj'
    & dotnet build $project -f net9.0 -c Release -r win-x64 "-p:SolutionDir=$($sourceRoot.FullName)\"
    if ($LASTEXITCODE -ne 0) { throw 'AssetStudioModCLI managed build failed.' }

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destinationPath) | Out-Null
    Copy-Item -LiteralPath $binary.DirectoryName -Destination $destinationPath -Recurse
    foreach ($name in @('AssetStudio', 'AssetStudio.PInvoke', 'AssetStudioFBXWrapper', 'AssetStudioUtility')) {
        $built = Join-Path $sourceRoot.FullName "$name\bin\Release\net9.0\$name.dll"
        Copy-Item -LiteralPath $built -Destination (Join-Path $destinationPath "$name.dll") -Force
    }
    $builtCli = Join-Path $sourceRoot.FullName 'AssetStudioCLI\bin\Release\net9.0\win-x64\AssetStudioModCLI.dll'
    Copy-Item -LiteralPath $builtCli -Destination (Join-Path $destinationPath 'AssetStudioModCLI.dll') -Force
    Copy-Item -LiteralPath (Join-Path $sourceRoot.FullName 'LICENSE') -Destination (Join-Path $destinationPath 'LICENSE-AssetStudioMod.txt')
    Set-Content -LiteralPath (Join-Path $destinationPath 'bushtail-patched.txt') -Value "AssetStudioMod $release source $commit; Animator mode includes AnimationClip for --fbx-animation all."
    & (Join-Path $destinationPath 'AssetStudioModCLI.exe') --help | Select-Object -First 1
    if ($LASTEXITCODE -ne 0) { throw 'Patched AssetStudioModCLI did not start.' }
    Write-Output "AssetStudioModCLI is ready: $(Join-Path $destinationPath 'AssetStudioModCLI.exe')"
}
finally {
    $tempRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\'
    $resolvedStage = [IO.Path]::GetFullPath($stage)
    if ($resolvedStage.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedStage)) {
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}

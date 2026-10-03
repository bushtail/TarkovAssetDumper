param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'bushtail\AssetStudioModCLI-weapon-dumper')
)

$ErrorActionPreference = 'Stop'
$release = 'v0.19.0'
$licenseCommit = '6b66ec74674f61d7b331d0766fc38511e9c885f3'
$destinationPath = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $destinationPath) {
    $executable = Join-Path $destinationPath 'AssetStudioModCLI.exe'
    if (Test-Path -LiteralPath $executable -PathType Leaf) {
        Write-Output "AssetStudioModCLI is ready for audio recovery: $executable"
        return
    }
    throw "Destination already exists without AssetStudioModCLI.exe: $destinationPath"
}

$stage = Join-Path ([IO.Path]::GetFullPath($env:TEMP)) ('bushtail-assetstudio-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    $archive = Join-Path $stage 'release.zip'
    Invoke-WebRequest -Uri "https://github.com/aelurum/AssetStudioMod/releases/download/$release/AssetStudioModCLI_net9_win64.zip" -OutFile $archive
    Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $stage 'release')
    $binary = Get-ChildItem -LiteralPath (Join-Path $stage 'release') -Recurse -Filter 'AssetStudioModCLI.exe' -File | Select-Object -First 1
    if (-not $binary) { throw 'AssetStudioModCLI archive has an unexpected layout.' }

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destinationPath) | Out-Null
    Copy-Item -LiteralPath $binary.DirectoryName -Destination $destinationPath -Recurse
    Invoke-WebRequest -Uri "https://raw.githubusercontent.com/aelurum/AssetStudioMod/$licenseCommit/LICENSE" -OutFile (Join-Path $destinationPath 'LICENSE-AssetStudioMod.txt')
    & (Join-Path $destinationPath 'AssetStudioModCLI.exe') --help | Select-Object -First 1
    if ($LASTEXITCODE -ne 0) { throw 'AssetStudioModCLI did not start.' }
    Write-Output "AssetStudioModCLI is ready for audio recovery: $(Join-Path $destinationPath 'AssetStudioModCLI.exe')"
}
finally {
    $tempRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\'
    $resolvedStage = [IO.Path]::GetFullPath($stage)
    if ($resolvedStage.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedStage)) {
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}

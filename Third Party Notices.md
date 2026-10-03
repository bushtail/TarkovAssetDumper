# Third-party notices

## AssetsTools.NET

- Upstream project: https://github.com/nesrak1/AssetsTools.NET
- Upstream version: `3.0.5`
- Author/copyright: `Copyright (c) 2020 nesrak1`
- License: MIT
- Included binary: `Editor/bushtail/Dependencies/BundleDumper.AssetsTools.NET.dll`
- Included license: [AssetsTools.NET.LICENSE.txt](Editor/bushtail/Dependencies/AssetsTools.NET.LICENSE.txt)

The distributed assembly name and implementation namespaces are prefixed for BundleDumper isolation. The existing dependency provenance is recorded in [Dependencies/README.txt](Editor/bushtail/Dependencies/README.txt). Keep the isolated DLL and its importer metadata together when updating the package.

## Separately installed dependencies

AssetRipper, AssetStudioModCLI, AssetBundleBrowser impostor support, Scriptable Build Pipeline, Newtonsoft.Json, and the destination SDK are supplied separately. The optional setup script downloads the AssetStudioModCLI release and its upstream license for local audio recovery. Their respective licenses and notices remain applicable to their own distributions. The dumper repository does not redistribute the SDK or original game assets.

## Original dumper code

No license has been selected for the original dumper code. The bundled library's MIT license applies to that library; it does not assign a license to the rest of this repository.

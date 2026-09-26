# Tarkov Asset Dumper

Unity editor tools for exporting AssetBundles into an existing compatible Tarkov/WTT SDK.

**Package:** `ca.bushtail.tarkov-asset-bundle-dumper`  
**Version:** `1.0.0`  
**Tested editor:** Unity `2022.3.43f1` on Windows

Yeah, this was written by AI. Only difference is, I test my shit.

The dumper extracts serialized assets with AssetRipper, matches scripts and shaders to the SDK, trims referenced dependencies, sorts them into Unity type folders, and prepares original shared references for an impostor-aware build.

## Requirements

| Requirement | Purpose |
| --- | --- |
| A compatible, compiling Tarkov/WTT SDK | Provides the game component definitions and SDK assets used during export. |
| AssetRipper `1.3.14` | Provides the extraction service. Install it separately and select its executable or configure `ASSETRIPPER_PATH`. |
| Matching original game/dependency bundles | Provide referenced objects and original CAB/PathID identities. |
| AssetBundleBrowser impostor package | Provides original-reference-aware building and canonical asset metadata. |
| Scriptable Build Pipeline `2.1.5` | Declared in this package's manifest. |
| Newtonsoft.Json `3.2.1` | Declared in this package's manifest. |

The package contains the dumper's editor code and its isolated AssetsTools.NET DLL. It does not include the SDK, AssetRipper, game assets, or extracted/built bundles.

## Install

### 1. Install the impostor dependency

If the SDK already includes AssetBundleBrowser impostor support, keep its compatible installed version. Otherwise, open **Window → Package Manager → + → Add package from git URL** and enter:

```text
https://github.com/bmpq/AssetBundles-Browser-Imposter.git#455ac661999cc199f28eb5b1296b313c1bcd10ee
```

That is the revision used by the inspected development SDK. Unity 2022.3 supports Git dependencies in the **project** manifest, so this prerequisite is installed separately from the dumper's registry dependencies.

### 2. Install the dumper

For development or a downloaded repository, choose **Package Manager → + → Add package from disk**, then select this repository's root `package.json`.

To install version `1.0.0`, use **Add package from git URL**:

```text
https://github.com/bushtail/TarkovAssetDumper.git#v1.0.0
```

To follow development on the main branch, use `https://github.com/bushtail/TarkovAssetDumper.git#main` instead. Unity installs the package under `Packages`; it does not copy it into `Assets/Editor`.

If the SDK currently has a manually copied dumper, remove those duplicate dumper scripts before installing the package. Keep the SDK's unrelated editor tools and dependencies. Two copies defining `Editor.bushtail.AssetBundleDumper` can conflict.

For a downloaded `.tgz` release, install the impostor prerequisite first, then choose **Package Manager → + → Add package from tarball** and select the file.

### Project manifest alternative

Merge these entries into the SDK's existing `Packages/manifest.json` → `dependencies` object:

```json
"com.bmpq.assetbundlebrowser-imposter": "https://github.com/bmpq/AssetBundles-Browser-Imposter.git#455ac661999cc199f28eb5b1296b313c1bcd10ee",
"ca.bushtail.tarkov-asset-bundle-dumper": "https://github.com/bushtail/TarkovAssetDumper.git#v1.0.0"
```

Retain the project's other entries. This is a JSON fragment for that object, not a complete project manifest.

## Quick start

1. Let the SDK compile after installation.
2. Open **Custom Windows → bushtail → Dump AssetBundle to SDK Format**.
3. Add the source bundle files.
4. Select the matching dependency folder, or leave it blank with a valid `SptRoot`.
5. Select the AssetRipper executable.
6. Enable **Assign one AssetBundle label to exported assets** to group editable source assets and retained dependencies.
7. Extract and review the report. Successful exports appear under `Assets/BundleDumps`.
8. Review/edit the assets, then build using the SDK's impostor-aware build process.

The checkbox assigns labels. It does not build a bundle, generate a mod manifest, or install a mod.

## Dependency rules

- The whole selected source bundle is retained; dependencies are filtered by referenced objects.
- Retained dependency assets are sorted by their Unity main type, such as `AudioClip`, `Mesh`, `Texture2D`, and `Material`.
- Normal extraction preserves original identities for cubemaps, shaders, and physics materials. They use `cubemaps`, `shaders`, and `assets/commonassets/physics/physicsmaterials.bundle` respectively.
- Ordinary dependency texture impostors are optional. The single-label option disables that texture option.
- **Ignore dependencies** also skips extraction-time impostor preparation. Leave it off for the normal weapon workflow; a successful import in this mode does not prove the required shared identities were configured.
- Empty and metadata-only leftover folders are pruned; populated folders and their metadata are preserved.

## Documentation and maintenance

- [Usage, options, builds, and troubleshooting](Documentation~/AssetBundleDumper.md)
- [Development and release workflow](Documentation~/Maintaining.md)
- [Changelog](CHANGELOG.md)
- [Third-party notices](Third%20Party%20Notices.md)

Run the package checks from the repository root:

```powershell
& './Tools~/Validate-Package.ps1'
```

The GitHub workflow runs the same metadata/content checks on pushes and pull requests. Unity compilation, extraction, and game behavior require separate validation.

## License

No license has been selected for the original dumper code. AssetsTools.NET is distributed under its existing MIT license, retained in `Editor/bushtail/Dependencies/AssetsTools.NET.LICENSE.txt`.

## Unity references

- [Package layout](https://docs.unity3d.com/2022.3/Documentation/Manual/cus-layout.html)
- [Package manifests](https://docs.unity3d.com/2022.3/Documentation/Manual/upm-manifestPkg.html)
- [Git dependencies](https://docs.unity3d.com/2022.3/Documentation/Manual/upm-git.html)
- [Install from a local folder](https://docs.unity3d.com/2022.3/Documentation/Manual/upm-ui-local.html)

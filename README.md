# Tarkov Asset Dumper

Unity editor tools for exporting AssetBundles into an existing compatible Tarkov/WTT SDK.

**Package:** `ca.bushtail.tarkov-asset-bundle-dumper`  
**Version:** `1.1.0`\
**Tested editor:** Unity `2022.3.43f1` on Windows

Yeah, this was written by AI. Only difference is, I test my shit.

The dumper extracts serialized assets with AssetRipper, matches scripts and shaders to the SDK, trims referenced dependencies, sorts them into Unity type folders, and prepares original shared references for an impostor-aware build.

## Requirements

| Requirement | Purpose |
| --- | --- |
| A compatible, compiling Tarkov/WTT SDK | Provides the game component definitions and SDK assets used during export. |
| AssetRipper `1.3.14` | Provides the extraction service. Install it separately and select its executable or configure `ASSETRIPPER_PATH`. |
| AssetStudioModCLI `0.19.0` (recommended for weapons) | Extracts original audio and an Animator FBX with editable curves. Run `Tools~/Setup-AssetStudioModCLI.ps1` once; the window detects its local installation. |
| Matching original game/dependency bundles | Provide referenced objects and original CAB/PathID identities. |
| AssetBundleBrowser impostor package | Provides original-reference-aware building and canonical asset metadata. |
| Scriptable Build Pipeline `2.1.5` | Declared in this package's manifest. |
| Newtonsoft.Json `3.2.1` | Declared in this package's manifest. |
| Unity FBX Exporter `4.2.1` | Declared in this package's manifest; exports editable weapon models and clips. |

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

To install the current development build, use **Add package from git URL**:

```text
https://github.com/bushtail/TarkovAssetDumper.git#main
```

The latest fixed tag is `v1.1.0`; it predates the automated AssetStudio and edited-FBX workflow. Unity installs the package under `Packages`; it does not copy it into `Assets/Editor`.

If the SDK currently has a manually copied dumper, remove those duplicate dumper scripts before installing the package. Keep the SDK's unrelated editor tools and dependencies. Two copies defining `Editor.bushtail.AssetBundleDumper` can conflict.

For a downloaded `.tgz` release, install the impostor prerequisite first, then choose **Package Manager → + → Add package from tarball** and select the file.

### Project manifest alternative

Merge these entries into the SDK's existing `Packages/manifest.json` → `dependencies` object:

```json
"com.bmpq.assetbundlebrowser-imposter": "https://github.com/bmpq/AssetBundles-Browser-Imposter.git#455ac661999cc199f28eb5b1296b313c1bcd10ee",
"ca.bushtail.tarkov-asset-bundle-dumper": "https://github.com/bushtail/TarkovAssetDumper.git#main"
```

Retain the project's other entries. This is a JSON fragment for that object, not a complete project manifest.

## Quick start

1. Let the SDK compile after installation.
2. Open **Custom Windows → bushtail → Dump AssetBundle to SDK Format**.
3. Add the source bundle files.
4. Select the matching dependency folder, or leave it blank with a valid `SptRoot`. Select the installed AssetStudioMod CLI executable for editable source animations and recovered audio.
5. Select the AssetRipper executable.
6. Enable **Assign one AssetBundle label to exported assets** to group editable source assets and retained dependencies.
7. Click **Extract**. The name popup asks for a distinct new item name, then exports to `Assets/BundleDumps/<new name>`. Choose **Dump original names** only when you want a normal copy of the source. Review the report.
8. Weapon containers also produce an editable FBX under `Assets/BundleDumperFBX/<dump name>`. With the patched CLI, it contains original animation curves.
9. **Build and verify after extraction** builds the unedited rip under `AssetBundles/Dumps/<dump name>` by default. Review the report and test the weapon in game.
10. The **New Item Guide** opens after a named export. It points to the FBX, prefab, built bundle key, CAB IDs, and each remaining manual task. You can reopen it with **Assets → bushtail → New Item Guide** from a dump folder.
11. After editing the FBX in Blender or Unity, use **Apply edited FBX and rebuild** in the guide, or select the FBX and choose **Assets → bushtail → Apply Edited Weapon FBX and Build**. Compatible mesh and transform animation edits are copied into the original assets, then the bundle is rebuilt.

The single-label checkbox groups build assets under one label. It does not generate a mod manifest or install a mod.

The FBX is an editing copy outside the bundle source tree. The apply command keeps original asset GUIDs, controller links, game components, and animation events. It skips meshes with incompatible bone order and clips whose transform paths do not fit the original skeleton; review its log before using the rebuilt bundle. It backs up affected source assets under `Library/BundleDumper/EditedFbxBackups`.

The weapon workflow also fills missing SoundBank `BlendOptions` from the SDK's `Standart` asset and restores shared left-hand and gesture motions from matching SDK clips. It uses the original SDK GUIDs when available, otherwise a unique clip match. Ambiguous matches are reported for manual review. The guide prompts for Blender edits, Unity pose/rig/audio inspection, mod bundle and item registration, and in-game testing. Naming changes the new dump folder and bundle key; internal bone, clip, and asset address names remain available for matching.

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

To also check for GUID collisions with a development SDK's `Assets` folder:

```powershell
& './Tools~/Validate-Package.ps1' -SdkRoot 'F:\path\to\SDK'
```

The GitHub workflow runs the same metadata/content checks on pushes and pull requests. Unity compilation, extraction, and game behavior require separate validation.

## License

No license has been selected for the original dumper code. AssetsTools.NET is distributed under its existing MIT license, retained in `Editor/bushtail/Dependencies/AssetsTools.NET.LICENSE.txt`.

## Unity references

- [Package layout](https://docs.unity3d.com/2022.3/Documentation/Manual/cus-layout.html)
- [Package manifests](https://docs.unity3d.com/2022.3/Documentation/Manual/upm-manifestPkg.html)
- [Git dependencies](https://docs.unity3d.com/2022.3/Documentation/Manual/upm-git.html)
- [Install from a local folder](https://docs.unity3d.com/2022.3/Documentation/Manual/upm-ui-local.html)

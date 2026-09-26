# AssetBundle Dumper guide

This guide covers the editor package `ca.bushtail.tarkov-asset-bundle-dumper`. The destination SDK, original game bundles, and AssetRipper are separate prerequisites. See the [README](../README.md) for installation.

## Contents

1. [Environment and source paths](#environment-and-source-paths)
2. [Window options](#window-options)
3. [Export pipeline](#export-pipeline)
4. [Dependency filtering and folders](#dependency-filtering-and-folders)
5. [Original shared references](#original-shared-references)
6. [Scripts, shaders, animation, and audio](#scripts-shaders-animation-and-audio)
7. [Labels and bundle building](#labels-and-bundle-building)
8. [Moving or sharing an export](#moving-or-sharing-an-export)
9. [Editor API](#editor-api)
10. [Troubleshooting](#troubleshooting)
11. [Validation and limits](#validation-and-limits)

## Environment and source paths

### AssetRipper

Use AssetRipper `1.3.14`. Select `AssetRipper.GUI.Free.exe` in the window or set `ASSETRIPPER_PATH` to its executable or containing directory. The tool reads process and persisted user environment values and can reuse its saved executable path.

```powershell
[Environment]::SetEnvironmentVariable('ASSETRIPPER_PATH', 'C:\Tools\AssetRipper', 'User')
```

### Original game/dependency bundles

Select a dependency folder containing the matching original bundles, usually:

```text
<game installation>/EscapeFromTarkov_Data/StreamingAssets/Windows
```

The selected source bundle's directory is also searched recursively. An explicit dependency folder overrides the environment-derived default. Avoid competing providers from different installations for the same CAB.

With the dependency field blank, `SptRoot` can identify the installed sources. It points to the **runtime folder**, whose parent is the game installation:

```text
GameInstallation/
├── EscapeFromTarkov.exe
├── UnityPlayer.dll
├── EscapeFromTarkov_Data/StreamingAssets/Windows/
├── BepInEx/
├── MonoBleedingEdge/
└── SPT_Runtime/                     SptRoot
    ├── SPT.Server.exe
    ├── SPT.Launcher.exe
    └── SPT_Data/
```

```powershell
[Environment]::SetEnvironmentVariable('SptRoot', 'C:\SPT\SPT_Runtime', 'User')
```

Process, user, and machine scopes are checked in that order. A configured but invalid installation reports the missing expected entries. When the variable is unset, the resolver can discover a `StreamingAssets/Windows` ancestor above an original input bundle. SDK `AssetBundles` output can provide additional missing rebuilt CABs.

## Window options

Open **Custom Windows → bushtail → Dump AssetBundle to SDK Format**.

| Control | Behavior |
| --- | --- |
| Add Files / drag area | Add one or more bundle inputs. Windows supports Ctrl/Shift selection. Duplicate normalized paths are ignored. |
| Remove / Clear | Remove a queue entry or clear the input queue. |
| Dependency search folder | Locate matching source dependencies. Blank uses environment/ancestor discovery. |
| AssetRipper executable | Locate the separately installed extractor. |
| Assign one AssetBundle label to exported assets | Group eligible source assets and editable retained dependencies under the source bundle's name. |
| Split into per-prefab bundles | When the single-label option is off, label prefabs from their lowercase filenames. |
| Use original dependency textures through impostors | Keep suitable ordinary dependency Texture2D assets as references to originals. |
| Use fallback for unresolved shaders | Use the selected SDK fallback for unresolved material shaders where preparation permits it. |
| Ignore dependencies | Extract the selected source without the dependency walk/filter or extraction-time impostor preparation. |
| Extract | Run the current queue sequentially. Each successful input has a separate output folder. |
| Clear Log | Clear the displayed report when extraction is idle. |

The single-label option disables per-prefab splitting and optional texture impostors. Cubemaps, shaders, and physics materials keep their original groups during normal extraction.

**Ignore dependencies is a restricted workflow.** It also skips `AssetBundleDumpImpostors.Apply` and `EnforceRequired` during extraction. Import validation still runs, but it does not establish that embedded shared objects received original identities. Leave this option off for the usual weapon workflow. The diagnostic builder rechecks required identities before building.

Batch inputs remain separate exports. A failed input is logged and the remaining queue continues. The tool selects successful output folders when the queue finishes. Failed runs can retain staging or partially imported files for diagnosis.

## Export pipeline

1. Resolve source/dependency serialized files by their internal CAB identities.
2. Trace serialized object pointers and stage filtered copies.
3. Start an isolated AssetRipper instance and export a Unity project.
4. Repair the supported unfinished WAV-header pattern.
5. Match exported scripts/shaders to SDK assets, remap references, and assign fresh exported GUIDs.
6. Remove generated script output from the imported payload and prune empty staging folders.
7. Import into `Assets/BundleDumps/<item name>` and validate persistent references.
8. Prune unneeded dependency files when the relevant options permit it; assign labels.
9. Configure original shared identities and reconstruct supported missing animator masks.
10. Sort dependency assets by their Unity main type, prune empty leftovers, validate again, and save build settings.

Original inputs are read; modified copies and extractor output live under `Library/BundleDumper/<run id>`. The isolated extractor executable is under `Library/BundleDumper/Tool`. It runs headlessly through a loopback HTTP service so the tool can apply extraction settings and collect a run-specific log.

## Dependency filtering and folders

### What is minimized

The filter preserves the full selected source bundle and traces its serialized `PPtr` references into dependency bundles. Containers/preload tables are adjusted so unrelated shared objects do not force the whole dependency bundle into the export. Raw `.resS`/`.resource` payloads are retained rather than parsed as serialized assets.

When ordinary texture impostors are disabled and Ignore dependencies is off, a second pass removes dependency files that Unity no longer considers required by the selected source assets. Existing SDK asset reuse can make some exported dependency files unnecessary.

This is not a one-prefab source minimizer. Runtime strings, server item records, and other external configuration can require assets that are absent from the serialized dependency graph.

### Typed output

An export can contain:

```text
Assets/BundleDumps/weapon_example_container/
├── Content/Weapons/example/         selected source layout, where applicable
├── AudioClip/
├── Mesh/
├── Texture2D/
├── Material/
├── AnimationClip/
├── GameObject/
├── TextAsset/
├── AvatarMask/                      generated masks, when required
└── other retained Unity/SDK type folders
```

The type sorter moves dependency assets. Selected-source assets can retain their original layout. A dependency's main Unity type determines its destination: audio clips go to `AudioClip`, meshes to `Mesh`, and so on. An undetected type uses `Other`. Existing folders are reused; filename collisions receive unique paths.

Unity moves preserve `.meta` GUIDs and references. Empty `Dependencies/<bundle>` staging trees, `Shader`, and `PhysicMaterial` folders are removed when they no longer contain assets.

### Empty-folder cleanup

Cleanup runs before import, after sorting/remapping, during diagnostic building, and through the older-dump sorting command. Imported folders are removed through AssetDatabase.

- Metadata alone does not count as folder content.
- The dump root is preserved.
- Populated descendants retain their ancestors.
- Hidden non-metadata files, such as `.keep`, count as content.
- Reparse-point directories are skipped.

For an older labeled dump still containing `Dependencies`, select its root and use **Assets → bushtail → Sort Existing Dump Dependencies**.

## Original shared references

An impostor is an editable stand-in carrying the original object's CAB and PathID. The impostor-aware build emits references to that original object.

| Shared type | Original bundle key |
| --- | --- |
| Cubemap | `cubemaps` |
| Shader | `shaders` |
| PhysicMaterial | `assets/commonassets/physics/physicsmaterials.bundle` |

Normal extraction applies this policy to selected-source shared objects and dependency exports. Original container addresses and native object names are inspected; missing or ambiguous identities fail rather than receive guessed IDs. Compatible canonical proxies can be reused after reference remapping.

Canonical importer metadata uses `imposter.canonicalCabID` and `imposter.canonicalPathID`. Preserve both, the asset GUID, and the original bundle group when editing a proxy. Optional ordinary texture impostors are a separate setting and apply to suitable dependency textures, not selected-source textures.

Ignore dependencies skips this preparation during extraction. Review that exception before using it for an item with embedded shared objects.

## Scripts, shaders, animation, and audio

### SDK script/shader matching

Generated script output is used to identify unique matching compiled SDK MonoScripts, then excluded from the imported payload. Missing or ambiguous script names are rejected. The tool supplies editable serialized data, not game component implementations.

Existing SDK shaders are matched by name; labeled previous dumps are excluded as authoritative shader candidates. The fallback setting cannot establish original rendering fidelity or fabricate a missing original CAB/PathID. Preparation checks external GUID resolution, followed by Unity import checks for missing prefab scripts and unresolved/nonpersistent object references.

### Animator masks

Original controllers can store bone masks inline, while exported layers have null AvatarMask references. Losing those filters can cause catch, hammer, or malfunction states to affect unrelated weapon/hand bones.

`AssetBundleAnimatorMaskRepair.Restore` reconstructs supported missing masks from original controller and avatar data into `AvatarMask`. Existing masks are preserved. Automatic repair supports the inspected full humanoid body-mask pattern and binary transform weights; restricted body masks, fractional weights, mismatched layers, and ambiguous sources can fail. Review omitted bone hashes reported in the log.

### Audio

`AssetBundleAudioRepair` repairs one known WAV-header defect: valid sample payloads with unfinished zero RIFF/data lengths. It preserves sample bytes and importer metadata. It does not shorten sound clips, change pitch, or recalculate SoundBank cached timing.

### Firearm changes

The dumper does not convert firing behavior automatically. An automatic conversion requires coordinated server fire modes/rate, looping FIRE clips, controller/state speeds, animation-cache values, event identities, layer masks, and automatic body/tail/suppressed audio banks.

When a full clip represents one shot cycle and other multipliers are one:

```text
shot interval = 60 / RPM
state speed = clip duration × RPM / 60
```

A 1-second clip at 1,000 RPM has a 0.06-second cycle and state speed about 16.666667. This formula does not update binary controller caches or sound configuration. Test trigger release, reload, and empty-magazine behavior as well as sustained firing.

## Labels and bundle building

The single-label checkbox prepares grouping for the SDK's usual impostor-aware build. It retains the source bundle name; it does not invent a custom mod key or combine an entire batch into one file.

For a custom item that should coexist with the original, coordinate the bundle name, internal CAB, preserved asset load addresses, item prefab path, and mod manifest key. Renaming a built file alone does not change its internal CAB.

### Diagnostic builder

`AssetBundleDumpBuilder.Build(root, output)` uses cached original addresses, rechecks required shared identities, restores supported missing masks, validates the import, builds explicit groups, and checks serialized references against local and recorded original objects. Temporary original-impostor group outputs are removed after verification. Failed verification renames the custom output with a `.failed-<id>` suffix.

`BuildStandalone` is a stricter diagnostic path. It attempts to exclude non-built-in external references and can reject required original impostors in a weapon dump. Use the original-reference-aware workflow for weapons that retain shared game objects.

Neither method creates `bundles.json`, registers server items, creates trader offers/presets, or installs a mod.

### Mod manifest

Use the manifest shape required by the mod's loader. For a loader using this wrapper, a custom bundle with all three shared groups can use:

```json
{
  "manifest": [
    {
      "key": "my_weapon.bundle",
      "dependencyKeys": [
        "cubemaps",
        "shaders",
        "assets/commonassets/physics/physicsmaterials.bundle"
      ]
    }
  ]
}
```

List the dependencies actually present in the built bundle. Optional external textures or other intentional groups require their original keys too. A loader-specific CAB manifest must map the custom bundle's actual built CAB, not another weapon's or a shared bundle's CAB.

## Moving or sharing an export

Build settings are stored outside the assets under:

```text
Library/BundleDumper/BuildSettings/<dump-folder-guid>.json
```

They record `BundleName`, original `Sources`, dependency roots/GUIDs, and original GUID-to-load-address mappings. Moving a dump through Unity's Project window preserves its folder GUID and cache lookup. Preserve `.meta` files when transferring assets outside Unity.

Clearing `Library` removes the cache. A Unity package containing editable assets does not automatically include those Library settings or original source bundles. Re-extract from compatible originals when metadata is lost; ensure source paths remain available for diagnostic rebuilding.

## Editor API

The namespace is `Editor.bushtail`. Run imports and asset edits on Unity's editor thread. The window handles reload locking around its asynchronous extraction queue; callers should provide equivalent lifecycle handling.

```csharp
var root = await Editor.bushtail.AssetBundleDumper.DumpAsync(
    input: originalBundlePath,
    executable: assetRipperExecutable,
    parent: "Assets/BundleDumps",
    splitPrefabs: false,
    fallback: "p0/Reflective/Bumped Specular SMap",
    log: report,
    dependencySearchFolder: originalBundleFolder,
    useDependencyImpostors: false,
    ignoreDependencies: false,
    singleBundleLabels: true);

var builtFile = Editor.bushtail.AssetBundleDumpBuilder.Build(root);
```

The snippet belongs inside an asynchronous editor method with initialized paths/report, error handling, and reload locking in `try/finally`. It is not an independent script.

| Entry point | Purpose |
| --- | --- |
| `AssetBundleDumper.DumpAsync` | Export one input and return its root. |
| `AssetBundleBatch.DumpAsync` | Process a normalized queue with per-input results. |
| `AssetBundleDumpBuilder.Build` | Build with original shared references and pointer checks. |
| `AssetBundleDumpBuilder.FindDumpRoot` | Find a recorded dump from a selection. |
| `AssetBundleExportFolders.PruneImportedEmptyFolders` | Prune imported empty children while preserving the root. |
| `AssetBundleAnimatorMaskRepair.Restore` | Reconstruct supported original masks. |
| `AssetBundleAudioRepair.RepairFile` | Repair the supported unfinished WAV header. |

## Troubleshooting

| Symptom | Check |
| --- | --- |
| No menu or package compile errors | Installed impostor/build dependencies, Editor assembly references, and duplicate manual dumper copies. |
| Package download fails | Repository URL, existing tag, Git availability, and access to a private repository. |
| No unique SDK script | Compatible compiled SDK definitions and ambiguous short names. |
| Missing CAB or PathID | Matching source installation and exact dependency provider, including competing sibling bundles. |
| AssetRipper fails | Executable/version and the run's `AssetRipper.log`. |
| Invalid SptRoot | Runtime folder and validated parent game layout. |
| Dependencies remain in old folders | Select the older labeled root and run the sorting command; new exports sort retained dependency files automatically. |
| Empty Shader/PhysicMaterial folders | Normal extraction, sorting, or diagnostic building prunes metadata-only leftovers. |
| Original identity preparation fails | Required originals, compatible providers, unique native identity, and multi-subasset limitations. |
| Missing animator mask / wrong empty pose | Original controllers, bone tables, mask restrictions, cache/events, and runtime layers. |
| Long or incorrect firing audio | Playback mode, automatic loops/tails, cached bank durations, variants, and server/controller timing. |
| Lost build settings | Folder metadata GUID, Library cache, recorded source paths; re-extract if lost. |
| Built weapon fails in game | Actual bundle addresses/CAB, loader dependency keys, item registration, compatible parts, and server/client logs. |

Useful files include the window report, `Library/BundleDumper/<run id>/AssetRipper.log`, `remap-report.txt`, staged `Inputs`/`Export` output, cached build settings, the built bundle audit, and mod loader logs. Early failures may not produce a completion report.

## Validation and limits

Package installation/compilation checks establish that the package imports and its references resolve. They do not establish export fidelity or gameplay behavior.

For a changed export/build pipeline, check a real firearm container, typed dependency destinations, original shared identities, import references, retained source addresses, and built serialized pointers. Test rendering, audio, firing release, reload, and empty/catch behavior in game.

Current limits include full-source retention, dependencies expressed only through external configuration/runtime strings, compatible SDK requirements, shader fallback fidelity, supported mask patterns, and the specific WAV repair scope. Intentionally null or valid-but-wrong gameplay configuration can pass reference validation.

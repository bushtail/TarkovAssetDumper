# Changelog

## 1.1.0

- Export an editable weapon FBX after a container dump, using the referenced weapon model prefab and original Animator Controller clips.
- Place FBX editing copies under `Assets/BundleDumperFBX/<dump name>` outside the labeled bundle source tree.
- Add Unity FBX Exporter 4.2.1 as a registry dependency and a context menu command to re-export an existing dump.

## 1.0.1

- Give the package's Editor folder its own GUID to prevent a conflict with the SDK's Assets/Editor folder.
- Add optional SDK asset GUID collision checks to the package validator.

## 1.0.0

Initial package prepared for distribution.

- Package identifier: `ca.bushtail.tarkov-asset-bundle-dumper`.
- Editor-only assembly for the nine asset bundle dumper scripts.
- AssetRipper extraction with batch input handling and dependency filtering.
- SDK script/shader matching, optional texture impostors, and required original shared identities during normal extraction.
- Typed dependency folders, empty-folder cleanup, supported WAV-header repair, and animator-mask reconstruction.
- Programmatic diagnostic building with original addresses and reference checks.
- Installation, usage, and maintenance documentation.
- Package metadata/content validation and GitHub validation workflow.

The package requires a compatible SDK, original sources, AssetRipper, and the separately installed impostor dependency. No original game assets or complete SDK are included.

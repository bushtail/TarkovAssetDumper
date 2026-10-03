# Changelog

## Unreleased

- Remove model export and edited-model apply commands, the associated package dependency, and the Animator export pass. Keep optional source-audio recovery.
- Prompt for a distinct new weapon/item name before extraction, use it for the new dump folder and bundle key, and open a reload-safe Unity guide for remaining manual work.
- Restore missing weapon SoundBank blend options and shared left-hand/gesture controller motions from SDK assets, with backups and review logs for ambiguous matches.
- Include the referenced original SDK animation and blend-option bundles in build verification after those repairs.
- Guide model/animation editing, prefab and audio review, mod registration, and in-game checks; expose the built bundle key and CAB IDs.
- Resolve duplicate CAB providers from the selected source and dependency folders using referenced object IDs. No sibling SPT installations are searched.
- Add optional AssetStudioModCLI source-audio recovery and automatic build verification.

## 1.1.0

- Add initial experimental weapon model export support (removed from the current development build).

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

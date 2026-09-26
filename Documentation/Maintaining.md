# Maintaining the dumper package

## One authoritative working copy

Edit the repository and install it into a development SDK through **Package Manager → Add package from disk → package.json**. A local package outside the SDK can be edited directly. Keep one installed dumper implementation in that SDK.

Use the root package identifier `ca.bushtail.tarkov-asset-bundle-dumper` consistently in project manifests, install instructions, and release notes. The assembly is `bushtail.AssetBundleDumper.Editor`; the script namespace is `Editor.bushtail`.

## Before a commit

1. Run `Tools~/Validate-Package.ps1` from PowerShell.
2. Check SDK compilation after changes to code, the DLL, dependencies, or the assembly definition.
3. Exercise the affected export/build behavior. A settings/UI-only edit needs a correspondingly focused check.
4. Preserve the existing GUIDs in `.meta` files. Move assets together with their metadata.
5. Keep extraction fixtures, game data, built bundles, logs, and Unity caches outside the package repository.
6. Update documentation when controls, dependency rules, APIs, or compatibility change.
7. Review `git status`, the diff, and the staged content before committing.

The package validator checks metadata and distribution contents. The GitHub workflow uses the same check; it does not run Unity or validate game assets.

## Release preparation

1. Update the version in `package.json` and add matching release notes in `CHANGELOG.md`.
2. Use patch versions for compatible fixes, minor versions for compatible features, and major versions for breaking changes.
3. Test installation of the package in a compatible SDK. Record the actual Unity, SDK, AssetRipper, and impostor revision used.
4. Recheck one representative weapon export/build when the relevant pipeline changed. Verify original shaders/cubemaps/physics materials and loader dependency keys.
5. Commit the reviewed release contents.
6. Tag the tested commit with the matching version, for example `v1.0.0`.
7. Push the commit/tag and create the GitHub release. Users can install that fixed tag through the Git URL.

Keep published tags fixed. Publish a new version for a fix. The impostor Git dependency belongs in the consuming SDK's project manifest, while this package declares its registry dependencies in `package.json`.

## First publication

Create the GitHub repository, then configure its actual URL as the local repository's `origin`. Review and commit the package files before pushing. Replace `YOUR_USERNAME` in the README's public install examples with the owner after the hosting location is known.

The current release version is `1.0.0`; create that tag only after reviewing the first committed package. The package can be loaded locally before any GitHub repository or tag exists.

## Dependency updates

Update one dependency at a time and test the editor/build APIs it supplies. Preserve the isolated AssetsTools assembly name and namespaces; replacing it with a regular SDK AssetsTools binary can introduce type conflicts.

Keep the DLL's Editor-only PluginImporter metadata and the included upstream license/provenance. Maintain third-party notices when changing dependencies. The original dumper currently has no selected license.

## Useful bug reports

Collect package version/tag, Unity version, SDK version, AssetRipper version, source/dependency installation version, selected options, and the relevant dumper/extractor log. Distinguish package installation, extraction, build verification, mod loading, and gameplay failures.

Prefer a reproducible description or a small synthetic fixture in the repository. Real game fixtures and their private source paths can remain in the developer's separate validation project.

## Validation performed for initial packaging

- The nine editor scripts compiled against Unity and the required build/browser/JSON dependencies without referencing SDK `Assembly-CSharp`.
- A separate Unity `2022.3.43f1` project installed the local UPM package and its pinned impostor prerequisite.
- The import check inspected the package version/source, editor assembly coverage, player exclusion, menu entry, and the bundled DLL's importer settings/GUID.

A distributable `.tgz` can also be produced with Unity's [Package Manager Pack API](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/PackageManager.Client.Pack.html). Keep generated release files outside tracked package content; `.build/` is ignored here. Users install an attached `.tgz` release through **Add package from tarball** after installing the impostor prerequisite.

These packaging checks do not claim that every extraction/build/gameplay regression fixture was rerun for this distribution.

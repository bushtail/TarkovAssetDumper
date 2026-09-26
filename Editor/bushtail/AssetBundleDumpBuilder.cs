#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetBundleBrowser;
using AssetBundleBrowser.Imposter;
using BundleDumperInternal.AssetsTools.NET;
using BundleDumperInternal.AssetsTools.NET.Extra;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.Build.Pipeline;
using UnityEngine;

namespace Editor.bushtail
{
    public static class AssetBundleDumpBuilder
    {
        private const string SettingsFile = "BundleDumpBuild.json";
        public sealed class Settings
        {
            public string BundleName = "";
            public string[] Sources = Array.Empty<string>();
            public string[] DependencyRoots = Array.Empty<string>();
            public string[] DependencyAssetGuids = Array.Empty<string>();
            public Dictionary<string, string> Addresses = new(); // asset GUID -> original load address
        }

        public static string SettingsPath(string root)
        {
            var project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var absolute = Path.GetFullPath(root);
            var assets = Path.GetFullPath(Application.dataPath).TrimEnd(Path.DirectorySeparatorChar);
            if (!absolute.StartsWith(assets + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("A dump root must be a folder inside Assets.");
            }

            var assetPath = absolute.Substring(project.Length + 1).Replace('\\', '/').TrimEnd('/');
            var guid = AssetDatabase.IsValidFolder(assetPath) ? AssetDatabase.AssetPathToGUID(assetPath) : "";
            if (string.IsNullOrEmpty(guid))
            {
                throw new InvalidOperationException("Dump folder is not imported: " + root);
            }

            // Folder GUID survives moving/renaming the item in the Project window.
            return Path.Combine(project, "Library", "BundleDumper", "BuildSettings", guid + ".json");
        }

        private static void SaveSettings(string root, Settings settings)
        {
            var path = SettingsPath(root);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonConvert.SerializeObject(settings, Formatting.Indented));
            if (File.Exists(path))
            {
                File.Replace(temporary, path, null);
            }
            else
            {
                File.Move(temporary, path);
            }
        }

        private static void RemoveLegacySettings(string root)
        {
            var legacy = Path.Combine(root, SettingsFile);
            if (!File.Exists(legacy))
            {
                return;
            }

            var project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var assetPath = Path.GetFullPath(legacy).Substring(project.Length + 1).Replace('\\', '/');
            if (!AssetDatabase.DeleteAsset(assetPath))
            {
                throw new IOException("Build settings were cached, but the old metadata asset could not be removed: " + assetPath);
            }
        }

        public static Settings ReadSettings(string root)
        {
            var legacy = Path.Combine(root, SettingsFile);
            var path = File.Exists(legacy) ? legacy : SettingsPath(root);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Build metadata is missing from Library/BundleDumper. Dump the item again to restore it.", path);
            }

            var settings = JsonConvert.DeserializeObject<Settings>(File.ReadAllText(path));
            if (settings == null || string.IsNullOrWhiteSpace(settings.BundleName) || settings.Addresses == null
                || settings.Sources == null || settings.Sources.Length == 0)
            {
                throw new InvalidDataException("Invalid bundle build metadata: " + path);
            }

            // Older cached dumps predate the explicit dependency-root list.
            if (settings.DependencyRoots.Length == 0)
            {
                settings.DependencyRoots = AssetDatabase.GetSubFolders(root).Where(p => 
                    Path.GetFileName(p).StartsWith("Dependencies", StringComparison.Ordinal)).ToArray();
            }

            if (path != legacy) { return settings; }

            SaveSettings(root, settings);
            RemoveLegacySettings(root);
            return settings;
        }

        public static string? FindDumpRoot(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            path = path.Replace('\\', '/').TrimEnd('/');
            if (File.Exists(path))
            {
                path = Path.GetDirectoryName(path)!.Replace('\\', '/');
            }

            while (!string.IsNullOrEmpty(path) && path != "Assets")
            {
                if (AssetDatabase.IsValidFolder(path) && (File.Exists(Path.Combine(path, SettingsFile)) || File.Exists(SettingsPath(path))))
                {
                    return path;
                }

                path = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";
            }
            return null;
        }

        public static void WriteSettings(string root, IReadOnlyList<AssetBundleDependencies.BundleInfo> bundles, 
            string[] dependencyRoots, string[]? dependencyAssetGuids = null, string[]? originalSources = null)
        {
            var settings = new Settings { BundleName = bundles[0].Name, Sources = originalSources ?? bundles.Select(b => b.Path).ToArray(), DependencyRoots = dependencyRoots, DependencyAssetGuids = dependencyAssetGuids ?? Array.Empty<string>() };
            
            if (!settings.BundleName.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase))
            {
                settings.BundleName += ".bundle";
            }

            var mainPaths = SourceAssets(root, dependencyRoots, settings.DependencyAssetGuids);
            var manager = new AssetsManager();
            try
            {
                var bundle = manager.LoadBundleFile(bundles[0].Path);
                for (var i = 0; i < bundle.file.BlockAndDirInfo.DirectoryInfos.Count; i++)
                {
                    if (!AssetBundleDependencies.IsSerializedFile(bundle.file, i))
                    {
                        continue;
                    }

                    var file = manager.LoadAssetsFileFromBundle(bundle, i);
                    foreach (var info in file.file.Metadata.AssetInfos.Where(a => a.TypeId == 142))
                    {
                        var value = manager.GetBaseField(file, info);
                        foreach (var entry in value["m_Container"]["Array"].Children)
                        {
                            var address = entry["first"].AsString.Replace('\\', '/');
                            var relative = address.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) ? address.Substring(7) : address;
                            var matches = mainPaths.Where(p => p.Substring(root.Length + 1).Equals(relative, StringComparison.OrdinalIgnoreCase)).ToArray();
                            if (matches.Length == 0)
                            {
                                matches = mainPaths.Where(p => Path.GetFileName(p).Equals(Path.GetFileName(address), StringComparison.OrdinalIgnoreCase)).ToArray();
                            }

                            if (matches.Length != 1)
                            {
                                throw new InvalidOperationException("Cannot uniquely preserve original asset address: " + address);
                            }

                            settings.Addresses[AssetDatabase.AssetPathToGUID(matches[0])] = address;
                        }
                    }
                }
            }
            finally { manager.UnloadAll(true); }
            SaveSettings(root, settings);
            RemoveLegacySettings(root);
        }

        public static string BuildStandalone(string root, string? output = null) => BuildCore(root, output, true);

        public static string[] SourceAssets(string root, IReadOnlyList<string> dependencyRoots,
            IReadOnlyCollection<string>? dependencyAssetGuids = null)
        {
            var excluded = dependencyAssetGuids == null || dependencyAssetGuids.Count == 0 ? null : new HashSet<string>(dependencyAssetGuids, StringComparer.OrdinalIgnoreCase);
            
            return AssetDatabase.GetAllAssetPaths().Where(p => p.StartsWith(root + "/", StringComparison.Ordinal)
                && !AssetDatabase.IsValidFolder(p)
                && (!excluded?.Contains(AssetDatabase.AssetPathToGUID(p)) ?? !dependencyRoots.Any(d => p.StartsWith(d + "/", StringComparison.Ordinal)))
                && !p.StartsWith(root + "/ExportSupport/", StringComparison.Ordinal)
                && AssetDatabase.LoadMainAssetAtPath(p) is not MonoScript)
                .OrderBy(p => p, StringComparer.Ordinal).ToArray();
        }

        public static void BuildSelected()
        {
            var path = FindDumpRoot(AssetDatabase.GetAssetPath(Selection.activeObject));
            if (string.IsNullOrEmpty(path))
            {
                throw new InvalidOperationException("Select an asset inside a completed bundle dump. If Library was cleared, dump the item again to restore build metadata.");
            }

            EditorUtility.RevealInFinder(Build(path!));
        }

        public static string Build(string root, string? output = null) => BuildCore(root, output, false);

        private static string BuildCore(string root, string? output, bool standalone)
        {
            var settings = ReadSettings(root);
            var requiredSources = AssetBundleDumpImpostors.EnforceRequired(root, settings.Sources);
            if (!settings.Sources.SequenceEqual(requiredSources, StringComparer.OrdinalIgnoreCase))
            {
                settings.Sources = requiredSources;
                SaveSettings(root, settings);
            }
            AssetBundleAnimatorMaskRepair.Restore(root, settings.Sources);
            var name = settings.BundleName.Replace('\\', '/').ToLowerInvariant();
            if (Path.IsPathRooted(name) || name.Split('/').Any(p => p is ".." or "."))
            {
                throw new InvalidDataException("Unsafe bundle name.");
            }

            AssetDatabase.SaveAssets();
            AssetBundleExportFolders.PruneImportedEmptyFolders(root);
            AssetBundleDumper.Validate(root);
            var main = settings.Addresses.Keys.Select(AssetDatabase.GUIDToAssetPath).ToArray();
            if (main.Any(string.IsNullOrEmpty))
            {
                throw new InvalidOperationException("An original root asset was removed from this dump.");
            }

            switch (standalone)
            {
                case false when main.Length == 0:
                {
                    throw new InvalidOperationException("The selected bundle has no recorded original asset addresses.");
                }
                case true:
                {
                    var dependencyRoots = settings.DependencyRoots;
                    main = SourceAssets(root, dependencyRoots, settings.DependencyAssetGuids);
                    if (main.Length == 0 || settings.Addresses.Keys.Any(g => !main.Contains(AssetDatabase.GUIDToAssetPath(g))))
                    {
                        throw new InvalidOperationException("The selected source assets are missing from this dump.");
                    }

                    break;
                }
            }

            var all = AssetDatabase.GetDependencies(main, true).OrderBy(p => p, StringComparer.Ordinal).ToArray();
            var generator = new ImposterIdentifierGenerator();
            var impostors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in all)
            {
                var id = AssetUserDataHelper.GetData<long>(path, ImposterBuilder.CanonicalPathIDKey);
                if (id == 0)
                {
                    continue;
                }

                if (standalone)
                {
                    if (path.StartsWith(root + "/", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("This dump contains an original-game impostor: " + path + ". Extract it again with dependency impostors disabled.");
                    }

                    continue;
                }
                var cab = AssetUserDataHelper.GetData<string>(path, ImposterBuilder.CanonicalCabIDKey);
                if (string.IsNullOrWhiteSpace(cab))
                {
                    throw new InvalidOperationException("Impostor has no CAB ID: " + path);
                }

                if (!identities.Add(cab + ":" + id))
                {
                    throw new InvalidOperationException("Duplicate impostor identity in the selected dump: " + path);
                }

                var importer = AssetImporter.GetAtPath(path);
                var group = importer.assetBundleName + (string.IsNullOrEmpty(importer.assetBundleVariant) ? "" : "." + importer.assetBundleVariant);
                
                if (string.IsNullOrEmpty(group))
                {
                    throw new InvalidOperationException("Original dependency has no bundle label: " + path);
                }

                if (generator.CustomCabIdMap.TryGetValue(group, out var priorCab) && !priorCab.Equals(cab, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Original dependency label contains multiple CABs: " + group);
                }

                generator.CustomCabIdMap[group] = cab;
                if (!impostors.TryGetValue(group, out var list))
                {
                    impostors[group] = list = new List<string>();
                }

                list.Add(path);
            }
            var builds = new List<AssetBundleBuild> 
            { 
                new() 
                {
                    assetBundleName = name, 
                    assetNames = main,
                    addressableNames = main.Select(p => settings.Addresses.TryGetValue(AssetDatabase.AssetPathToGUID(p), out var address) 
                        ? address : p.ToLowerInvariant()).ToArray()
            }};
            
            builds.AddRange(impostors.Select(p => new AssetBundleBuild { assetBundleName = p.Key, assetNames = p.Value.ToArray() }));

            output ??= standalone ? Path.Combine("AssetBundles", "Dumps", Path.GetFileName(root), "Single-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]) : Path.Combine("AssetBundles", "Dumps", Path.GetFileName(root));
            output = Path.GetFullPath(output);
            Directory.CreateDirectory(output);
            var parameters = new BundleBuildParameters(BuildTarget.StandaloneWindows64, BuildTargetGroup.Standalone, output)
            {
                UseCache = false, 
                AppendHash = false, 
                BundleCompression = BuildCompression.LZ4
            };
            var code = ContentPipeline.BuildAssetBundles(parameters, new BundleBuildContent(builds), out var results, DefaultBuildTasks.Create(DefaultBuildTasks.Preset.AssetBundleCompatible), generator);
            if (code < ReturnCode.Success)
            {
                throw new InvalidOperationException("Bundle build failed: " + code);
            }

            if (!results.BundleInfos.TryGetValue(name, out var details))
            {
                throw new InvalidDataException("The build did not produce the requested bundle: " + name);
            }

            var result = Path.GetFullPath(details.FileName);
            try
            {
                if (standalone && (results.BundleInfos.Count != 1 || details.Dependencies?.Length > 0))
                {
                    throw new InvalidDataException("Build produced additional bundle dependencies: " + string.Join(", ", details.Dependencies ?? Array.Empty<string>()));
                }

                VerifyReferences(result, settings.Sources, !standalone);
                return result;
            }
            catch
            {
                if (File.Exists(result))
                {
                    File.Move(result, result + ".failed-" + Guid.NewGuid().ToString("N"));
                }

                throw;
            }
            finally
            {
                foreach (var imp in impostors.Keys)
                {
                    var temporary = Path.GetFullPath(results.BundleInfos[imp].FileName);
                    if (!temporary.StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("Temporary impostor output escaped build directory.");
                    }

                    if (File.Exists(temporary))
                    {
                        File.Delete(temporary);
                    }
                }
            }
        }

        public static void VerifyReferences(string output, IEnumerable<string> sources, bool allowOriginalDependencies = true)
        {
            var ids = new Dictionary<string, HashSet<long>>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in sources)
            {
                var reader = new AssetsManager();
                try
                {
                    var bundle = reader.LoadBundleFile(path);
                    for (var i = 0; i < bundle.file.BlockAndDirInfo.DirectoryInfos.Count; i++)
                    {
                        if (AssetBundleDependencies.IsSerializedFile(bundle.file, i))
                        {
                            var file = reader.LoadAssetsFileFromBundle(bundle, i);
                            ids[Key(bundle.file.GetFileName(i))] = new HashSet<long>(file.file.Metadata.AssetInfos.Select(a => a.PathId));
                        }
                    }
                }
                finally { reader.UnloadAll(true); }
            }
            var manager = new AssetsManager();

            void Check(AssetTypeValueField field, AssetsFileInstance file, HashSet<long> local)
            {
                if (field.TypeName.StartsWith("PPtr<", StringComparison.Ordinal))
                {
                    var id = field["m_PathID"].AsLong;
                    if (id == 0)
                    {
                        return;
                    }

                    var index = field["m_FileID"].AsInt;
                    if (index == 0) { if (!local.Contains(id))
                        {
                            throw new InvalidDataException("Unresolved internal object: " + id);
                        }

                        return; }
                    var cab = Key(file.file.Metadata.Externals[index - 1].PathName);
                    if (cab.StartsWith("unity", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    if (!allowOriginalDependencies)
                    {
                        throw new InvalidDataException("Built bundle still references external file " + cab + ":" + id);
                    }

                    if (!ids.TryGetValue(cab, out var target) || !target.Contains(id))
                    {
                        throw new InvalidDataException("Built bundle references missing original object " + cab + ":" + id);
                    }

                    return;
                }
                foreach (var child in field.Children)
                {
                    Check(child, file, local);
                }
            }
            try
            {
                var bundle = manager.LoadBundleFile(output);
                for (var i = 0; i < bundle.file.BlockAndDirInfo.DirectoryInfos.Count; i++)
                {
                    if (!AssetBundleDependencies.IsSerializedFile(bundle.file, i))
                    {
                        continue;
                    }

                    var file = manager.LoadAssetsFileFromBundle(bundle, i);
                    var local = new HashSet<long>(file.file.Metadata.AssetInfos.Select(a => a.PathId));
                    foreach (var asset in file.file.Metadata.AssetInfos)
                    {
                        var template = manager.GetTemplateBaseField(file, asset);
                        if (template == null || template.Children.Count == 0)
                        {
                            throw new InvalidDataException("Cannot verify built type tree.");
                        }

                        if (HasPointers(template))
                        {
                            Check(manager.GetBaseField(file, asset), file, local);
                        }
                    }
                }
            }
            finally { manager.UnloadAll(true); }
            Debug.Log("DUMP_BUILD_VERIFIED: all serialized object references resolve: " + output);
            return;

            bool HasPointers(AssetTypeTemplateField field) => field.Type.StartsWith("PPtr<", StringComparison.Ordinal) 
                                                              || field.Type.IndexOf("managedReference", StringComparison.OrdinalIgnoreCase) >= 0 
                                                              || field.Children.Any(HasPointers);

            string Key(string value) => value.Replace('\\', '/').Split('/').Last();
        }
    }
}

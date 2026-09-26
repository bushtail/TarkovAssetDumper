#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetBundleBrowser.Imposter;
using BundleDumperInternal.AssetsTools.NET.Extra;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Editor.bushtail
{
    public static class AssetBundleDumpImpostors
    {
        private static int RequiredType(UnityEngine.Object? asset) => asset switch
        {
            Cubemap => 89,
            Shader => 48,
            PhysicMaterial => 134,
            _ => 0
        };

        private static string RequiredBundle(int type) => type switch
        {
            89 => "cubemaps",
            48 => "shaders",
            _ => "assets/commonassets/physics/physicsmaterials.bundle"
        };

        private static string OriginalPath(string name, IReadOnlyList<AssetBundleDependencies.BundleInfo> bundles)
        {
            var matches = bundles.Where(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            switch (matches.Length)
            {
                case 1:
                {
                    return matches[0].Path;
                }
                case > 1:
                {
                    throw new InvalidOperationException("Ambiguous original game bundle: " + name);
                }
            }

            const string marker = "/StreamingAssets/Windows/";
            
            var candidates = bundles.Select(b => b.Path.Replace('\\', '/')).Where(p => p.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(p => p[..(p.IndexOf(marker, StringComparison.OrdinalIgnoreCase) + marker.Length)] + name)
                .Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            
            switch (candidates.Length)
            {
                case 1:
                {
                    return candidates[0];
                }
                case > 1:
                {
                    throw new InvalidOperationException("Original shared bundle exists in multiple source installations: " + name);
                }
            }

            var fallback = Path.Combine(AssetBundleDependencies.EnvironmentBundleFolder(), name);
            return File.Exists(fallback) ? fallback : throw new FileNotFoundException("Cannot find original shared game bundle: " + name);
        }
        
        public static string[] EnforceRequired(string root, string[] sources, List<string>? log = null)
        {
            var bundles = sources.Select(p => new AssetBundleDependencies.BundleInfo 
            { 
                Path = p,
                Name = new[]
                {
                    "cubemaps", 
                    "shaders", 
                    "assets/commonassets/physics/physicsmaterials.bundle"
                }.FirstOrDefault(n => p.Replace('\\', '/').EndsWith("/" + n, StringComparison.OrdinalIgnoreCase)) ?? Path.GetFileName(p) 
            }).ToArray();
            
            var exported = AssetDatabase.GetAllAssetPaths().Where(p => p.StartsWith(root + "/", StringComparison.Ordinal)).ToArray();
            var names = AssetDatabase.GetDependencies(exported, true).Select(AssetDatabase.LoadMainAssetAtPath)
                .Select(RequiredType).Where(t => t != 0).Select(RequiredBundle).Distinct().ToArray();
            var originals = names.Select(n => OriginalPath(n, bundles)).ToArray();
            Apply(new[] { root }, bundles, "", log ?? new List<string>());
            return sources.Concat(originals).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static Dictionary<string, List<(string address, string cab, long id, int type)>> ReadOriginals(IReadOnlyList<AssetBundleDependencies.BundleInfo> bundles, IEnumerable<string> names)
        {
            var result = new Dictionary<string, List<(string, string, long, int)>>(StringComparer.OrdinalIgnoreCase);
            var manager = new AssetsManager();
            try
            {
                foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var entries = result[name] = new List<(string, string, long, int)>();
                    var source = manager.LoadBundleFile(OriginalPath(name, bundles));
                    for (var i = 0; i < source.file.BlockAndDirInfo.DirectoryInfos.Count; i++)
                    {
                        if (!AssetBundleDependencies.IsSerializedFile(source.file, i))
                        {
                            continue;
                        }

                        var file = manager.LoadAssetsFileFromBundle(source, i);
                        var types = file.file.Metadata.AssetInfos.ToDictionary(a => a.PathId, a => a.TypeId);
                        var cab = source.file.GetFileName(i);
                        foreach (var info in file.file.Metadata.AssetInfos.Where(a => a.TypeId == 142))
                        {
                            var value = manager.GetBaseField(file, info);
                            foreach (var item in value["m_Container"]["Array"].Children)
                            {
                                var id = item["second"]["asset"]["m_PathID"].AsLong;
                                if (types.TryGetValue(id, out var type))
                                {
                                    entries.Add((item["first"].AsString.Replace('\\', '/'), cab, id, type));
                                }
                            }
                        }

                        foreach (var info in file.file.Metadata.AssetInfos.Where(a => a.TypeId is 48 or 89 or 134))
                        {
                            var value = manager.GetBaseField(file, info);
                            var nameField = info.TypeId == 48 ? value["m_ParsedForm"]["m_Name"] : value["m_Name"];
                            if (!nameField.IsDummy)
                            {
                                entries.Add((nameField.AsString, cab, info.PathId, info.TypeId));
                            }
                        }
                    }
                    manager.UnloadAll(true);
                }
            }
            finally { manager.UnloadAll(true); }
            return result;
        }

        // One canonical PathID is safe only for assets with one serialized object.
        // Shared game assets always remain original dependencies; ordinary textures are optional.
        public static void Apply(IReadOnlyList<string> destinations,
            IReadOnlyList<AssetBundleDependencies.BundleInfo> bundles, string mapPath, List<string> log,
            bool includeTextures = false)
        {
            var plan = new List<(string path, string cab, long id, string label)>();
            var paths = AssetDatabase.GetAllAssetPaths();
            var selected = new List<(string path, int bundleIndex, UnityEngine.Object asset, int type)>();
            for (var i = 0; i < destinations.Count; i++)
            {
                foreach (var path in paths.Where(p => p.StartsWith(destinations[i] + "/", StringComparison.Ordinal)))
                {
                    var asset = AssetDatabase.LoadMainAssetAtPath(path);
                    var type = RequiredType(asset);
                    if (type == 0 && !(i != 0 && includeTextures && asset is Texture2D))
                    {
                        continue;
                    }

                    if (AssetDatabase.LoadAllAssetsAtPath(path).Count(a => a) != 1)
                    {
                        throw new InvalidOperationException("Cannot assign one original PathID to an asset with subassets: " + path);
                    }

                    selected.Add((path, i, asset, type));
                }
            }
            var originals = ReadOriginals(bundles, selected.Where(s => s.type != 0).Select(s => RequiredBundle(s.type)));
            JArray? files = null;
            if (selected.Any(s => s.type == 0))
            {
                files = (JArray)(JObject.Parse(File.ReadAllText(mapPath))["Files"]
                                 ?? throw new InvalidDataException("AssetRipper produced no PathID file map."));
            }

            foreach (var entry in selected)
            {
                if (entry.type != 0)
                {
                    var label = RequiredBundle(entry.type);
                    var matches = originals[label].Where(o => o.type == entry.type
                        && (Path.GetFileName(o.address).Equals(Path.GetFileName(entry.path), StringComparison.OrdinalIgnoreCase)
                            || o.address.Equals(Path.GetFileNameWithoutExtension(entry.path), StringComparison.OrdinalIgnoreCase)
                            || o.address.Equals(entry.asset.name, StringComparison.OrdinalIgnoreCase)))
                        .GroupBy(o => o.cab + ":" + o.id, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToArray();
                    if (matches.Length != 1 || matches[0].id == 0)
                    {
                        throw new InvalidOperationException("No unique original " + label + " CAB/PathID for " + entry.path + " (" + matches.Length + " matches).");
                    }

                    plan.Add((entry.path, matches[0].cab, matches[0].id, label));
                    continue;
                }
                var sourceFiles = files!.OfType<JObject>().Where(f => bundles[entry.bundleIndex].Files.Contains((string?)f["Name"] ?? ""));
                var textureMatches = sourceFiles.SelectMany(f => ((JArray?)f["Assets"] ?? new JArray()).OfType<JObject>()
                    .Where(a => (string?)a["Type"] == "Texture2D" && (string?)a["Name"] == entry.asset.name)
                    .Select(a => (cab: (string)f["Name"]!, id: (long)a["PathID"]!))).ToArray();
                if (textureMatches.Length != 1 || textureMatches[0].id == 0)
                {
                    throw new InvalidOperationException("No unique original CAB/PathID for texture " + entry.path);
                }

                var cab = bundles[entry.bundleIndex].Files.Single(f => f.Equals(textureMatches[0].cab, StringComparison.OrdinalIgnoreCase));
                plan.Add((entry.path, cab, textureMatches[0].id, bundles[entry.bundleIndex].Name));
            }
            var canonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var plannedPaths = new HashSet<string>(plan.Select(p => p.path));
            foreach (var path in paths.Where(p => !plannedPaths.Contains(p)))
            {
                var importer = AssetImporter.GetAtPath(path);
                if (!importer || string.IsNullOrEmpty(importer.assetBundleName) || string.IsNullOrEmpty(importer.userData))
                {
                    continue;
                }

                var cab = AssetUserDataHelper.GetData<string>(path, ImposterBuilder.CanonicalCabIDKey);
                var id = AssetUserDataHelper.GetData<long>(path, ImposterBuilder.CanonicalPathIDKey);
                if (string.IsNullOrEmpty(cab) || id == 0)
                {
                    continue;
                }

                var label = importer.assetBundleName + (string.IsNullOrEmpty(importer.assetBundleVariant)
                    ? "" : "." + importer.assetBundleVariant);
                canonical.TryAdd(cab + ":" + id + "|" + label, path);
            }
            var replacements = new Dictionary<string, (long fileId, string guid)>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in plan)
            {
                var importer = AssetImporter.GetAtPath(entry.path);
                var key = entry.cab + ":" + entry.id + "|" + entry.label;
                if (canonical.TryGetValue(key, out var existing))
                {
                    var target = AssetDatabase.LoadMainAssetAtPath(existing);
                    var source = AssetDatabase.LoadMainAssetAtPath(entry.path);
                    if (!target || target.GetType() != source.GetType())
                    {
                        throw new InvalidOperationException("Conflicting impostor type: " + existing);
                    }

                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(target, out string guid, out long fileId);
                    replacements[AssetDatabase.AssetPathToGUID(entry.path)] = (fileId, guid);
                    importer.SetAssetBundleNameAndVariant("", "");
                    log.Add("REUSE IMPOSTOR: " + entry.path + " -> " + existing);
                    continue;
                }
                var data = string.IsNullOrWhiteSpace(importer.userData) ? new JObject() : JObject.Parse(importer.userData);
                data[ImposterBuilder.CanonicalPathIDKey] = entry.id;
                data[ImposterBuilder.CanonicalCabIDKey] = entry.cab;
                importer.userData = data.ToString(Newtonsoft.Json.Formatting.None);
                var hasVariant = entry.label.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase);
                importer.SetAssetBundleNameAndVariant(hasVariant ? entry.label.Substring(0, entry.label.Length - 7) : entry.label,
                    hasVariant ? "bundle" : "");
                importer.SaveAndReimport();
                canonical[key] = entry.path;
                log.Add("ORIGINAL IMPOSTOR: " + entry.path + " -> " + entry.label + " " + entry.cab + ":" + entry.id);
            }
            if (replacements.Count != 0)
            {
                AssetDatabase.SaveAssets();
                var pattern = new System.Text.RegularExpressions.Regex(@"\{fileID: -?\d+, guid: ([0-9a-fA-F]{32}), type: (\d+)\}");
                var itemRoot = Path.GetDirectoryName(destinations[0])!;
                foreach (var file in Directory.GetFiles(AssetBundleDumper.FileSystemPath(itemRoot), "*", SearchOption.AllDirectories))
                {
                    using var reader = new StreamReader(file);
                    var header = new char[5];
                    if (reader.Read(header, 0, 5) != 5 || new string(header) != "%YAML")
                    {
                        continue;
                    }

                    reader.Close();
                    var original = File.ReadAllText(file);
                    var changed = pattern.Replace(original, m => replacements.TryGetValue(m.Groups[1].Value, out var target)
                        ? "{fileID: " + target.fileId + ", guid: " + target.guid + ", type: " + m.Groups[2].Value + "}" : m.Value);
                    if (changed != original)
                    {
                        File.WriteAllText(file, changed, new System.Text.UTF8Encoding(false));
                    }
                }
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                var redundant = new HashSet<string>(plan.Select(p => p.path)
                    .Where(p => replacements.ContainsKey(AssetDatabase.AssetPathToGUID(p))), StringComparer.OrdinalIgnoreCase);
                var remaining = AssetDatabase.GetAllAssetPaths().Where(p => p.StartsWith(itemRoot + "/", StringComparison.Ordinal)
                    && !redundant.Contains(p) && !AssetDatabase.IsValidFolder(p)).ToArray();
                var referenced = AssetDatabase.GetDependencies(remaining, true);
                foreach (var path in redundant)
                {
                    if (referenced.Contains(path, StringComparer.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("A copied shared asset is still referenced after impostor remapping: " + path);
                    }

                    if (!AssetDatabase.DeleteAsset(path))
                    {
                        throw new IOException("Could not remove redundant copied shared asset: " + path);
                    }

                    log.Add("REMOVED REDUNDANT COPY: " + path);
                }
            }
            // Existing SDK impostors can be referenced by the imported prefabs.
            // Some older SDK assets carry the canonical identity but no label.
            var exported = AssetDatabase.GetAllAssetPaths().Where(p => destinations.Any(d =>
                p.StartsWith(d + "/", StringComparison.Ordinal))).ToArray();
            foreach (var path in AssetDatabase.GetDependencies(exported, true))
            {
                if (exported.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var type = RequiredType(AssetDatabase.LoadMainAssetAtPath(path));
                if (type == 0 || AssetUserDataHelper.GetData<long>(path, ImposterBuilder.CanonicalPathIDKey) == 0)
                {
                    continue;
                }

                var label = RequiredBundle(type);
                var importer = AssetImporter.GetAtPath(path);
                if (!importer)
                {
                    continue;
                }

                var hasVariant = label.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase);
                var name = hasVariant ? label.Substring(0, label.Length - 7) : label;
                var variant = hasVariant ? "bundle" : "";
                if (importer.assetBundleName == name && importer.assetBundleVariant == variant)
                {
                    continue;
                }

                importer.SetAssetBundleNameAndVariant(name, variant);
                log.Add("LABEL EXISTING ORIGINAL: " + path + " -> " + label);
            }
            log.Add("Configured " + plan.Count + " original dependency impostors. Cubemaps, shaders and physics materials always retain original game identities.");
        }
    }
}

#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BundleDumperInternal.AssetsTools.NET;
using BundleDumperInternal.AssetsTools.NET.Extra;

namespace Editor.bushtail
{
    public static class AssetBundlePruner
    {
        private sealed class Collection
        {
            public AssetsFileInstance File = null !;
            public Dictionary<long, AssetFileInfo> Objects = null !;
            public readonly HashSet<long> Keep = new();
        }

        public static List<string> WriteInputs(List<AssetBundleDependencies.BundleInfo> bundles, string output, HashSet<string> sdkShaders, List<string> log, IProgress<string>? progress = null)
        {
            var manager = new AssetsManager();
            var loaded = new Dictionary<int, BundleFileInstance>();
            var collections = new Dictionary<string, Collection>(StringComparer.OrdinalIgnoreCase);
            var owners = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string Key(string s) => s.Replace('\\', '/').Split('/').Last();
            for (var i = 0; i < bundles.Count; i++)
            {
                foreach (var name in bundles[i].Files)
                {
                    if (owners.TryGetValue(name, out var owner) && owner != i)
                    {
                        throw new InvalidDataException("Duplicate bundle file: " + name);
                    }

                    owners[name] = i;
                }
            }

            void Load(int index)
            {
                if (loaded.ContainsKey(index))
                {
                    return;
                }

                progress?.Report("Reading referenced assets: " + Path.GetFileName(bundles[index].Path));
                var bundle = manager.LoadBundleFile(bundles[index].Path);
                loaded.Add(index, bundle);
                for (var j = 0; j < bundle.file.BlockAndDirInfo.DirectoryInfos.Count; j++)
                {
                    if (!AssetBundleDependencies.IsSerializedFile(bundle.file, j))
                    {
                        continue;
                    }

                    var file = manager.LoadAssetsFileFromBundle(bundle, j);
                    collections.Add(Key(bundle.file.GetFileName(j)), new Collection { File = file, Objects = file.file.Metadata.AssetInfos.ToDictionary(a => a.PathId) });
                }
            }

            var pending = new Queue<(Collection file, long id)>();
            Collection? Target(Collection source, AssetTypeValueField pointer)
            {
                if (pointer["m_PathID"].AsLong == 0)
                {
                    return null;
                }

                var fileId = pointer["m_FileID"].AsInt;
                if (fileId == 0)
                {
                    return source;
                }

                if (fileId < 0 || fileId > source.File.file.Metadata.Externals.Count)
                {
                    throw new InvalidDataException("Invalid external file index " + fileId);
                }

                var name = Key(source.File.file.Metadata.Externals[fileId - 1].PathName);
                if (name.Equals("unity_builtin_extra", StringComparison.OrdinalIgnoreCase) || name.Equals("unity default resources", StringComparison.OrdinalIgnoreCase) || name.Equals("unity_builtin_resources", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                if (!owners.TryGetValue(name, out var index))
                {
                    throw new InvalidDataException("Missing referenced file " + name);
                }

                Load(index);
                if (!collections.TryGetValue(name, out var target))
                {
                    throw new InvalidDataException("Not a serialized file: " + name);
                }

                return target;
            }

            void Enqueue(Collection file, long id)
            {
                if (!file.Objects.ContainsKey(id))
                {
                    throw new InvalidDataException("Missing object " + id + " in " + file.File.name);
                }

                if (file.Keep.Add(id))
                {
                    pending.Enqueue((file, id));
                }
            }

            IEnumerable<AssetTypeValueField> Pointers(AssetTypeValueField field)
            {
                if (field.TypeName.StartsWith("PPtr<", StringComparison.Ordinal))
                {
                    yield return field;
                    yield break;
                }

                foreach (var child in field.Children)
                {
                    foreach (var pointer in Pointers(child))
                    {
                        yield return pointer;
                    }
                }
            }

            bool HasPointers(AssetTypeTemplateField field) => field.Type.StartsWith("PPtr<", StringComparison.Ordinal) || field.Type.IndexOf("managedReference", StringComparison.OrdinalIgnoreCase) >= 0 || field.Children.Any(HasPointers);
            try
            {
                Load(0);
                // Preserve the complete selected bundle; filter only its shared dependencies.
                foreach (var file in collections.Values.ToArray())
                {
                    foreach (var asset in file.Objects.Values.Where(a => a.TypeId != 142))
                    {
                        Enqueue(file, asset.PathId);
                    }
                }

                while (pending.Count > 0)
                {
                    var(file, id) = pending.Dequeue();
                    var info = file.Objects[id];
                    if (info.TypeId == 142)
                    {
                        continue; // Containers/preload tables are not object dependencies.
                    }

                    var template = manager.GetTemplateBaseField(file.File, info);
                    if (template == null || template.Children.Count == 0)
                    {
                        throw new InvalidDataException("Cannot safely inspect type tree for object " + id + " in " + file.File.name);
                    }

                    if (!HasPointers(template))
                    {
                        continue;
                    }

                    var value = manager.GetBaseField(file.File, info);
                    var pointers = Pointers(value).ToArray();
                    var name = value["m_Name"].IsDummy ? "" : value["m_Name"].AsString;
                    if (info.TypeId == 48 && string.IsNullOrEmpty(name) && !value["m_ParsedForm"].IsDummy)
                    {
                        name = value["m_ParsedForm"]["m_Name"].AsString;
                    }

                    if (info.TypeId == 48 && sdkShaders.Contains(name))
                    {
                        // The exported dummy is remapped to this exact SDK shader before import.
                        foreach (var pointer in pointers)
                        {
                            pointer["m_FileID"].AsInt = 0;
                            pointer["m_PathID"].AsLong = 0;
                        }

                        info.SetNewData(value);
                        continue;
                    }

                    foreach (var pointer in pointers)
                    {
                        var target = Target(file, pointer);
                        if (target != null)
                        {
                            Enqueue(target, pointer["m_PathID"].AsLong);
                        }
                    }
                }

                Directory.CreateDirectory(output);
                var paths = new List<string>();
                var retained = new List<AssetBundleDependencies.BundleInfo>();
                var kept = 0;
                var total = 0;
                foreach (var pair in loaded.OrderBy(p => p.Key))
                {
                    var bundle = pair.Value;
                    var hasAssets = false;
                    for (var j = 0; j < bundle.file.BlockAndDirInfo.DirectoryInfos.Count; j++)
                    {
                        if (!collections.TryGetValue(Key(bundle.file.GetFileName(j)), out var file))
                        {
                            continue; // Keep raw resource streams.
                        }

                        total += file.Objects.Count;
                        if (file.Keep.Count == 0)
                        {
                            bundle.file.BlockAndDirInfo.DirectoryInfos[j].SetRemoved();
                            continue;
                        }

                        hasAssets = true;
                        foreach (var info in file.Objects.Values)
                        {
                            if (info.TypeId == 142)
                            {
                                var value = manager.GetBaseField(file.File, info);
                                bool KeepPointer(AssetTypeValueField p)
                                {
                                    var target = file;
                                    var external = p["m_FileID"].AsInt;
                                    if (external != 0)
                                    {
                                        if (external < 0 || external > file.File.file.Metadata.Externals.Count)
                                        {
                                            return false;
                                        }

                                        collections.TryGetValue(Key(file.File.file.Metadata.Externals[external - 1].PathName), out target);
                                    }

                                    return target != null && target.Keep.Contains(p["m_PathID"].AsLong);
                                }

                                var entries = value["m_Container"]["Array"].Children;
                                entries.RemoveAll(e => !KeepPointer(e["second"]["asset"]));
                                foreach (var entry in entries)
                                {
                                    entry["second"]["preloadIndex"].AsInt = 0;
                                    entry["second"]["preloadSize"].AsInt = 0;
                                }

                                value["m_PreloadTable"]["Array"].Children.Clear();
                                value["m_Dependencies"]["Array"].Children.Clear();
                                var main = value["m_MainAsset"];
                                main["preloadIndex"].AsInt = 0;
                                main["preloadSize"].AsInt = 0;
                                if (!KeepPointer(main["asset"]))
                                {
                                    main["asset"]["m_FileID"].AsInt = 0;
                                    main["asset"]["m_PathID"].AsLong = 0;
                                }

                                info.SetNewData(value);
                            }
                            else if (!file.Keep.Contains(info.PathId))
                            {
                                info.SetRemoved();
                            }
                            else
                            {
                                kept++;
                            }
                        }

                        bundle.file.BlockAndDirInfo.DirectoryInfos[j].SetNewData(file.File.file);
                    }

                    if (!hasAssets)
                    {
                        continue;
                    }

                    // Separate directories preserve original file names used for output folder naming.
                    var dir = Path.Combine(output, pair.Key.ToString());
                    Directory.CreateDirectory(dir);
                    var path = Path.Combine(dir, Path.GetFileName(bundles[pair.Key].Path));
                    using (var writer = new AssetsFileWriter(path))
                    {
                        bundle.file.Write(writer);
                    }

                    paths.Add(path);
                    retained.Add(bundles[pair.Key]);
                }

                log.Add("REFERENCE FILTER: " + bundles.Count + " candidate bundles -> " + retained.Count + "; kept " + kept + " objects out of " + total + " in inspected bundles.");
                bundles.Clear();
                bundles.AddRange(retained);
                return paths;
            }
            finally
            {
                manager.UnloadAll(true);
            }
        }
    }
}
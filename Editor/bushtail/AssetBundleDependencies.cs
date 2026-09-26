#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BundleDumperInternal.AssetsTools.NET;
using BundleDumperInternal.AssetsTools.NET.Extra;

namespace Editor.bushtail
{
    // Read bundle metadata off-thread. Never instantiate game objects or load game scripts.
    public static class AssetBundleDependencies
    {
        public sealed class BundleInfo
        {
            public string Path = "";
            public string Name = "";
            public readonly HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> Dependencies = new(StringComparer.OrdinalIgnoreCase);
        }

        private static string Key(string value) => value.Replace('\\', '/').Split('/').Last();
        public static bool IsSerializedFile(AssetBundleFile bundle, int index)
        {
            var name = bundle.GetFileName(index);
            return !name.EndsWith(".resS", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".resource", StringComparison.OrdinalIgnoreCase) && bundle.IsAssetsFile(index);
        }

        private static bool Builtin(string value) => value.Equals("unity_builtin_extra", StringComparison.OrdinalIgnoreCase) || value.Equals("unity default resources", StringComparison.OrdinalIgnoreCase) || value.Equals("unity_builtin_resources", StringComparison.OrdinalIgnoreCase);
        public static string EnvironmentInstallRoot()
        {
            var runtime = Environment.GetEnvironmentVariable("SptRoot");
            if (string.IsNullOrWhiteSpace(runtime))
            {
                runtime = Environment.GetEnvironmentVariable("SptRoot", EnvironmentVariableTarget.User);
            }

            if (string.IsNullOrWhiteSpace(runtime))
            {
                runtime = Environment.GetEnvironmentVariable("SptRoot", EnvironmentVariableTarget.Machine);
            }

            return string.IsNullOrWhiteSpace(runtime) ? "" : ValidateSptRoot(runtime);
        }

        public static string ValidateSptRoot(string runtime)
        {
            if (string.IsNullOrWhiteSpace(runtime))
            {
                throw new ArgumentException("SptRoot is empty.", nameof(runtime));
            }

            runtime = Environment.ExpandEnvironmentVariables(runtime.Trim().Trim('"'));
            var directory = new DirectoryInfo(Path.GetFullPath(runtime));
            if (directory.Parent == null)
            {
                throw new InvalidOperationException("SptRoot must point to the runtime folder inside the SPT installation.");
            }

            if (!directory.Exists)
            {
                throw new DirectoryNotFoundException("SptRoot runtime folder does not exist: " + directory.FullName);
            }

            var install = directory.Parent.FullName;
            var missing = new List<string>();
            foreach (var file in new[]
            {
                "EscapeFromTarkov.exe",
                "UnityPlayer.dll"
            }

            )
            {
                if (!File.Exists(Path.Combine(install, file)))
                {
                    missing.Add("file: " + Path.Combine(install, file));
                }
            }

            foreach (var folder in new[]
            {
                "EscapeFromTarkov_Data",
                "EscapeFromTarkov_Data/StreamingAssets/Windows",
                "BepInEx",
                "MonoBleedingEdge"
            }

            )
            {
                if (!Directory.Exists(Path.Combine(install, folder)))
                {
                    missing.Add("folder: " + Path.Combine(install, folder));
                }
            }

            foreach (var file in new[]
            {
                "SPT.Server.exe",
                "SPT.Launcher.exe"
            }

            )
            {
                if (!File.Exists(Path.Combine(directory.FullName, file)))
                {
                    missing.Add("file: " + Path.Combine(directory.FullName, file));
                }
            }

            if (!Directory.Exists(Path.Combine(directory.FullName, "SPT_Data")))
            {
                missing.Add("folder: " + Path.Combine(directory.FullName, "SPT_Data"));
            }

            return missing.Count != 0 ? throw new InvalidDataException("Invalid SPT installation for SptRoot '" + directory.FullName + "'. The game installation must be one folder above it: " + install + "\nMissing required entries:\n" + string.Join("\n", missing)) : install;
        }

        public static string EnvironmentBundleFolder()
        {
            var install = EnvironmentInstallRoot();
            if (string.IsNullOrEmpty(install))
            {
                return "";
            }

            var folder = Path.Combine(install, "EscapeFromTarkov_Data", "StreamingAssets", "Windows");
            return !Directory.Exists(folder) ? throw new DirectoryNotFoundException("SptRoot resolves to an installation without a bundle folder: " + folder) : folder;
        }

        public static List<BundleInfo> Resolve(string input, string? extraFolder, List<string> log, IProgress<string>? progress = null, string? cachePath = null, string? buildOutputFolder = null, bool includeDependencies = true)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            input = Path.GetFullPath(input);
            if (includeDependencies && string.IsNullOrWhiteSpace(extraFolder))
            {
                extraFolder = EnvironmentBundleFolder();
                if (!string.IsNullOrEmpty(extraFolder))
                {
                    log.Add("SPT dependencies from parent of SptRoot: " + extraFolder);
                }
            }

            if (includeDependencies && !string.IsNullOrWhiteSpace(extraFolder) && !Directory.Exists(extraFolder))
            {
                throw new DirectoryNotFoundException("Dependency folder does not exist: " + extraFolder);
            }

            var cache = new Dictionary<string, (long size, long ticks, bool full, BundleInfo info)>(StringComparer.OrdinalIgnoreCase);
            var cacheDirty = false;
            if (!string.IsNullOrWhiteSpace(cachePath) && File.Exists(cachePath))
            {
                try
                {
                    using var reader = new BinaryReader(File.OpenRead(cachePath));
                    var version = reader.ReadInt32();
                    if (version != 1 && version != 2)
                    {
                    }

                    cacheDirty = version != 2;
                    var count = reader.ReadInt32();
                    for (var n = 0; n < count; n++)
                    {
                        var path = reader.ReadString();
                        var size = reader.ReadInt64();
                        var ticks = reader.ReadInt64();
                        var full = reader.ReadBoolean();
                        var info = new BundleInfo
                        {
                            Path = path,
                            Name = reader.ReadString()
                        };
                        var files = reader.ReadInt32();
                        for (var j = 0; j < files; j++)
                        {
                            info.Files.Add(reader.ReadString());
                        }

                        var deps = reader.ReadInt32();
                        for (var j = 0; j < deps; j++)
                        {
                            info.Dependencies.Add(reader.ReadString());
                        }

                        cache.Add(path, (size, ticks, full, info));
                    }
                }
                catch (Exception ex)
                {
                    cache.Clear();
                    log.Add("Rebuilding bundle cache: " + ex.Message);
                }
            }

            var hits = 0;
            var signatureReads = 0;
            var bundleReads = 0;
            var examined = 0;
            long lastProgress = -250;
            var main = ReadCached(input);
            var result = new List<BundleInfo>
            {
                main
            };
            if (!includeDependencies)
            {
                log.Add("Dependency lookup skipped; extracting only " + input);
                return result;
            }

            if (string.IsNullOrWhiteSpace(extraFolder))
            {
                // EFT keeps shared dependencies above the individual weapon folder.
                for (var parent = new DirectoryInfo(Path.GetDirectoryName(input)!); parent != null; parent = parent.Parent)
                {
                    if (parent.Name.Equals("Windows", StringComparison.OrdinalIgnoreCase) && parent.Parent?.Name.Equals("StreamingAssets", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        extraFolder = parent.FullName;
                        break;
                    }
                }
            }

            var requestedRoots = new[]
            {
                Path.GetDirectoryName(input)!,
                extraFolder
            }.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Path.GetFullPath(p!)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            // A parent traversal already includes its child folders.
            var roots = requestedRoots.Where(root => !requestedRoots.Any(other => !root.Equals(other, StringComparison.OrdinalIgnoreCase) && root.StartsWith(other.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))).ToArray();
            BundleInfo? ReadCandidate(FileInfo stat)
            {
                examined++;
                if (timer.ElapsedMilliseconds - lastProgress >= 250)
                {
                    progress?.Report("Checking dependencies: " + examined + " files (" + hits + " cached)");
                    lastProgress = timer.ElapsedMilliseconds;
                }

                var path = stat.FullName;
                // DirectoryInfo enumeration supplies the size/timestamp without a
                // second stat. Unchanged files need no signature or bundle reads.
                if (cache.TryGetValue(path, out var entry) && entry.size == stat.Length && entry.ticks == stat.LastWriteTimeUtc.Ticks)
                {
                    hits++;
                    return entry.info.Files.Count == 0 ? null : entry.info;
                }

                signatureReads++;
                var info = IsBundle(path) ? Read(path, true) : new BundleInfo
                {
                    Path = path
                };
                if (info.Files.Count != 0)
                {
                    bundleReads++;
                }

                cache[path] = (stat.Length, stat.LastWriteTimeUtc.Ticks, false, info);
                cacheDirty = true;
                // Version 2 caches non-bundles too, but changed files are always probed again.
                return info.Files.Count == 0 ? null : info;
            }

            var available = new Dictionary<string, List<BundleInfo>>(StringComparer.OrdinalIgnoreCase);
            void Index(BundleInfo bundle)
            {
                foreach (var name in bundle.Files.Append(Path.GetFileName(bundle.Path)))
                {
                    if (!available.TryGetValue(name, out var entries))
                    {
                        available[name] = entries = new List<BundleInfo>();
                    }

                    if (!entries.Contains(bundle))
                    {
                        entries.Add(bundle);
                    }
                }
            }

            Index(main);
            var indexed = false;
            var indexedBuildOutput = false;
            for (var i = 0; i < result.Count; i++)
            {
                foreach (var dependency in result[i].Dependencies.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    if (Builtin(dependency) || result.Any(b => b.Files.Contains(dependency)))
                    {
                        continue;
                    }

                    if (!available.ContainsKey(dependency) && !indexed)
                    {
                        indexed = true;
                        var candidates = roots.SelectMany(root => new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories)).Where(p => !p.FullName.Equals(input, StringComparison.OrdinalIgnoreCase)).OrderBy(p => p.FullName, StringComparer.OrdinalIgnoreCase);
                        foreach (var candidate in candidates)
                        {
                            try
                            {
                                var info = ReadCandidate(candidate);
                                if (info != null)
                                {
                                    Index(info);
                                }
                            }
                            catch (Exception ex)
                            {
                                log.Add("WARNING: Cannot index " + candidate + ": " + ex.Message);
                            }
                        }
                    }

                    // A rebuilt bundle can reference new CABs that only exist in
                    // the SDK output. Keep the selected source folders authoritative
                    // for existing CABs; do not mix in competing rebuilt copies.
                    if (!available.ContainsKey(dependency) && !indexedBuildOutput)
                    {
                        indexedBuildOutput = true;
                        if (!string.IsNullOrWhiteSpace(buildOutputFolder) && Directory.Exists(buildOutputFolder))
                        {
                            var sourceKeys = new HashSet<string>(available.Keys, StringComparer.OrdinalIgnoreCase);
                            foreach (var candidate in new DirectoryInfo(buildOutputFolder).EnumerateFiles("*", SearchOption.AllDirectories).OrderBy(p => p.FullName, StringComparer.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    var info = ReadCandidate(candidate);
                                    if (info == null)
                                    {
                                        continue;
                                    }

                                    foreach (var name in info.Files.Append(Path.GetFileName(info.Path)))
                                    {
                                        if (sourceKeys.Contains(name))
                                        {
                                            continue;
                                        }

                                        if (!available.TryGetValue(name, out var entries))
                                        {
                                            available[name] = entries = new List<BundleInfo>();
                                        }

                                        if (!entries.Any(b => b.Path.Equals(info.Path, StringComparison.OrdinalIgnoreCase)))
                                        {
                                            entries.Add(info);
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    log.Add("WARNING: Cannot index build output " + candidate + ": " + ex.Message);
                                }
                            }

                            log.Add("Checked SDK build output for missing rebuilt dependencies: " + buildOutputFolder);
                        }
                    }

                    if (!available.TryGetValue(dependency, out var matches))
                    {
                        // Filtered exports retain external-table slots so fileIDs stay
                        // stable. A slot with no remaining object references is not a
                        // dependency and must not prevent recovery/re-export.
                        if (!ReferencesFile(result[i].Path, dependency))
                        {
                            log.Add("Ignored unused external-table entry: " + dependency + " in " + result[i].Path);
                            continue;
                        }

                        throw new InvalidOperationException("Missing dependency '" + dependency + "' required by '" + result[i].Path + "'. Searched: " + string.Join(", ", roots) + (indexedBuildOutput && Directory.Exists(buildOutputFolder) ? ", " + buildOutputFolder : "") + ". Select the matching dependency build in Dependency search folder, or dump an original bundle with its original dependencies.");
                    }

                    if (matches.Count != 1)
                    {
                        throw new InvalidOperationException("Multiple bundles provide '" + dependency + "': " + string.Join(", ", matches.Select(b => b.Path)) + ". Use a search folder containing one copy.");
                    }

                    if (!result.Any(b => b.Path.Equals(matches[0].Path, StringComparison.OrdinalIgnoreCase)))
                    {
                        result.Add(ReadCached(matches[0].Path));
                        log.Add("DEPENDENCY: " + result[i].Name + " -> " + matches[0].Path);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(cachePath) && cacheDirty)
            {
                try
                {
                    cachePath = Path.GetFullPath(cachePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                    var temporary = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    using (var writer = new BinaryWriter(File.Create(temporary)))
                    {
                        writer.Write(2);
                        writer.Write(cache.Count);
                        foreach (var pair in cache)
                        {
                            var entry = pair.Value;
                            writer.Write(pair.Key);
                            writer.Write(entry.size);
                            writer.Write(entry.ticks);
                            writer.Write(entry.full);
                            writer.Write(entry.info.Name);
                            writer.Write(entry.info.Files.Count);
                            foreach (var name in entry.info.Files)
                            {
                                writer.Write(name);
                            }

                            writer.Write(entry.info.Dependencies.Count);
                            foreach (var name in entry.info.Dependencies)
                            {
                                writer.Write(name);
                            }
                        }
                    }

                    if (File.Exists(cachePath))
                    {
                        File.Replace(temporary, cachePath, null);
                    }
                    else
                    {
                        File.Move(temporary, cachePath);
                    }
                }
                catch (IOException ex)
                {
                    log.Add("WARNING: Could not save bundle cache: " + ex.Message);
                }
            }

            log.Add("DEPENDENCY TIMING: " + timer.ElapsedMilliseconds + " ms; " + examined + " files checked; " + hits + " cache hits; " + signatureReads + " signature reads; " + bundleReads + " bundle metadata reads.");
            log.Add("Found " + result.Count + " candidate bundles before object-reference filtering.");
            return result;
            BundleInfo ReadCached(string path, bool namesOnly = false)
            {
                var stat = new FileInfo(path);
                if (cache.TryGetValue(path, out var entry) && entry.size == stat.Length && entry.ticks == stat.LastWriteTimeUtc.Ticks && (namesOnly || entry.full))
                {
                    hits++;
                    return entry.info;
                }

                var info = Read(path, namesOnly);
                bundleReads++;
                cacheDirty = true;
                cache[path] = (stat.Length, stat.LastWriteTimeUtc.Ticks, !namesOnly, info);
                return info;
            }
        }

        private static bool IsBundle(string path)
        {
            using var stream = File.OpenRead(path);
            var magic = new byte[7];
            if (stream.Read(magic, 0, magic.Length) != magic.Length)
            {
                return false;
            }

            return System.Text.Encoding.ASCII.GetString(magic)is "UnityFS" or "UnityRa" or "UnityWe";
        }

        private static bool ReferencesFile(string path, string dependency)
        {
            var manager = new AssetsManager();
            bool HasPointers(AssetTypeTemplateField field) => field.Type.StartsWith("PPtr<", StringComparison.Ordinal) || field.Type.IndexOf("managedReference", StringComparison.OrdinalIgnoreCase) >= 0 || field.Children.Any(HasPointers);
            bool References(AssetTypeValueField field, HashSet<int> indices)
            {
                if (field.TypeName.StartsWith("PPtr<", StringComparison.Ordinal))
                {
                    return field["m_PathID"].AsLong != 0 && indices.Contains(field["m_FileID"].AsInt);
                }

                return field.Children.Any(child => References(child, indices));
            }

            try
            {
                var bundle = manager.LoadBundleFile(path);
                for (var i = 0; i < bundle.file.BlockAndDirInfo.DirectoryInfos.Count; i++)
                {
                    if (!IsSerializedFile(bundle.file, i))
                    {
                        continue;
                    }

                    var file = manager.LoadAssetsFileFromBundle(bundle, i);
                    var indices = new HashSet<int>(file.file.Metadata.Externals.Select((e, n) => (e, n)).Where(p => Key(p.e.PathName).Equals(dependency, StringComparison.OrdinalIgnoreCase)).Select(p => p.n + 1));
                    if (indices.Count == 0)
                    {
                        continue;
                    }

                    foreach (var asset in file.file.Metadata.AssetInfos.Where(a => a.TypeId != 142))
                    {
                        var template = manager.GetTemplateBaseField(file, asset);
                        if (template == null || template.Children.Count == 0)
                        {
                            return true;
                        }

                        if (HasPointers(template) && References(manager.GetBaseField(file, asset), indices))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }
            finally
            {
                manager.UnloadAll(true);
            }
        }

        private static BundleInfo Read(string path, bool namesOnly = false)
        {
            var manager = new AssetsManager();
            try
            {
                var bundle = manager.LoadBundleFile(path, !namesOnly);
                if (bundle == null)
                {
                    throw new InvalidDataException("Cannot read bundle " + path);
                }

                var info = new BundleInfo
                {
                    Path = path,
                    Name = Path.GetFileName(path)
                };
                if (bundle.file.BlockAndDirInfo.DirectoryInfos.Count == 0)
                {
                    throw new InvalidDataException("Bundle contains no serialized files: " + path);
                }

                for (var i = 0; i < bundle.file.BlockAndDirInfo.DirectoryInfos.Count; i++)
                {
                    info.Files.Add(Key(bundle.file.GetFileName(i)));
                    if (namesOnly || !IsSerializedFile(bundle.file, i))
                    {
                        continue;
                    }

                    var assets = manager.LoadAssetsFileFromBundle(bundle, i);
                    foreach (var dependency in assets.file.Metadata.Externals)
                    {
                        info.Dependencies.Add(Key(dependency.PathName));
                    }

                    foreach (var asset in assets.file.Metadata.AssetInfos.Where(a => a.TypeId == 142))
                    {
                        assets.file.Reader.Position = asset.GetAbsoluteByteOffset(assets.file);
                        var name = assets.file.Reader.ReadCountStringInt32();
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            info.Name = name;
                        }
                    }
                }

                return info;
            }
            finally
            {
                manager.UnloadAll(true);
            }
        }
    }
}

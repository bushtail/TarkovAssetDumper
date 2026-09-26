#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BundleDumperInternal.AssetsTools.NET;
using BundleDumperInternal.AssetsTools.NET.Extra;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Editor.bushtail
{
    public static class AssetBundleAnimatorMaskRepair
    {
        public static int Restore(string root, IEnumerable<string> sources, List<string>? log = null)
        {
            var controllers = AssetDatabase.FindAssets("t:AnimatorController", new[] { root })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<AnimatorController>).Where(c => c).ToArray();
            if (controllers.Length == 0)
            {
                return 0;
            }

            var wanted = new HashSet<string>(controllers.Select(c => c.name));
            var originals = new Dictionary<string, AssetTypeValueField>();
            var paths = new Dictionary<uint, string> { [0] = "" };
            var manager = new AssetsManager();
            try
            {
                foreach (var source in sources.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!File.Exists(source))
                    {
                        throw new FileNotFoundException("Original animation bundle is missing.", source);
                    }

                    var bundle = manager.LoadBundleFile(source);
                    for (var index = 0; index < bundle.file.BlockAndDirInfo.DirectoryInfos.Count; index++)
                    {
                        if (!AssetBundleDependencies.IsSerializedFile(bundle.file, index))
                        {
                            continue;
                        }

                        var file = manager.LoadAssetsFileFromBundle(bundle, index);
                        foreach (var info in file.file.Metadata.AssetInfos.Where(a => a.TypeId == 90 || a.TypeId == 91))
                        {
                            var value = manager.GetBaseField(file, info);
                            if (info.TypeId == 90)
                            {
                                foreach (var item in value["m_TOS"]["Array"].Children)
                                {
                                    var hash = item["first"].AsUInt;
                                    var path = item["second"].AsString;
                                    if (unchecked((uint)Animator.StringToHash(path)) != hash)
                                    {
                                        throw new InvalidDataException("Original avatar path does not match its bone hash: " + path);
                                    }

                                    if (paths.TryGetValue(hash, out var previous) && previous != path)
                                    {
                                        throw new InvalidDataException("Ambiguous original animation bone hash: " + hash);
                                    }

                                    paths[hash] = path;
                                }
                            }
                            else if (wanted.Contains(value["m_Name"].AsString))
                            {
                                var name = value["m_Name"].AsString;
                                if (!originals.TryAdd(name, value))
                                {
                                    throw new InvalidDataException("Ambiguous original animator controller: " + name);
                                }
                            }
                        }
                    }
                    manager.UnloadAll(true);
                }
            }
            finally { manager.UnloadAll(true); }

            var restored = 0;
            foreach (var controller in controllers)
            {
                if (!originals.TryGetValue(controller.name, out var original))
                {
                    continue;
                }

                var nativeLayers = original["m_Controller"]["m_LayerArray"]["Array"].Children;
                var layers = controller.layers;
                if (layers.Length != nativeLayers.Count)
                {
                    throw new InvalidDataException("Animator layer count differs from the original: " + controller.name);
                }

                for (var index = 0; index < layers.Length; index++)
                {
                    if (layers[index].avatarMask)
                    {
                        continue; // Keep an existing or user-edited mask.
                    }

                    var native = nativeLayers[index]["data"];
                    if (native["m_Binding"].AsUInt != unchecked((uint)Animator.StringToHash(layers[index].name)))
                    {
                        throw new InvalidDataException("Animator layer order differs from the original: " + controller.name);
                    }

                    var entries = native["m_SkeletonMask"]["data"]["m_Data"]["Array"].Children;
                    if (entries.Count == 0)
                    {
                        continue;
                    }

                    var body = native["m_BodyMask"];
                    if (body["word0"].AsUInt != uint.MaxValue || body["word1"].AsUInt != uint.MaxValue
                        || body["word2"].AsUInt != 524287)
                    {
                        throw new InvalidDataException("Original animator uses a restricted humanoid body mask; an explicit AvatarMask is required: " + controller.name);
                    }

                    var resolved = entries.Where(e => paths.ContainsKey(e["m_PathHash"].AsUInt)).ToArray();
                    if (resolved.Length == 0)
                    {
                        throw new InvalidDataException("Cannot resolve original mask bones: " + controller.name);
                    }

                    var mask = new AvatarMask { name = controller.name + "_" + layers[index].name };
                    // These generic rigs use the transform mask. Preserve enabled
                    // humanoid body parts as well; do not disable IK/body channels.
                    for (var part = 0; part < (int)AvatarMaskBodyPart.LastBodyPart; part++)
                    {
                        mask.SetHumanoidBodyPartActive((AvatarMaskBodyPart)part, true);
                    }

                    mask.transformCount = resolved.Length;
                    for (var bone = 0; bone < resolved.Length; bone++)
                    {
                        var entry = resolved[bone];
                        var weight = entry["m_Weight"].AsFloat;
                        if (weight != 0 && !Mathf.Approximately(weight, 1))
                        {
                            throw new InvalidDataException("Unsupported fractional animator mask weight.");
                        }

                        mask.SetTransformPath(bone, paths[entry["m_PathHash"].AsUInt]);
                        mask.SetTransformActive(bone, weight != 0);
                    }
                    var folder = root + "/AvatarMask";
                    Directory.CreateDirectory(folder);
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                    var safe = string.Concat(mask.name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                    var destination = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + safe + ".mask");
                    AssetDatabase.CreateAsset(mask, destination);
                    var originalImporter = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(controller));
                    AssetImporter.GetAtPath(destination).SetAssetBundleNameAndVariant(originalImporter.assetBundleName, originalImporter.assetBundleVariant);
                    layers[index].avatarMask = mask;
                    restored++;
                    log?.Add("ANIMATOR MASK: " + controller.name + "/" + layers[index].name + " -> " + resolved.Length + " original bone paths (" + (entries.Count - resolved.Length) + " absent from source avatars).");
                }
                controller.layers = layers;
                EditorUtility.SetDirty(controller);
            }
            if (restored != 0)
            {
                AssetDatabase.SaveAssets();
            }

            return restored;
        }
    }
}

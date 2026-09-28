#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Editor.bushtail
{
    /// <summary>Safe SDK replacements for shared hand actions and missing audio blend options.</summary>
    internal static class AssetBundleWeaponSharedRepair
    {
        private static readonly HashSet<string> SharedStates = new(StringComparer.Ordinal)
        {
            "weapon_root_anim_fix", "generic_0", "generic_90", "generic_180", "generic_270",
            "Fallback", "Trunk Close", "Trunk Open", "Take Loot", "hand_nv_on", "hand_nv_off",
            "hand_key_use", "hand_drop_stuff", "hand_faceshield_off", "hand_faceshield_on",
            "hand_slap_forward", "OPEN_PUSH_RIGHT_HINGE", "OPEN_PUSH_LEFT_HINGE",
            "OPEN_PULL_LEFT_HINGE", "OPEN_PULL_RIGHT_HINGE", "pull_hinge_right",
            "pull_hinge_left", "push_hinge_left", "push_hinge_right"
        };
        private static readonly Dictionary<string, string> OriginalSdkClips = new(StringComparer.Ordinal)
        {
            ["weapon_root_anim_fix"] = "f5d6de42a6ad38a47819d80fed8ef704",
            ["generic_0"] = "7e68e116281e8764ca5442972e63bd42",
            ["generic_90"] = "02216f1a966d5214e98357677fac1a4d",
            ["generic_180"] = "e8122ffeac79eae4e8b2da92e67ab6e2",
            ["generic_270"] = "411676f0ab5383d4cad22e3d37ab03e8",
            ["Fallback"] = "d1eee6a189a5c0d49861eb7c46fbb47b",
            ["Trunk Close"] = "3b8a87d17bb4533418c20f4290ea8508",
            ["Trunk Open"] = "f9fc3e23472e5b24aa789ace2b24dcc3",
            ["Take Loot"] = "db026a82f1234694e8d4ae7d25c60f81",
            ["hand_nv_on"] = "e0198e182a712fc439a8e9cc6a981d4b",
            ["hand_nv_off"] = "4f5753f8efdd466449693b6d627e50e2",
            ["hand_key_use"] = "01577f7ffe97b8e4d91e80551ee5ad43",
            ["hand_drop_stuff"] = "f0f710241332e1b44b92558c8854f719",
            ["hand_faceshield_off"] = "d69ffc0685dd6d541aceb05e5cd4011a",
            ["hand_faceshield_on"] = "618103670aaf3104793ce018fd3a1490",
            ["hand_slap_forward"] = "43978e2c579084f41bf013a54aa82ddd",
            ["OPEN_PUSH_RIGHT_HINGE"] = "24b70f869b5848f449ef39869e2cbebf",
            ["OPEN_PUSH_LEFT_HINGE"] = "7051a33c3f945bf428b9471bdfd3e4e7",
            ["OPEN_PULL_LEFT_HINGE"] = "1d656d1ad3485cf438fa13ea7a522e7a",
            ["OPEN_PULL_RIGHT_HINGE"] = "d80524d7cbfa13f4ca51650602391b53",
            ["pull_hinge_right"] = "dbe2fc508c9a8504294bdfaea90d52a3",
            ["pull_hinge_left"] = "c7ef54c05e7b7d543ba739accac44f7a",
            ["push_hinge_left"] = "cbf7362623d7d8e43a71221e11889ac8",
            ["push_hinge_right"] = "ef5988755f209484ba55722747d6af0d"
        };
        private static readonly string[] OriginalGestureClips =
        {
            "d0d12327826bf10429efc885adce9c4c", "c92d4a7beeb16c94db4ed396a4b39acc",
            "52c6b5112f031e447b750bee7c88e5c6", "11030c3da37700c469f405297f37f50a",
            "ecc9009c8edfb114b9d0224e1ab3880f", "43c31682dafa38944adfb44971b88c1d",
            "63b5783ae856193428325e536ae75601"
        };

        private sealed class MotionEdit
        {
            internal AnimatorState State = null!;
            internal Motion Motion = null!;
        }

        private sealed class GestureEdit
        {
            internal BlendTree Tree = null!;
            internal ChildMotion[] Children = Array.Empty<ChildMotion>();
        }

        internal static void Restore(string dumpRoot, List<string> log)
        {
            RestoreBlendOptions(dumpRoot, log);
            RestoreSharedHandMotions(dumpRoot, log);
        }

        private static void RestoreBlendOptions(string dumpRoot, List<string> log)
        {
            const string standardPath = "Assets/Content/Audio/BlendOptions/Standart.asset";
            var standard = AssetDatabase.LoadMainAssetAtPath(standardPath);
            var soundBanks = AssetDatabase.GetAllAssetPaths()
                .Where(path => path.StartsWith(dumpRoot + "/", StringComparison.Ordinal)
                    && path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                .Select(path => (path, asset: AssetDatabase.LoadMainAssetAtPath(path)))
                .Where(item => item.asset && item.asset.GetType().Name == "SoundBank").ToArray();
            if (soundBanks.Length == 0) { return; }
            if (!standard || standard.GetType().Name != "DistanceBlendOptions")
            {
                log.Add("SOUND BANK REVIEW: SDK Standart DistanceBlendOptions is unavailable at " + standardPath);
                return;
            }

            var repaired = 0;
            foreach (var (path, asset) in soundBanks)
            {
                var serialized = new SerializedObject(asset);
                var blend = serialized.FindProperty("BlendOptions");
                if (blend == null || blend.propertyType != SerializedPropertyType.ObjectReference)
                {
                    log.Add("SOUND BANK REVIEW: no BlendOptions field on " + path);
                    continue;
                }
                if (blend.objectReferenceValue) { continue; }
                var backup = Backup(path);
                try
                {
                    Undo.RecordObject(asset, "Restore standard audio blend options");
                    blend.objectReferenceValue = standard;
                    serialized.ApplyModifiedProperties();
                    EditorUtility.SetDirty(asset);
                    repaired++;
                    log.Add("SOUND BANK: restored Standart BlendOptions for " + path + " (backup " + backup + ")");
                }
                catch
                {
                    RestoreBackup(path, backup);
                    throw;
                }
            }
            if (repaired > 0) { AssetDatabase.SaveAssets(); }
            log.Add("SOUND BANK: restored " + repaired + " missing SDK BlendOptions reference(s).");
        }

        private static void RestoreSharedHandMotions(string dumpRoot, List<string> log)
        {
            var controllerPaths = AssetDatabase.FindAssets("t:AnimatorController", new[] { dumpRoot })
                .Select(AssetDatabase.GUIDToAssetPath).Where(path => path.EndsWith(".controller", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (controllerPaths.Length == 0) { return; }
            const string sdkRoot = "Assets/Content/Weapons";
            var sdkClips = AssetDatabase.FindAssets("t:AnimationClip", new[] { sdkRoot })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
                .Select(path => (path, clip: AssetDatabase.LoadAssetAtPath<AnimationClip>(path)))
                .Where(item => item.clip)
                .ToArray();
            if (sdkClips.Length == 0)
            {
                log.Add("LEFT HAND REVIEW: no SDK shared weapon animations were found under " + sdkRoot);
                return;
            }

            AnimationClip? Match(string name)
            {
                if (OriginalSdkClips.TryGetValue(name, out var guid))
                {
                    var exact = AssetDatabase.LoadAssetAtPath<AnimationClip>(AssetDatabase.GUIDToAssetPath(guid));
                    if (exact && sdkClips.Any(item => item.clip == exact)) { return exact; }
                }
                var matches = sdkClips.Where(item => item.clip.name.Equals(name, StringComparison.Ordinal)).ToArray();
                var preferred = matches.Where(item => item.path.Contains("/additional_hands/Anims/", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (preferred.Length == 1) { return preferred[0].clip; }
                return matches.Length == 1 ? matches[0].clip : null;
            }

            for (var controllerIndex = 0; controllerIndex < controllerPaths.Length; controllerIndex++)
            {
                var path = controllerPaths[controllerIndex];
                var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
                if (!controller) { continue; }
                var states = controller.layers.SelectMany(layer => States(layer.stateMachine)).Distinct().ToArray();
                var edits = new List<MotionEdit>();
                var gestures = new List<GestureEdit>();
                foreach (var state in states)
                {
                    if (SharedStates.Contains(state.name))
                    {
                        var sdk = Match(state.name);
                        if (!sdk) { log.Add("LEFT HAND REVIEW: no unique SDK clip for state " + state.name + " in " + path); }
                        else if (state.motion != sdk) { edits.Add(new MotionEdit { State = state, Motion = sdk }); }
                    }
                    if (state.motion is not BlendTree tree || tree.blendParameter != "GestureIndex") { continue; }
                    var children = tree.children;
                    if (children.Length != 7)
                    {
                        log.Add("GESTURE REVIEW: GestureIndex tree has " + children.Length + " children in " + path);
                        continue;
                    }
                    var replacement = new ChildMotion[children.Length];
                    var complete = true;
                    for (var i = 0; i < children.Length; i++)
                    {
                        var prefix = "gestures_0" + i;
                        var exact = AssetDatabase.LoadAssetAtPath<AnimationClip>(AssetDatabase.GUIDToAssetPath(OriginalGestureClips[i]));
                        var candidates = sdkClips.Where(item => item.path.Contains("/additional_hands/Anims/", StringComparison.OrdinalIgnoreCase)
                            && item.clip.name.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
                        var clip = exact && sdkClips.Any(item => item.clip == exact) ? exact
                            : candidates.Length == 1 ? candidates[0].clip : null;
                        if (!clip) { complete = false; break; }
                        replacement[i] = children[i];
                        replacement[i].motion = clip;
                    }
                    if (complete && children.Where((child, index) => child.motion != replacement[index].motion).Any())
                    {
                        gestures.Add(new GestureEdit { Tree = tree, Children = replacement });
                    }
                    else if (!complete) { log.Add("GESTURE REVIEW: no unique SDK gesture set for " + path); }
                }
                if (edits.Count == 0 && gestures.Count == 0) { continue; }
                var backup = Backup(path);
                try
                {
                    foreach (var edit in edits)
                    {
                        Undo.RecordObject(edit.State, "Restore shared hand animation");
                        edit.State.motion = edit.Motion;
                        EditorUtility.SetDirty(edit.State);
                    }
                    foreach (var edit in gestures)
                    {
                        Undo.RecordObject(edit.Tree, "Restore SDK gestures");
                        edit.Tree.children = edit.Children;
                        EditorUtility.SetDirty(edit.Tree);
                    }
                    AssetDatabase.SaveAssets();
                    log.Add("LEFT HAND: restored " + edits.Count + " shared states and " + gestures.Count + " gesture trees in " + path + " (backup " + backup + ")");
                }
                catch
                {
                    RestoreBackup(path, backup);
                    throw;
                }
            }
        }

        private static IEnumerable<AnimatorState> States(AnimatorStateMachine machine)
        {
            foreach (var child in machine.states) { yield return child.state; }
            foreach (var child in machine.stateMachines)
            {
                foreach (var state in States(child.stateMachine)) { yield return state; }
            }
        }

        private static string Backup(string assetPath)
        {
            var project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var original = Path.Combine(project, assetPath.Replace('/', Path.DirectorySeparatorChar));
            var backup = Path.Combine(project, "Library", "BundleDumper", "SharedRepairBackups",
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8],
                assetPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(original, backup);
            if (File.Exists(original + ".meta")) { File.Copy(original + ".meta", backup + ".meta"); }
            return backup;
        }

        private static void RestoreBackup(string assetPath, string backup)
        {
            var project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            File.Copy(backup, Path.Combine(project, assetPath.Replace('/', Path.DirectorySeparatorChar)), true);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
        }
    }
}

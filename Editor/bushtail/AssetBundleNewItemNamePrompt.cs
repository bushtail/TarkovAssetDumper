#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Editor.bushtail
{
    /// <summary>Collects distinct names before any new-item files or bundle labels are written.</summary>
    internal sealed class AssetBundleNewItemNamePrompt : EditorWindow
    {
        internal sealed class NameInfo
        {
            internal string DisplayName = "";
            internal string Slug = "";
        }

        private string[] inputs = Array.Empty<string>();
        private string[] names = Array.Empty<string>();
        private Action<IReadOnlyDictionary<string, NameInfo>?>? completed;
        private Vector2 scroll;

        internal static void Open(string[] paths, Action<IReadOnlyDictionary<string, NameInfo>?> onComplete)
        {
            var window = CreateInstance<AssetBundleNewItemNamePrompt>();
            window.titleContent = new GUIContent("Name New Item");
            window.minSize = new Vector2(490, 260);
            window.inputs = AssetBundleBatch.Normalize(paths);
            window.names = new string[window.inputs.Length];
            window.completed = onComplete;
            window.ShowUtility();
            window.Focus();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Create a new item", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Give each new weapon or item a distinct name. The dumper uses it for a new folder and bundle key. Original model, bone, clip, and asset address names stay intact for animation and controller matching.", MessageType.Info);
            scroll = EditorGUILayout.BeginScrollView(scroll);
            for (var i = 0; i < inputs.Length; i++)
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.LabelField(Path.GetFileName(inputs[i]), EditorStyles.boldLabel);
                names[i] = EditorGUILayout.TextField("New item name", names[i] ?? "");
                var slug = Slug(names[i]);
                EditorGUILayout.LabelField("Bundle key", string.IsNullOrEmpty(slug) ? "Enter a name" : slug + ".bundle");
            }
            EditorGUILayout.EndScrollView();

            var problem = Validate();
            if (!string.IsNullOrEmpty(problem)) { EditorGUILayout.HelpBox(problem, MessageType.Warning); }
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!string.IsNullOrEmpty(problem)))
                {
                    if (GUILayout.Button("Create new item", GUILayout.Height(28)))
                    {
                        var selected = new Dictionary<string, NameInfo>(StringComparer.OrdinalIgnoreCase);
                        for (var i = 0; i < inputs.Length; i++)
                        {
                            selected[inputs[i]] = new NameInfo { DisplayName = names[i].Trim(), Slug = Slug(names[i]) };
                        }
                        Finish(selected);
                    }
                }
                if (GUILayout.Button("Dump original names", GUILayout.Height(28))) { Finish(null); }
                if (GUILayout.Button("Cancel", GUILayout.Height(28))) { Close(); }
            }
        }

        private string? Validate()
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var labels = new HashSet<string>(AssetDatabase.GetAllAssetBundleNames(), StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < inputs.Length; i++)
            {
                var slug = Slug(names[i]);
                if (string.IsNullOrEmpty(slug)) { return "Enter a name for every new item."; }
                if (slug.Length > 64) { return "Keep each bundle name to 64 characters or fewer."; }
                if (slug.Equals(Path.GetFileNameWithoutExtension(inputs[i]), StringComparison.OrdinalIgnoreCase))
                {
                    return "Choose a new name distinct from the source bundle.";
                }
                if (!used.Add(slug)) { return "Two selected items have the same bundle name."; }
                if (Directory.Exists(Path.Combine(project, "Assets", "BundleDumps", slug))
                    || Directory.Exists(Path.Combine(project, "AssetBundles", "Dumps", slug))
                    || labels.Contains(slug + ".bundle"))
                {
                    return "The name '" + slug + "' already exists in this SDK. Choose another name to avoid overwriting an item.";
                }
            }
            return null;
        }

        internal static string Slug(string? name)
        {
            var value = new StringBuilder();
            var separator = false;
            foreach (var character in (name ?? "").ToLowerInvariant())
            {
                if ((character >= 'a' && character <= 'z') || (character >= '0' && character <= '9'))
                {
                    if (separator && value.Length > 0) { value.Append('_'); }
                    value.Append(character);
                    separator = false;
                }
                else { separator = true; }
            }
            return value.ToString();
        }

        private void Finish(IReadOnlyDictionary<string, NameInfo>? result)
        {
            var callback = completed;
            completed = null;
            Close();
            callback?.Invoke(result);
        }
    }
}

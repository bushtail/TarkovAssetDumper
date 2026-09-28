#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;
using Process = System.Diagnostics.Process;

namespace Editor.bushtail
{
    // Extract serialized files before Unity sees them. Loading a bundle into Unity first
    // discards unresolvable MonoBehaviours; no later m_Script reassignment can recover them.
    public sealed class AssetBundleDumper : EditorWindow
    {
        [SerializeField]
        private string bundlePath = "";
        [SerializeField]
        private List<string> bundlePaths = new();
        private Vector2 bundleScroll;
        [SerializeField]
        private string ripperPath = "";
        [SerializeField]
        private string assetStudioPath = "";
        [SerializeField]
        private string dependencyFolder = "";
        [SerializeField]
        private bool ignoreDependencies;
        [SerializeField]
        private bool assignPrefabBundleNames;
        [SerializeField]
        private bool singleBundleLabels;
        [SerializeField]
        private bool buildAfterDump = true;
        [SerializeField]
        private bool useDependencyImpostors;
        [SerializeField]
        private bool useFallbackShader = true;
        [SerializeField]
        private string fallbackShaderName = "p0/Reflective/Bumped Specular SMap";
        private bool busy;
        private Vector2 scroll;
        private string status = "Choose one or more bundles to export into Assets/BundleDumps.";
        private readonly List<string> report = new();
        private static readonly Regex GuidToken = new(@"\bguid: ([0-9a-fA-F]{32})", RegexOptions.Compiled);
        private static readonly Regex Reference = new(@"\{fileID: (-?\d+), guid: ([0-9a-fA-F]{32}), type: \d+\}", RegexOptions.Compiled);
        private static readonly Regex ScriptRef = new(@"(?m)^  m_Script: (\{[^\r\n]+\})", RegexOptions.Compiled);
        private static readonly Regex ShaderRef = new(@"(?m)^  m_Shader: (\{[^\r\n]+\})", RegexOptions.Compiled);
        private static readonly UTF8Encoding Utf8 = new(false);
        [MenuItem("Custom Windows/bushtail/Dump AssetBundle to SDK Format")]
        public static void ShowWindow() => GetWindow<AssetBundleDumper>("AssetBundle Dumper").Show();

        [MenuItem("Assets/bushtail/Sort Existing Dump Dependencies", false, 2002)]
        public static void SortSelectedExistingDump()
        {
            var root = AssetDatabase.GetAssetPath(Selection.activeObject);
            var staging = root + "/Dependencies";
            if (!AssetDatabase.IsValidFolder(staging))
            {
                throw new InvalidOperationException("Select an older dump folder containing Dependencies.");
            }

            var roots = AssetDatabase.GetSubFolders(staging);
            var log = new List<string>();
            SortDependencyAssets(root, roots, out _, log);
            AssetDatabase.SaveAssets();
            log.Add("Removed " + AssetBundleExportFolders.PruneImportedEmptyFolders(root) + " empty leftover dump folders.");
            Validate(root);
            Debug.Log(string.Join("\n", log));
        }

        [MenuItem("Assets/bushtail/Sort Existing Dump Dependencies", true)]
        private static bool CanSortSelectedExistingDump()
        {
            var root = AssetDatabase.GetAssetPath(Selection.activeObject);
            return !string.IsNullOrEmpty(root) && AssetDatabase.IsValidFolder(root + "/Dependencies")
                && AssetDatabase.GetLabels(Selection.activeObject).Contains("BundleDumperExport");
        }
        private void OnEnable()
        {
            if (!string.IsNullOrWhiteSpace(bundlePath))
            {
                AddBundles(new[] { bundlePath });
                bundlePath = ""; // Migrate an existing single-file window once.
            }

            var environmentPath = EnvironmentRipperPath();
            if (!string.IsNullOrEmpty(environmentPath))
            {
                ripperPath = environmentPath;
            }

            if (string.IsNullOrEmpty(ripperPath))
            {
                ripperPath = EditorPrefs.GetString("bushtail.AssetRipperPath", "");
                var sibling = Path.GetFullPath(Path.Combine(Application.dataPath, "../../Asset Ripper/AssetRipper.GUI.Free.exe"));
                if (!File.Exists(ripperPath) && File.Exists(sibling))
                {
                    ripperPath = sibling;
                }
            }

            if (string.IsNullOrWhiteSpace(assetStudioPath))
            {
                assetStudioPath = Environment.GetEnvironmentVariable("ASSETSTUDIOCLI_PATH")
                    ?? EditorPrefs.GetString("bushtail.AssetStudioModCLIPath", "");
                if (string.IsNullOrWhiteSpace(assetStudioPath))
                {
                    var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "bushtail", "AssetStudioModCLI-weapon-dumper", "AssetStudioModCLI.exe");
                    if (File.Exists(installed)) { assetStudioPath = installed; }
                }
            }
        }

        private static string EnvironmentRipperPath()
        {
            // Read the persisted Windows value as well: an already-open Unity editor
            // does not inherit user environment changes made after it was launched.
            var value = Environment.GetEnvironmentVariable("ASSETRIPPER_PATH");
            if (string.IsNullOrWhiteSpace(value))
            {
                value = Environment.GetEnvironmentVariable("ASSETRIPPER_PATH", EnvironmentVariableTarget.User);
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            value = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
            return Directory.Exists(value) ? Path.Combine(value, "AssetRipper.GUI.Free.exe") : value;
        }

        private void OnGUI()
        {
            using (new EditorGUI.DisabledScope(busy))
            {
                BundleSelection();
                ignoreDependencies = EditorGUILayout.ToggleLeft(new GUIContent("Ignore dependencies", "Skip dependency extraction and impostor preparation. Missing references still fail validation."), ignoreDependencies);
                using (new EditorGUI.DisabledScope(ignoreDependencies))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        dependencyFolder = EditorGUILayout.TextField(new GUIContent("Dependency search folder", "Leave blank to use the installation above SptRoot. The selected bundles' folders are also searched."), dependencyFolder);
                        if (GUILayout.Button("Browse", GUILayout.Width(65)))
                        {
                            var selected = EditorUtility.OpenFolderPanel("Dependency search folder", PickerDirectory(dependencyFolder), "");
                            if (!string.IsNullOrEmpty(selected))
                            {
                                dependencyFolder = selected;
                            }
                        }
                    }
                }

                PathField("AssetRipper executable", ref ripperPath, "exe");
                PathField("AssetStudioMod CLI", ref assetStudioPath, "exe");
                EditorGUILayout.LabelField("Output", "Assets/BundleDumps/<item name>");
                singleBundleLabels = EditorGUILayout.ToggleLeft(new GUIContent("Assign one AssetBundle label to exported assets",
                    "Group the selected assets and editable dependencies. Shared cubemaps, shaders and physics materials retain their original game bundle labels."), singleBundleLabels);
                buildAfterDump = EditorGUILayout.ToggleLeft(new GUIContent("Build and verify after extraction",
                    "Build the untouched ripped source with original references and check serialized pointers. The editable FBX stays outside the bundle."), buildAfterDump);
                using (new EditorGUI.DisabledScope(singleBundleLabels))
                {
                    assignPrefabBundleNames = EditorGUILayout.ToggleLeft("Split into per-prefab bundles", assignPrefabBundleNames);
                }

                using (new EditorGUI.DisabledScope(ignoreDependencies || singleBundleLabels))
                {
                    useDependencyImpostors = EditorGUILayout.ToggleLeft("Use original dependency textures through impostors", useDependencyImpostors);
                }
                useFallbackShader = EditorGUILayout.ToggleLeft("Use fallback for unresolved shaders", useFallbackShader);
                if (useFallbackShader)
                {
                    fallbackShaderName = EditorGUILayout.TextField("Fallback shader", fallbackShaderName);
                }

                using (new EditorGUI.DisabledScope(bundlePaths.Count == 0))
                {
                    if (GUILayout.Button($"Extract {bundlePaths.Count} Selected AssetBundle(s)"))
                    {
                        AssetBundleNewItemNamePrompt.Open(bundlePaths.ToArray(), names =>
                        {
                            if (this) { RunDump(names); }
                        });
                    }
                }


            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Log", EditorStyles.boldLabel);
                using (new EditorGUI.DisabledScope(busy))
                {
                    if (GUILayout.Button("Clear Log", GUILayout.Width(90)))
                    {
                        report.Clear();
                        scroll = Vector2.zero;
                        status = "Choose one or more bundles to export into Assets/BundleDumps.";
                    }
                }

            }

            EditorGUILayout.HelpBox(status, MessageType.Info);
            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var line in report)
            {
                EditorGUILayout.SelectableLabel(line, EditorStyles.wordWrappedLabel, GUILayout.MinHeight(20));
            }

            EditorGUILayout.EndScrollView();
        }

        private void AddBundles(IEnumerable<string> paths)
        {
            try
            {
                bundlePaths = AssetBundleBatch.Normalize(bundlePaths.Concat(paths)).ToList();
            }
            catch (Exception e)
            {
                status = "Could not add selection: " + e.Message;
            }
        }

        private void BundleSelection()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("AssetBundles", bundlePaths.Count + " selected");
                if (GUILayout.Button("Add Files", GUILayout.Width(90)))
                {
                    try
                    {
                        AddBundles(AssetBundleBatch.PickFiles(PickerDirectory(bundlePaths.LastOrDefault())));
                    }
                    catch (Exception e)
                    {
                        status = e.Message;
                    }
                }

                if (GUILayout.Button("Clear", GUILayout.Width(50)))
                {
                    bundlePaths.Clear();
                }
            }

            var drop = GUILayoutUtility.GetRect(0, 38, GUILayout.ExpandWidth(true));
            GUI.Box(drop, "Drag multiple bundle files here, or use Ctrl/Shift in Add Files");
            var ev = Event.current;
            if (!busy && drop.Contains(ev.mousePosition) && (ev.type == EventType.DragUpdated || ev.type == EventType.DragPerform))
            {
                var files = DragAndDrop.paths.Where(File.Exists).ToArray();
                DragAndDrop.visualMode = files.Length > 0 ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
                if (ev.type == EventType.DragPerform && files.Length > 0)
                {
                    DragAndDrop.AcceptDrag();
                    AddBundles(files);
                }

                ev.Use();
            }

            if (bundlePaths.Count == 0)
            {
                return;
            }

            bundleScroll = EditorGUILayout.BeginScrollView(bundleScroll, GUILayout.Height(Math.Min(150, bundlePaths.Count * 24 + 8)));
            for (var i = 0; i < bundlePaths.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(new GUIContent(Path.GetFileName(bundlePaths[i]), bundlePaths[i]));
                    if (GUILayout.Button("Remove", GUILayout.Width(65)))
                    {
                        bundlePaths.RemoveAt(i);
                        i--;
                    }
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private static void PathField(string label, ref string value, string extension)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                value = EditorGUILayout.TextField(label, value);
                if (GUILayout.Button("Browse", GUILayout.Width(65)))
                {
                    var selected = EditorUtility.OpenFilePanel(label, PickerDirectory(value), extension);
                    if (!string.IsNullOrEmpty(selected))
                    {
                        value = selected;
                    }
                }
            }
        }

        private static string NormalizeInputPath(string? value) => string.IsNullOrWhiteSpace(value) ? "" : Environment.ExpandEnvironmentVariables(value!.Trim().Trim('"'));
        private static string PickerDirectory(string? value)
        {
            try
            {
                var path = NormalizeInputPath(value);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    if (Directory.Exists(path))
                    {
                        return Path.GetFullPath(path);
                    }

                    var directory = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                    {
                        return Path.GetFullPath(directory);
                    }
                }
            }
            catch (ArgumentException)
            {
            }
            catch (NotSupportedException)
            {
            }
            catch (IOException)
            {
            }

            return Application.dataPath;
        }

        private async void RunDump(IReadOnlyDictionary<string, AssetBundleNewItemNamePrompt.NameInfo>? newItemNames)
        {
            var guideRoots = new List<string>();
            try
            {
                if (busy)
                {
                    return;
                }

                busy = true;
                report.Clear();
                var reloadLocked = false;
                try
                {
                    EditorApplication.LockReloadAssemblies();
                    reloadLocked = true;
                    var technicalNames = newItemNames?.ToDictionary(entry => entry.Key, entry => entry.Value.Slug, StringComparer.OrdinalIgnoreCase);
                    var results = await AssetBundleBatch.DumpAsync(bundlePaths.ToArray(), ripperPath, "Assets/BundleDumps", assignPrefabBundleNames, useFallbackShader ? fallbackShaderName : null, report, message =>
                    {
                        status = message;
                        Repaint();
                    }, dependencyFolder, useDependencyImpostors, ignoreDependencies, singleBundleLabels, assetStudioPath, buildAfterDump, technicalNames);
                    var successes = results.Where(r => r.Succeeded).ToArray();
                    status = $"Finished: {successes.Length} exported and validated, {results.Count - successes.Length} failed. See the report below.";
                    Selection.objects = successes.Select(r => AssetDatabase.LoadAssetAtPath<DefaultAsset>(r.Output)).Where(a => a).Cast<Object>().ToArray();
                    if (newItemNames != null)
                    {
                        foreach (var result in successes)
                        {
                            if (!newItemNames.TryGetValue(result.Input, out var name)) { continue; }
                            AssetBundleNewItemGuide.SaveDisplayName(result.Output, name.DisplayName);
                            guideRoots.Add(result.Output);
                        }
                    }
                }
                catch (Exception ex)
                {
                    status = "Dump failed: " + ex.Message;
                    report.Add(ex.ToString());
                    Debug.LogException(ex);
                }
                finally
                {
                    try
                    {
                        if (reloadLocked)
                        {
                            EditorApplication.UnlockReloadAssemblies();
                        }
                    }
                    finally
                    {
                        busy = false;
                        Repaint();
                        if (!Application.isBatchMode && guideRoots.Count > 0)
                        {
                            AssetBundleNewItemGuide.QueueOpen(guideRoots);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        public static async Task<string> DumpAsync(string input, string executable, string parent, bool splitPrefabs, string? fallback, List<string> log, Action<string>? progress = null, string? dependencySearchFolder = null, bool useDependencyImpostors = true, bool ignoreDependencies = false, bool singleBundleLabels = false, string? assetStudioExecutable = null, bool buildAfterDump = true, string? newBundleName = null)
        {
            if (singleBundleLabels) { useDependencyImpostors = false; }

            if (newBundleName != null && !Regex.IsMatch(newBundleName, "^[a-z0-9_]{1,64}$"))
            {
                throw new ArgumentException("A new item bundle name must be 1-64 lowercase letters, digits, or underscores.");
            }

            input = NormalizeInputPath(input);
            executable = NormalizeInputPath(executable);
            assetStudioExecutable = NormalizeInputPath(assetStudioExecutable);
            dependencySearchFolder = NormalizeInputPath(dependencySearchFolder);
            
            if (string.IsNullOrWhiteSpace(executable))
            {
                executable = EnvironmentRipperPath();
            }

            if (!File.Exists(input) || !File.Exists(executable))
            {
                throw new FileNotFoundException("Choose an existing bundle and AssetRipper executable.");
            }
            if (!string.IsNullOrEmpty(assetStudioExecutable) && !File.Exists(assetStudioExecutable))
            {
                throw new FileNotFoundException("AssetStudioModCLI executable was not found.", assetStudioExecutable);
            }

            parent = parent.Replace('\\', '/').TrimEnd('/');
            if (parent != "Assets" && !parent.StartsWith("Assets/", StringComparison.Ordinal))
            {
                throw new ArgumentException("Output must be inside Assets.");
            }

            var project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            if (newBundleName != null && (Directory.Exists(Path.Combine(project, parent, newBundleName))
                || Directory.Exists(Path.Combine(project, "AssetBundles", "Dumps", newBundleName))
                || AssetDatabase.GetAllAssetBundleNames().Contains(newBundleName + ".bundle", StringComparer.OrdinalIgnoreCase)))
            {
                throw new IOException("An item folder, output, or bundle label with this name already exists: " + newBundleName);
            }
            var absoluteParent = Path.GetFullPath(Path.Combine(project, parent));
            if (!absoluteParent.StartsWith(Application.dataPath.Replace('/', Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !string.Equals(absoluteParent, Application.dataPath.Replace('/', Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Output must stay inside Assets.");
            }

            string run;
            do
            {
                run = Path.Combine(project, "Library", "BundleDumper", Guid.NewGuid().ToString("N").Substring(0, 8));
            }
            while (Directory.Exists(run));
            Directory.CreateDirectory(run);
            log.Add("Staging and extraction log: " + run);
            EditorPrefs.SetString("bushtail.AssetRipperPath", executable);
            progress?.Invoke("Finding bundle dependencies");
            var dependencyLog = new List<string>();
            var dependencyProgress = new Progress<string>(message => progress?.Invoke(message));
            var bundles = await Task.Run(() => AssetBundleDependencies.Resolve(input, dependencySearchFolder, dependencyLog, dependencyProgress, Path.Combine(project, "Library", "BundleDumper", "bundle-index.bin"), Path.Combine(project, "AssetBundles"), !ignoreDependencies));
            var originalSources = bundles.Select(b => b.Path).ToArray();
            var previousRoots = AssetDatabase.FindAssets("l:BundleDumperExport").Select(AssetDatabase.GUIDToAssetPath).ToArray();
            var sdkShaders = new HashSet<string>(AssetDatabase.FindAssets("t:Shader").Select(AssetDatabase.GUIDToAssetPath).Where(p => !previousRoots.Any(root => p.StartsWith(root + "/", StringComparison.Ordinal))).Select(AssetDatabase.LoadAssetAtPath<Shader>).Where(s => s).GroupBy(s => s.name).Where(g => g.Count() == 1).Select(g => g.Key), StringComparer.Ordinal);
            progress?.Invoke("Filtering dependencies to referenced objects");
            var inputs = ignoreDependencies ? new List<string> { input } : await Task.Run(() => AssetBundlePruner.WriteInputs(bundles, Path.Combine(run, "Inputs"), sdkShaders, dependencyLog, dependencyProgress));
            log.AddRange(dependencyLog);
            progress?.Invoke("Extracting " + bundles.Count + " bundle(s) with AssetRipper");
            await Extract(inputs, executable, run);
            string? assetStudioOutput = null;
            if (!string.IsNullOrEmpty(assetStudioExecutable))
            {
                EditorPrefs.SetString("bushtail.AssetStudioModCLIPath", assetStudioExecutable);
                try
                {
                    progress?.Invoke("Exporting source animations and audio with AssetStudioModCLI");
                    assetStudioOutput = await AssetStudioModBridge.Export(assetStudioExecutable, inputs, run, log);
                }
                catch (Exception ex)
                {
                    log.Add("ASSETSTUDIO FAILED: " + ex);
                }
            }
            var assets = Directory.GetDirectories(FileSystemPath(run), "Assets", SearchOption.AllDirectories).SingleOrDefault(p => !p.Contains(Path.DirectorySeparatorChar + "Assets" + Path.DirectorySeparatorChar));
            if (assets == null)
            {
                throw new InvalidOperationException("AssetRipper produced no Assets directory. See " + run);
            }

            progress?.Invoke("Compacting bundle staging folders");
            var payloads = StageBundleFolders(assets, bundles, run);
            if (assetStudioOutput != null)
            {
                AssetStudioModBridge.ReplaceAudio(Path.Combine(run, "Payloads"), Path.Combine(assetStudioOutput, "Audio"), log);
            }
            progress?.Invoke("Checking exported WAV headers");
            foreach (var wave in Directory.GetFiles(FileSystemPath(Path.Combine(run, "Payloads")), "*.wav", SearchOption.AllDirectories))
            {
                if (AssetBundleAudioRepair.RepairFile(wave))
                {
                    log.Add("AUDIO: repaired unfinished WAV header: " + wave);
                }
            }

            progress?.Invoke("Remapping SDK scripts, shaders and asset GUIDs");
            PrepareExport(Path.Combine(run, "Payloads"), fallback, log);
            var emptyFolders = AssetBundleExportFolders.PruneEmptyFolders(Path.Combine(run, "Payloads"));
            log.Add("Removed " + emptyFolders + " empty export folders (metadata does not count as content).");
            if (!Directory.Exists(FileSystemPath(payloads[0])))
            {
                throw new InvalidOperationException("The selected bundle has no exportable files after SDK reference remapping.");
            }

            Directory.CreateDirectory(absoluteParent);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            var destinations = new List<string>();
            var importedBundles = new List<AssetBundleDependencies.BundleInfo>();
            var itemName = newBundleName ?? ItemFolderName(payloads[0], bundles[0].Path);
            var itemRoot = AssetDatabase.GenerateUniqueAssetPath(parent + "/" + itemName);
            var dependencyRoot = "";
            AssetDatabase.DisallowAutoRefresh();
            try
            {
                for (var i = 0; i < payloads.Count; i++)
                {
                    if (!Directory.Exists(FileSystemPath(payloads[i])))
                    {
                        continue;
                    }

                    var name = i == 0 ? itemName : ItemFolderName(payloads[i], bundles[i].Path);
                    var container = i == 0 ? parent : dependencyRoot;
                    var destination = i == 0 ? itemRoot : container + "/" + name;
                    var suffix = 1;
                    while (Directory.Exists(FileSystemPath(Path.Combine(project, destination))) || File.Exists(FileSystemPath(Path.Combine(project, destination))) || File.Exists(FileSystemPath(Path.Combine(project, destination) + ".meta")))
                    {
                        destination = container + "/" + name + " " + suffix++;
                    }

                    Directory.CreateDirectory(FileSystemPath(Path.Combine(project, container)));
                    Directory.Move(FileSystemPath(payloads[i]), FileSystemPath(Path.Combine(project, destination)));
                    if (File.Exists(FileSystemPath(payloads[i] + ".meta")))
                    {
                        File.Move(FileSystemPath(payloads[i] + ".meta"), FileSystemPath(Path.Combine(project, destination) + ".meta"));
                    }

                    destinations.Add(destination);
                    importedBundles.Add(bundles[i]);
                    log.Add("BUNDLE: " + bundles[i].Path + " -> " + destination);

                    if (i != 0) { continue; }

                    itemRoot = destination;
                    dependencyRoot = itemRoot + "/Dependencies";
                    var dependencySuffix = 1;
                        
                    while (Directory.Exists(FileSystemPath(Path.Combine(project, dependencyRoot))) || File.Exists(FileSystemPath(Path.Combine(project, dependencyRoot))) || File.Exists(FileSystemPath(Path.Combine(project, dependencyRoot) + ".meta")))
                    {
                        dependencyRoot = itemRoot + "/Dependencies " + dependencySuffix++;
                    }
                }
            }
            finally
            {
                AssetDatabase.AllowAutoRefresh();
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            progress?.Invoke("Validating imported assets");
            AssetDatabase.SetLabels(AssetDatabase.LoadAssetAtPath<DefaultAsset>(itemRoot), new[] { "BundleDumperExport" });
            Validate(itemRoot);
            if (!useDependencyImpostors && !ignoreDependencies)
            {
                PruneUnreferencedDependencies(itemRoot, destinations.Skip(1).ToArray(), log);
            }
            var sharedName = newBundleName ?? bundles[0].Name;
            if (sharedName.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase))
            {
                sharedName = sharedName[..^".bundle".Length];
            }

            foreach (var path in AssetDatabase.GetAllAssetPaths().Where(p => destinations.Any(d => p == d || p.StartsWith(d + "/", StringComparison.Ordinal))))
            {
                var importer = AssetImporter.GetAtPath(path);
                if (!importer)
                {
                    continue;
                }

                if (singleBundleLabels)
                {
                    var asset = AssetDatabase.LoadMainAssetAtPath(path);
                    var include = asset && asset is not DefaultAsset && asset is not MonoScript
                        && !path.StartsWith(itemRoot + "/ExportSupport/", StringComparison.Ordinal);
                    importer.SetAssetBundleNameAndVariant(include ? sharedName : "", include ? "bundle" : "");
                }
                else if (splitPrefabs)
                {
                    importer.SetAssetBundleNameAndVariant(path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(path).ToLowerInvariant() : "", path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) ? "bundle" : "");
                }
                else
                {
                    var isDependency = destinations.Skip(1).Any(d => path == d || path.StartsWith(d + "/", StringComparison.Ordinal));
                    var asset = AssetDatabase.LoadMainAssetAtPath(path);
                    var name = sharedName;
                    if (name.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase))
                    {
                        name = name[..^".bundle".Length];
                    }

                    var include = !isDependency && asset && asset is not DefaultAsset && asset is not MonoScript && !path.StartsWith(itemRoot + "/ExportSupport/", StringComparison.Ordinal);
                    importer.SetAssetBundleNameAndVariant(include ? name : "", include ? "bundle" : "");
                }
            }

            if (!ignoreDependencies)
            {
                AssetBundleDumpImpostors.Apply(destinations, importedBundles, Path.Combine(run, "Export", "AuxiliaryFiles", "path_id_map.json"), log, useDependencyImpostors);
                originalSources = AssetBundleDumpImpostors.EnforceRequired(itemRoot, originalSources, log);
            }
            if (singleBundleLabels)
            {
                log.Add("BUNDLE LABEL: editable source and dependency assets -> " + sharedName + ".bundle");
            }

            AssetBundleAnimatorMaskRepair.Restore(itemRoot, originalSources, log);
            progress?.Invoke("Sorting referenced assets by Unity type");
            var sortedDependencyRoots = SortDependencyAssets(itemRoot, destinations.Skip(1).ToArray(), out var sortedDependencyGuids, log);
            progress?.Invoke("Restoring shared weapon animations and audio options");
            AssetBundleWeaponSharedRepair.Restore(itemRoot, log);
            AssetDatabase.SaveAssets();
            log.Add("Removed " + AssetBundleExportFolders.PruneImportedEmptyFolders(itemRoot) + " empty leftover dump folders.");
            Validate(itemRoot);
            AssetBundleDumpBuilder.WriteSettings(itemRoot, importedBundles, sortedDependencyRoots, sortedDependencyGuids, originalSources,
                newBundleName == null ? null : newBundleName + ".bundle");
            progress?.Invoke("Exporting editable weapon FBX");
            AssetBundleWeaponFbx.ExportFromDump(itemRoot, log, assetStudioOutput);
            if (buildAfterDump)
            {
                progress?.Invoke("Building and verifying the unedited weapon bundle");
                try { log.Add("BUILD: " + AssetBundleDumpBuilder.Build(itemRoot)); }
                catch (Exception ex)
                {
                    log.Add("BUILD FAILED: " + ex);
                    await File.WriteAllLinesAsync(Path.Combine(run, "remap-report.txt"), log);
                    throw;
                }
            }
            log.Add("Validated imported scripts and persistent object references.");
            await File.WriteAllLinesAsync(Path.Combine(run, "remap-report.txt"), log);
            return destinations[0];
        }

        private static void PruneUnreferencedDependencies(string root, IReadOnlyList<string> dependencyRoots, List<string> log)
        {
            if (dependencyRoots.Count == 0) { return; }

            var source = AssetBundleDumpBuilder.SourceAssets(root, dependencyRoots);
            if (source.Length == 0)
            {
                throw new InvalidOperationException("No selected source assets remain in " + root);
            }

            var needed = new HashSet<string>(AssetDatabase.GetDependencies(source, true), StringComparer.Ordinal);
            var unused = AssetDatabase.GetAllAssetPaths().Where(p => !AssetDatabase.IsValidFolder(p) 
                                                                     && dependencyRoots.Any(d => p.StartsWith(d + "/", StringComparison.Ordinal)) 
                                                                     && !needed.Contains(p)).ToArray();
            
            foreach (var path in unused)
            {
                if (!AssetDatabase.DeleteAsset(path)) { throw new IOException("Could not remove unused dependency asset: " + path); }

                log.Add("PRUNED UNUSED DEPENDENCY: " + path);
            }
            if (unused.Length != 0)
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }

            var count = AssetDatabase.GetAllAssetPaths().Count(p => !AssetDatabase.IsValidFolder(p) && dependencyRoots.Any(d => p.StartsWith(d + "/", StringComparison.Ordinal)));
            log.Add("Dependency files needed by selected source assets: " + count + "; removed unused files: " + unused.Length + ".");
        }

        private static string[] SortDependencyAssets(string root, IReadOnlyList<string> dependencyRoots, out string[] dependencyGuids, List<string> log)
        {
            if (dependencyRoots.Count == 0) { dependencyGuids = Array.Empty<string>(); return Array.Empty<string>(); }
            var categories = new Dictionary<string, string>(StringComparer.Ordinal);
            var guids = new List<string>();
            var paths = AssetDatabase.GetAllAssetPaths().Where(p => !AssetDatabase.IsValidFolder(p) && dependencyRoots.Any(d => p.StartsWith(d + "/", StringComparison.Ordinal))).OrderBy(p => p, StringComparer.Ordinal).ToArray();
            foreach (var path in paths)
            {
                var type = AssetDatabase.GetMainAssetTypeAtPath(path);
                var category = type?.Name ?? "Other";
                if (!categories.TryGetValue(category, out var folder))
                {
                    folder = root + "/" + category;
                    if (!AssetDatabase.IsValidFolder(folder))
                    {
                        folder = AssetDatabase.GenerateUniqueAssetPath(folder);
                        var folderGuid = AssetDatabase.CreateFolder(root, Path.GetFileName(folder));
                        if (string.IsNullOrEmpty(folderGuid))
                        {
                            throw new IOException("Could not create dependency type folder: " + folder);
                        }
                    }
                    categories.Add(category, folder);
                }
                var guid = AssetDatabase.AssetPathToGUID(path);
                if (string.IsNullOrEmpty(guid))
                {
                    throw new InvalidDataException("Dependency asset has no GUID: " + path);
                }

                var target = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + Path.GetFileName(path));
                var error = AssetDatabase.MoveAsset(path, target);
                if (!string.IsNullOrEmpty(error))
                {
                    throw new IOException("Could not sort dependency asset " + path + ": " + error);
                }

                guids.Add(guid);
                log.Add("SORT DEPENDENCY: " + path + " -> " + target);
            }
            var oldParent = Path.GetDirectoryName(dependencyRoots[0])?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(oldParent) && AssetDatabase.IsValidFolder(oldParent)
                && !AssetDatabase.GetAllAssetPaths().Any(p => p.StartsWith(oldParent + "/", StringComparison.Ordinal)
                    && !AssetDatabase.IsValidFolder(p)) && !AssetDatabase.DeleteAsset(oldParent))
            {
                throw new IOException("Could not remove empty dependency staging folders: " + oldParent);
            }

            log.Add("Sorted " + paths.Length + " dependency files into " + categories.Count + " type folders.");
            dependencyGuids = guids.ToArray();
            return categories.Values.OrderBy(p => p, StringComparer.Ordinal).ToArray();
        }


        private static List<string> StageBundleFolders(string assets, List<AssetBundleDependencies.BundleInfo> bundles, string run)
        {
            var groupRoot = Path.GetFullPath(Path.Combine(assets, "AssetBundles"));
            var sources = bundles.Select(b =>
            {
                var name = b.Name.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase) ? b.Name.Substring(0, b.Name.Length - 7) : b.Name;
                var path = Path.GetFullPath(Path.Combine(groupRoot, name.Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(groupRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(FileSystemPath(path)))
                {
                    throw new InvalidOperationException("AssetRipper did not export the expected folder for bundle '" + b.Name + "': " + path);
                }

                return path;
            }).ToArray();
            if (sources.Distinct(StringComparer.OrdinalIgnoreCase).Count() != sources.Length)
            {
                throw new InvalidOperationException("Dependency bundles have duplicate internal bundle names; they cannot be exported separately.");
            }

            var payloadRoot = Path.Combine(run, "Payloads");
            Directory.CreateDirectory(payloadRoot);
            var payloads = Enumerable.Range(0, bundles.Count).Select(i => Path.Combine(payloadRoot, i.ToString())).ToList();
            
            foreach (var i in Enumerable.Range(0, sources.Length).OrderByDescending(i => sources[i].Length))
            {
                Directory.Move(FileSystemPath(sources[i]), FileSystemPath(payloads[i]));
                if (File.Exists(FileSystemPath(sources[i] + ".meta")))
                {
                    File.Move(FileSystemPath(sources[i] + ".meta"), FileSystemPath(payloads[i] + ".meta"));
                }
            }

            var scripts = Directory.GetDirectories(FileSystemPath(assets)).SingleOrDefault(p => string.Equals(Path.GetFileName(p), "Scripts", StringComparison.OrdinalIgnoreCase));
            if (scripts != null)
            {
                var target = FileSystemPath(Path.Combine(payloadRoot, "Scripts"));
                Directory.Move(scripts, target);
                if (File.Exists(scripts + ".meta"))
                {
                    File.Move(scripts + ".meta", target + ".meta");
                }
            }

            var support = Path.Combine(payloads[0], "ExportSupport");
            while (Directory.Exists(support))
            {
                support += "_";
            }

            Directory.Move(FileSystemPath(assets), FileSystemPath(support));
            return payloads;
        }

        private static string ItemFolderName(string assets, string bundle)
        {
            var prefabs = Directory.GetFiles(FileSystemPath(assets), "*.prefab", SearchOption.AllDirectories);
            var name = Path.GetFileNameWithoutExtension(prefabs.Length == 1 ? prefabs[0] : bundle);
            var invalid = Path.GetInvalidFileNameChars();
            name = new string (name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
            if (string.IsNullOrWhiteSpace(name))
            {
                name = "Item";
            }

            if (Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase))
            {
                name = "_" + name;
            }

            return name;
        }

        private static async Task Extract(IEnumerable<string> inputs, string executable, string run)
        {
            var tool = Path.Combine(Path.GetDirectoryName(run)!, "Tool");
            Directory.CreateDirectory(tool);
            var isolated = Path.Combine(tool, Path.GetFileName(executable));
            if (!File.Exists(isolated) || File.GetLastWriteTimeUtc(isolated) != File.GetLastWriteTimeUtc(executable))
            {
                File.Copy(executable, isolated, true);
            }

            var capstone = Path.Combine(Path.GetDirectoryName(executable)!, "capstone.dll");
            if (File.Exists(capstone))
            {
                File.Copy(capstone, Path.Combine(tool, "capstone.dll"), true);
            }

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            using var process = Process.Start(new System.Diagnostics.ProcessStartInfo(isolated, "--headless --port " + port + " --log-path \"" + Path.Combine(run, "AssetRipper.log") + "\"") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden, WorkingDirectory = run });
            if (process == null)
            {
                throw new InvalidOperationException("Could not start AssetRipper.");
            }

            try
            {
                using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
                client.BaseAddress = new Uri("http://127.0.0.1:" + port);
                client.Timeout = TimeSpan.FromHours(1);
                var ready = false;
                for (var attempt = 0; attempt < 120; attempt++)
                {
                    if (process.HasExited)
                    {
                        throw new InvalidOperationException("AssetRipper exited. See " + run);
                    }

                    try
                    {
                        using var response = await client.GetAsync("/");
                        ready = response.IsSuccessStatusCode;
                    }
                    catch (HttpRequestException)
                    {
                    }

                    if (ready)
                    {
                        break;
                    }

                    await Task.Delay(250);
                }

                if (!ready)
                {
                    throw new TimeoutException("AssetRipper did not start within 30 seconds.");
                }

                await Post(client, "/Settings/Update", new[] { Pair("ScriptContentLevel", "Level2"), Pair("ScriptExportMode", "Decompiled"), Pair("DefaultVersion", Application.unityVersion), Pair("ShaderExportMode", "Dummy"), Pair("BundledAssetsExportMode", "GroupByBundleName"), Pair("ImageExportFormat", "Png"), Pair("SpriteExportMode", "Yaml"), Pair("ExportUnreadableAssets", "true") });
                await Post(client, "/LoadFile", inputs.Select(input => Pair("Path", Path.GetFullPath(input))));
                await Post(client, "/Export/UnityProject", new[] { Pair("Path", Path.Combine(run, "Export")) });
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    await Task.Run(process.WaitForExit);
                }
            }
        }

        private static KeyValuePair<string, string> Pair(string key, string value) => new(key, value);
        private static async Task Post(HttpClient client, string url, IEnumerable<KeyValuePair<string, string>> values)
        {
            using var content = new FormUrlEncodedContent(values);
            using var response = await client.PostAsync(url, content);
            response.EnsureSuccessStatusCode();
        }

        private sealed class LocalReference
        {
            public string Guid = "";
            public long FileId;
            public string Name = "";
            public string Yaml => "{fileID: " + FileId + ", guid: " + Guid + ", type: 3}";
        }

        private static LocalReference Describe(Object value)
        {
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out var guid, out long id))
            {
                throw new InvalidOperationException("No persistent reference for " + value.name);
            }

            return new LocalReference
            {
                Guid = guid,
                FileId = id,
                Name = value.name
            };
        }

        public static void PrepareExport(string assets, string? fallbackName, List<string> log)
        {
            assets = FileSystemPath(assets);
            var scripts = new Dictionary<string, List<LocalReference>>(StringComparer.Ordinal);
            foreach (var script in MonoImporter.GetAllRuntimeMonoScripts())
            {
                var type = script.GetClass();
                if (type == null || !typeof(MonoBehaviour).IsAssignableFrom(type) && !typeof(ScriptableObject).IsAssignableFrom(type))
                {
                    continue;
                }

                foreach (var name in new[]
                {
                    script.name,
                    type.Name,
                    type.FullName!
                }.Distinct())
                {
                    if (!scripts.TryGetValue(name, out var list))
                    {
                        scripts[name] = list = new List<LocalReference>();
                    }

                    var reference = Describe(script);
                    if (!list.Any(r => r.Guid == reference.Guid && r.FileId == reference.FileId))
                    {
                        list.Add(reference);
                    }
                }
            }

            // Never mistake a previous dump's dummy shaders for original SDK shaders.
            var previousDumps = AssetDatabase.FindAssets("l:BundleDumperExport").Select(AssetDatabase.GUIDToAssetPath).ToArray();
            var shaders = AssetDatabase.FindAssets("t:Shader").Select(AssetDatabase.GUIDToAssetPath).Where(p => !previousDumps.Any(root => p.StartsWith(root + "/", StringComparison.Ordinal))).Select(AssetDatabase.LoadAssetAtPath<Shader>).Where(s => s).GroupBy(s => s.name).ToDictionary(g => g.Key, g => g.Select(Describe).ToList(), StringComparer.Ordinal);
            var replacement = new Dictionary<string, LocalReference>(StringComparer.OrdinalIgnoreCase);
            var replacedShaders = new List<string>();
            var guids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var files = Directory.GetFiles(assets, "*", SearchOption.AllDirectories);
            var scriptsFolder = Directory.GetDirectories(assets).SingleOrDefault(p => string.Equals(Path.GetFileName(p), "Scripts", StringComparison.OrdinalIgnoreCase));
            bool IsScriptOutput(string path) => scriptsFolder != null && (string.Equals(path, scriptsFolder, StringComparison.OrdinalIgnoreCase) || path.StartsWith(scriptsFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            var removed = new HashSet<string>(files.Where(IsScriptOutput), StringComparer.OrdinalIgnoreCase);
            var scriptNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var meta in files.Where(p => p.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)))
            {
                var match = GuidToken.Match(File.ReadAllText(meta));
                if (!match.Success)
                {
                    continue;
                }

                var guid = match.Groups[1].Value;
                var file = meta.Substring(0, meta.Length - 5);
                var relative = file.Substring(assets.Length + 1).Replace('\\', '/');
                LocalReference? local = null;
                if (IsScriptOutput(file) && file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    scriptNames[guid] = name;
                    if (scripts.TryGetValue(name, out var candidates) && candidates.Count == 1)
                    {
                        local = candidates[0];
                    }
                }
                else if (file.EndsWith(".shader", StringComparison.OrdinalIgnoreCase))
                {
                    var shaderName = Regex.Match(File.ReadAllText(file), "Shader\\s+\"([^\"]+)\"");
                    var name = shaderName.Success ? shaderName.Groups[1].Value : Path.GetFileNameWithoutExtension(file);
                    if (shaders.TryGetValue(name, out var candidates) && candidates.Count == 1)
                    {
                        local = candidates[0];
                    }
                }

                if (local != null)
                {
                    replacement[guid] = local;
                    if (file.EndsWith(".shader", StringComparison.OrdinalIgnoreCase))
                    {
                        replacedShaders.Add(file);
                    }

                    log.Add("REMAP: " + relative + " -> " + local.Name);
                }

                if (!IsScriptOutput(file))
                {
                    guids.Add(guid, Guid.NewGuid().ToString("N"));
                }
            }

            LocalReference? fallback = null;
            if (!string.IsNullOrEmpty(fallbackName) && shaders.TryGetValue(fallbackName!, out var fallbackMatches) && fallbackMatches.Count == 1)
            {
                fallback = fallbackMatches[0];
            }

            var stagedText = new Dictionary<string, string>();
            foreach (var file in files.Where(p => !p.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) && !removed.Contains(p)))
            {
                // Only parse Unity text serialization. Binary textures/audio are copied unchanged.
                using var reader = new StreamReader(file);
                var header = new char[5];
                if (reader.Read(header, 0, header.Length) != header.Length || new string (header) != "%YAML")
                {
                    continue;
                }

                var text = File.ReadAllText(file);
                foreach (Match script in ScriptRef.Matches(text))
                {
                    var guid = GuidToken.Match(script.Value);
                    if (!guid.Success || !replacement.ContainsKey(guid.Groups[1].Value))
                    {
                        throw new InvalidOperationException("No unique SDK script by name for " + (guid.Success && scriptNames.TryGetValue(guid.Groups[1].Value, out var name) ? name : script.Value) + " in " + file + ". Staged data was retained.");
                    }
                }

                text = ShaderRef.Replace(text, match =>
                {
                    var guid = GuidToken.Match(match.Value);
                    if (guid.Success && (replacement.ContainsKey(guid.Groups[1].Value) || guids.ContainsKey(guid.Groups[1].Value) || IsBuiltin(guid.Groups[1].Value)))
                    {
                        return match.Value;
                    }

                    if (fallback == null)
                    {
                        throw new InvalidOperationException("Unresolved shader in " + file + ". Supply its dependency bundle or select an SDK fallback.");
                    }

                    log.Add("WARNING: fallback shader " + fallback.Name + " used for " + file);
                    return "  m_Shader: " + fallback.Yaml;
                });
                text = Reference.Replace(text, match => replacement.TryGetValue(match.Groups[2].Value, out var local) ? local.Yaml : match.Value);
                // Reject unresolved references before import, including AssetRipper's missing-asset sentinel GUIDs.
                foreach (Match match in GuidToken.Matches(text))
                {
                    var guid = match.Groups[1].Value;
                    if (guids.ContainsKey(guid) || IsBuiltin(guid) || !string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(guid)))
                    {
                        continue;
                    }

                    throw new InvalidOperationException("Unresolved dependency GUID " + guid + " in " + file + ". The bundle requires an external dependency that was not exported.");
                }

                stagedText[file] = GuidToken.Replace(text, m => guids.TryGetValue(m.Groups[1].Value, out var fresh) ? "guid: " + fresh : m.Value);
            }

            // Commit only after the entire serialized graph passes preflight.
            foreach (var entry in stagedText)
            {
                File.WriteAllText(entry.Key, entry.Value, Utf8);
            }

            foreach (var shader in replacedShaders)
            {
                var excluded = Path.Combine(Path.GetDirectoryName(assets)!, "ExcludedShaders");
                Directory.CreateDirectory(excluded);
                var destination = Path.Combine(excluded, Guid.NewGuid().ToString("N") + ".shader");
                File.Move(shader, destination);
                File.Move(shader + ".meta", destination + ".meta");
            }

            if (scriptsFolder != null)
            {
                // Keep the script evidence in staging, outside the Assets tree that we import.
                var excluded = Path.Combine(Path.GetDirectoryName(assets)!, "ExcludedScripts-" + Guid.NewGuid().ToString("N"));
                Directory.Move(scriptsFolder, excluded);
                if (File.Exists(scriptsFolder + ".meta"))
                {
                    File.Move(scriptsFolder + ".meta", excluded + ".meta");
                }
            }

            foreach (var meta in files.Where(p => p.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) && File.Exists(p)))
            {
                var text = File.ReadAllText(meta);
                text = Reference.Replace(text, m => replacement.TryGetValue(m.Groups[2].Value, out var local) ? local.Yaml : m.Value);
                text = GuidToken.Replace(text, m => guids.TryGetValue(m.Groups[1].Value, out var fresh) ? "guid: " + fresh : m.Value);
                File.WriteAllText(meta, text, Utf8);
            }

            log.Add("Preserved serialized fields and local fileIDs; assigned fresh GUIDs for " + guids.Count + " exported assets/folders.");
        }

        // Unity's Windows Mono file APIs otherwise fail around MAX_PATH even when
        // AssetRipper (.NET) has successfully written the file. Keep this prefix out
        // of AssetDatabase paths, URLs and command-line arguments.
        public static string FileSystemPath(string path)
        {
            if (Path.DirectorySeparatorChar != '\\' || path.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                return path;
            }

            path = Path.GetFullPath(path);
            return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path.Substring(2) : @"\\?\" + path;
        }

        private static bool IsBuiltin(string guid) => guid == "00000000000000000000000000000000" || guid == "0000000000000000e000000000000000" || guid == "0000000000000000f000000000000000";
        public static void Validate(string root)
        {
            var errors = new List<string>();
            foreach (var path in AssetDatabase.GetAllAssetPaths().Where(p => p.StartsWith(root + "/", StringComparison.Ordinal) && !AssetDatabase.IsValidFolder(p)))
            {
                var objects = AssetDatabase.LoadAllAssetsAtPath(path).Where(o => o).ToList();
                foreach (var prefab in objects.OfType<GameObject>().ToArray())
                {
                    foreach (var transform in prefab.GetComponentsInChildren<Transform>(true))
                    {
                        if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(transform.gameObject) != 0)
                        {
                            errors.Add(path + ": missing script on " + transform.name);
                        }

                        objects.AddRange(transform.GetComponents<Component>().Where(c => c));
                    }
                }

                foreach (var obj in objects.Distinct())
                {
                    using var serialized = new SerializedObject(obj);
                    var property = serialized.GetIterator();
                    while (property.Next(true))
                    {
                        if (property.propertyType != SerializedPropertyType.ObjectReference)
                        {
                            continue;
                        }

                        var value = property.objectReferenceValue;
                        if (!value && property.objectReferenceInstanceIDValue != 0 || value && !EditorUtility.IsPersistent(value))
                        {
                            errors.Add(path + ": unresolved " + property.propertyPath);
                        }
                    }
                }
            }

            if (errors.Count != 0)
            {
                throw new InvalidOperationException("Import validation failed:\n" + string.Join("\n", errors.Take(30)));
            }
        }
    }
}

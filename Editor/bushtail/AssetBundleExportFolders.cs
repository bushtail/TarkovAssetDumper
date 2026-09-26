using System;
using System.IO;
using System.Linq;
using UnityEditor;

namespace Editor.bushtail
{
    public static class AssetBundleExportFolders
    {
        // Imported folders must be deleted through Unity so their metadata and
        // AssetDatabase entries stay in sync. Preserve the dump root and any real file.
        public static int PruneImportedEmptyFolders(string dumpRoot)
        {
            dumpRoot = dumpRoot.Replace('\\', '/').TrimEnd('/');
            if (!dumpRoot.StartsWith("Assets/", StringComparison.Ordinal) || !AssetDatabase.IsValidFolder(dumpRoot)
                || dumpRoot.Split('/').Any(part => part == "." || part == ".."))
                throw new ArgumentException("Choose an imported dump folder inside Assets.", nameof(dumpRoot));
            var removed = 0;
            void Visit(string assetPath)
            {
                var directory = AssetBundleDumper.FileSystemPath(Path.GetFullPath(assetPath));
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return;
                foreach (var child in Directory.GetDirectories(directory))
                {
                    var childPath = assetPath + "/" + Path.GetFileName(child);
                    if (AssetDatabase.IsValidFolder(childPath)) Visit(childPath);
                }
                if (assetPath == dumpRoot || Directory.EnumerateDirectories(directory).Any()
                    || Directory.EnumerateFiles(directory).Any(file => !file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))) return;
                if (!AssetDatabase.DeleteAsset(assetPath))
                    throw new IOException("Could not remove empty dump folder: " + assetPath);
                removed++;
            }
            Visit(dumpRoot);
            return removed;
        }

        public static int PruneEmptyFolders(string stagingRoot)
        {
            var root = AssetBundleDumper.FileSystemPath(Path.GetFullPath(stagingRoot));
            var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var removed = 0;

            Visit(root);
            return removed;

            void Visit(string directory)
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                {
                    return;
                }

                foreach (var child in Directory.GetDirectories(directory))
                {
                    Visit(child);
                }

                if (directory == root || Directory.EnumerateDirectories(directory).Any())
                {
                    return;
                }

                var files = Directory.GetFiles(directory);
                if (files.Any(file => !file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                if (!directory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Empty-folder cleanup escaped its staging root.");
                }

                foreach (var meta in files)
                {
                    File.Delete(meta);
                }

                Directory.Delete(directory, false);
                if (File.Exists(directory + ".meta"))
                {
                    File.Delete(directory + ".meta");
                }

                removed++;
            }
        }
    }
}
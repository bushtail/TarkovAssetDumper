#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using JetBrains.Annotations;
using UnityEditor;

namespace Editor.bushtail
{
    public static class AssetBundleBatch
    {
        [UsedImplicitly(ImplicitUseTargetFlags.Members)]
        public sealed class Result
        {
            public string Input = "", Output = "", Error = "";
            public bool Succeeded => string.IsNullOrEmpty(Error);
        }

        public static string[] Normalize(IEnumerable<string> paths) => paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Path.GetFullPath(Environment.ExpandEnvironmentVariables(p.Trim().Trim('"')))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        public static async Task<List<Result>> DumpAsync(IEnumerable<string> inputs, string executable, string parent, bool splitPrefabs, string? fallback, List<string> log, Action<string>? progress = null, string? dependencyFolder = null, bool useImpostors = true, bool ignoreDependencies = false, bool singleBundleLabels = false, string? assetStudioExecutable = null, bool buildAfterDump = true)
        {
            var paths = Normalize(inputs);
            if (paths.Length == 0)
            {
                throw new ArgumentException("Select at least one asset bundle.");
            }

            var results = new List<Result>();
            
            for (var i = 0; i < paths.Length; i++)
            {
                var label = $"[{i + 1}/{paths.Length}] {Path.GetFileName(paths[i])}";
                log.Add("BATCH START: " + label);
                
                var result = new Result
                {
                    Input = paths[i]
                };
                try
                {
                    result.Output = await AssetBundleDumper.DumpAsync(paths[i], executable, parent, splitPrefabs, fallback, log, message => progress?.Invoke(label + ": " + message), dependencyFolder, useImpostors, ignoreDependencies, singleBundleLabels, assetStudioExecutable, buildAfterDump);
                    log.Add("BATCH SUCCESS: " + label + " -> " + result.Output);
                }
                catch (Exception e)
                {
                    result.Error = e.Message;
                    log.Add("BATCH FAILED: " + label + "\n" + e);
                    progress?.Invoke(label + ": failed; continuing with remaining bundles.");
                }

                results.Add(result);
            }

            return results;
        }

        public static string[] ParseSelection(string buffer)
        {
            var parts = buffer.Split('\0').TakeWhile(s => s.Length > 0).ToArray();
            return parts.Length <= 1 ? parts : parts.Skip(1).Select(name => Path.Combine(parts[0], name)).ToArray();
        }

        public static string[] PickFiles(string directory)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                var path = EditorUtility.OpenFilePanel("Add asset bundle (or drag multiple files into the list)", directory, "");
                return string.IsNullOrEmpty(path) ? Array.Empty<string>() : new[]
                {
                    path
                };
            }

            const int capacity = 65536;
            var buffer = Marshal.AllocHGlobal(capacity * sizeof(char));
            try
            {
                Marshal.Copy(new byte[capacity * sizeof(char)], 0, buffer, capacity * sizeof(char));
                
                var dialog = new OpenFileName
                {
                    Size = Marshal.SizeOf(typeof(OpenFileName)),
                    Owner = GetActiveWindow(),
                    Filter = "Asset bundles\0*.bundle\0All files\0*.*\0\0",
                    FilterIndex = 1,
                    File = buffer,
                    MaxFile = capacity,
                    InitialDirectory = directory,
                    Title = "Select asset bundles",
                    Flags = 0x00080000 | 0x00000200 | 0x00001000 | 0x00000800 | 0x00000008
                };
                if (GetOpenFileName(ref dialog))
                {
                    return ParseSelection(Marshal.PtrToStringUni(buffer, capacity) ?? "");
                }

                var error = CommDlgExtendedError();
                return error != 0 ? throw new IOException("File picker failed (0x" + error.ToString("X") + "). Try selecting fewer files or drag files into the list.") : Array.Empty<string>(); // Cancel keeps the existing selection.
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OpenFileName
        {
            public int Size;
            public IntPtr Owner, Instance;
            public string? Filter;
            public IntPtr CustomFilter;
            public int MaxCustomFilter, FilterIndex;
            public IntPtr File;
            public int MaxFile;
            public IntPtr FileTitle;
            public int MaxFileTitle;
            public string? InitialDirectory, Title;
            public int Flags;
            public short FileOffset, ExtensionOffset;
            public string? DefaultExtension;
            public IntPtr CustomData, Hook, TemplateName, Reserved;
            public int ReservedValue, FlagsEx;
        }

        [DllImport("comdlg32.dll", EntryPoint = "GetOpenFileNameW", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetOpenFileName(ref OpenFileName dialog);
        
        [DllImport("comdlg32.dll")]
        private static extern uint CommDlgExtendedError();
        
        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();
    }
}

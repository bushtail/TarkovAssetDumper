#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Editor.bushtail
{
    // AssetStudioModCLI is an optional, separately installed extraction tool.
    // Keep its converted files outside the original buildable dump until they
    // have been checked and deliberately applied.
    internal static class AssetStudioModBridge
    {
        internal static async Task<string> Export(string executable, IReadOnlyList<string> inputs, string run, List<string> log)
        {
            if (!File.Exists(executable)) { throw new FileNotFoundException("AssetStudioModCLI executable was not found.", executable); }
            var inputRoot = Path.Combine(run, "Inputs");
            if (!Directory.Exists(inputRoot))
            {
                // Ignore-dependencies mode passes the original bundle directly.
                // Give AssetStudio a bounded input folder instead of its parent installation.
                inputRoot = Path.Combine(run, "AssetStudioInputs");
                Directory.CreateDirectory(inputRoot);
                for (var i = 0; i < inputs.Count; i++)
                {
                    var folder = Path.Combine(inputRoot, i.ToString());
                    Directory.CreateDirectory(folder);
                    File.Copy(inputs[i], Path.Combine(folder, Path.GetFileName(inputs[i])));
                }
            }

            var output = Path.Combine(run, "AssetStudio");
            Directory.CreateDirectory(output);
            await Run(executable, inputRoot, "-m animator --fbx-animation all", Path.Combine(output, "Animator"), Path.Combine(run, "AssetStudio-Animator.log"));
            await Run(executable, inputRoot, "-m export -t audio -g type --audio-format wav", Path.Combine(output, "Audio"), Path.Combine(run, "AssetStudio-Audio.log"));
            log.Add("AssetStudioModCLI exported editable FBX and original audio into " + output);
            return output;
        }

        private static async Task Run(string executable, string input, string options, string output, string logPath)
        {
            Directory.CreateDirectory(output);
            var args = Quote(input) + " " + options + " -o " + Quote(output) + " --log-level info";
            var start = new ProcessStartInfo(executable, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(start) ?? throw new IOException("Could not launch AssetStudioModCLI.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            var completed = await Task.Run(() => process.WaitForExit(600000));
            if (!completed)
            {
                process.Kill();
                throw new TimeoutException("AssetStudioModCLI exceeded ten minutes. See " + logPath);
            }

            var standardOutput = await stdout;
            var standardError = await stderr;
            var report = standardOutput + Environment.NewLine + standardError;
            File.WriteAllText(logPath, report);
            if (process.ExitCode != 0)
            {
                throw new InvalidDataException("AssetStudioModCLI failed (exit " + process.ExitCode + "). See " + logPath);
            }
        }

        private static string Quote(string path)
        {
            if (path.Contains('"')) { throw new ArgumentException("A tool path contains an invalid quote.", nameof(path)); }
            return "\"" + path + "\"";
        }

        internal static int ReplaceAudio(string payloadRoot, string output, List<string> log)
        {
            if (!Directory.Exists(output)) { return 0; }
            var originals = Directory.GetFiles(output, "*.wav", SearchOption.AllDirectories)
                .GroupBy(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);
            var targets = Directory.GetFiles(payloadRoot, "*", SearchOption.AllDirectories)
                .Where(path => path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
                .GroupBy(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase);
            var count = 0;
            foreach (var group in targets)
            {
                if (group.Count() != 1 || !originals.TryGetValue(group.Key, out var source)) { continue; }
                var target = group.Single();
                var destination = Path.ChangeExtension(target, ".wav");
                if (!target.Equals(destination, StringComparison.OrdinalIgnoreCase)
                    && (File.Exists(destination) || File.Exists(destination + ".meta"))) { continue; }
                if (!IsWav(source)) { log.Add("AUDIO SKIPPED: AssetStudio output is not WAV: " + source); continue; }
                if (!target.Equals(destination, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(source, destination);
                    var meta = target + ".meta";
                    if (File.Exists(meta)) { File.Move(meta, destination + ".meta"); }
                    File.Delete(target);
                }
                else { File.Copy(source, destination, true); }
                count++;
            }

            log.Add("AUDIO: replaced " + count + " uniquely named ripped clips with AssetStudio WAVs.");
            return count;
        }

        private static bool IsWav(string path)
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            if (reader.BaseStream.Length < 12 || new string(reader.ReadChars(4)) != "RIFF") { return false; }
            reader.ReadUInt32();
            return new string(reader.ReadChars(4)) == "WAVE";
        }
    }
}

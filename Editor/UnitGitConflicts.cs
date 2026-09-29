using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;

namespace Orbiters.UnitGit.Editor
{
    /// <summary>What Git is in the middle of, when files conflict.</summary>
    internal enum UnitGitOperation { None, Merge, CherryPick, Revert, Rebase }

    internal enum ConflictChoice { None, Mine, Theirs, Both }

    /// <summary>One part of a conflicted text file: shared text, or a place where both sides changed the same lines.</summary>
    internal sealed class ConflictBlock
    {
        public bool IsConflict;
        public string Common = string.Empty;
        public string Mine = string.Empty;
        public string Base = string.Empty;
        public string Theirs = string.Empty;
        // For Unity files: the object and component the lines belong to ("Hips (Transform)").
        public string Context = string.Empty;
        public ConflictChoice Choice;
    }

    /// <summary>A property Unity's merge could not decide: both sides set it differently.</summary>
    internal sealed class UnityMergeConflict
    {
        public string Object = string.Empty;
        public string Property = string.Empty;
        public string Mine = string.Empty;
        public string Theirs = string.Empty;
    }

    internal sealed class UnityMergeResult
    {
        public bool Available;
        public bool Clean;
        public string Output;
        public readonly List<UnityMergeConflict> Conflicts = new List<UnityMergeConflict>();
        public string Message = string.Empty;
    }

    /// <summary>
    /// Resolving conflicted files: Unity's own merge for scenes and prefabs first (it merges by object, not by line), then
    /// choices part by part, or a whole side. Unit Git never starts an external merge tool.
    /// </summary>
    internal static class UnitGitConflicts
    {
        public const string MineLabel = "Mine", BaseLabel = "Base", TheirsLabel = "Theirs";

        public static bool IsConflict(UnitGitStatusEntry entry)
        {
            string code = (entry.IndexStatus.ToString() + entry.WorkTreeStatus).ToUpperInvariant();
            return code == "UU" || code == "AA" || code == "DD" || code == "AU" || code == "UA" || code == "DU" || code == "UD";
        }

        /// <summary>A short description of the conflict: "Changed on both sides", "Deleted by you"…</summary>
        public static string Describe(UnitGitStatusEntry entry)
        {
            switch ((entry.IndexStatus.ToString() + entry.WorkTreeStatus).ToUpperInvariant())
            {
                case "AA": return "Added on both sides";
                case "DD": return "Deleted on both sides";
                case "AU": return "Added by you";
                case "UA": return "Added by them";
                case "DU": return "Deleted by you";
                case "UD": return "Deleted by them";
                default: return "Changed on both sides";
            }
        }

        public static UnitGitOperation Operation(string root, out string title)
        {
            string git = Path.Combine(root, ".git");
            title = string.Empty;
            if (File.Exists(Path.Combine(git, "MERGE_HEAD")))
            {
                string message = File.Exists(Path.Combine(git, "MERGE_MSG")) ? File.ReadAllLines(Path.Combine(git, "MERGE_MSG")).FirstOrDefault() ?? string.Empty : string.Empty;
                title = message.Length > 0 ? message : "Merge";
                return UnitGitOperation.Merge;
            }
            if (File.Exists(Path.Combine(git, "CHERRY_PICK_HEAD"))) { title = "Copying a commit"; return UnitGitOperation.CherryPick; }
            if (File.Exists(Path.Combine(git, "REVERT_HEAD"))) { title = "Reverting a commit"; return UnitGitOperation.Revert; }
            if (Directory.Exists(Path.Combine(git, "rebase-merge")) || Directory.Exists(Path.Combine(git, "rebase-apply"))) { title = "Rebasing"; return UnitGitOperation.Rebase; }
            return UnitGitOperation.None;
        }

        /// <summary>A stage of the conflicted file: 1 base, 2 mine, 3 theirs. Null when that side has no such file.</summary>
        public static string Stage(UnitGitService git, string path, int stage)
        {
            var result = git.RunGit(UnitGitService.DefaultTimeoutMilliseconds, "--literal-pathspecs", "show", ":" + stage + ":" + path);
            // Git's output arrives line by line; Unity text assets use LF, so the sides are compared and written with LF.
            return result.Success ? Normalize(result.StandardOutput) : null;
        }

        public static bool IsBinary(string text)
        {
            if (text == null) return false;
            int limit = Math.Min(text.Length, 8000);
            for (int i = 0; i < limit; i++) if (text[i] == '\0') return true;
            return false;
        }

        // ---- Unity's merge ---------------------------------------------------------------------------------------------

        public static string UnityMergeTool()
        {
            string tools = Path.Combine(EditorApplication.applicationContentsPath, "Tools");
            string exe = Path.Combine(tools, "UnityYAMLMerge.exe");
            if (File.Exists(exe)) return exe;
            string plain = Path.Combine(tools, "UnityYAMLMerge");
            return File.Exists(plain) ? plain : null;
        }

        /// <summary>
        /// Runs Unity's merge on the three versions in a scratch folder (never on the project file). "--fallback none" keeps
        /// it from opening another merge program when it cannot decide.
        /// </summary>
        public static UnityMergeResult UnityMerge(string tool, string mine, string baseText, string theirs, string extension)
        {
            var result = new UnityMergeResult { Available = tool != null };
            if (tool == null)
            {
                result.Message = "Unity's merge tool was not found next to this editor.";
                return result;
            }
            string folder = Path.Combine(Path.GetTempPath(), "unitgit-merge-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string basePath = Path.Combine(folder, "base" + extension), minePath = Path.Combine(folder, "mine" + extension);
                string theirsPath = Path.Combine(folder, "theirs" + extension), output = Path.Combine(folder, "merged" + extension);
                File.WriteAllText(basePath, baseText ?? string.Empty);
                File.WriteAllText(minePath, mine ?? string.Empty);
                File.WriteAllText(theirsPath, theirs ?? string.Empty);
                File.WriteAllText(output, mine ?? string.Empty);
                var start = new ProcessStartInfo(tool, "merge -h -p --force --fallback none " + Quote(basePath) + " " + Quote(theirsPath) + " " + Quote(minePath) + " " + Quote(output))
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    CreateNoWindow = true
                };
                using (var process = Process.Start(start))
                {
                    process.StandardInput.Close();
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    string stderr = process.StandardError.ReadToEnd();
                    if (!process.WaitForExit(120000))
                    {
                        try { process.Kill(); } catch (InvalidOperationException) { }
                        result.Message = "Unity's merge did not finish in two minutes.";
                        return result;
                    }
                    result.Clean = process.ExitCode == 0;
                    result.Output = File.ReadAllText(output);
                    ParseConflicts(stdout.Result, result);
                    if (!result.Clean && result.Conflicts.Count == 0) result.Message = (stdout.Result + stderr).Trim();
                }
            }
            catch (Exception ex) when (ex is IOException || ex is System.ComponentModel.Win32Exception || ex is UnauthorizedAccessException)
            {
                result.Message = ex.Message;
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            return result;
        }

        // "Left  558243963.GameObject.m_Name change to String Right" (theirs) and "Right ..." (mine) for one property.
        internal static void ParseConflicts(string output, UnityMergeResult result)
        {
            var sides = new Dictionary<string, UnityMergeConflict>();
            foreach (Match match in Regex.Matches(output ?? string.Empty, @"^(Left|Right)\s+(\S+?)\s+(?:change to|changed to)\s?(.*)$", RegexOptions.Multiline))
            {
                string key = match.Groups[2].Value;
                if (!sides.TryGetValue(key, out var conflict))
                {
                    int dot = key.IndexOf('.');
                    int second = dot >= 0 ? key.IndexOf('.', dot + 1) : -1;
                    conflict = new UnityMergeConflict
                    {
                        Object = dot > 0 ? key.Substring(0, second > 0 ? second : dot) : key,
                        Property = second > 0 ? key.Substring(second + 1) : key
                    };
                    sides[key] = conflict;
                    result.Conflicts.Add(conflict);
                }
                if (match.Groups[1].Value == "Left") conflict.Theirs = match.Groups[3].Value.TrimEnd('\r');
                else conflict.Mine = match.Groups[3].Value.TrimEnd('\r');
            }
        }

        // ---- Parts ------------------------------------------------------------------------------------------------------

        /// <summary>The file as shared parts and conflicting parts, from Git's line merge of the three versions.</summary>
        public static List<ConflictBlock> Blocks(UnitGitService git, string mine, string baseText, string theirs)
        {
            string folder = Path.Combine(Path.GetTempPath(), "unitgit-blocks-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string m = Path.Combine(folder, "mine"), b = Path.Combine(folder, "base"), t = Path.Combine(folder, "theirs");
                File.WriteAllText(m, Normalize(mine));
                File.WriteAllText(b, Normalize(baseText));
                File.WriteAllText(t, Normalize(theirs));
                // Exit status is the number of conflicts; the merged text is on standard output either way.
                var merged = git.RunGit(UnitGitService.LongTimeoutMilliseconds, "merge-file", "-p", "--diff3", "-L", MineLabel, "-L", BaseLabel, "-L", TheirsLabel, m, b, t);
                if (merged.ExitCode < 0 || merged.ExitCode > 127) throw new InvalidOperationException(merged.Message);
                return Parse(merged.StandardOutput);
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        internal static List<ConflictBlock> Parse(string merged)
        {
            var blocks = new List<ConflictBlock>();
            var common = new StringBuilder();
            var lines = Normalize(merged).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (!line.StartsWith("<<<<<<< ", StringComparison.Ordinal))
                {
                    common.Append(line);
                    if (i < lines.Length - 1) common.Append('\n');
                    continue;
                }
                if (common.Length > 0) { blocks.Add(new ConflictBlock { Common = common.ToString() }); common.Clear(); }
                var block = new ConflictBlock { IsConflict = true };
                var mine = new StringBuilder();
                var baseText = new StringBuilder();
                var theirs = new StringBuilder();
                var target = mine;
                for (i++; i < lines.Length; i++)
                {
                    line = lines[i];
                    if (line.StartsWith("||||||| ", StringComparison.Ordinal)) { target = baseText; continue; }
                    if (line == "=======") { target = theirs; continue; }
                    if (line.StartsWith(">>>>>>> ", StringComparison.Ordinal)) break;
                    target.Append(line).Append('\n');
                }
                block.Mine = mine.ToString();
                block.Base = baseText.ToString();
                block.Theirs = theirs.ToString();
                blocks.Add(block);
            }
            if (common.Length > 0) blocks.Add(new ConflictBlock { Common = common.ToString() });
            AddUnityContext(blocks);
            return blocks;
        }

        // Each conflicting part of a Unity file: the object and component it sits in, from the text before it.
        private static void AddUnityContext(List<ConflictBlock> blocks)
        {
            string type = string.Empty, name = string.Empty;
            var header = new Regex(@"^--- !u!\d+ &(-?\d+)", RegexOptions.Multiline);
            foreach (var block in blocks)
            {
                if (!block.IsConflict)
                {
                    foreach (string line in block.Common.Split('\n'))
                    {
                        if (header.IsMatch(line)) { type = string.Empty; name = string.Empty; continue; }
                        if (type.Length == 0 && line.Length > 1 && line[0] != ' ' && line.EndsWith(":", StringComparison.Ordinal)) { type = line.TrimEnd(':'); continue; }
                        if (line.StartsWith("  m_Name: ", StringComparison.Ordinal)) name = line.Substring(10).Trim();
                    }
                    continue;
                }
                // The name itself may be what conflicts: then the object goes by both names.
                string own = name;
                if (own.Length == 0)
                {
                    string mine = NameIn(block.Mine), theirs = NameIn(block.Theirs);
                    own = mine == theirs || theirs.Length == 0 ? mine : mine.Length == 0 ? theirs : mine + " / " + theirs;
                }
                if (type.Length > 0) block.Context = own.Length > 0 ? own + " (" + Semantic.UnitySemanticNames.Words(type) + ")" : Semantic.UnitySemanticNames.Words(type);
            }
        }

        private static string NameIn(string text)
        {
            foreach (string line in (text ?? string.Empty).Split('\n'))
                if (line.StartsWith("  m_Name: ", StringComparison.Ordinal)) return line.Substring(10).Trim();
            return string.Empty;
        }

        /// <summary>The file with every conflicting part replaced by its choice. Null while a part has no choice.</summary>
        public static string Compose(IList<ConflictBlock> blocks)
        {
            var builder = new StringBuilder();
            foreach (var block in blocks)
            {
                if (!block.IsConflict) { builder.Append(block.Common); if (!block.Common.EndsWith("\n", StringComparison.Ordinal)) builder.Append('\n'); continue; }
                switch (block.Choice)
                {
                    case ConflictChoice.Mine: builder.Append(block.Mine); break;
                    case ConflictChoice.Theirs: builder.Append(block.Theirs); break;
                    case ConflictChoice.Both: builder.Append(block.Mine).Append(block.Theirs); break;
                    default: return null;
                }
            }
            return builder.ToString();
        }

        // ---- Resolving --------------------------------------------------------------------------------------------------

        /// <summary>Writes the resolved text over the project file and marks it resolved.</summary>
        public static GitCommandResult Resolve(UnitGitService git, string path, string text)
        {
            string full = Path.Combine(git.ProjectRoot, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, text);
            return git.RunGit(UnitGitService.DefaultTimeoutMilliseconds, "--literal-pathspecs", "add", "--", path);
        }

        /// <summary>Keeps one side for the whole file, including "the file is deleted" when that side deleted it.</summary>
        public static GitCommandResult TakeSide(UnitGitService git, string path, bool mine)
        {
            string side = mine ? "--ours" : "--theirs";
            var content = Stage(git, path, mine ? 2 : 3);
            if (content == null) return git.RunGit(UnitGitService.DefaultTimeoutMilliseconds, "--literal-pathspecs", "rm", "--quiet", "--", path);
            var checkout = git.RunGit(UnitGitService.DefaultTimeoutMilliseconds, "--literal-pathspecs", "checkout", side, "--", path);
            if (!checkout.Success) return checkout;
            return git.RunGit(UnitGitService.DefaultTimeoutMilliseconds, "--literal-pathspecs", "add", "--", path);
        }

        /// <summary>Records the resolved merge (or continues the copy/revert/rebase) with Git's prepared message.</summary>
        public static GitCommandResult Finish(UnitGitService git, UnitGitOperation operation)
        {
            switch (operation)
            {
                case UnitGitOperation.Merge: return git.RunGit(UnitGitService.LongTimeoutMilliseconds, "commit", "--no-edit");
                case UnitGitOperation.CherryPick: return git.RunGit(UnitGitService.LongTimeoutMilliseconds, "-c", "core.editor=true", "cherry-pick", "--continue");
                case UnitGitOperation.Revert: return git.RunGit(UnitGitService.LongTimeoutMilliseconds, "-c", "core.editor=true", "revert", "--continue");
                case UnitGitOperation.Rebase: return git.RunGit(UnitGitService.LongTimeoutMilliseconds, "-c", "core.editor=true", "rebase", "--continue");
                default: return new GitCommandResult { StandardOutput = "All conflicts are resolved." };
            }
        }

        public static GitCommandResult Abort(UnitGitService git, UnitGitOperation operation)
        {
            switch (operation)
            {
                case UnitGitOperation.Merge: return git.RunGit(UnitGitService.LongTimeoutMilliseconds, "merge", "--abort");
                case UnitGitOperation.CherryPick: return git.RunGit(UnitGitService.LongTimeoutMilliseconds, "cherry-pick", "--abort");
                case UnitGitOperation.Revert: return git.RunGit(UnitGitService.LongTimeoutMilliseconds, "revert", "--abort");
                case UnitGitOperation.Rebase: return git.RunGit(UnitGitService.LongTimeoutMilliseconds, "rebase", "--abort");
                default: return new GitCommandResult { ExitCode = 1, StandardError = "Nothing to cancel." };
            }
        }

        /// <summary>Merges a branch into the current one. Conflicts are a normal outcome here, not a failure.</summary>
        public static GitCommandResult MergeBranch(UnitGitService git, string branch)
        {
            var result = git.RunGit(UnitGitService.LongTimeoutMilliseconds, "merge", "--no-edit", "--no-ff", branch);
            if (!result.Success && (result.StandardOutput + result.StandardError).IndexOf("CONFLICT", StringComparison.Ordinal) >= 0)
                return new GitCommandResult { StandardOutput = "The merge has conflicts: resolve them in the Conflicts tab." };
            return result;
        }

        private static string Normalize(string text) => (text ?? string.Empty).Replace("\r\n", "\n");

        private static string Quote(string path) => "\"" + path.Replace("\"", "\\\"") + "\"";
    }
}

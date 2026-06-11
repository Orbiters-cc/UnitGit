using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Orbiters.UnitGit.Editor
{
    public static class UnitGitEditorHealthChecks
    {
        [MenuItem("Tools/Orbiters/Unit Git/Health Checks/All Deterministic")]
        public static void RunAllFromMenu()
        {
            try
            {
                RunAllOrThrow();
                Debug.Log("[UnitGitEditorHealthChecks] All deterministic health checks passed.");
            }
            catch (Exception ex)
            {
                Debug.LogError("[UnitGitEditorHealthChecks] Failed: " + ex);
            }
        }

        public static void RunAllBatchmode()
        {
            try
            {
                RunAllOrThrow();
                Debug.Log("[UnitGitEditorHealthChecks] All deterministic health checks passed.");
                EditorApplication.Exit(0);
            }
            catch (Exception ex)
            {
                Debug.LogError("[UnitGitEditorHealthChecks] Failed: " + ex);
                EditorApplication.Exit(1);
            }
        }

        public static void RunAllOrThrow()
        {
            ValidatePackageFiles();
            ValidateParsingSmoke();
            ValidateArgumentEscaping();
            ValidateNoPushAction();
        }

        private static void ValidatePackageFiles()
        {
            ThrowIf(!File.Exists("Packages/orbiters.unitgit/package.json"), "UnitGit package.json was not found.");
            ThrowIf(!File.Exists("Packages/orbiters.unitgit/Editor/UnitGitService.cs"), "UnitGitService.cs was not found.");
            ThrowIf(!File.Exists("Packages/orbiters.unitgit/Tests/Editor/orbiters.unitgit.Editor.Tests.asmdef"), "UnitGit editor test assembly was not found.");
        }

        private static void ValidateParsingSmoke()
        {
            var snapshot = UnitGitService.ParseStatusOutput("## main...origin/main [ahead 1, behind 2]\nMM Assets/Test.cs\n");
            ThrowIf(snapshot.CurrentBranch != "main", "Status parser did not read the current branch.");
            ThrowIf(snapshot.Ahead != 1 || snapshot.Behind != 2, "Status parser did not read ahead/behind counts.");
            ThrowIf(snapshot.Changes.Count != 1 || !snapshot.Changes[0].IsStaged || !snapshot.Changes[0].IsUnstaged, "Status parser did not preserve mixed staged/unstaged state.");

            var diff = UnitGitService.ParseUnifiedDiff("@@ -1 +1 @@\n-old\n+new\n");
            ThrowIf(diff.DifferenceCount != 1 || diff.Lines.All(line => line.Kind != UnitGitDiffLineKind.Changed), "Unified diff parser did not pair changed lines.");
        }

        private static void ValidateArgumentEscaping()
        {
            const string windowsPath = @"H:\metaverse\unity projects\MCB Test";
            string command = UnitGitService.FormatCommandLine("gh", "repo", "create", "--source", windowsPath);
            ThrowIf(command.Contains(@"H:\\metaverse\\unity projects\\MCB Test"), "Windows path separators were doubled in command preview.");
            ThrowIf(!command.Contains("\"" + windowsPath + "\""), "Windows path with spaces was not quoted.");
        }

        private static void ValidateNoPushAction()
        {
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            string[] methodNames = typeof(UnitGitWindow).GetMethods(flags)
                .Concat(typeof(UnitGitService).GetMethods(flags))
                .Select(method => method.Name)
                .Where(name => name.IndexOf("Push", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToArray();
            ThrowIf(methodNames.Length > 0, "UnitGit exposes push-named actions: " + string.Join(", ", methodNames));
        }

        private static void ThrowIf(bool condition, string message)
        {
            if (condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}

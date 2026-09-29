using System;
using System.IO;
using NUnit.Framework;

namespace Orbiters.UnitGit.Editor.Tests
{
    // A throwaway Unity-shaped Git repository in the temp folder, for tests of features that read or write a real repo.
    internal sealed class UnitGitTestRepo : IDisposable
    {
        public readonly string Root;
        public readonly UnitGitService Git;

        private UnitGitTestRepo(string root)
        {
            Root = root;
            Git = new UnitGitService(root);
        }

        public static UnitGitTestRepo Create()
        {
            if (!new UnitGitService(Path.GetTempPath()).IsGitAvailable()) Assert.Ignore("Git is not available on PATH.");
            string root = Path.Combine(Path.GetTempPath(), "unitgit-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "Assets"));
            Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
            Directory.CreateDirectory(Path.Combine(root, "Packages"));
            File.WriteAllText(Path.Combine(root, "Packages", "manifest.json"), "{}\n");
            var repo = new UnitGitTestRepo(root);
            repo.Run("init", "-b", "main");
            repo.Run("config", "user.email", "unitgit@example.test");
            repo.Run("config", "user.name", "Unit Git Tests");
            repo.Run("config", "core.autocrlf", "false");
            repo.Run("config", "commit.gpgsign", "false");
            string hooks = Path.Combine(root, ".git", "test-empty-hooks");
            Directory.CreateDirectory(hooks);
            repo.Run("config", "core.hooksPath", hooks);
            return repo;
        }

        public string Full(string relative) => System.IO.Path.Combine(Root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

        public void Write(string relative, string text)
        {
            string path = Full(relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
        }

        public string Read(string relative) => File.ReadAllText(Full(relative));

        public string Run(params string[] args)
        {
            GitCommandResult result = Git.RunGit(60000, args);
            Assert.That(result.Success, Is.True, "git " + string.Join(" ", args) + ": " + result.Message);
            return result.StandardOutput;
        }

        public string Commit(string message)
        {
            Run("add", "-A");
            Run("commit", "-q", "-m", message);
            return Run("rev-parse", "HEAD").Trim();
        }

        public void Dispose()
        {
            if (!Directory.Exists(Root)) return;
            foreach (string path in Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }
    }
}

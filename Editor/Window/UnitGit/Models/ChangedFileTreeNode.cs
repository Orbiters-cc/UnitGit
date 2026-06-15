using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Orbiters.UnitGit;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    internal sealed partial class UnitGitWindow
    {
        private sealed class ChangedFileTreeNode
        {
            public readonly SortedDictionary<string, ChangedFileTreeNode> Folders =
                new SortedDictionary<string, ChangedFileTreeNode>(StringComparer.OrdinalIgnoreCase);

            public readonly List<string> Files = new List<string>();
            public string Name;
            public string FullPath;

            public int FileCount
            {
                get { return Files.Count + Folders.Values.Sum(folder => folder.FileCount); }
            }

            public static ChangedFileTreeNode CreateRoot()
            {
                return new ChangedFileTreeNode
                {
                    Name = string.Empty,
                    FullPath = string.Empty
                };
            }

            public void AddFile(string file)
            {
                if (string.IsNullOrWhiteSpace(file))
                {
                    return;
                }

                string normalized = file.Replace('\\', '/');
                string[] parts = normalized
                    .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length <= 1)
                {
                    Files.Add(normalized);
                    return;
                }

                ChangedFileTreeNode current = this;
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    string folderName = parts[i];
                    if (!current.Folders.TryGetValue(folderName, out ChangedFileTreeNode child))
                    {
                        string fullPath = string.IsNullOrWhiteSpace(current.FullPath)
                            ? folderName
                            : current.FullPath + "/" + folderName;
                        child = new ChangedFileTreeNode
                        {
                            Name = folderName,
                            FullPath = fullPath
                        };
                        current.Folders[folderName] = child;
                    }

                    current = child;
                }

                current.Files.Add(normalized);
            }
        }
    }
}

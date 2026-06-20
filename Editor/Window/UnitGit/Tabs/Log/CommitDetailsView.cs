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
        private VisualElement BuildDetailsPane()
        {
            var pane = new VisualElement();
            pane.AddToClassList("unitgit-details-pane");

            UnitGitReleaseEntry selectedRelease = GetSelectedRelease();
            if (selectedRelease != null)
            {
                pane.Add(BuildReleaseDetailsPanel(selectedRelease));
                return pane;
            }

            pane.Add(BuildChangedFilesTree());
            pane.Add(BuildCommitDetailsCard());

            return pane;
        }

        private UnitGitReleaseEntry GetSelectedRelease()
        {
            if (selectedCommit == null ||
                string.IsNullOrEmpty(selectedReleaseId) ||
                !string.Equals(selectedCommit.ReleaseId, selectedReleaseId, StringComparison.Ordinal))
            {
                return null;
            }

            return GetReleaseForCommit(selectedCommit);
        }

        private VisualElement BuildChangedFilesTree()
        {
            List<string> changedFiles = GetSelectedChangedFiles();
            int selectedCount = GetSelectedCommitsInLogOrder().Count;
            var panel = new VisualElement();
            panel.AddToClassList("unitgit-files-panel");
            panel.Add(BuildSectionHeader(
                "Changed Files",
                changedFiles.Count > 0
                    ? changedFiles.Count + " files" + (selectedCount > 1 ? " across " + selectedCount + " commits" : string.Empty)
                    : "No commit selected"));

            var scroll = new ScrollView();
            scroll.name = "unitgit-changed-files-scroll";
            scroll.AddToClassList("unitgit-files-scroll");

            if (changedFiles.Count == 0)
            {
                scroll.Add(BuildEmptyState("Select a commit to inspect changed files."));
            }
            else
            {
                AddChangedFileTree(scroll, changedFiles);
            }

            panel.Add(scroll);
            return panel;
        }

        private List<string> GetSelectedChangedFiles()
        {
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (UnitGitCommitDetails details in GetSelectedDetailsInLogOrder())
            {
                if (details == null || details.ChangedFiles == null)
                {
                    continue;
                }

                foreach (string file in details.ChangedFiles)
                {
                    if (!string.IsNullOrWhiteSpace(file))
                    {
                        files.Add(file.Trim());
                    }
                }
            }

            return files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private List<UnitGitCommitDetails> GetSelectedDetailsInLogOrder()
        {
            var details = new List<UnitGitCommitDetails>();
            foreach (UnitGitCommit commit in GetSelectedCommitsInLogOrder())
            {
                UnitGitCommitDetails commitDetails = GetDetailsForCommit(commit);
                if (commitDetails != null)
                {
                    details.Add(commitDetails);
                }
            }

            return details;
        }

        private UnitGitCommitDetails GetDetailsForCommit(UnitGitCommit commit)
        {
            if (commit == null || gitService == null)
            {
                return null;
            }

            if (selectedDetails != null &&
                selectedDetails.Commit != null &&
                string.Equals(selectedDetails.Commit.FullHash, commit.FullHash, StringComparison.Ordinal))
            {
                return selectedDetails;
            }

            return gitService.GetCommitDetails(commit.FullHash);
        }

        private void AddChangedFileTree(VisualElement parent, IEnumerable<string> files)
        {
            var root = ChangedFileTreeNode.CreateRoot();
            foreach (string file in files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                root.AddFile(file);
            }

            AddChangedFileTreeNode(parent, root, 0);
        }

        private void AddChangedFileTreeNode(VisualElement parent, ChangedFileTreeNode node, int depth)
        {
            foreach (ChangedFileTreeNode folder in node.Folders.Values)
            {
                string prefKey = GetFoldoutPrefKey("ChangedFilesFolder", folder.FullPath);
                bool expanded = GetFoldoutExpanded(prefKey, true);
                var folderButton = BuildFoldoutButton(
                    folder.Name,
                    folder.FileCount + " files",
                    expanded,
                    () => ToggleFoldout(prefKey, true),
                    "unitgit-file-folder");
                folderButton.style.marginLeft = 8f + depth * 18f;
                parent.Add(folderButton);

                if (expanded)
                {
                    AddChangedFileTreeNode(parent, folder, depth + 1);
                }
            }

            foreach (string file in node.Files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                parent.Add(BuildChangedFileButton(file, depth));
            }
        }

        private VisualElement BuildChangedFileButton(string file, int depth)
        {
            var row = new Button(() => OpenProjectPath(file));
            row.tooltip = file;
            row.AddToClassList("unitgit-file-row");
            row.style.marginLeft = 24f + depth * 18f;

            row.Add(BuildProjectFileIcon(file, "unitgit-file-row-icon"));

            var name = new Label(GetFileLeaf(file));
            name.AddToClassList("unitgit-file-row-name");
            row.Add(name);

            return row;
        }

        private VisualElement BuildCommitDetailsCard()
        {
            var card = new VisualElement();
            card.AddToClassList("unitgit-details-card");

            List<UnitGitCommit> selectedCommits = GetSelectedCommitsInLogOrder();
            if (selectedCommits.Count > 1)
            {
                return BuildMultiCommitDetailsCard(card, selectedCommits);
            }

            if (selectedDetails == null || selectedDetails.Commit == null)
            {
                card.Add(BuildEmptyState("No commit selected."));
                return card;
            }

            var subject = new Label(selectedDetails.Commit.Subject);
            subject.AddToClassList("unitgit-details-title");
            card.Add(subject);

            var hash = new Label(selectedDetails.Commit.ShortHash + "  " + selectedDetails.Commit.AuthorName);
            hash.AddToClassList("unitgit-details-hash");
            card.Add(hash);

            var author = new Label("<" + selectedDetails.Commit.AuthorEmail + "> on " + selectedDetails.AuthorDate);
            author.AddToClassList("unitgit-details-text");
            card.Add(author);

            if (!string.IsNullOrWhiteSpace(selectedDetails.CommitterName))
            {
                var committer = new Label("committed by " + selectedDetails.CommitterName + " on " + selectedDetails.CommitterDate);
                committer.AddToClassList("unitgit-details-muted");
                card.Add(committer);
            }

            if (!string.IsNullOrWhiteSpace(selectedDetails.Commit.Decorations))
            {
                var decorations = new Label(selectedDetails.Commit.Decorations);
                decorations.AddToClassList("unitgit-decoration-line");
                card.Add(decorations);
            }

            return card;
        }

        private VisualElement BuildMultiCommitDetailsCard(VisualElement card, IList<UnitGitCommit> commits)
        {
            var subject = new Label(commits.Count + " commits selected");
            subject.AddToClassList("unitgit-details-title");
            card.Add(subject);

            foreach (UnitGitCommit commit in commits)
            {
                var hash = new Label(commit.ShortHash + "  " + commit.AuthorName);
                hash.AddToClassList("unitgit-details-hash");
                card.Add(hash);

                var title = new Label(commit.Subject);
                title.AddToClassList("unitgit-details-muted");
                card.Add(title);
            }

            return card;
        }
    }
}

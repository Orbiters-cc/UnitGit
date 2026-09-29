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
            EnsureCommitDetailsLoaded();

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
            List<string> changedFiles = loadedSelection != null ? loadedSelection.Files : new List<string>();
            int selectedCount = GetSelectedCommitsInLogOrder().Count;
            var panel = new VisualElement();
            panel.AddToClassList("unitgit-files-panel");
            panel.Add(BuildSectionHeader(
                "Changed Files",
                changedFiles.Count > 0
                    ? changedFiles.Count + " files" + (selectedCount > 1 ? " across " + selectedCount + " commits" : string.Empty)
                    : "No commit selected"));

            var scroll = new VisualElement();
            scroll.AddToClassList("unitgit-files-scroll");

            if (changedFiles.Count == 0)
            {
                scroll.Add(BuildEmptyState(detailsRead.IsBusy ? "Loading changed files..." : "No changed files."));
            }
            else
            {
                var rows = loadedSelection.FileList.GetVisibleRows(row =>
                    GetFoldoutExpanded(GetFoldoutPrefKey("ChangedFilesFolder", row.Path), true));
                scroll.Add(BuildVirtualList(rows, 28f, BuildCommitFileRow, "unitgit-changed-files-scroll"));
            }

            panel.Add(scroll);
            return panel;
        }


        private static void AddChangedFileTreeNode(UnitGitFileList rows, ChangedFileTreeNode node, int depth)
        {
            foreach (ChangedFileTreeNode folder in node.Folders.Values)
            {
                rows.Add(new UnitGitFileRow
                {
                    IsFolder = true, Name = folder.Name, Path = folder.FullPath,
                    FileCount = folder.FileCount, Depth = depth
                });
                AddChangedFileTreeNode(rows, folder, depth + 1);
            }

            foreach (string file in node.Files)
            {
                rows.Add(new UnitGitFileRow { Path = file, Depth = depth });
            }
        }

        private VisualElement BuildCommitFileRow(UnitGitFileRow row)
        {
            if (!row.IsFolder)
                return BuildChangedFileButton(row.Path, row.Depth);
            string prefKey = GetFoldoutPrefKey("ChangedFilesFolder", row.Path);
            var button = BuildFoldoutButton(row.Name, row.FileCount + " files",
                GetFoldoutExpanded(prefKey, true), () => ToggleFoldout(prefKey, true), "unitgit-file-folder");
            button.style.marginLeft = 8f + row.Depth * 18f;
            return button;
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

            // Scenes and prefabs: what this commit changed in them, object by object.
            var details = selectedDetails;
            if (details != null && details.Commit != null && (IsUnityYamlPath(file) || Semantic.ModelVersions.IsPreviewable(file)))
            {
                var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>("Packages/orbiters.unitgit/Editor/Styles/unitgit-semantic.uss");
                if (sheet != null && !row.styleSheets.Contains(sheet)) row.styleSheets.Add(sheet);
                var compare = new Button(() => SceneChangesWindow.Show(gitService.ProjectRoot, file, details.Commit.FullHash, details.Commit.Subject))
                {
                    text = "Compare",
                    tooltip = "See what this commit changed in " + GetFileLeaf(file) + ", object by object (beta)."
                };
                compare.AddToClassList("ugs-compare");
                // The row itself opens the asset: a press on Compare stays on Compare.
                compare.RegisterCallback<PointerDownEvent>(evt => evt.StopPropagation());
                row.Add(compare);
            }

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
                card.Add(BuildEmptyState(detailsRead.IsBusy ? "Loading commit..." : "No commit selected."));
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

            var list = BuildVirtualList(commits, 44f, commit =>
            {
                var row = new VisualElement();
                var hash = new Label(commit.FullHash + "  " + commit.AuthorName);
                hash.AddToClassList("unitgit-details-hash");
                row.Add(hash);

                var title = new Label(commit.Subject);
                title.AddToClassList("unitgit-details-muted");
                row.Add(title);
                row.tooltip = commit.FullHash + "\n" + commit.AuthorName + " <" + commit.AuthorEmail + ">\n" + commit.Subject;
                return row;
            }, "unitgit-selected-commits-scroll");
            list.style.height = 180;
            list.style.flexBasis = 180;
            card.Add(list);

            return card;
        }

        private readonly UnitGitLatestRequest<SelectionDetails> detailsRead = new UnitGitLatestRequest<SelectionDetails>();
        private readonly Dictionary<string, UnitGitCommitDetails> detailsCache = new Dictionary<string, UnitGitCommitDetails>();
        private SelectionDetails loadedSelection;
        private string requestedSelection;

        private sealed class SelectionDetails
        {
            public readonly List<UnitGitCommitDetails> Details = new List<UnitGitCommitDetails>();
            public List<string> Files;
            public readonly UnitGitFileList FileList = new UnitGitFileList();
        }

        private void EnsureCommitDetailsLoaded()
        {
            var commits = GetSelectedCommitsInLogOrder();
            if (commits.Count == 0 && selectedCommit != null)
                commits.Add(selectedCommit);
            string key = string.Join(";", commits.Select(commit => commit.FullHash));
            if (key == requestedSelection)
            {
                selectedDetails = loadedSelection?.Details.FirstOrDefault(item => item.Commit.FullHash == selectedCommit?.FullHash);
                return;
            }
            requestedSelection = key;
            loadedSelection = null;
            selectedDetails = null;
            string root = gitService.ProjectRoot;
            detailsRead.Request(stale =>
            {
                var result = new SelectionDetails();
                var files = new HashSet<string>(StringComparer.Ordinal);
                var service = new UnitGitService(root) { ReadSuperseded = stale };
                foreach (var commit in commits)
                {
                    if (stale())
                        return null;
                    if (!detailsCache.TryGetValue(commit.FullHash, out var details))
                    {
                        details = service.GetCommitDetails(commit.FullHash);
                        if (stale())
                            return null;
                        if (details != null && details.ChangedFiles.Count <= 10000)
                        {
                            if (detailsCache.Count >= 64 || detailsCache.Values.Sum(item => item.ChangedFiles.Count) > 50000)
                                detailsCache.Clear();
                            detailsCache[commit.FullHash] = details;
                        }
                    }
                    if (details == null)
                        continue;
                    result.Details.Add(details);
                    foreach (string file in details.ChangedFiles)
                        files.Add(file);
                }
                result.Files = files.OrderBy(file => file, StringComparer.OrdinalIgnoreCase).ToList();
                var tree = ChangedFileTreeNode.CreateRoot();
                foreach (string file in result.Files)
                    tree.AddFile(file);
                AddChangedFileTreeNode(result.FileList, tree, 0);
                return result;
            });
            EnsureEditorUpdatePump();
        }

        private void PollCommitDetails()
        {
            if (!detailsRead.Poll(out SelectionDetails result, out Exception error))
                return;
            loadedSelection = result;
            if (error != null)
                AppendConsole("commit details", error.Message);
            selectedDetails = result?.Details.FirstOrDefault(item => item.Commit.FullHash == selectedCommit?.FullHash);
            if (activeTab == UnitGitTab.Log && commitDetailsRoot != null)
            {
                RememberScrollOffsets();
                commitDetailsRoot.Clear();
                commitDetailsRoot.Add(BuildDetailsPane());
                RestoreScrollOffsets();
            }
        }
    }
}

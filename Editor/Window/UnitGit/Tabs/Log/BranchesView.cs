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
        private VisualElement BuildBranchesPane()
        {
            var pane = new VisualElement();
            pane.AddToClassList("unitgit-branches-pane");

            var search = new TextField();
            search.value = branchSearch;
            search.AddToClassList("unitgit-search");
            search.RegisterValueChangedCallback(evt =>
            {
                branchSearch = evt.newValue;
                RebuildContent();
            });
            pane.Add(search);

            var scroll = new ScrollView();
            scroll.name = "unitgit-branch-scroll";
            scroll.AddToClassList("unitgit-branch-scroll");

            var current = new Label("HEAD (Current Branch)");
            current.AddToClassList("unitgit-tree-head");
            scroll.Add(current);

            AddBranchSection(scroll, "Local", snapshot.Branches.Where(branch => !branch.IsRemote), BranchLocalFoldPref);
            AddBranchSection(scroll, "Remote", snapshot.Branches.Where(branch => branch.IsRemote), BranchRemoteFoldPref);
            pane.Add(scroll);

            var footer = new VisualElement();
            footer.AddToClassList("unitgit-branch-footer");
            footer.Add(BuildActionButton("Checkout", string.Empty, CheckoutSelectedBranch));
            var merge = BuildActionButton("Merge into current", string.Empty, MergeSelectedBranch);
            merge.tooltip = "Bring the selected branch's commits into the current branch. Conflicts open in the Conflicts tab.";
            footer.Add(merge);
            pane.Add(footer);

            return pane;
        }

        private void AddBranchSection(VisualElement parent, string title, IEnumerable<UnitGitBranch> branches, string prefKey)
        {
            var section = new VisualElement();
            section.AddToClassList("unitgit-tree-section");

            bool expanded = GetFoldoutExpanded(prefKey, true);
            section.Add(BuildFoldoutButton(
                title,
                string.Empty,
                expanded,
                () => ToggleFoldout(prefKey, true),
                "unitgit-tree-section-title"));

            var filtered = branches
                .Where(branch => MatchesBranchSearch(branch))
                .ToList();

            if (!expanded)
            {
                parent.Add(section);
                return;
            }

            if (filtered.Count == 0)
            {
                var empty = new Label("No " + title.ToLowerInvariant() + " branches");
                empty.AddToClassList("unitgit-branch-empty");
                section.Add(empty);
                parent.Add(section);
                return;
            }

            foreach (IGrouping<string, UnitGitBranch> group in filtered.GroupBy(GetBranchFolder).OrderBy(group => group.Key))
            {
                if (!string.IsNullOrEmpty(group.Key))
                {
                    string folderPrefKey = GetFoldoutPrefKey("BranchFolder." + title, group.Key);
                    bool folderExpanded = GetFoldoutExpanded(folderPrefKey, true);
                    section.Add(BuildFoldoutButton(
                        group.Key,
                        string.Empty,
                        folderExpanded,
                        () => ToggleFoldout(folderPrefKey, true),
                        "unitgit-tree-folder"));
                    if (!folderExpanded)
                    {
                        continue;
                    }
                }

                foreach (UnitGitBranch branch in group.OrderBy(branch => branch.Name, StringComparer.OrdinalIgnoreCase))
                {
                    section.Add(BuildBranchRow(branch, !string.IsNullOrEmpty(group.Key)));
                }
            }

            parent.Add(section);
        }

        private bool MatchesBranchSearch(UnitGitBranch branch)
        {
            return branch != null &&
                   (string.IsNullOrWhiteSpace(branchSearch) ||
                    branch.Name.IndexOf(branchSearch.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string GetBranchFolder(UnitGitBranch branch)
        {
            if (branch == null || string.IsNullOrWhiteSpace(branch.Name))
            {
                return string.Empty;
            }

            string name = branch.IsRemote && branch.Name.Contains("/")
                ? branch.Name.Substring(branch.Name.IndexOf("/", StringComparison.Ordinal) + 1)
                : branch.Name;

            int slash = name.IndexOf("/", StringComparison.Ordinal);
            return slash > 0 ? name.Substring(0, slash) : string.Empty;
        }

        private VisualElement BuildBranchRow(UnitGitBranch branch, bool indented)
        {
            var row = new Button(() =>
            {
                selectedBranch = branch;
                RebuildContent();
            });
            row.AddToClassList("unitgit-branch-row");
            row.tooltip = branch.Name;
            if (indented)
            {
                row.AddToClassList("unitgit-branch-row--indented");
            }

            if (branch.IsCurrent)
            {
                row.AddToClassList("unitgit-branch-row--current");
            }

            if (selectedBranch != null && selectedBranch.FullRef == branch.FullRef)
            {
                row.AddToClassList("unitgit-branch-row--selected");
            }

            var dot = new VisualElement();
            dot.AddToClassList("unitgit-branch-dot");
            dot.style.backgroundColor = GetGraphColorForKey(branch.Name);
            row.Add(dot);

            var name = new Label(GetBranchLeafName(branch));
            name.AddToClassList("unitgit-branch-name");
            row.Add(name);

            if (!string.IsNullOrWhiteSpace(branch.Upstream))
            {
                var upstream = new Label(branch.Upstream);
                upstream.AddToClassList("unitgit-branch-upstream");
                row.Add(upstream);
            }

            if (!string.IsNullOrWhiteSpace(branch.ShortHash))
            {
                var hash = new Label(branch.ShortHash);
                hash.AddToClassList("unitgit-branch-hash");
                row.Add(hash);
            }

            row.RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.clickCount == 2)
                {
                    selectedBranch = branch;
                    CheckoutSelectedBranch();
                    evt.StopPropagation();
                }
            });

            return row;
        }

        private static string GetBranchLeafName(UnitGitBranch branch)
        {
            if (branch == null || string.IsNullOrWhiteSpace(branch.Name))
            {
                return string.Empty;
            }

            string name = branch.Name;
            if (branch.IsRemote && name.Contains("/"))
            {
                name = name.Substring(name.IndexOf("/", StringComparison.Ordinal) + 1);
            }

            int slash = name.LastIndexOf("/", StringComparison.Ordinal);
            return slash >= 0 ? name.Substring(slash + 1) : name;
        }
    }
}

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
        private VisualElement BuildShelfBody()
        {
            var workspace = new VisualElement();
            workspace.AddToClassList("unitgit-workspace");

            var branchSplit = BuildTrackedSplit(BranchLogSplitPref, 0, 448f);
            branchSplit.Add(BuildBranchesPane());

            var shelfPane = new VisualElement();
            shelfPane.AddToClassList("unitgit-local-pane");
            shelfPane.Add(BuildSectionHeader("Shelf", "Git stash entries"));

            var actions = new VisualElement();
            actions.AddToClassList("unitgit-row-actions");
            actions.Add(BuildActionButton("Shelve All Changes", "unitgit-button--primary", ShelveAll));
            actions.Add(BuildActionButton("Apply Selected", string.Empty, ApplySelectedShelf));
            actions.Add(BuildActionButton("Drop Selected", string.Empty, DropSelectedShelf));
            shelfPane.Add(actions);

            var shelves = GetShelves();
            var list = new ScrollView();
            list.name = "unitgit-shelf-list";
            list.AddToClassList("unitgit-scroll");
            if (shelves.Count == 0)
            {
                list.Add(BuildEmptyState("No shelf entries."));
            }
            else
            {
                foreach (string shelf in shelves)
                {
                    list.Add(BuildShelfRow(shelf));
                }
            }

            shelfPane.Add(list);

            var console = new VisualElement();
            console.AddToClassList("unitgit-commit-pane");
            console.Add(BuildSectionHeader("Console", "Latest Git output"));
            console.Add(BuildConsoleTail());

            var shelfConsoleSplit = BuildTrackedSplit(ShelfConsoleSplitPref, 1, 420f);
            shelfConsoleSplit.Add(shelfPane);
            shelfConsoleSplit.Add(console);

            branchSplit.Add(shelfConsoleSplit);
            workspace.Add(branchSplit);

            return workspace;
        }

        private VisualElement BuildShelfRow(string shelf)
        {
            var row = new Button(() =>
            {
                selectedShelf = ExtractShelfRef(shelf);
                RebuildContent();
            });
            row.text = shelf;
            row.AddToClassList("unitgit-shelf-row");
            if (!string.IsNullOrEmpty(selectedShelf) && shelf.StartsWith(selectedShelf + ":", StringComparison.Ordinal))
            {
                row.AddToClassList("unitgit-shelf-row--selected");
            }

            return row;
        }

        private List<string> GetShelves()
        {
            return snapshot != null ? snapshot.Shelves : new List<string>();
        }

        private static string ExtractShelfRef(string shelf)
        {
            if (string.IsNullOrWhiteSpace(shelf))
            {
                return string.Empty;
            }

            int colon = shelf.IndexOf(':');
            return colon > 0 ? shelf.Substring(0, colon) : shelf;
        }
    }
}

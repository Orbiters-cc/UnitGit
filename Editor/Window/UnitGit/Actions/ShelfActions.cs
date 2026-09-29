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
        private void ShelveAll()
        {
            string shelfMessage = "Unit Git shelf " + DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            if (!ConfirmGitOperation(
                    "Shelve All Changes",
                    "Shelve",
                    GitCommand("stash", "push", "-u", "-m", shelfMessage),
                    "Move local changes into a Git stash entry.",
                    "This rewrites the working tree back to HEAD after creating the shelf.",
                    GetLocalChangeCount()))
            {
                return;
            }

            RunAction("shelve", () => gitService.ShelveAll(shelfMessage));
        }

        // Actions run on a worker thread: pass the confirmed selection, never read the live field there.
        private void MergeSelectedBranch()
        {
            UnitGitBranch branch = selectedBranch;
            if (branch == null || branch.IsCurrent)
            {
                AppendConsole("merge", branch == null ? "Select a branch first." : "Select another branch than the current one.");
                RebuildContent();
                return;
            }

            if (!ConfirmGitOperation(
                    "Merge Branch",
                    "Merge",
                    GitCommand("merge", "--no-edit", "--no-ff", branch.Name),
                    "Merge " + branch.Name + " into " + snapshot.CurrentBranch + ".",
                    "Both histories are kept. If the same parts changed on both sides, Unit Git opens them in the Conflicts tab.",
                    GetLocalChangeCount()))
            {
                return;
            }

            RunAction("merge " + branch.Name, () => UnitGitConflicts.MergeBranch(gitService, branch.Name), result =>
            {
                AssetDatabase.Refresh();
                if (UnitGitConflicts.Operation(gitService.ProjectRoot, out _) != UnitGitOperation.None) activeTab = UnitGitTab.Conflicts;
            });
        }

        private void CheckoutSelectedBranch()
        {
            UnitGitBranch branch = selectedBranch;
            if (branch == null)
            {
                AppendConsole("checkout", "Select a branch first.");
                RebuildContent();
                return;
            }

            string command = branch.IsRemote
                ? GitCommand("checkout", "-t", branch.Name)
                : GitCommand("checkout", branch.Name);
            if (!ConfirmGitOperation(
                    "Checkout Branch",
                    "Checkout",
                    command,
                    "Checkout " + branch.Name + ".",
                    "This can rewrite working tree files. Git will refuse if local changes would be overwritten.",
                    GetLocalChangeCount()))
            {
                return;
            }

            RunAction("checkout " + branch.Name, () => gitService.Checkout(branch));
        }

        private void ApplySelectedShelf()
        {
            string shelf = selectedShelf;
            if (string.IsNullOrWhiteSpace(shelf))
            {
                AppendConsole("shelf", "Select a shelf entry first.");
                RebuildContent();
                return;
            }

            if (!ConfirmGitOperation(
                    "Apply Shelf",
                    "Apply",
                    GitCommand("stash", "apply", shelf),
                    "Apply " + shelf + " to the working tree.",
                    "This can modify local files and may produce conflicts.",
                    GetLocalChangeCount()))
            {
                return;
            }

            RunAction("stash apply " + shelf, () => gitService.RunGit(30000, "stash", "apply", shelf));
        }

        private void DropSelectedShelf()
        {
            string shelf = selectedShelf;
            if (string.IsNullOrWhiteSpace(shelf))
            {
                AppendConsole("shelf", "Select a shelf entry first.");
                RebuildContent();
                return;
            }

            if (!ConfirmGitOperation(
                    "Drop Shelf",
                    "Drop",
                    GitCommand("stash", "drop", shelf),
                    "Drop " + shelf + ".",
                    "This deletes the selected shelf entry.",
                    0))
            {
                return;
            }

            RunAction("stash drop " + shelf, () => gitService.RunGit(30000, "stash", "drop", shelf));
            selectedShelf = string.Empty;
        }
    }
}

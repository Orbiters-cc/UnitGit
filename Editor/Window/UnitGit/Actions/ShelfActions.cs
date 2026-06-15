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

        private void CheckoutSelectedBranch()
        {
            if (selectedBranch == null)
            {
                AppendConsole("checkout", "Select a branch first.");
                RebuildContent();
                return;
            }

            string command = selectedBranch.IsRemote
                ? GitCommand("checkout", "-t", selectedBranch.Name)
                : GitCommand("checkout", selectedBranch.Name);
            if (!ConfirmGitOperation(
                    "Checkout Branch",
                    "Checkout",
                    command,
                    "Checkout " + selectedBranch.Name + ".",
                    "This can rewrite working tree files. Git will refuse if local changes would be overwritten.",
                    GetLocalChangeCount()))
            {
                return;
            }

            RunAction("checkout " + selectedBranch.Name, () => gitService.Checkout(selectedBranch));
        }

        private void ApplySelectedShelf()
        {
            if (string.IsNullOrWhiteSpace(selectedShelf))
            {
                AppendConsole("shelf", "Select a shelf entry first.");
                RebuildContent();
                return;
            }

            if (!ConfirmGitOperation(
                    "Apply Shelf",
                    "Apply",
                    GitCommand("stash", "apply", selectedShelf),
                    "Apply " + selectedShelf + " to the working tree.",
                    "This can modify local files and may produce conflicts.",
                    GetLocalChangeCount()))
            {
                return;
            }

            RunAction("stash apply " + selectedShelf, () => gitService.RunGit(30000, "stash", "apply", selectedShelf));
        }

        private void DropSelectedShelf()
        {
            if (string.IsNullOrWhiteSpace(selectedShelf))
            {
                AppendConsole("shelf", "Select a shelf entry first.");
                RebuildContent();
                return;
            }

            if (!ConfirmGitOperation(
                    "Drop Shelf",
                    "Drop",
                    GitCommand("stash", "drop", selectedShelf),
                    "Drop " + selectedShelf + ".",
                    "This deletes the selected shelf entry.",
                    0))
            {
                return;
            }

            RunAction("stash drop " + selectedShelf, () => gitService.RunGit(30000, "stash", "drop", selectedShelf));
            selectedShelf = string.Empty;
        }
    }
}

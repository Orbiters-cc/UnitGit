using System;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace Orbiters.UnitGit.Editor
{
    // Visit This Commit: the project as it was at an older commit, every later commit kept on its branch and in the Log,
    // and one click back to the latest. Git calls it a detached HEAD; the way back (branch, shelf made before visiting)
    // is kept per project (UnitGitSettings.Visit) so it outlives domain reloads and restarts.
    internal sealed partial class UnitGitWindow
    {
        private VisualElement visitBanner;
        private Label visitTitle;
        private Label visitDetail;
        private Button visitNewBranchButton;
        private Button visitBackButton;
        // Shown before Git is done: the commit a visit checks out, the branch "Back" checks out.
        private UnitGitCommit pendingVisit;
        private string pendingReturnBranch;
        // When the last visit or return finished (UTC ticks): Git state read after it replaces the pending state.
        private long visitActionDone;
        // The visit ends through Back, which says itself what became of the shelf.
        private bool visitReturning;

        private bool IsVisiting => snapshot != null && snapshot.IsDetached && snapshot.DetachedCommit != null;

        private string VisitedShortHash => snapshot?.DetachedCommit?.ShortHash ?? "a commit";

        // Between the top bar and the content: what is being visited, how far behind the branch it is, and the way back.
        private VisualElement BuildVisitBanner()
        {
            visitBanner = new VisualElement();
            visitBanner.AddToClassList("ug-visit");
            visitBanner.style.display = DisplayStyle.None;
            var icon = new UnitGitIconElement(UnitGitIconKind.Log);
            icon.AddToClassList("ug-visit__icon");
            visitBanner.Add(icon);
            var texts = new VisualElement();
            texts.AddToClassList("ug-visit__texts");
            visitTitle = UnitGitUi.Text(string.Empty, "ug-visit__title");
            texts.Add(visitTitle);
            visitDetail = UnitGitUi.Text(string.Empty, "ug-visit__detail");
            texts.Add(visitDetail);
            visitBanner.Add(texts);
            visitNewBranchButton = UnitGitUi.Pill("New Branch Here…", () => PromptCreateBranchFrom(snapshot?.DetachedCommit?.FullHash), "ghost",
                UnitGitIconKind.CreateBranch, "Make a branch at this commit and check it out: commits you make here then belong to it.");
            visitNewBranchButton.AddToClassList("ug-visit__button");
            visitBanner.Add(visitNewBranchButton);
            visitBackButton = UnitGitUi.Pill("Back", () => ReturnFromVisit(visitBackButton), "primary", UnitGitIconKind.Undo);
            visitBackButton.AddToClassList("ug-visit__button");
            visitBanner.Add(visitBackButton);
            return visitBanner;
        }

        private void UpdateVisitBanner()
        {
            if (visitBanner == null) return;
            bool ready = snapshot != null && snapshot.HasRepository && snapshot.HasCommits && string.IsNullOrWhiteSpace(snapshot.LastError);
            // A rebase also detaches HEAD: the Conflicts tab shows that one.
            bool visiting = ready && IsVisiting && UnitGitConflicts.Operation(gitService.ProjectRoot, out _) != UnitGitOperation.Rebase;
            UnitGitCommit commit = pendingVisit ?? (visiting ? snapshot.DetachedCommit : null);
            bool show = pendingReturnBranch != null || commit != null;
            visitBanner.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (!show) return;

            bool idle = !busy && commitWhenIncluded == null && pendingVisit == null && pendingReturnBranch == null;
            visitBackButton.SetEnabled(idle);
            visitNewBranchButton.SetEnabled(idle);
            if (pendingReturnBranch != null)
            {
                visitTitle.text = "Going back to ‘" + pendingReturnBranch + "’…";
                visitDetail.text = string.Empty;
                visitBanner.tooltip = null;
                return;
            }

            visitTitle.text = "Visiting " + commit.ShortHash;
            string branch = pendingVisit != null ? string.Empty : snapshot.ReturnBranch;
            var parts = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrWhiteSpace(commit.Subject)) parts.Add(Shorten(commit.Subject, 80));
            if (!string.IsNullOrWhiteSpace(commit.RelativeDate)) parts.Add(commit.RelativeDate);
            if (pendingVisit != null) parts.Add("checking out…");
            else if (branch.Length > 0 && snapshot.NewerCommits > 0)
                parts.Add(snapshot.NewerCommits + " newer commit" + (snapshot.NewerCommits == 1 ? string.Empty : "s") + " on ‘" + branch + "’");
            visitDetail.text = string.Join("  ·  ", parts);

            UnitGitVisit visit = UnitGitSettings.Visit;
            bool shelved = visit != null && visit.branch == branch && HasShelf(visit.shelf);
            UnitGitUi.SetText(visitBackButton, branch.Length > 0 ? "Back to ‘" + Shorten(branch, 28) + "’" : "Back to a Branch…");
            visitBackButton.tooltip = branch.Length > 0
                ? "Check out ‘" + branch + "’ again, at its latest commit" + (shelved ? ", and bring back the changes shelved before visiting." : ".")
                : "Choose the branch to check out again.";
            visitBanner.tooltip = "You are looking at the project as it was at " + commit.ShortHash + " (" + commit.RelativeDate + "). No branch is checked out " +
                                  "(Git calls it a detached HEAD): every branch keeps its later commits, and the Log still shows them." +
                                  (visit != null && !string.IsNullOrWhiteSpace(visit.branchTip) && branch.Length > 0 && visit.branch == branch
                                      ? "\n‘" + branch + "’ was at " + visit.branchTip.Substring(0, Math.Min(7, visit.branchTip.Length)) + " when the visit started."
                                      : string.Empty);
        }

        private bool HasShelf(string message)
        {
            return !string.IsNullOrWhiteSpace(message) && snapshot?.Shelves != null &&
                   snapshot.Shelves.Any(line => line.EndsWith(": " + message.Trim(), StringComparison.Ordinal));
        }

        private bool CanStartVisitAction()
        {
            if (gitService == null || snapshot == null) return false;
            if (busy || commitWhenIncluded != null)
            {
                ShowToast("Wait for " + (busyLabel ?? "the current Git operation") + " to finish.", error: true);
                return false;
            }

            if (HasConflictWork())
            {
                ShowToast("Finish or cancel the current operation first.", error: true, "Conflicts", () => SetActiveTab(UnitGitTab.Conflicts));
                return false;
            }

            return true;
        }

        // Unsaved scenes and assets would be lost, or written over the other version later: saved first (Cancel stops).
        private static bool SaveOpenWork()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return false;
            AssetDatabase.SaveAssets();
            return true;
        }

        private static string Timestamp() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        private static string Files(int count) => count + " file" + (count == 1 ? string.Empty : "s");

        private static string ChangedWhileVisitingText(int tracked, string visited)
        {
            return Files(tracked) + " changed while you were visiting " + visited + ". Unity re-saves some files when it opens an older version.\n\n" +
                   "Discard Changes puts them back as they were committed (new files stay). Shelve Them keeps them on the shelf.";
        }

        private void VisitCommit(UnitGitCommit commit)
        {
            if (commit == null || string.IsNullOrWhiteSpace(commit.FullHash) || !CanStartVisitAction()) return;
            if (!SaveOpenWork()) return;
            string root = gitService.ProjectRoot;
            UnitGitHeadState head = null;
            int tracked = 0, untracked = 0;
            ReadForPrompt(() =>
            {
                var service = new UnitGitService(root);
                GitCommandResult state = service.ReadHeadState(out head);
                return state.Success ? service.ReadChangeCount(out tracked, out untracked) : state;
            }, _ => ConfirmVisit(commit, head, tracked, untracked));
        }

        private void ConfirmVisit(UnitGitCommit commit, UnitGitHeadState head, int tracked, int untracked)
        {
            if (head == null || !CanStartVisitAction()) return;
            if (head.Head == commit.FullHash)
            {
                ShowToast(commit.ShortHash + " is already checked out.");
                return;
            }

            UnitGitVisit visit = UnitGitSettings.Visit;
            string shelf = null;
            bool discard = false;
            bool keepShelf = false;
            if (!string.IsNullOrEmpty(head.BranchRef))
            {
                // From a branch: its changes are set aside, and come back with it.
                int changes = tracked + untracked;
                if (changes > 0)
                {
                    if (!EditorUtility.DisplayDialog("Visit " + commit.ShortHash,
                            Files(changes) + " changed since your last commit on ‘" + head.BranchName + "’. Unit Git shelves them before visiting " +
                            commit.ShortHash + " and brings them back when you go back to ‘" + head.BranchName + "’.",
                            "Shelve " + changes + (changes == 1 ? " Change" : " Changes") + " and Visit", "Cancel"))
                        return;
                    shelf = "Before visiting " + commit.ShortHash + " (" + Timestamp() + ")";
                    keepShelf = true;
                }

                visit = new UnitGitVisit
                {
                    branch = head.BranchName,
                    branchTip = head.Head,
                    shelf = shelf ?? string.Empty,
                    started = DateTime.UtcNow.Ticks
                };
            }
            else
            {
                // Already visiting: the way back stays the first visit's. What changed while visiting goes, or is shelved apart.
                if (tracked > 0)
                {
                    int choice = EditorUtility.DisplayDialogComplex("Visit " + commit.ShortHash, ChangedWhileVisitingText(tracked, VisitedShortHash),
                        "Discard Changes", "Cancel", "Shelve Them");
                    if (choice == 1) return;
                    discard = choice == 0;
                    if (choice == 2) shelf = "Changed while visiting " + VisitedShortHash + " (" + Timestamp() + ")";
                }

                visit = visit ?? new UnitGitVisit { branch = snapshot.ReturnBranch, started = DateTime.UtcNow.Ticks };
            }

            visit.commit = commit.FullHash;
            // Kept before Git starts: a domain reload during the checkout still knows the way back.
            UnitGitSettings.Visit = visit;
            pendingVisit = commit;
            string hash = commit.FullHash;
            string shelfMessage = shelf;
            bool discardChanges = discard;
            bool shelved = false;
            RunAction("visit " + commit.ShortHash, () => gitService.VisitCommit(hash, shelfMessage, discardChanges, out shelved), result =>
            {
                visitActionDone = DateTime.UtcNow.Ticks;
                if (shelved) ForgetShelfFiles();
                if (keepShelf && !shelved && result != null && result.Success)
                {
                    // Nothing needed shelving after all.
                    var current = UnitGitSettings.Visit;
                    if (current != null) { current.shelf = string.Empty; UnitGitSettings.Visit = current; }
                }
                AssetDatabase.Refresh();
            }, "Visiting " + commit.ShortHash + "…", "Visiting " + commit.ShortHash + ": every later commit stays on its branch");
            UpdateTopBar();
        }

        private void ReturnFromVisit(VisualElement anchor)
        {
            if (!IsVisiting || !CanStartVisitAction()) return;
            string branch = snapshot.ReturnBranch;
            if (!string.IsNullOrWhiteSpace(branch))
            {
                ReturnFromVisitTo(branch);
                return;
            }

            var menu = new GenericMenu();
            var locals = snapshot.Branches.Where(candidate => !candidate.IsRemote).ToList();
            if (locals.Count == 0) menu.AddDisabledItem(new GUIContent("No local branch"));
            foreach (UnitGitBranch local in locals)
            {
                string name = local.Name;
                menu.AddItem(new GUIContent(name.Replace("/", "∕")), false, () => ReturnFromVisitTo(name));
            }
            if (anchor != null) menu.DropDown(anchor.worldBound);
            else menu.ShowAsContext();
        }

        private void ReturnFromVisitTo(string branch)
        {
            if (!IsVisiting || !CanStartVisitAction() || !SaveOpenWork()) return;
            string root = gitService.ProjectRoot;
            int tracked = 0;
            ReadForPrompt(() => new UnitGitService(root).ReadChangeCount(out tracked, out _), _ => ConfirmReturn(branch, tracked));
        }

        private void ConfirmReturn(string branch, int tracked)
        {
            if (!IsVisiting || !CanStartVisitAction()) return;
            string visited = VisitedShortHash;
            bool discard = false;
            string changedShelf = null;
            if (tracked > 0)
            {
                int choice = EditorUtility.DisplayDialogComplex("Back to ‘" + branch + "’", ChangedWhileVisitingText(tracked, visited),
                    "Discard Changes", "Cancel", "Shelve Them");
                if (choice == 1) return;
                discard = choice == 0;
                if (choice == 2) changedShelf = "Changed while visiting " + visited + " (" + Timestamp() + ")";
            }

            // The shelf made before visiting goes back onto its own branch only.
            UnitGitVisit visit = UnitGitSettings.Visit;
            string restore = visit != null && visit.branch == branch && HasShelf(visit.shelf) ? visit.shelf : string.Empty;
            visitReturning = restore.Length > 0;
            pendingReturnBranch = branch;
            bool discardChanges = discard;
            RunAction("back to " + branch, () =>
            {
                if (changedShelf != null)
                {
                    GitCommandResult shelve = gitService.ShelveAll(changedShelf);
                    if (!shelve.Success) return shelve;
                }
                return gitService.ReturnFromVisit(branch, discardChanges, restore);
            }, result =>
            {
                visitActionDone = DateTime.UtcNow.Ticks;
                ForgetShelfFiles();
                AssetDatabase.Refresh();
                if (result != null && !result.Success && HasConflictWork()) SetActiveTab(UnitGitTab.Conflicts);
            }, "Going back to ‘" + branch + "’…", "Back on ‘" + branch + "’" + (restore.Length > 0 ? ", with your changes from before the visit" : string.Empty));
            UpdateTopBar();
        }

        /// <summary>After each refresh: the pending state gives way to Git's, and a visit that ended (Back, a checkout, a new branch) is forgotten.</summary>
        private void ReconcileVisit(long readStarted)
        {
            if (pendingVisit != null || pendingReturnBranch != null)
            {
                if (visitActionDone == 0 || readStarted < visitActionDone) return;
                pendingVisit = null;
                pendingReturnBranch = null;
                visitActionDone = 0;
            }

            if (snapshot == null || !snapshot.HasRepository || !string.IsNullOrWhiteSpace(snapshot.LastError)) return;
            if (snapshot.IsDetached)
            {
                visitReturning = false;
                return;
            }

            UnitGitVisit visit = UnitGitSettings.Visit;
            if (visit == null || readStarted < visit.started) return;
            UnitGitSettings.Visit = null;
            bool returning = visitReturning;
            visitReturning = false;
            if (!returning && HasShelf(visit.shelf))
                ShowToast("Your changes from before the visit are on the shelf ‘" + Shorten(visit.shelf, 48) + "’", false, "Shelf", () => SetActiveTab(UnitGitTab.Shelf));
        }

        /// <summary>
        /// Commits made on a visited commit belong to no branch and leave the history on the way back: refused, with the way
        /// to keep them (a branch here). A rebase's own detached HEAD is left to Git.
        /// </summary>
        private bool RefuseWhileVisiting(string what)
        {
            if (snapshot == null || !snapshot.IsDetached || UnitGitConflicts.Operation(gitService.ProjectRoot, out _) == UnitGitOperation.Rebase) return false;
            string hash = snapshot.DetachedCommit?.FullHash;
            ShowToast("You are visiting " + VisitedShortHash + ": " + what + " would belong to no branch. Make a branch here first.", error: true,
                "New Branch Here…", () => PromptCreateBranchFrom(hash));
            return true;
        }
    }
}

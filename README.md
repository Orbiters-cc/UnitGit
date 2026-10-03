# Unit Git

## 0.2.4 — 2026-10-03

- Show before-and-after material spheres in Scene view comparisons alongside the material property diff.
- Import temporary material revisions without object-name/filename warnings and preserve the original material names.
- Include the previously unpushed commit workflow fixes for avatar work.

Unit Git is a Unity editor window for working with the Git repository at the root of the current Unity project.

Open it from `Tools > Orbiters > Unit Git`.

## Features

The window follows JetBrains IDEs' Git tool window, in the style of the other Orbiters tools.

- **Top bar**: the tabs (Changes with its count, Log, Shelf, Conflicts when needed, Backups, Console), what is running,
  the **branch widget** (current branch, commits to push and pull; click for every branch with Checkout, New branch
  from, Merge into current, Update and Delete) and Update (pull, fast-forward only), Push, Fetch, Refresh and Settings.
  Shortcuts: **Ctrl+K** commit, **Ctrl+Shift+K** push, **Ctrl+T** update, **F5** refresh. Results show as short
  notifications; failures stay longer and lead to the Console.
- **Changes**: every change with a box saying whether it goes into the next commit (Git's staging, per file or folder;
  a folder's box shows when only some of its files are in), grouped as changes and unversioned files, in folders
  (single-child chains shown as one row) or flat, with JetBrains' status colours and letters and a filter. Multi-select
  with Ctrl/Shift, arrows, Space to include or exclude, Delete to roll back; the context menu opens, reveals and copies
  paths. **Rollback** returns tracked files to their last commit and moves new files to the trash (assets) after a
  confirmation. Under the list: the message (Ctrl+Enter commits), **Amend** (edits the last commit and its message) and
  **Commit** / **Commit and Push**, which can be clicked while ticked files are still being added (the commit follows).
- **Diff**: side by side, as in JetBrains IDEs: line numbers at each side's edge, added/removed/changed lines tinted and
  the changed part of a changed line highlighted, a middle column joining the two versions of each change, and markers
  beside each side showing where every change is in the whole file (drag them to scroll, click to jump). Unchanged
  stretches fold away with 4 lines kept around each change (the band shows them all, its arrows 20 more lines; the
  toolbar switches folding off). A dropdown ignores whitespace as JetBrains does (trim, all, all and empty lines). It
  opens at the first change; the arrows (F7 / Shift+F7) move between changes, then on to the next file, or between
  matches while searching (Ctrl+F). In Changes, **»** puts one change of the file on disk back as its last commit had it
  (the notification can undo it). Scenes, prefabs and models also offer the Scene view and 3D (beta, below).
- **Log**: branches on the left (HEAD, local, remote in folders; a click shows that branch's history, a double click
  checks it out, a right click has the rest), the commit graph with every lane, branch and tag labels, avatar
  initials, dates and hashes, search (text or hash) and Branch, User and Date filters; older commits load as you
  scroll. Columns give way to the subject when the log is narrow. The selected commit's files (status colours, tree;
  a double click opens its diff in a window) sit above its message, author, date, hash (copy) and parents (a click
  selects them). A commit's menu: New branch from here, Cherry-Pick, Revert, Rename, Reset current branch to here
  (Soft, Mixed, Keep, Hard), Squash selected commits, copy hash or subject.
- **Shelf**: changes set aside (Git stashes) with their files and diff; **Shelve changes** asks for a name,
  **Unshelve** brings them back and removes the shelf, **Apply** keeps it, **Delete** asks first.
- **Console**: every Git command and its output, newest at the bottom, filterable and copyable.
- initialize a missing project-root Git repository with a VRChat Unity `.gitignore`
- keep MCB's downloaded version patches (`Assets/MCB/assets/*/versions/**/*.bin`) out of Git, also in existing
  repositories; when some were committed earlier, Changes offers **Stop tracking** (they stay on disk)
- connect a GitHub or GitLab remote with their command line tools (Settings or the Log's branches)

Only destructive operations ask first: deleting a branch or a shelf, rolling files back, resetting, squashing or
rewriting commits. Commit, pull (fast-forward only), push (never forced), checkout and merge run at once; Git refuses
anything that would lose local changes.

### Backups

The **Backups** tab saves the whole history (every commit of every branch, as one `git bundle` file) to a folder of
your choice, by default `Documents/Unit Git Backups/<project>`. Put it on another drive or in a synced folder
(OneDrive, Google Drive, Dropbox) to survive a broken disk or a deleted project. Each backup is read back in full to
check it; **Check** does it again later, **Restore…** recreates the project in a new, empty folder (this project is not
touched), and older backups beyond **Keep the last** are deleted. **Back up automatically once a day** makes a backup in
the background when Unity opens the project and the history changed since the last one. Uncommitted changes and
ignored files (Library, downloads) are not part of a backup; Git LFS files are kept as pointers.

### Scene view and 3D (beta)

For scenes, prefabs and other Unity YAML files, the diff pane of Local Changes opens on **Scene view**: one card per
GameObject (or prefab instance) with its components and each changed value as before → after, rotations in degrees and
references by name. VRChat, VRCFury and Modular Avatar components carry their badge. **Text** shows the raw lines.
Unity's bookkeeping fields (hints, serialization versions, object order) are left out.

Models (`.fbx`, `.obj`) and prefabs also have a **3D** view: both versions side by side under one camera (drag to turn,
right-drag to move, scroll to zoom, double-click to reset), each coloured by what changed: moved vertices from yellow to
red, whole parts moved, other materials, other blendshapes or bones, added parts. A table lists every mesh with its
numbers (vertices moved and how far, vertex counts, blendshapes added, removed or reshaped). Models are read straight
from the file with Orbiters Toolkit's mesh comparison, so nothing is imported; prefab versions are imported into `Assets/__UnitGit Compare` (listed in the
repository's local exclude file) and deleted afterwards. In the Log, **Compare** on a scene, prefab or model of a
commit opens the same views for what that commit changed.

### Conflicts (beta)

When a merge, a shelf or another Git operation leaves conflicting files, a **Conflicts** tab appears with their count.
For each file:

- scenes and prefabs go through Unity's own merge first (`UnityYAMLMerge`, never with an external fallback tool): it
  merges object by object and lists the values it could not decide, with both sides;
- text is split into parts, each chosen as **Mine**, **Theirs** or **Both**, labelled with the object it belongs to;
- **Keep mine** / **Take theirs** keep a whole side (models, images and other binary files only offer this, with the
  3D view to compare them);
- a file deleted on one side is kept or deleted.

The banner shows the progress, then **Finish merge** records the result with Git's prepared message; **Cancel merge**
goes back to before it started.

## Package Structure

- `Runtime`: package metadata constants
- `Editor`: Git subprocess wrapper, project initializer, UI Toolkit window, and USS styling
- `Tests/Editor`: parser/process and temp-repo smoke tests for the editor package

## Validation

Run deterministic package checks from `Tools > Orbiters > Unit Git > Health Checks > All Deterministic`.

For release automation, call the same hook in Unity batchmode:

```powershell
Unity.exe -batchmode -nographics -projectPath "<project>" -executeMethod Orbiters.UnitGit.Editor.UnitGitEditorHealthChecks.RunAllBatchmode -quit
```

## Git

This folder is initialized as its own Git repository so it can be versioned independently from the main Unity project.

## Releases and VPM

Version changes pushed to `master` run **Build Release**. Published releases are
imported through the signed release webhook into the shared Orbiters VPM feed at
<https://orbiters.cc/vpm/orbiters/index.json>, alongside My Avatar and Toolkit.
This package does not build a separate GitHub Pages listing.

# Unit Git

Unit Git is a Unity editor window for working with the Git repository at the root of the current Unity project.

Open it from `Tools > Orbiters > Unit Git`.

## Features

- browse local and remote branches
- inspect commit history and changed files
- view project status and local changes
- stage, unstage, commit, fetch, pull, checkout, and shelve changes
- initialize a missing project-root Git repository with a VRChat Unity `.gitignore`
- keep MCB's downloaded version patches (`Assets/MCB/assets/*/versions/**/*.bin`) out of Git, also in existing
  repositories; when some were committed earlier, Local Changes offers **Stop tracking** (they stay on disk)
- merge a branch into the current one (**Merge into current** under the branches)

Unit Git intentionally does not expose a push action. Pushes should happen only from an explicit external request or workflow.

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

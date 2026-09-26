# Unit Git

Unit Git is a Unity editor window for working with the Git repository at the root of the current Unity project.

Open it from `Tools > Orbiters > Unit Git`.

## Features

- browse local and remote branches
- inspect commit history and changed files
- view project status and local changes
- stage, unstage, commit, fetch, pull, checkout, and shelve changes
- initialize a missing project-root Git repository with a VRChat Unity `.gitignore`

Unit Git intentionally does not expose a push action. Pushes should happen only from an explicit external request or workflow.

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

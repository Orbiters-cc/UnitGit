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
        private VisualElement BuildReleaseDetailsPanel(UnitGitReleaseEntry release)
        {
            var panel = new VisualElement();
            panel.AddToClassList("unitgit-files-panel");
            panel.AddToClassList("unitgit-release-panel");

            string headerDetail = string.IsNullOrWhiteSpace(release.tool)
                ? "Release checkpoint"
                : "published with " + release.tool;
            panel.Add(BuildSectionHeader("Release", headerDetail));

            var scroll = new ScrollView();
            scroll.name = "unitgit-release-details-scroll";
            scroll.AddToClassList("unitgit-files-scroll");

            var card = new VisualElement();
            card.AddToClassList("unitgit-release-card");
            Color branchColor = selectedCommit != null ? GetGraphColor(selectedCommit) : new Color(0f, 0.85f, 0.43f);
            SetBorderColor(card, branchColor);

            var headline = new VisualElement();
            headline.AddToClassList("unitgit-release-headline");

            string displayName = !string.IsNullOrWhiteSpace(release.name)
                ? release.name
                : (selectedCommit != null ? selectedCommit.Subject : "Release");
            var name = new Label(displayName);
            name.AddToClassList("unitgit-release-details-name");
            headline.Add(name);

            if (!string.IsNullOrWhiteSpace(release.version))
            {
                var pill = new Label(release.version);
                pill.AddToClassList("unitgit-release-pill");
                pill.style.color = branchColor;
                SetBorderColor(pill, branchColor);
                headline.Add(pill);
            }

            if (!string.IsNullOrWhiteSpace(release.scope))
            {
                var scope = new Label(release.scope);
                scope.AddToClassList("unitgit-release-scope");
                headline.Add(scope);
            }

            card.Add(headline);

            if (!string.IsNullOrWhiteSpace(release.title))
            {
                var title = new Label(release.title);
                title.AddToClassList("unitgit-release-details-title");
                card.Add(title);
            }

            VisualElement thumbnail = BuildReleaseThumbnail(release);
            if (thumbnail != null)
            {
                card.Add(thumbnail);
            }

            string formattedDate = FormatReleaseDate(release.date);
            if (!string.IsNullOrWhiteSpace(formattedDate))
            {
                card.Add(BuildReleaseMetaRow("Released", formattedDate));
            }

            if (!string.IsNullOrWhiteSpace(release.author))
            {
                card.Add(BuildReleaseMetaRow("Author", release.author));
            }
            else if (selectedCommit != null && !string.IsNullOrWhiteSpace(selectedCommit.AuthorName))
            {
                card.Add(BuildReleaseMetaRow("Author", selectedCommit.AuthorName));
            }

            if (!string.IsNullOrWhiteSpace(release.type))
            {
                card.Add(BuildReleaseMetaRow("Type", release.type));
            }

            if (release.fields != null)
            {
                foreach (UnitGitReleaseField field in release.fields)
                {
                    if (field != null && !string.IsNullOrWhiteSpace(field.key) && !string.IsNullOrWhiteSpace(field.value))
                    {
                        card.Add(BuildReleaseMetaRow(field.key, field.value));
                    }
                }
            }

            if (selectedCommit != null && !string.IsNullOrWhiteSpace(selectedCommit.ShortHash))
            {
                card.Add(BuildReleaseMetaRow("Commit", selectedCommit.ShortHash));
            }

            scroll.Add(card);

            var changelogHeader = new Label("Changelog");
            changelogHeader.AddToClassList("unitgit-release-changelog-header");
            scroll.Add(changelogHeader);

            var changelog = new Label(string.IsNullOrWhiteSpace(release.changelog)
                ? "No changelog was provided for this release."
                : release.changelog);
            changelog.AddToClassList("unitgit-release-changelog");
            scroll.Add(changelog);

            panel.Add(scroll);
            return panel;
        }

        private VisualElement BuildReleaseMetaRow(string key, string value)
        {
            var row = new VisualElement();
            row.AddToClassList("unitgit-release-meta-row");

            var keyLabel = new Label(key);
            keyLabel.AddToClassList("unitgit-release-meta-key");
            row.Add(keyLabel);

            var valueLabel = new Label(value);
            valueLabel.AddToClassList("unitgit-release-meta-value");
            row.Add(valueLabel);

            return row;
        }

        private VisualElement BuildReleaseThumbnail(UnitGitReleaseEntry release)
        {
            string fullPath = ResolveReleaseProjectPath(release != null ? release.thumbnailPath : string.Empty);
            if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
            {
                return null;
            }

            try
            {
                var texture = new Texture2D(2, 2);
                if (!texture.LoadImage(File.ReadAllBytes(fullPath)))
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                    return null;
                }

                texture.name = Path.GetFileName(fullPath);

                var frame = new VisualElement();
                frame.AddToClassList("unitgit-release-thumbnail-frame");
                frame.RegisterCallback<DetachFromPanelEvent>(_ =>
                {
                    if (texture != null)
                    {
                        UnityEngine.Object.DestroyImmediate(texture);
                    }
                });

                var image = new Image
                {
                    image = texture,
                    scaleMode = ScaleMode.ScaleToFit
                };
                image.AddToClassList("unitgit-release-thumbnail");
                frame.Add(image);
                return frame;
            }
            catch
            {
                return null;
            }
        }

        private string ResolveReleaseProjectPath(string projectRelativePath)
        {
            if (string.IsNullOrWhiteSpace(projectRelativePath) || Path.IsPathRooted(projectRelativePath))
            {
                return string.Empty;
            }

            string projectRoot = snapshot != null && !string.IsNullOrWhiteSpace(snapshot.ProjectRoot)
                ? snapshot.ProjectRoot
                : (gitService != null ? gitService.ProjectRoot : string.Empty);
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                return string.Empty;
            }

            string rootFullPath = Path.GetFullPath(projectRoot);
            string candidatePath = Path.GetFullPath(Path.Combine(
                rootFullPath,
                projectRelativePath.Trim().Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
            if (!IsPathInsideRoot(rootFullPath, candidatePath))
            {
                return string.Empty;
            }

            return candidatePath;
        }

        private static bool IsPathInsideRoot(string rootFullPath, string candidatePath)
        {
            string root = rootFullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                          + Path.DirectorySeparatorChar;
            string candidate = candidatePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                               + Path.DirectorySeparatorChar;
            return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatReleaseDate(string isoDate)
        {
            if (string.IsNullOrWhiteSpace(isoDate))
            {
                return string.Empty;
            }

            if (DateTime.TryParse(isoDate, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime parsed))
            {
                return parsed.ToLocalTime().ToString("MM/dd/yyyy hh:mm tt");
            }

            return isoDate;
        }

        private UnitGitReleaseEntry GetReleaseForCommit(UnitGitCommit commit)
        {
            if (commit == null || !commit.HasRelease)
            {
                return null;
            }

            UnitGitReleaseFile releases = snapshot != null ? snapshot.Releases : null;
            if (UnitGitReleases.IsHidden(releases, commit.ReleaseId))
            {
                return null;
            }

            UnitGitReleaseEntry release = UnitGitReleases.FindById(releases, commit.ReleaseId);
            if (release != null)
            {
                return release;
            }

            // The releases file no longer contains this entry; fall back to commit data so the
            // checkpoint stays visible in the history.
            return new UnitGitReleaseEntry
            {
                id = commit.ReleaseId.Trim(),
                name = commit.Subject,
                author = commit.AuthorName
            };
        }

        private static void SetBorderColor(VisualElement element, Color color)
        {
            element.style.borderTopColor = color;
            element.style.borderRightColor = color;
            element.style.borderBottomColor = color;
            element.style.borderLeftColor = color;
        }

        private VisualElement BuildReleaseCheckpointRow(UnitGitCommit commit, UnitGitReleaseEntry release)
        {
            VisualElement row = BuildSelectableRow(evt => HandleReleaseRowMouseDown(evt, commit, release), () => SelectCommitFromRow(commit, true));
            row.AddToClassList("unitgit-release-row");

            // The checkpoint follows the color of the branch its commit belongs to.
            Color branchColor = GetGraphColor(commit);
            bool isSelected = selectedCommit != null &&
                              selectedCommit.FullHash == commit.FullHash &&
                              string.Equals(selectedReleaseId, commit.ReleaseId, StringComparison.Ordinal);
            if (isSelected)
            {
                row.AddToClassList("unitgit-release-row--selected");
            }

            SetBorderColor(row, isSelected ? Color.Lerp(branchColor, Color.white, 0.35f) : branchColor);
            row.style.backgroundColor = new Color(branchColor.r, branchColor.g, branchColor.b, isSelected ? 0.16f : 0.06f);

            string displayName = !string.IsNullOrWhiteSpace(release.name)
                ? release.name
                : (!string.IsNullOrWhiteSpace(release.tool) ? release.tool : "Release");
            var name = new Label(Shorten(displayName, 32));
            name.AddToClassList("unitgit-release-name");
            row.Add(name);

            if (!string.IsNullOrWhiteSpace(release.version))
            {
                var pill = new Label(Shorten(release.version, 16));
                pill.AddToClassList("unitgit-release-pill");
                pill.style.color = branchColor;
                SetBorderColor(pill, branchColor);
                row.Add(pill);
            }

            if (!string.IsNullOrWhiteSpace(release.title))
            {
                var title = new Label(Shorten(release.title, 48));
                title.AddToClassList("unitgit-release-title");
                row.Add(title);
            }

            row.tooltip = "Release checkpoint" +
                          (string.IsNullOrWhiteSpace(release.tool) ? string.Empty : " published by " + release.tool) +
                          ". Click to inspect the release details.";
            return row;
        }
    }
}

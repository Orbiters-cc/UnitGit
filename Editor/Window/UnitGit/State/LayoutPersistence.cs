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
        private void RememberScrollOffsets()
        {
            if (contentRoot == null)
            {
                return;
            }

            foreach (var scrollView in contentRoot.Query<ScrollView>().ToList())
            {
                if (!string.IsNullOrEmpty(scrollView.name))
                {
                    scrollOffsets[scrollView.name] = scrollView.scrollOffset;
                }
            }
        }

        private void RestoreScrollOffsets()
        {
            if (contentRoot == null)
            {
                return;
            }

            foreach (var scrollView in contentRoot.Query<ScrollView>().ToList())
            {
                if (string.IsNullOrEmpty(scrollView.name) ||
                    !scrollOffsets.TryGetValue(scrollView.name, out var offset))
                {
                    continue;
                }

                scrollView.schedule.Execute(() => scrollView.scrollOffset = offset);
            }
        }

        private TwoPaneSplitView BuildTrackedSplit(string prefKey, int fixedPaneIndex, float defaultFixedPaneSize)
        {
            float fixedPaneSize = splitSizes.TryGetValue(prefKey, out float sessionSize)
                ? sessionSize
                : EditorPrefs.GetFloat(prefKey, defaultFixedPaneSize);
            var split = new TwoPaneSplitView(
                fixedPaneIndex,
                fixedPaneSize,
                TwoPaneSplitViewOrientation.Horizontal);
            split.AddToClassList("unitgit-split-view");
            TrackSplitDrag(split, prefKey, fixedPaneIndex);
            return split;
        }

        private void TrackSplitDrag(TwoPaneSplitView split, string prefKey, int fixedPaneIndex)
        {
            bool handlersInstalled = false;
            bool isDragging = false;
            split.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (IsPointerNearSplitDivider(split, fixedPaneIndex, evt.position))
                {
                    isDragging = true;
                }
            });
            split.RegisterCallback<PointerUpEvent>(_ =>
            {
                if (!isDragging)
                {
                    return;
                }

                isDragging = false;
                SaveSplitSize(split, prefKey, fixedPaneIndex);
            });
            split.RegisterCallback<PointerCaptureOutEvent>(_ =>
            {
                if (!isDragging)
                {
                    return;
                }

                isDragging = false;
                SaveSplitSize(split, prefKey, fixedPaneIndex);
            });

            split.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                if (handlersInstalled)
                {
                    if (isDragging)
                    {
                        SaveSplitSize(split, prefKey, fixedPaneIndex);
                    }

                    return;
                }

                VisualElement dragline = split.Q(className: "unity-two-pane-split-view__dragline-anchor")
                    ?? split.Q(className: "unity-two-pane-split-view__dragline");
                if (dragline == null)
                {
                    return;
                }

                handlersInstalled = true;
                dragline.RegisterCallback<PointerDownEvent>(_ => isDragging = true);
                dragline.RegisterCallback<PointerUpEvent>(_ =>
                {
                    if (!isDragging)
                    {
                        return;
                    }

                    isDragging = false;
                    SaveSplitSize(split, prefKey, fixedPaneIndex);
                });
            });
        }

        private static bool IsPointerNearSplitDivider(TwoPaneSplitView split, int fixedPaneIndex, Vector2 worldPosition)
        {
            if (split == null || split.childCount <= fixedPaneIndex)
            {
                return false;
            }

            Vector2 localPosition = split.WorldToLocal(worldPosition);
            float dividerX = fixedPaneIndex == 0
                ? split[fixedPaneIndex].resolvedStyle.width
                : split.resolvedStyle.width - split[fixedPaneIndex].resolvedStyle.width;
            return Mathf.Abs(localPosition.x - dividerX) <= 10f;
        }

        private void SaveSplitSize(TwoPaneSplitView split, string prefKey, int fixedPaneIndex)
        {
            if (split == null || split.childCount <= fixedPaneIndex)
            {
                return;
            }

            var fixedPane = split[fixedPaneIndex];
            float width = fixedPane.resolvedStyle.width;
            if (width >= 160f && width <= 1400f)
            {
                splitSizes[prefKey] = width;
                EditorPrefs.SetFloat(prefKey, width);
            }
        }

        private bool GetFoldoutExpanded(string prefKey, bool defaultValue)
        {
            return EditorPrefs.GetBool(prefKey, defaultValue);
        }

        private void ToggleFoldout(string prefKey, bool defaultValue)
        {
            EditorPrefs.SetBool(prefKey, !GetFoldoutExpanded(prefKey, defaultValue));
            RebuildContent();
        }

        private static string GetFoldoutPrefKey(string scope, string value)
        {
            return FoldPrefPrefix + scope + "." + (value ?? string.Empty)
                .Replace('\\', '/')
                .Replace('/', '.')
                .Replace(' ', '_');
        }
    }
}

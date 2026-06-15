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
        private VisualElement BuildSectionHeader(string title, string detail)
        {
            var header = new VisualElement();
            header.AddToClassList("unitgit-section-header");

            var titleLabel = new Label(title);
            titleLabel.AddToClassList("unitgit-section-title");
            header.Add(titleLabel);

            var detailLabel = new Label(detail);
            detailLabel.AddToClassList("unitgit-section-detail");
            header.Add(detailLabel);

            return header;
        }

        private VisualElement BuildMessagePanel(string title, string body)
        {
            var panel = new VisualElement();
            panel.AddToClassList("unitgit-message-panel");

            var titleLabel = new Label(title);
            titleLabel.AddToClassList("unitgit-message-title");
            panel.Add(titleLabel);

            var bodyLabel = new Label(body);
            bodyLabel.AddToClassList("unitgit-message-body");
            panel.Add(bodyLabel);

            return panel;
        }

        private VisualElement BuildEmptyState(string text)
        {
            var label = new Label(text);
            label.AddToClassList("unitgit-empty");
            return label;
        }

        private Button BuildFoldoutButton(string title, string detail, bool expanded, Action toggle, params string[] classNames)
        {
            var button = new Button();
            button.AddToClassList("unitgit-foldout-button");
            foreach (string className in classNames)
            {
                if (!string.IsNullOrWhiteSpace(className))
                {
                    button.AddToClassList(className);
                }
            }

            var arrow = new UnitGitIconElement(expanded ? UnitGitIconKind.ChevronExpanded : UnitGitIconKind.ChevronCollapsed);
            arrow.AddToClassList("unitgit-foldout-arrow");
            button.Add(arrow);

            var titleLabel = new Label(title);
            titleLabel.AddToClassList("unitgit-foldout-title");
            button.Add(titleLabel);

            if (!string.IsNullOrWhiteSpace(detail))
            {
                var detailLabel = new Label(detail);
                detailLabel.AddToClassList("unitgit-foldout-detail");
                button.Add(detailLabel);
            }

            bool handledOnMouseDown = false;
            button.RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.button != 0)
                {
                    return;
                }

                handledOnMouseDown = true;
                toggle();
                evt.StopPropagation();
            });
            button.clicked += () =>
            {
                if (handledOnMouseDown)
                {
                    handledOnMouseDown = false;
                    return;
                }

                toggle();
            };
            return button;
        }

        private Label BuildToolbarChip(string text)
        {
            var chip = new Label(text);
            chip.AddToClassList("unitgit-toolbar-chip");
            return chip;
        }

        private Label BuildHeaderLabel(string text, string className)
        {
            var label = new Label(text);
            label.AddToClassList("unitgit-log-header-label");
            label.AddToClassList(className);
            return label;
        }

        private Button BuildActionButton(string text, string extraClass, Action action)
        {
            var button = new Button(() => action());
            button.text = text;
            button.AddToClassList("unitgit-button");
            if (!string.IsNullOrWhiteSpace(extraClass))
            {
                button.AddToClassList(extraClass);
            }

            return button;
        }
    }
}

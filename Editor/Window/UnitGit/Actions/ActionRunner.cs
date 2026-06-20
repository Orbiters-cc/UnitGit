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
        private void RunAction(string label, Func<GitCommandResult> action, Action<GitCommandResult> onComplete = null)
        {
            if (busy)
            {
                return;
            }

            busy = true;
            AppendConsole(label, "started");
            BuildShell();
            RebuildContent();
            EnsureEditorUpdatePump();

            Task.Run(() =>
                {
                    gitService.ProcessLogReceived = line => QueueConsoleLine(label, line);
                    try
                    {
                        return ExecuteGitAction(action);
                    }
                    finally
                    {
                        gitService.ProcessLogReceived = null;
                    }
                })
                .ContinueWith(task => QueueMainThreadAction(() => CompleteGitAction(label, task, onComplete)));
        }

        private static GitCommandResult ExecuteGitAction(Func<GitCommandResult> action)
        {
            try
            {
                return action();
            }
            catch (Exception ex)
            {
                return new GitCommandResult
                {
                    ExitCode = 1,
                    StandardError = ex.Message
                };
            }
        }

        private void CompleteGitAction(string label, Task<GitCommandResult> task, Action<GitCommandResult> onComplete)
        {
            if (this == null)
            {
                return;
            }

            busy = false;
            GitCommandResult result = task.Status == TaskStatus.RanToCompletion
                ? task.Result
                : new GitCommandResult
                {
                    ExitCode = 1,
                    StandardError = task.Exception != null ? task.Exception.GetBaseException().Message : "The command failed."
                };
            AppendConsole(label, result);
            if (result == null || !result.Success)
            {
                string message = result != null && !string.IsNullOrWhiteSpace(result.Message)
                    ? result.Message
                    : "The command failed.";
                EditorUtility.DisplayDialog("Unit Git", message, "OK");
            }

            try
            {
                onComplete?.Invoke(result);
            }
            catch (Exception ex)
            {
                AppendConsole(label, "completion callback failed: " + ex.Message);
            }

            RefreshSnapshot();
        }
    }
}

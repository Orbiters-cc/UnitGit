using System;
using System.IO;
using UnityEditor;

namespace Orbiters.UnitGit.Editor
{
    internal static class UnitGitSettings
    {
        private const string PrefPrefix = "Orbiters.UnitGit.Project.";
        private const string AvatarUploadCommitKey = "AvatarUpload.Commit";
        private const string AvatarUploadReleaseRowKey = "AvatarUpload.ReleaseRow";

        public static bool AvatarUploadCommitEnabled
        {
            get { return GetBool(AvatarUploadCommitKey, true); }
            set { SetBool(AvatarUploadCommitKey, value); }
        }

        public static bool AvatarUploadReleaseRowEnabled
        {
            get { return GetBool(AvatarUploadReleaseRowKey, true); }
            set { SetBool(AvatarUploadReleaseRowKey, value); }
        }

        private static bool GetBool(string key, bool defaultValue)
        {
            return EditorPrefs.GetBool(BuildProjectKey(key), defaultValue);
        }

        private static void SetBool(string key, bool value)
        {
            EditorPrefs.SetBool(BuildProjectKey(key), value);
        }

        private static string BuildProjectKey(string key)
        {
            string projectRoot = string.Empty;
            try
            {
                projectRoot = Path.GetFullPath(UnitGitService.GetUnityProjectRoot())
                    .Replace('\\', '/')
                    .ToLowerInvariant();
            }
            catch
            {
                projectRoot = "unknown";
            }

            return PrefPrefix + StableHash(projectRoot) + "." + key;
        }

        private static string StableHash(string value)
        {
            unchecked
            {
                const ulong offset = 14695981039346656037UL;
                const ulong prime = 1099511628211UL;
                ulong hash = offset;
                foreach (char character in value ?? string.Empty)
                {
                    hash ^= character;
                    hash *= prime;
                }

                return hash.ToString("x16");
            }
        }
    }
}

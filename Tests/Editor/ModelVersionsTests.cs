using System.IO;
using System.Linq;
using NUnit.Framework;
using Orbiters.UnitGit.Editor.Semantic;
using UnityEditor;
using UnityEngine;

namespace Orbiters.UnitGit.Editor.Tests
{
    public sealed class ModelVersionsTests
    {
        // The reader must place meshes exactly where Unity's importer does, or the 3D compare would show false changes.
        [Test]
        public void FbxReaderMatchesUnitysImport()
        {
            string path = AssetDatabase.FindAssets("t:Model").Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => p.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase) && p.StartsWith("Assets/", System.StringComparison.Ordinal))
                .OrderBy(p => new FileInfo(p).Length).FirstOrDefault(p => AssetDatabase.LoadAssetAtPath<GameObject>(p)?.GetComponentInChildren<Renderer>(true) != null);
            if (path == null) Assert.Ignore("This project has no FBX model to compare with.");
            var bytes = File.ReadAllBytes(path);
            if (!FbxReader.IsBinary(bytes)) Assert.Ignore("The smallest FBX of this project is not binary.");
            var parsed = FbxReader.Read(bytes);
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var mesh = renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null) continue;
                var mine = parsed.FirstOrDefault(m => m.Path.EndsWith(renderer.name, System.StringComparison.Ordinal));
                Assert.That(mine, Is.Not.Null, "No mesh read for " + renderer.name);
                var toRoot = root.transform.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
                var unity = GeometryUtility.CalculateBounds(mesh.vertices, toRoot);
                var read = GeometryUtility.CalculateBounds(mine.Points, Matrix4x4.identity);
                Assert.That(Vector3.Distance(unity.center, read.center), Is.LessThan(unity.size.magnitude * 0.01f + 1e-4f), renderer.name + " centre");
                Assert.That(Vector3.Distance(unity.size, read.size), Is.LessThan(unity.size.magnitude * 0.01f + 1e-4f), renderer.name + " size");
                Assert.That(mine.Shapes.Count, Is.EqualTo(mesh.blendShapeCount), renderer.name + " blendshapes");
            }
        }

        [Test]
        public void SubdividedSurfaceIsNotReportedAsMoved()
        {
            var square = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0) };
            var triangles = new[] { 0, 1, 2, 0, 2, 3 };
            var subdivided = new[] { new Vector3(0.5f, 0.5f, 0f), new Vector3(0.5f, 0f, 0f), new Vector3(0.25f, 0.75f, 0f) };
            Assert.That(ModelVersions.SurfaceDistances(square, triangles, subdivided, 1.4f).Max(), Is.LessThan(1e-5f));
            var pushed = new[] { new Vector3(0.5f, 0.5f, 0.2f) };
            Assert.That(ModelVersions.SurfaceDistances(square, triangles, pushed, 1.4f)[0], Is.EqualTo(0.2f).Within(1e-4f));
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Orbiters.UnitGit.Editor.Semantic
{
    /// <summary>One mesh of an FBX file, in the file root's space and Unity's axes (metres, Y up, left-handed).</summary>
    internal sealed class FbxMesh
    {
        public string Path = string.Empty;
        public Vector3[] Points = new Vector3[0];
        public int[] Triangles = new int[0];
        public string[] Materials = new string[0];
        // Blendshape name → a signature of its offsets: equal signatures, same shape.
        public readonly Dictionary<string, long> Shapes = new Dictionary<string, long>();
        public int Bones;
    }

    /// <summary>
    /// Reads the geometry of a binary FBX file without Unity's importer: meshes (points and polygons), their place in the
    /// hierarchy, blendshapes, bones and material names. Used for comparing model versions, so nothing is imported into the
    /// project (an avatar's import cache can take gigabytes). Safe to run off the main thread.
    /// </summary>
    internal static class FbxReader
    {
        private sealed class Node
        {
            public string Name = string.Empty;
            public readonly List<object> Properties = new List<object>();
            public readonly List<Node> Children = new List<Node>();

            public Node Child(string name) => Children.FirstOrDefault(c => c.Name == name);
            public IEnumerable<Node> All(string name) => Children.Where(c => c.Name == name);
        }

        // An array property, decoded when first read.
        private sealed class ArrayProperty
        {
            public char Type;
            public int Length;
            public bool Compressed;
            public byte[] Data;

            private byte[] Bytes()
            {
                if (!Compressed) return Data;
                using (var input = new MemoryStream(Data, 2, Data.Length - 2))
                using (var inflate = new DeflateStream(input, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    inflate.CopyTo(output);
                    return output.ToArray();
                }
            }

            public double[] Doubles()
            {
                var bytes = Bytes();
                var result = new double[Length];
                if (Type == 'd') Buffer.BlockCopy(bytes, 0, result, 0, Length * 8);
                else if (Type == 'f') for (int i = 0; i < Length; i++) result[i] = BitConverter.ToSingle(bytes, i * 4);
                return result;
            }

            public int[] Ints()
            {
                var bytes = Bytes();
                var result = new int[Length];
                if (Type == 'i') Buffer.BlockCopy(bytes, 0, result, 0, Length * 4);
                else if (Type == 'l') for (int i = 0; i < Length; i++) result[i] = (int)BitConverter.ToInt64(bytes, i * 8);
                return result;
            }

            public long Signature()
            {
                // FNV-1a over the stored bytes: two equal arrays sign the same.
                unchecked
                {
                    long hash = (long)14695981039346656037UL;
                    foreach (byte b in Bytes()) { hash ^= b; hash *= 1099511628211L; }
                    return hash;
                }
            }
        }

        public static bool IsBinary(byte[] bytes)
        {
            return bytes.Length > 27 && Encoding.ASCII.GetString(bytes, 0, 18) == "Kaydara FBX Binary";
        }

        public static List<FbxMesh> Read(byte[] bytes)
        {
            if (!IsBinary(bytes)) throw new NotSupportedException("Only binary FBX files can be shown in 3D (this one is text FBX).");
            uint version = BitConverter.ToUInt32(bytes, 23);
            bool wide = version >= 7500;
            int position = 27;
            var root = new Node();
            while (position < bytes.Length)
            {
                var node = ReadNode(bytes, ref position, wide);
                if (node == null) break;
                root.Children.Add(node);
            }
            return Build(root);
        }

        private static Node ReadNode(byte[] bytes, ref int position, bool wide)
        {
            long end = wide ? (long)BitConverter.ToUInt64(bytes, position) : BitConverter.ToUInt32(bytes, position);
            long count = wide ? (long)BitConverter.ToUInt64(bytes, position + 8) : BitConverter.ToUInt32(bytes, position + 4);
            position += wide ? 24 : 12;
            int nameLength = bytes[position++];
            if (end == 0) return null;
            var node = new Node { Name = Encoding.ASCII.GetString(bytes, position, nameLength) };
            position += nameLength;
            for (long i = 0; i < count; i++) node.Properties.Add(ReadProperty(bytes, ref position));
            while (position < end)
            {
                var child = ReadNode(bytes, ref position, wide);
                if (child == null) break;
                node.Children.Add(child);
            }
            position = (int)end;
            return node;
        }

        private static object ReadProperty(byte[] bytes, ref int position)
        {
            char type = (char)bytes[position++];
            switch (type)
            {
                case 'Y': position += 2; return (long)BitConverter.ToInt16(bytes, position - 2);
                case 'C': position += 1; return (long)bytes[position - 1];
                case 'I': position += 4; return (long)BitConverter.ToInt32(bytes, position - 4);
                case 'F': position += 4; return (double)BitConverter.ToSingle(bytes, position - 4);
                case 'D': position += 8; return BitConverter.ToDouble(bytes, position - 8);
                case 'L': position += 8; return BitConverter.ToInt64(bytes, position - 8);
                case 'S':
                case 'R':
                {
                    int length = BitConverter.ToInt32(bytes, position);
                    position += 4;
                    object value = type == 'S' ? (object)Encoding.UTF8.GetString(bytes, position, length) : null;
                    position += length;
                    return value;
                }
                case 'f': case 'd': case 'l': case 'i': case 'b':
                {
                    int length = BitConverter.ToInt32(bytes, position);
                    int encoding = BitConverter.ToInt32(bytes, position + 4);
                    int stored = BitConverter.ToInt32(bytes, position + 8);
                    position += 12;
                    var data = new byte[stored];
                    Buffer.BlockCopy(bytes, position, data, 0, stored);
                    position += stored;
                    return new ArrayProperty { Type = type, Length = length, Compressed = encoding == 1, Data = data };
                }
                default:
                    throw new InvalidDataException("Unknown FBX property type '" + type + "'.");
            }
        }

        // ---- Scene ---------------------------------------------------------------------------------------------------------

        private sealed class Model
        {
            public long Id;
            public string Name = string.Empty;
            public string Kind = string.Empty;
            public long Parent;
            public Matrix4x4 Local = Matrix4x4.identity;
            public Matrix4x4 Geometric = Matrix4x4.identity;
        }

        private static List<FbxMesh> Build(Node root)
        {
            var objects = root.Child("Objects") ?? new Node();
            var connections = (root.Child("Connections") ?? new Node()).All("C").ToList();
            var parentOf = new Dictionary<long, List<long>>();
            var childrenOf = new Dictionary<long, List<long>>();
            foreach (var c in connections)
            {
                if (c.Properties.Count < 3 || !(c.Properties[1] is long child) || !(c.Properties[2] is long parent)) continue;
                if (!parentOf.TryGetValue(child, out var ps)) parentOf[child] = ps = new List<long>();
                ps.Add(parent);
                if (!childrenOf.TryGetValue(parent, out var cs)) childrenOf[parent] = cs = new List<long>();
                cs.Add(child);
            }

            var byId = new Dictionary<long, Node>();
            foreach (var node in objects.Children)
                if (node.Properties.Count > 0 && node.Properties[0] is long id) byId[id] = node;

            var models = new Dictionary<long, Model>();
            foreach (var pair in byId.Where(p => p.Value.Name == "Model"))
            {
                var model = new Model { Id = pair.Key, Name = CleanName(pair.Value), Kind = pair.Value.Properties.Count > 2 ? pair.Value.Properties[2] as string ?? string.Empty : string.Empty };
                Transform(pair.Value, model);
                models[pair.Key] = model;
            }
            foreach (var model in models.Values)
                if (parentOf.TryGetValue(model.Id, out var parents)) model.Parent = parents.FirstOrDefault(models.ContainsKey);

            var settings = Settings(root);
            var result = new List<FbxMesh>();
            foreach (var pair in byId.Where(p => p.Value.Name == "Geometry" && ClassOf(p.Value) == "Mesh"))
            {
                var geometry = pair.Value;
                var vertices = (geometry.Child("Vertices")?.Properties.FirstOrDefault() as ArrayProperty)?.Doubles();
                var polygons = (geometry.Child("PolygonVertexIndex")?.Properties.FirstOrDefault() as ArrayProperty)?.Ints();
                if (vertices == null || polygons == null) continue;
                long owner = parentOf.TryGetValue(pair.Key, out var owners) ? owners.FirstOrDefault(models.ContainsKey) : 0;
                models.TryGetValue(owner, out var model);
                var world = settings * (model != null ? Global(model, models) * model.Geometric : Matrix4x4.identity);

                var mesh = new FbxMesh { Path = model != null ? PathOf(model, models) : CleanName(geometry) };
                mesh.Points = new Vector3[vertices.Length / 3];
                for (int i = 0; i < mesh.Points.Length; i++)
                    mesh.Points[i] = world.MultiplyPoint3x4(new Vector3((float)vertices[i * 3], (float)vertices[i * 3 + 1], (float)vertices[i * 3 + 2]));
                mesh.Triangles = Triangulate(polygons, mesh.Points.Length);
                if (model != null && childrenOf.TryGetValue(model.Id, out var attached))
                    mesh.Materials = attached.Where(id => byId.TryGetValue(id, out var n) && n.Name == "Material").Select(id => CleanName(byId[id])).ToArray();

                // Deformers on the geometry: blendshape channels with their shapes, and skin clusters (one per bone).
                if (childrenOf.TryGetValue(pair.Key, out var deformers))
                {
                    foreach (long deformerId in deformers)
                    {
                        if (!byId.TryGetValue(deformerId, out var deformer) || deformer.Name != "Deformer") continue;
                        string kind = ClassOf(deformer);
                        if (!childrenOf.TryGetValue(deformerId, out var subs)) continue;
                        foreach (long subId in subs)
                        {
                            if (!byId.TryGetValue(subId, out var sub)) continue;
                            if (kind == "Skin" && ClassOf(sub) == "Cluster") mesh.Bones++;
                            if (kind != "BlendShape" || ClassOf(sub) != "BlendShapeChannel") continue;
                            long signature = 17;
                            if (childrenOf.TryGetValue(subId, out var shapes))
                                foreach (long shapeId in shapes)
                                {
                                    if (!byId.TryGetValue(shapeId, out var shape) || shape.Name != "Geometry") continue;
                                    var indexes = shape.Child("Indexes")?.Properties.FirstOrDefault() as ArrayProperty;
                                    var offsets = shape.Child("Vertices")?.Properties.FirstOrDefault() as ArrayProperty;
                                    unchecked { signature = signature * 31 + (indexes?.Signature() ?? 0) * 7 + (offsets?.Signature() ?? 0); }
                                }
                            mesh.Shapes[CleanName(sub)] = signature;
                        }
                    }
                }
                result.Add(mesh);
            }
            return result;
        }

        // Polygons end with a negative index (bitwise not of the last one); each is split into a fan of triangles.
        private static int[] Triangulate(int[] polygons, int count)
        {
            var triangles = new List<int>(polygons.Length * 2);
            int start = 0;
            for (int i = 0; i < polygons.Length; i++)
            {
                if (polygons[i] >= 0) continue;
                int first = polygons[start];
                for (int k = start + 1; k < i; k++)
                {
                    int b = polygons[k], c = k + 1 == i ? ~polygons[i] : polygons[k + 1];
                    if (first < count && b < count && c < count && first >= 0 && b >= 0 && c >= 0)
                    {
                        // Unity's axes mirror X, so the winding flips to keep faces pointing out.
                        triangles.Add(first);
                        triangles.Add(c);
                        triangles.Add(b);
                    }
                }
                start = i + 1;
            }
            return triangles.ToArray();
        }

        // Unit scale (centimetres to metres) and the file's axes to Unity's, as the model importer does.
        private static Matrix4x4 Settings(Node root)
        {
            var global = root.Child("GlobalSettings")?.Child("Properties70");
            double Value(string name, double fallback)
            {
                var p = global?.All("P").FirstOrDefault(n => n.Properties.Count > 4 && n.Properties[0] as string == name);
                return p != null && p.Properties[4] is double d ? d : p != null && p.Properties[4] is long l ? l : fallback;
            }
            float scale = (float)(Value("UnitScaleFactor", 1d) / 100d);
            int up = (int)Value("UpAxis", 1), upSign = (int)Value("UpAxisSign", 1);
            int front = (int)Value("FrontAxis", 2), frontSign = (int)Value("FrontAxisSign", 1);
            int coord = (int)Value("CoordAxis", 0), coordSign = (int)Value("CoordAxisSign", 1);
            var m = Matrix4x4.zero;
            m[0, coord] = -coordSign * scale;
            m[1, up] = upSign * scale;
            m[2, front] = frontSign * scale;
            m[3, 3] = 1f;
            return m;
        }

        private static void Transform(Node node, Model model)
        {
            var properties = node.Child("Properties70");
            Vector3 V(string name, Vector3 fallback)
            {
                var p = properties?.All("P").FirstOrDefault(n => n.Properties.Count > 6 && n.Properties[0] as string == name);
                if (p == null) return fallback;
                return new Vector3(Number(p.Properties[4]), Number(p.Properties[5]), Number(p.Properties[6]));
            }
            int order = 0;
            var rotationOrder = properties?.All("P").FirstOrDefault(n => n.Properties.Count > 4 && n.Properties[0] as string == "RotationOrder");
            if (rotationOrder != null) order = (int)Number(rotationOrder.Properties[4]);
            var t = V("Lcl Translation", Vector3.zero);
            var r = V("Lcl Rotation", Vector3.zero);
            var s = V("Lcl Scaling", Vector3.one);
            var pre = V("PreRotation", Vector3.zero);
            var post = V("PostRotation", Vector3.zero);
            // The inverse of the post-rotation (XYZ): the opposite angles in the opposite order.
            var postInverse = Axis(0, -post.x) * Axis(1, -post.y) * Axis(2, -post.z);
            model.Local = Matrix4x4.Translate(t) * Rotation(pre, 0) * Rotation(r, order) * postInverse * Matrix4x4.Scale(s);
            model.Geometric = Matrix4x4.Translate(V("GeometricTranslation", Vector3.zero)) * Rotation(V("GeometricRotation", Vector3.zero), 0) * Matrix4x4.Scale(V("GeometricScaling", Vector3.one));
        }

        // Euler angles in degrees, applied in the FBX rotation order (0 = XYZ: X first, then Y, then Z).
        private static Matrix4x4 Rotation(Vector3 degrees, int order)
        {
            Matrix4x4 X = Axis(0, degrees.x), Y = Axis(1, degrees.y), Z = Axis(2, degrees.z);
            switch (order)
            {
                case 1: return Y * Z * X; // XZY
                case 2: return X * Z * Y; // YZX
                case 3: return Z * X * Y; // YXZ
                case 4: return Y * X * Z; // ZXY
                case 5: return X * Y * Z; // ZYX
                default: return Z * Y * X; // XYZ
            }
        }

        private static Matrix4x4 Axis(int axis, float degrees)
        {
            double radians = degrees * Math.PI / 180d;
            float c = (float)Math.Cos(radians), s = (float)Math.Sin(radians);
            var m = Matrix4x4.identity;
            int a = (axis + 1) % 3, b = (axis + 2) % 3;
            m[a, a] = c; m[a, b] = -s;
            m[b, a] = s; m[b, b] = c;
            return m;
        }

        private static float Number(object value) => value is double d ? (float)d : value is long l ? l : 0f;

        private static Matrix4x4 Global(Model model, Dictionary<long, Model> models)
        {
            var matrix = model.Local;
            var current = model;
            for (int guard = 0; guard < 256 && current.Parent != 0 && models.TryGetValue(current.Parent, out var parent); guard++)
            {
                matrix = parent.Local * matrix;
                current = parent;
            }
            return matrix;
        }

        private static string PathOf(Model model, Dictionary<long, Model> models)
        {
            var names = new List<string> { model.Name };
            var current = model;
            for (int guard = 0; guard < 256 && current.Parent != 0 && models.TryGetValue(current.Parent, out var parent); guard++)
            {
                names.Add(parent.Name);
                current = parent;
            }
            names.Reverse();
            return string.Join("/", names);
        }

        // "Body\0\u0001Model" → "Body".
        private static string CleanName(Node node)
        {
            string raw = node.Properties.Count > 1 ? node.Properties[1] as string ?? string.Empty : string.Empty;
            int split = raw.IndexOf("\0\u0001", StringComparison.Ordinal);
            if (split >= 0) raw = raw.Substring(0, split);
            int colon = raw.IndexOf("::", StringComparison.Ordinal);
            return colon >= 0 ? raw.Substring(colon + 2) : raw;
        }

        private static string ClassOf(Node node) => node.Properties.Count > 2 ? node.Properties[2] as string ?? string.Empty : string.Empty;
    }
}

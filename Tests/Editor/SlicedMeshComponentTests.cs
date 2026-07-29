using Loop.MeshSlicer;
using NUnit.Framework;
using UnityEngine;

namespace Loop.MeshSlicer.Tests
{
    /// <summary>
    /// Tests for the component's contract with the scene: it must never modify the source mesh, it
    /// must give the MeshFilter its asset back when it goes away, and it must leave nothing behind
    /// that could be serialized into a scene or a prefab.
    ///
    /// Those are the failures that cost an artist real work — a corrupted FBX or a prefab pointing
    /// at a mesh that no longer exists — and none of them is visible while the component is running,
    /// which is exactly why they are worth asserting.
    ///
    /// The fixture mesh is asymmetric on purpose (y from 0 to 0.6, pivot at the base) because that
    /// is what the real art does.
    /// </summary>
    public class SlicedMeshComponentTests
    {
        private GameObject _host;
        private Mesh _source;

        [SetUp]
        public void SetUp()
        {
            _source = BuildFixtureMesh();
            _host = new GameObject("SlicedMeshFixture", typeof(MeshFilter), typeof(MeshRenderer));
            _host.GetComponent<MeshFilter>().sharedMesh = _source;
        }

        [TearDown]
        public void TearDown()
        {
            if (_host != null)
            {
                Object.DestroyImmediate(_host);
            }

            if (_source != null)
            {
                Object.DestroyImmediate(_source);
            }
        }

        [Test]
        public void Attaching_AdoptsTheMeshFiltersMesh_AsSource()
        {
            // Regression: Unity runs Reset *after* OnEnable, so at Reset time the MeshFilter is
            // already showing a generated mesh. A Reset that cleared the source field lost the only
            // reference to the imported asset, and the guard against adopting generated meshes then
            // correctly refused to re-find it — leaving the component silently sourceless.
            SlicedMesh slicer = Attach();

            Assert.AreSame(_source, slicer.SourceMesh);
        }

        [Test]
        public void Attaching_DoesNotModifyTheSourceAsset()
        {
            Vector3[] before = _source.vertices;

            SlicedMesh slicer = Attach();
            slicer.MarginMin = new Vector3(0.1f, 0.1f, 0.1f);
            slicer.MarginMax = new Vector3(0.1f, 0.1f, 0.1f);
            _host.transform.localScale = new Vector3(4f, 4f, 4f);
            slicer.Rebuild();

            // The whole non-destructive promise, in one assertion: whatever the component did, the
            // imported asset is byte-for-byte what it was.
            Vector3[] after = _source.vertices;
            Assert.AreEqual(before.Length, after.Length);
            for (int i = 0; i < before.Length; i++)
            {
                Assert.AreEqual(before[i], after[i], $"Source vertex {i} was modified.");
            }
        }

        [Test]
        public void GeneratedMesh_IsNeverTheSourceMesh()
        {
            SlicedMesh slicer = Attach();
            _host.transform.localScale = new Vector3(3f, 1f, 1f);
            slicer.Rebuild();

            Assert.AreNotSame(_source, _host.GetComponent<MeshFilter>().sharedMesh);
        }

        [Test]
        public void GeneratedMesh_IsMarkedDontSave_SoItCannotEnterASceneOrPrefab()
        {
            SlicedMesh slicer = Attach();
            slicer.Rebuild();

            Mesh generated = _host.GetComponent<MeshFilter>().sharedMesh;
            Assert.AreNotEqual(0, (int)(generated.hideFlags & HideFlags.DontSave));
        }

        [Test]
        public void Disabling_PutsTheSourceMeshBack()
        {
            SlicedMesh slicer = Attach();
            _host.transform.localScale = new Vector3(3f, 1f, 1f);
            slicer.Rebuild();

            slicer.enabled = false;

            Assert.AreSame(_source, _host.GetComponent<MeshFilter>().sharedMesh);
        }

        [Test]
        public void ReEnabling_AfterDisable_ProducesASlicedMeshAgain()
        {
            SlicedMesh slicer = Attach();
            _host.transform.localScale = new Vector3(3f, 1f, 1f);
            slicer.Rebuild();

            slicer.enabled = false;
            slicer.enabled = true;

            Assert.AreNotSame(_source, _host.GetComponent<MeshFilter>().sharedMesh);
            Assert.AreSame(_source, slicer.SourceMesh, "Re-enabling must not adopt the generated mesh as the new source.");
        }

        [Test]
        public void Duplicating_GivesTheCopyItsOwnMesh_AndTheRealSource()
        {
            // Ctrl+D is routine, and this is the failure this kind of tool is prone to: the copy
            // inherits the MeshFilter's reference to the *generated* mesh, so either the two objects
            // silently share one mesh (moving one resizes the other) or the copy adopts that
            // generated mesh as its source and compounds the slicing on every rebuild.
            //
            // It works here because _generatedMesh is not serialized while _sourceMesh is — which
            // is a one-word property of the field declaration, and exactly the kind of thing a
            // later edit undoes without noticing.
            SlicedMesh original = Attach();
            _host.transform.localScale = new Vector3(1f, 3f, 1f);
            original.Rebuild();

            GameObject copyObject = Object.Instantiate(_host);
            try
            {
                var copy = copyObject.GetComponent<SlicedMesh>();

                Assert.AreSame(_source, copy.SourceMesh,
                    "The copy must point at the imported asset, not at the original's generated mesh.");
                Assert.AreNotSame(
                    _host.GetComponent<MeshFilter>().sharedMesh,
                    copyObject.GetComponent<MeshFilter>().sharedMesh,
                    "The copy must own its mesh; sharing one means resizing either object resizes both.");

                // And they must stay independent once they diverge.
                copyObject.transform.localScale = new Vector3(1f, 6f, 1f);
                copy.Rebuild();
                Assert.IsNotNull(_host.GetComponent<MeshFilter>().sharedMesh,
                    "Rebuilding the copy must not invalidate the original's mesh.");
            }
            finally
            {
                Object.DestroyImmediate(copyObject);
            }
        }

        [Test]
        public void GeneratedMesh_KeepsTheSourceTopology()
        {
            SlicedMesh slicer = Attach();
            _host.transform.localScale = new Vector3(3f, 2f, 1.5f);
            slicer.Rebuild();

            Mesh generated = _host.GetComponent<MeshFilter>().sharedMesh;

            Assert.AreEqual(_source.vertexCount, generated.vertexCount);
            Assert.AreEqual(_source.subMeshCount, generated.subMeshCount);
            for (int i = 0; i < _source.subMeshCount; i++)
            {
                Assert.AreEqual(_source.GetTriangles(i).Length, generated.GetTriangles(i).Length,
                    $"Submesh {i} triangle count changed.");
            }

            Assert.AreEqual(_source.uv.Length, generated.uv.Length);
        }

        [Test]
        public void GeneratedMesh_EqualsTheSource_AtReferenceScale()
        {
            _host.transform.localScale = new Vector3(0.71f, 1.11f, 1.18f);
            SlicedMesh slicer = Attach();
            slicer.CaptureReferenceScale();
            slicer.MarginMin = new Vector3(0.05f, 0.05f, 0.05f);
            slicer.MarginMax = new Vector3(0.05f, 0.05f, 0.05f);
            slicer.Rebuild();

            // The claim that makes the component safe to attach to anything: at the scale it was
            // attached at, it changes nothing.
            Vector3[] expected = _source.vertices;
            Vector3[] actual = _host.GetComponent<MeshFilter>().sharedMesh.vertices;

            for (int i = 0; i < expected.Length; i++)
            {
                Assert.AreEqual(expected[i].x, actual[i].x, 1e-4f, $"Vertex {i}.x moved at reference scale.");
                Assert.AreEqual(expected[i].y, actual[i].y, 1e-4f, $"Vertex {i}.y moved at reference scale.");
                Assert.AreEqual(expected[i].z, actual[i].z, 1e-4f, $"Vertex {i}.z moved at reference scale.");
            }
        }

        [Test]
        public void Stretching_KeepsTheRenderedFootprint_OfPlainScaling()
        {
            SlicedMesh slicer = Attach();
            slicer.MarginMin = new Vector3(0f, 0.1f, 0f);
            slicer.MarginMax = new Vector3(0f, 0.1f, 0f);
            _host.transform.localScale = new Vector3(1f, 5f, 1f);
            slicer.Rebuild();

            Bounds generated = _host.GetComponent<MeshFilter>().sharedMesh.bounds;

            Assert.AreEqual(_source.bounds.min.y, generated.min.y, 1e-4f);
            Assert.AreEqual(_source.bounds.max.y, generated.max.y, 1e-4f);
        }

        [Test]
        public void AutoDetectMargins_FindsTheGap_InAModelBuiltWithOne()
        {
            SlicedMesh slicer = Attach();
            slicer.AutoDetectMargins();

            // The fixture has vertex rings only at the ends on Y, so the middle must come out
            // stretchable and the ends rigid.
            float length = _source.bounds.size.y;
            Assert.Greater(slicer.MarginMin.y, 0f);
            Assert.Greater(slicer.MarginMax.y, 0f);
            Assert.Less(slicer.MarginMin.y + slicer.MarginMax.y, length,
                "Auto-detect must leave a middle to stretch.");
        }

        [Test]
        public void Margins_AreClampedToTheMesh_SoTheMiddleCanNeverGoNegative()
        {
            SlicedMesh slicer = Attach();
            float length = _source.bounds.size.y;

            slicer.MarginMin = new Vector3(0f, length * 5f, 0f);
            slicer.MarginMax = new Vector3(0f, length * 5f, 0f);

            Assert.LessOrEqual(slicer.MarginMin.y + slicer.MarginMax.y, length + 1e-4f);
        }

        [Test]
        public void BoxCollider_FollowsTheSlicedShape()
        {
            var box = _host.AddComponent<BoxCollider>();
            SlicedMesh slicer = Attach();
            slicer.MarginMin = new Vector3(0f, 0.1f, 0f);
            slicer.MarginMax = new Vector3(0f, 0.1f, 0f);
            _host.transform.localScale = new Vector3(1f, 4f, 1f);
            slicer.Rebuild();

            // Same footprint as the mesh: a collider that disagreed with the visual would be worse
            // than no collider update at all.
            Assert.AreEqual(_source.bounds.size.y, box.size.y, 1e-4f);
            Assert.AreEqual(_source.bounds.center.y, box.center.y, 1e-4f);
        }

        [Test]
        public void UvSlicing_IsOff_ByDefault_AndLeavesUvsUntouched()
        {
            // The opt-in guarantee: a model that was never asked for UV slicing must come out with
            // byte-identical UVs, whatever the scale.
            SlicedMesh slicer = Attach();
            _host.transform.localScale = new Vector3(1f, 7f, 1f);
            slicer.Rebuild();

            Vector2[] expected = _source.uv;
            Vector2[] actual = _host.GetComponent<MeshFilter>().sharedMesh.uv;

            for (int i = 0; i < expected.Length; i++)
            {
                Assert.AreEqual(expected[i], actual[i], $"UV {i} moved with slicing off.");
            }
        }

        [Test]
        public void UvSlicing_LeavesUvsUntouched_AtReferenceScale()
        {
            SlicedMesh slicer = AttachWithUvSlicing();

            Vector2[] expected = _source.uv;
            Vector2[] actual = _host.GetComponent<MeshFilter>().sharedMesh.uv;

            for (int i = 0; i < expected.Length; i++)
            {
                Assert.AreEqual(expected[i].y, actual[i].y, 1e-4f,
                    $"UV {i} moved at reference scale — attaching must still change nothing.");
            }
        }

        [Test]
        public void UvSlicing_KeepsTexelDensityConstant_WhichIsTheWholePoint()
        {
            SlicedMesh slicer = AttachWithUvSlicing();

            float sourceSpan = UvSpanV(_source.uv);
            float sourceHeight = _source.bounds.size.y * 1f; // reference scale is 1 here

            _host.transform.localScale = new Vector3(1f, 4f, 1f);
            slicer.Rebuild();

            Mesh generated = _host.GetComponent<MeshFilter>().sharedMesh;
            float newSpan = UvSpanV(generated.uv);
            float newHeight = generated.bounds.size.y * 4f;

            Assert.AreEqual(sourceSpan / sourceHeight, newSpan / newHeight, 1e-3f,
                "UV per rendered unit must not change — that is what stops the texture stretching.");
        }

        [Test]
        public void UvSlicing_LeavesTheRigidEndsAlone()
        {
            SlicedMesh slicer = AttachWithUvSlicing();
            slicer.MarginMin = new Vector3(0f, 0.1f, 0f);
            slicer.MarginMax = new Vector3(0f, 0.1f, 0f);

            Vector2[] before = _source.uv;
            Vector3[] positions = _source.vertices;
            float headEnd = _source.bounds.min.y + 0.1f;

            _host.transform.localScale = new Vector3(1f, 5f, 1f);
            slicer.Rebuild();
            Vector2[] after = _host.GetComponent<MeshFilter>().sharedMesh.uv;

            int checkedVerts = 0;
            for (int i = 0; i < positions.Length; i++)
            {
                if (positions[i].y > headEnd)
                {
                    continue;
                }

                checkedVerts++;
                Assert.AreEqual(before[i].y, after[i].y, 1e-4f,
                    $"UV {i} is in the rigid head and must not move.");
            }

            Assert.Greater(checkedVerts, 0, "Fixture must have vertices in the head to be meaningful.");
        }

        [Test]
        public void UvSlicing_WorksWithoutMargins_BecauseAFlatQuadHasNoRigidFeatures()
        {
            // The real subject for this feature is a 4-vertex tiled plane: its only vertices sit on
            // the bounds, so geometry slicing can do nothing for it and UV slicing has to stand on
            // its own.
            SlicedMesh slicer = AttachWithUvSlicing();
            slicer.MarginMin = Vector3.zero;
            slicer.MarginMax = Vector3.zero;

            float sourceSpan = UvSpanV(_source.uv);

            _host.transform.localScale = new Vector3(1f, 3f, 1f);
            slicer.Rebuild();

            float newSpan = UvSpanV(_host.GetComponent<MeshFilter>().sharedMesh.uv);
            Assert.AreEqual(sourceSpan * 3f, newSpan, 1e-3f,
                "With no margins the whole axis is middle, so the UV span should scale with it.");
        }

        [Test]
        public void TurningUvSlicingBackOff_RestoresTheSourceUvs()
        {
            SlicedMesh slicer = AttachWithUvSlicing();
            _host.transform.localScale = new Vector3(1f, 4f, 1f);
            slicer.Rebuild();

            slicer.SetUvMode(1, UvSliceMode.Stretch);
            slicer.Rebuild();

            Vector2[] expected = _source.uv;
            Vector2[] actual = _host.GetComponent<MeshFilter>().sharedMesh.uv;
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.AreEqual(expected[i].y, actual[i].y, 1e-4f,
                    $"UV {i} kept its sliced value after the feature was switched off.");
            }
        }

        [Test]
        public void AutoFitUvDensity_MeasuresTheFixturesOwnDensity()
        {
            SlicedMesh slicer = Attach();
            SlicedMesh.UvFit[] fit = slicer.AutoFitUvDensity();

            // The fixture's V runs 1:1 with Y, so the density is 1 and the fit is exact.
            Assert.IsTrue(fit[1].Valid);
            Assert.AreEqual(UvChannel.V, fit[1].Channel);
            Assert.AreEqual(1f, fit[1].Density, 1e-3f);
            Assert.AreEqual(1f, Mathf.Abs(fit[1].Correlation), 1e-3f);
            Assert.Less(fit[1].WorstResidual, SlicedMesh.PoorFitResidual);
        }

        private SlicedMesh AttachWithUvSlicing()
        {
            SlicedMesh slicer = Attach();
            slicer.AutoFitUvDensity();
            slicer.SetUvMode(1, UvSliceMode.PreserveDensity);
            slicer.SetUvChannel(1, UvChannel.V);
            slicer.Rebuild();
            return slicer;
        }

        private static float UvSpanV(Vector2[] uvs)
        {
            float lo = float.MaxValue;
            float hi = float.MinValue;
            foreach (Vector2 uv in uvs)
            {
                lo = Mathf.Min(lo, uv.y);
                hi = Mathf.Max(hi, uv.y);
            }

            return hi - lo;
        }

        private SlicedMesh Attach()
        {
            // AddComponent fires OnEnable, which is what a real attach does.
            return _host.AddComponent<SlicedMesh>();
        }

        /// <summary>
        /// A box with extra vertex rings clustered near each end of Y and nothing in between —
        /// the shape of every model this package is for. Asymmetric on Y (0 .. 0.6) so a mapping
        /// that assumed a centred pivot would fail.
        /// </summary>
        private static Mesh BuildFixtureMesh()
        {
            var mesh = new Mesh { name = "SliceFixture" };

            float[] rows = { 0f, 0.05f, 0.1f, 0.5f, 0.55f, 0.6f };
            var vertices = new Vector3[rows.Length * 4];
            var uvs = new Vector2[vertices.Length];
            var normals = new Vector3[vertices.Length];

            for (int r = 0; r < rows.Length; r++)
            {
                vertices[r * 4 + 0] = new Vector3(-0.5f, rows[r], -0.5f);
                vertices[r * 4 + 1] = new Vector3(0.5f, rows[r], -0.5f);
                vertices[r * 4 + 2] = new Vector3(0.5f, rows[r], 0.5f);
                vertices[r * 4 + 3] = new Vector3(-0.5f, rows[r], 0.5f);

                for (int c = 0; c < 4; c++)
                {
                    uvs[r * 4 + c] = new Vector2(c * 0.25f, rows[r]);
                    normals[r * 4 + c] = new Vector3(vertices[r * 4 + c].x, 0f, vertices[r * 4 + c].z).normalized;
                }
            }

            var triangles = new System.Collections.Generic.List<int>();
            for (int r = 0; r < rows.Length - 1; r++)
            {
                int a = r * 4;
                int b = (r + 1) * 4;
                for (int c = 0; c < 4; c++)
                {
                    int c2 = (c + 1) % 4;
                    triangles.Add(a + c);
                    triangles.Add(b + c);
                    triangles.Add(b + c2);

                    triangles.Add(a + c);
                    triangles.Add(b + c2);
                    triangles.Add(a + c2);
                }
            }

            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.triangles = triangles.ToArray();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}

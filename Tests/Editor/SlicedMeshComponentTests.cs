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
        private const string FixtureName = "SlicedMeshFixture";

        private GameObject _host;
        private Mesh _source;

        [SetUp]
        public void SetUp()
        {
            _source = BuildFixtureMesh();
            _host = new GameObject(FixtureName, typeof(MeshFilter), typeof(MeshRenderer));
            _host.GetComponent<MeshFilter>().sharedMesh = _source;
        }

        [TearDown]
        public void TearDown()
        {
            if (_host != null)
            {
                // Two of these tests add the component the way the inspector does, which registers
                // an undo entry. DestroyImmediate on an object Unity still has undo records for
                // errors out, and an error here would abort the rest of this method and leave the
                // fixture sitting in whatever scene the editor happens to have open — which is how
                // test objects end up in a real scene file.
                UnityEditor.Undo.ClearUndo(_host);
                Object.DestroyImmediate(_host);
            }

            // Belt and braces: nothing this fixture creates may outlive the test, whatever went
            // wrong above. These tests run against the open scene, so a leak is a diff in somebody's
            // project.
            foreach (GameObject stray in Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None))
            {
                if (stray != null && stray.name.StartsWith(FixtureName))
                {
                    UnityEditor.Undo.ClearUndo(stray);
                    Object.DestroyImmediate(stray);
                }
            }

            if (_source != null)
            {
                Object.DestroyImmediate(_source);
            }
        }

        [Test]
        public void AttachingToAnInactiveObject_StillInitializes_WhenMarginsAreDetected()
        {
            // The defect this pins: AddComponent on an INACTIVE GameObject runs no OnEnable, so
            // nothing fetched the MeshFilter or captured the source — AutoDetectMargins then found
            // no source and silently left the margins at zero, and a zero-margin slice is a plain
            // stretch. The driver that hit this had its target hidden by ANOTHER system
            // (SetActive(false)) at bind time; when the object came back, OnEnable captured the
            // by-then-stretched transform as the reference scale, making the no-op permanent.
            _host.SetActive(false);

            var slicer = _host.AddComponent<SlicedMesh>();
            slicer.AutoDetectMargins();

            Assert.AreSame(_source, slicer.SourceMesh,
                "AutoDetectMargins on an inactive object must self-initialize the source.");
            Assert.Greater(slicer.MarginMax.y, 0f,
                "Margins stayed zero — the slice would be a plain stretch.");
        }

        [Test]
        public void AttachingToAnInactiveObject_DoesNotAdoptALaterStretchAsAuthored()
        {
            // Second half of the same hole: the reference scale. Initialize while inactive, let the
            // driver stretch, THEN activate — OnEnable must keep the pinned reference rather than
            // re-capture the stretched transform as "authored", which would make the slice compare
            // the scale to itself and generate a mesh identical to the source.
            _host.SetActive(false);

            var slicer = _host.AddComponent<SlicedMesh>();
            slicer.SetReferenceScale(_host.transform.localScale);
            slicer.AutoDetectMargins();

            _host.transform.localScale = new Vector3(1f, 12f, 1f);
            _host.SetActive(true);

            Mesh generated = _host.GetComponent<MeshFilter>().sharedMesh;
            Assert.AreNotSame(_source, generated, "no generated mesh appeared on activation");

            // Under a real slice at 12x, the cap vertices compress toward the ends in mesh space.
            // A generated mesh with the source's own vertex layout means the reference was adopted
            // from the stretched transform and the slice is a no-op.
            float sourceTop = TopCapWidth(_source);
            float generatedTop = TopCapWidth(generated);
            Assert.Less(generatedTop, sourceTop * 0.5f,
                "The generated mesh has the source's own cap width — the stretched scale was " +
                "adopted as the reference and the slice does nothing.");
        }

        /// <summary>
        /// Mesh-space width of the top cap along Y — the fixture mesh's long axis. Measured as the
        /// span from the end of the largest inter-vertex gap (the empty middle) to the top.
        /// </summary>
        private static float TopCapWidth(Mesh mesh)
        {
            Vector3[] vertices = mesh.vertices;
            var zs = new System.Collections.Generic.List<float>();
            foreach (Vector3 v in vertices)
            {
                zs.Add(v.y);
            }

            zs.Sort();

            // The fixture mesh (like the real art) is empty across the middle, so the largest gap
            // separates the two caps; the top cap spans from the gap's end to max z.
            float bestGap = 0f;
            float gapHigh = zs[zs.Count - 1];
            for (int i = 1; i < zs.Count; i++)
            {
                if (zs[i] - zs[i - 1] > bestGap)
                {
                    bestGap = zs[i] - zs[i - 1];
                    gapHigh = zs[i];
                }
            }

            return zs[zs.Count - 1] - gapHigh;
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
        public void BoxCollider_KeepsItsAuthoredShape_WhenTheComponentIsAttached()
        {
            // A collider is often deliberately smaller than the model — a trimmed hitbox, a trigger
            // around one part. Deriving it from the mesh bounds would silently replace that with
            // the full model box, and at reference scale, where nothing is supposed to change.
            var box = _host.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, 0.3f, 0f);
            box.size = new Vector3(0.2f, 0.4f, 0.2f);

            SlicedMesh slicer = Attach();
            slicer.MarginMin = new Vector3(0f, 0.1f, 0f);
            slicer.MarginMax = new Vector3(0f, 0.1f, 0f);
            slicer.Rebuild();

            AssertVector(new Vector3(0f, 0.3f, 0f), box.center, "Authored collider centre was overwritten.");
            AssertVector(new Vector3(0.2f, 0.4f, 0.2f), box.size, "Authored collider size was overwritten.");
        }

        [Test]
        public void BoxCollider_IsRestored_WhenTheComponentGoesAway()
        {
            // Unlike the mesh, collider values are serialized into the scene — so a collider left in
            // its sliced state is a permanent change to the project, not a temporary display.
            var box = _host.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, 0.3f, 0f);
            box.size = new Vector3(0.2f, 0.4f, 0.2f);

            SlicedMesh slicer = Attach();
            slicer.MarginMin = new Vector3(0f, 0.1f, 0f);
            slicer.MarginMax = new Vector3(0f, 0.1f, 0f);
            _host.transform.localScale = new Vector3(1f, 5f, 1f);
            slicer.Rebuild();

            slicer.enabled = false;

            AssertVector(new Vector3(0f, 0.3f, 0f), box.center,
                "Collider centre was not restored; the scene keeps the sliced value.");
            AssertVector(new Vector3(0.2f, 0.4f, 0.2f), box.size,
                "Collider size was not restored; the scene keeps the sliced value.");
        }

        // Vector3 equality in NUnit goes through Equals, which compares floats exactly — and every
        // value here has been through the mapping, so it carries the usual float dust.
        private static void AssertVector(Vector3 expected, Vector3 actual, string message)
        {
            Assert.AreEqual(expected.x, actual.x, 1e-4f, message + " (x)");
            Assert.AreEqual(expected.y, actual.y, 1e-4f, message + " (y)");
            Assert.AreEqual(expected.z, actual.z, 1e-4f, message + " (z)");
        }

        [Test]
        public void SqueezedMargins_ProduceFiniteUnitNormals()
        {
            // Shrink the object below the sum of its margins and the middle collapses to zero
            // rendered length. Dividing a normal by that Jacobian gives infinity, which survives a
            // plain magnitude check and reaches the vertex buffer as NaN.
            SlicedMesh slicer = Attach();
            slicer.MarginMin = new Vector3(0f, 0.3f, 0f);
            slicer.MarginMax = new Vector3(0f, 0.3f, 0f);
            _host.transform.localScale = new Vector3(1f, 0.2f, 1f);
            slicer.Rebuild();

            Vector3[] normals = _host.GetComponent<MeshFilter>().sharedMesh.normals;
            for (int i = 0; i < normals.Length; i++)
            {
                Vector3 n = normals[i];
                Assert.IsFalse(float.IsNaN(n.x) || float.IsNaN(n.y) || float.IsNaN(n.z),
                    $"Normal {i} is NaN: {n}");
                Assert.IsFalse(float.IsInfinity(n.x) || float.IsInfinity(n.y) || float.IsInfinity(n.z),
                    $"Normal {i} is infinite: {n}");
                Assert.AreEqual(1f, n.magnitude, 1e-3f, $"Normal {i} is not unit length: {n}");
            }
        }

        [Test]
        public void SqueezedMargins_KeepTheMappingMonotonic()
        {
            // The same regime, seen from the geometry side: a negative middle would fold the mesh
            // back through itself.
            SlicedMesh slicer = Attach();
            slicer.MarginMin = new Vector3(0f, 0.3f, 0f);
            slicer.MarginMax = new Vector3(0f, 0.3f, 0f);
            _host.transform.localScale = new Vector3(1f, 0.2f, 1f);
            slicer.Rebuild();

            Vector3[] source = _source.vertices;
            Vector3[] sliced = _host.GetComponent<MeshFilter>().sharedMesh.vertices;

            for (int i = 0; i < source.Length; i++)
            {
                for (int j = 0; j < source.Length; j++)
                {
                    if (source[i].y < source[j].y - 1e-5f)
                    {
                        Assert.LessOrEqual(sliced[i].y, sliced[j].y + 1e-4f,
                            "Vertex order on Y was reversed — the mapping folded.");
                    }
                }
            }
        }

        [Test]
        public void AttachingFromScript_CapturesTheCurrentScale_AsReference()
        {
            // AddComponent never runs Reset (only the inspector's add path does), so a component
            // added from code used to be measured against scale 1 — and models routinely import at
            // something else.
            _host.transform.localScale = new Vector3(0.71f, 1.11f, 1.18f);

            SlicedMesh slicer = Attach();
            slicer.MarginMin = new Vector3(0.05f, 0.05f, 0.05f);
            slicer.MarginMax = new Vector3(0.05f, 0.05f, 0.05f);
            slicer.Rebuild();

            AssertVector(new Vector3(0.71f, 1.11f, 1.18f), slicer.ReferenceScale,
                "Reference scale was not captured from the object.");

            Vector3[] expected = _source.vertices;
            Vector3[] actual = _host.GetComponent<MeshFilter>().sharedMesh.vertices;
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.AreEqual(expected[i].y, actual[i].y, 1e-4f,
                    $"Vertex {i} moved even though the component was added at this very scale.");
            }
        }

        [Test]
        public void NegativeScale_ProducesTheSameShapeAsItsMagnitude()
        {
            // Mirroring is the transform's job; the mesh should be identical either way.
            SlicedMesh slicer = Attach();
            slicer.MarginMin = new Vector3(0f, 0.1f, 0f);
            slicer.MarginMax = new Vector3(0f, 0.1f, 0f);

            _host.transform.localScale = new Vector3(1f, 3f, 1f);
            slicer.Rebuild();
            Vector3[] positive = (Vector3[])_host.GetComponent<MeshFilter>().sharedMesh.vertices.Clone();

            _host.transform.localScale = new Vector3(1f, -3f, 1f);
            slicer.Rebuild();
            Vector3[] negative = _host.GetComponent<MeshFilter>().sharedMesh.vertices;

            for (int i = 0; i < positive.Length; i++)
            {
                Assert.AreEqual(positive[i].y, negative[i].y, 1e-4f, $"Vertex {i} differs under negative scale.");
            }
        }

        [Test]
        public void FinalizeSlicing_LeavesTheSlicedMeshBehind_AndRemovesTheComponent()
        {
            // The answer to "what does this cost in a build": bake once, then stop paying for the
            // per-frame check. The mesh has to survive the component's own OnDestroy to be useful.
            SlicedMesh slicer = Attach();
            slicer.MarginMin = new Vector3(0f, 0.1f, 0f);
            slicer.MarginMax = new Vector3(0f, 0.1f, 0f);
            _host.transform.localScale = new Vector3(1f, 4f, 1f);
            slicer.Rebuild();

            Mesh slicedBefore = _host.GetComponent<MeshFilter>().sharedMesh;
            slicer.FinalizeSlicing();

            Assert.IsNull(_host.GetComponent<SlicedMesh>(), "The component should be gone.");

            Mesh after = _host.GetComponent<MeshFilter>().sharedMesh;
            Assert.IsNotNull(after, "Finalizing destroyed the mesh it was supposed to keep.");
            Assert.AreSame(slicedBefore, after);
            Assert.AreNotSame(_source, after, "Finalizing must leave the sliced mesh, not the source.");
            Assert.AreEqual(0, (int)(after.hideFlags & HideFlags.DontSave),
                "A finalized mesh has to be saveable, or the scene stores nothing.");
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

        [Test]
        public void AddingThroughTheInspector_CapturesTheSource()
        {
            // Not the same code path as AddComponent: only ObjectFactory runs Reset(), which is
            // where the inspector-add behaviour lives. Every other test here uses AddComponent, so
            // without this one the Reset() body is never executed by the suite at all.
            var slicer = UnityEditor.ObjectFactory.AddComponent<SlicedMesh>(_host);

            Assert.AreSame(_source, slicer.SourceMesh);
            Assert.Greater(slicer.MarginMin.y, 0f, "Reset should have guessed margins from the model.");
        }

        [Test]
        public void ResetFromTheContextMenu_DoesNotLeaveTheComponentSourceless()
        {
            // The header's Reset item wipes serialized fields *before* calling Reset(), so the
            // source field arrives null while the MeshFilter still shows a generated mesh — which
            // the component rightly refuses to adopt as a source. Without a recovery path that
            // leaves it permanently unable to find the model again.
            var slicer = UnityEditor.ObjectFactory.AddComponent<SlicedMesh>(_host);
            slicer.Rebuild();

            UnityEditor.Unsupported.SmartReset(slicer);

            Assert.AreSame(_source, slicer.SourceMesh,
                "After a context-menu Reset the component lost the imported mesh for good.");
        }

        private SlicedMesh Attach()
        {
            // AddComponent fires OnEnable, which is what a script-driven attach does. The
            // inspector's add path goes through ObjectFactory and is covered separately.
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
            // Tangents are transformed along a different rule from normals (with the mapping rather
            // than against it), so the fixture carries them or that half of the code is untested.
            var tangents = new Vector4[vertices.Length];

            for (int r = 0; r < rows.Length; r++)
            {
                vertices[r * 4 + 0] = new Vector3(-0.5f, rows[r], -0.5f);
                vertices[r * 4 + 1] = new Vector3(0.5f, rows[r], -0.5f);
                vertices[r * 4 + 2] = new Vector3(0.5f, rows[r], 0.5f);
                vertices[r * 4 + 3] = new Vector3(-0.5f, rows[r], 0.5f);

                for (int c = 0; c < 4; c++)
                {
                    uvs[r * 4 + c] = new Vector2(c * 0.25f, rows[r]);
                    Vector3 outward = new Vector3(vertices[r * 4 + c].x, 0f, vertices[r * 4 + c].z).normalized;
                    normals[r * 4 + c] = outward;
                    // Around the ring, perpendicular to the outward normal and to Y.
                    Vector3 along = Vector3.Cross(Vector3.up, outward).normalized;
                    tangents[r * 4 + c] = new Vector4(along.x, along.y, along.z, -1f);
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
            mesh.tangents = tangents;
            mesh.uv = uvs;
            mesh.triangles = triangles.ToArray();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}

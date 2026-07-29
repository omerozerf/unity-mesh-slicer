using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Loop.MeshSlicer.Tests
{
    /// <summary>
    /// The save hook, driven through a real scene save.
    ///
    /// This is the one piece of the package whose behaviour cannot be asserted without touching the
    /// editor's scene machinery: it exists to swap meshes around <c>EditorSceneManager</c>'s save
    /// callbacks, and the bug it had was purely about the ORDER Unity fires those callbacks in for a
    /// multi-scene save. So these tests create throwaway scenes under a distinctive folder, save
    /// them for real, and clean up in <c>TearDown</c>.
    ///
    /// Why the assertions are valid immediately after the save returns: <c>SlicedMesh</c> is
    /// <c>[ExecuteAlways]</c> and its <c>LateUpdate</c> would notice a swapped mesh and re-slice it
    /// on the next editor tick — but no tick happens inside a synchronous test method, so what we
    /// read is exactly what the hook left behind. That self-healing is also why this bug was
    /// invisible in practice, and why it needs a test rather than an artist noticing.
    /// </summary>
    public class SlicedMeshSaveHookTests
    {
        private const string TempFolder = "Assets/__MeshSlicerSaveHookTests";
        private const string FixtureName = "SaveHookFixture";

        private readonly List<Scene> _openedScenes = new List<Scene>();
        private readonly List<Mesh> _sources = new List<Mesh>();

        [SetUp]
        public void SetUp()
        {
            // A previous run may have had to leave the folder behind (see TearDown).
            if (AssetDatabase.IsValidFolder(TempFolder))
            {
                AssetDatabase.DeleteAsset(TempFolder);
            }

            AssetDatabase.CreateFolder("Assets", "__MeshSlicerSaveHookTests");
        }

        [TearDown]
        public void TearDown()
        {
            bool anyStillLoaded = false;

            for (int i = 0; i < _openedScenes.Count; i++)
            {
                Scene scene = _openedScenes[i];
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    continue;
                }

                // Never close the last loaded scene — Unity refuses, and an editor with no scene at
                // all is a worse state than a leftover. The EditMode runner restores the user's own
                // scene setup when the run finishes, so whatever is left here is short-lived.
                if (SceneManager.loadedSceneCount <= 1 || !EditorSceneManager.CloseScene(scene, true))
                {
                    anyStillLoaded = true;
                }
            }

            _openedScenes.Clear();

            for (int i = 0; i < _sources.Count; i++)
            {
                if (_sources[i] != null)
                {
                    Object.DestroyImmediate(_sources[i]);
                }
            }

            _sources.Clear();

            // Deleting the asset of a scene that is still open logs errors and buys nothing; the next
            // SetUp clears it instead.
            if (!anyStillLoaded && AssetDatabase.IsValidFolder(TempFolder))
            {
                AssetDatabase.DeleteAsset(TempFolder);
            }
        }

        [Test]
        public void AMultiSceneSave_ReSlicesEveryScene_NotJustTheLastOne()
        {
            // The regression, exactly. Measured on Unity 6000.3, SaveScenes fires
            // saving:A -> saving:B -> saved:A -> saved:B. A single shared list cleared at the top of
            // every `sceneSaving` therefore loses A's entries the moment B starts, so A's slicers are
            // restored to their source mesh and never rebuilt.
            SlicedMesh first = CreateSceneWithSlicer("A", out Scene sceneA, out Mesh sourceA);
            SlicedMesh second = CreateSceneWithSlicer("B", out Scene sceneB, out Mesh sourceB);

            Assert.AreNotSame(sourceA, MeshOf(first), "Precondition: A must start out sliced.");
            Assert.AreNotSame(sourceB, MeshOf(second), "Precondition: B must start out sliced.");

            EditorSceneManager.MarkSceneDirty(sceneA);
            EditorSceneManager.MarkSceneDirty(sceneB);

            Assert.IsTrue(EditorSceneManager.SaveScenes(new[] { sceneA, sceneB }),
                "The batch save itself failed, so this test proves nothing about the hook.");

            Assert.AreNotSame(sourceA, MeshOf(first),
                "The first scene in a multi-scene save was left holding its source mesh — its " +
                "slicers were restored and never rebuilt.");
            Assert.AreNotSame(sourceB, MeshOf(second),
                "The last scene in a multi-scene save was left un-sliced.");
        }

        [Test]
        public void ASingleSceneSave_StillLeavesTheObjectSliced_AndTheFileHoldingTheSource()
        {
            // The hook's original purpose, which the per-scene rewrite must not break: the file gets
            // the real mesh reference (so scene diffs stay quiet) while the editor keeps showing the
            // sliced one.
            SlicedMesh slicer = CreateSceneWithSlicer("Single", out Scene scene, out Mesh source);

            EditorSceneManager.MarkSceneDirty(scene);
            Assert.IsTrue(EditorSceneManager.SaveScene(scene));

            Assert.AreNotSame(source, MeshOf(slicer),
                "After the save the object must be sliced again.");

            string yaml = System.IO.File.ReadAllText(scene.path);
            Assert.IsFalse(yaml.Contains("m_Mesh: {fileID: 0}"),
                "The saved file records an unsaveable mesh reference as null — the whole reason " +
                "this hook exists.");
            Assert.IsTrue(yaml.Contains("m_Mesh:"),
                "Sanity check: the saved scene has no MeshFilter in it at all, so the assertion " +
                "above proved nothing.");
        }

        [Test]
        public void SavingTheSameSceneTwice_RebuildsBothTimes()
        {
            // The per-scene list is reused across saves rather than reallocated, so a stale entry
            // from the previous save would be a real hazard. Two saves in a row is the cheapest way
            // to notice.
            SlicedMesh slicer = CreateSceneWithSlicer("Twice", out Scene scene, out Mesh source);

            for (int pass = 0; pass < 2; pass++)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                Assert.IsTrue(EditorSceneManager.SaveScene(scene), $"Save {pass + 1} failed.");
                Assert.AreNotSame(source, MeshOf(slicer), $"Save {pass + 1} left the object un-sliced.");
            }
        }

        private static Mesh MeshOf(SlicedMesh slicer)
        {
            return slicer.GetComponent<MeshFilter>().sharedMesh;
        }

        /// <summary>
        /// A saved-to-disk scene holding one stretched, sliced object.
        ///
        /// **Why the roundabout way in.** The scene needs a path on disk before it can take part in a
        /// save, and <c>NewScene(..., Additive)</c> is not available here: Unity refuses it while any
        /// untitled scene is unsaved, and the EditMode test runner guarantees exactly that — measured
        /// on 6000.3, during a run the only open scene is untitled (<c>path=""</c>, not dirty, one
        /// root object of the runner's own), and the first additive <c>NewScene</c> throws.
        ///
        /// So instead: <c>SaveScene(open, path, saveAsCopy: true)</c> writes a scene file without
        /// touching the open scene's identity (measured: its path stays empty), and
        /// <c>OpenScene(path, Additive)</c> — which has no such restriction — brings that file in as a
        /// real, titled, disposable scene. The runner's scene is never modified, saved or closed.
        ///
        /// The copy inherits the runner's root object. It is left alone: it carries no
        /// <see cref="SlicedMesh"/>, so it changes nothing the hook or these assertions look at, and
        /// destroying a clone of the runner's own bookkeeping object is a needless risk.
        /// </summary>
        private SlicedMesh CreateSceneWithSlicer(string suffix, out Scene scene, out Mesh source)
        {
            string path = TempFolder + "/" + FixtureName + suffix + ".unity";

            Assert.IsTrue(EditorSceneManager.SaveScene(SceneManager.GetSceneAt(0), path, true),
                "Could not write the throwaway scene file, so this test cannot run.");

            scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            _openedScenes.Add(scene);

            Assert.IsTrue(scene.IsValid() && scene.isLoaded,
                "The throwaway scene did not open, so this test cannot run.");

            source = BuildFixtureMesh();
            _sources.Add(source);

            var host = new GameObject(FixtureName + suffix, typeof(MeshFilter), typeof(MeshRenderer));
            SceneManager.MoveGameObjectToScene(host, scene);
            host.GetComponent<MeshFilter>().sharedMesh = source;

            var slicer = host.AddComponent<SlicedMesh>();
            slicer.MarginMin = new Vector3(0f, 0.1f, 0f);
            slicer.MarginMax = new Vector3(0f, 0.1f, 0f);

            // Stretched, so the generated mesh genuinely differs from the source and "is it sliced?"
            // is a question with a visible answer.
            host.transform.localScale = new Vector3(1f, 4f, 1f);
            slicer.Rebuild();

            return slicer;
        }

        /// <summary>
        /// Vertex rings at each end of Y and nothing between — the shape this package is for. Kept
        /// deliberately separate from the copy in SlicedMeshComponentTests: sharing it would couple
        /// two fixtures that are free to diverge.
        /// </summary>
        private static Mesh BuildFixtureMesh()
        {
            var mesh = new Mesh { name = "SaveHookFixtureMesh" };

            float[] rows = { 0f, 0.05f, 0.55f, 0.6f };
            var vertices = new Vector3[rows.Length * 4];
            var uvs = new Vector2[vertices.Length];

            for (int r = 0; r < rows.Length; r++)
            {
                vertices[r * 4 + 0] = new Vector3(-0.5f, rows[r], -0.5f);
                vertices[r * 4 + 1] = new Vector3(0.5f, rows[r], -0.5f);
                vertices[r * 4 + 2] = new Vector3(0.5f, rows[r], 0.5f);
                vertices[r * 4 + 3] = new Vector3(-0.5f, rows[r], 0.5f);

                for (int c = 0; c < 4; c++)
                {
                    uvs[r * 4 + c] = new Vector2(c * 0.25f, rows[r]);
                }
            }

            var triangles = new List<int>();
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
            mesh.uv = uvs;
            mesh.triangles = triangles.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}

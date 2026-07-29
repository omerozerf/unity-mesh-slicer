using System.Collections.Generic;
using Loop.MeshSlicer;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Loop.MeshSlicer.EditorTools
{
    /// <summary>
    /// Keeps sliced objects out of scene diffs.
    ///
    /// The generated mesh is marked <see cref="HideFlags.HideAndDontSave"/>, so the mesh *data*
    /// never reaches the scene file — but the MeshFilter's reference to it does, and Unity writes an
    /// unsaveable reference as null. Measured on a scratch scene: an object slicing a normal FBX
    /// saves as <c>m_Mesh: {fileID: 0}</c>.
    ///
    /// Nothing breaks — the component rebuilds on load and the object looks right again — which is
    /// exactly why it is worth fixing: every save silently rewrites that line, so sliced objects
    /// show up in diffs nobody made, and sliced prefab instances carry a permanent Mesh override.
    /// On a team that resolves scene YAML by hand, that noise has a real cost.
    ///
    /// So: put the real mesh back just before the file is written, and re-slice immediately after.
    /// The swap is invisible in the editor because both callbacks run inside the same save.
    ///
    /// **Tracked per scene, because a multi-scene save batches the callbacks.** Measured on Unity
    /// 6000.3 with two scenes through <c>EditorSceneManager.SaveScenes</c>: the order is
    /// <c>saving:A → saving:B → saved:A → saved:B</c>, not interleaved per scene. A single shared
    /// list cleared at the top of every <c>sceneSaving</c> therefore loses scene A's entries when
    /// scene B starts, and A's slicers are never re-sliced. Nothing is corrupted on disk — the file
    /// gets exactly the source-mesh reference this hook wants it to have — but A's objects sit
    /// un-sliced in the editor until something else pokes them.
    /// </summary>
    [InitializeOnLoad]
    internal static class SlicedMeshSaveHook
    {
        // Per scene, and only the slicers actually restored for it, so the rebuild pass cannot touch
        // anything the restore pass skipped — nor anything belonging to another scene in the batch.
        private static readonly Dictionary<Scene, List<SlicedMesh>> Restored =
            new Dictionary<Scene, List<SlicedMesh>>();

        static SlicedMeshSaveHook()
        {
            EditorSceneManager.sceneSaving -= OnSceneSaving;
            EditorSceneManager.sceneSaving += OnSceneSaving;
            EditorSceneManager.sceneSaved -= OnSceneSaved;
            EditorSceneManager.sceneSaved += OnSceneSaved;
        }

        private static void OnSceneSaving(Scene scene, string path)
        {
            List<SlicedMesh> restored = TakeList(scene);

            if (!scene.IsValid() || !scene.isLoaded)
            {
                return;
            }

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                // Inactive objects included: their MeshFilter reference is written to the file just
                // the same.
                foreach (SlicedMesh slicer in root.GetComponentsInChildren<SlicedMesh>(true))
                {
                    if (slicer == null || !slicer.isActiveAndEnabled)
                    {
                        continue;
                    }

                    slicer.RestoreSource();
                    restored.Add(slicer);
                }
            }
        }

        private static void OnSceneSaved(Scene scene)
        {
            if (!Restored.TryGetValue(scene, out List<SlicedMesh> restored))
            {
                return;
            }

            for (int i = 0; i < restored.Count; i++)
            {
                SlicedMesh slicer = restored[i];

                // A save can destroy objects in between (entering play mode saves first), so this
                // has to survive the reference having gone stale.
                if (slicer != null)
                {
                    slicer.Rebuild();
                }
            }

            // Dropped rather than kept empty: a Scene struct is only a handle, and holding one for a
            // scene that has since been closed would keep this dictionary growing for the session.
            Restored.Remove(scene);
        }

        /// <summary>
        /// The scene's list, emptied and ready to fill. Reuses the allocation across saves of the
        /// same scene, which is the common case by a wide margin.
        /// </summary>
        private static List<SlicedMesh> TakeList(Scene scene)
        {
            if (!Restored.TryGetValue(scene, out List<SlicedMesh> restored))
            {
                restored = new List<SlicedMesh>();
                Restored[scene] = restored;
                return restored;
            }

            // Non-empty here means the previous save of this scene never got its `sceneSaved` — a
            // cancelled save, or an exception in another handler. Those entries are already restored
            // to source and will be rebuilt by the component's own LateUpdate; dropping them is
            // correct, keeping them would rebuild objects this save never touched.
            restored.Clear();
            return restored;
        }
    }
}

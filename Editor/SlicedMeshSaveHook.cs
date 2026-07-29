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
    /// </summary>
    [InitializeOnLoad]
    internal static class SlicedMeshSaveHook
    {
        // Only the slicers actually restored for this save, so the rebuild pass cannot touch
        // anything the restore pass skipped.
        private static readonly List<SlicedMesh> Restored = new List<SlicedMesh>();

        static SlicedMeshSaveHook()
        {
            EditorSceneManager.sceneSaving -= OnSceneSaving;
            EditorSceneManager.sceneSaving += OnSceneSaving;
            EditorSceneManager.sceneSaved -= OnSceneSaved;
            EditorSceneManager.sceneSaved += OnSceneSaved;
        }

        private static void OnSceneSaving(Scene scene, string path)
        {
            Restored.Clear();

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
                    Restored.Add(slicer);
                }
            }
        }

        private static void OnSceneSaved(Scene scene)
        {
            for (int i = 0; i < Restored.Count; i++)
            {
                SlicedMesh slicer = Restored[i];

                // A save can destroy objects in between (entering play mode saves first), so this
                // has to survive the reference having gone stale.
                if (slicer != null)
                {
                    slicer.Rebuild();
                }
            }

            Restored.Clear();
        }
    }
}

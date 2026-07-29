using UnityEditor;
using UnityEngine;

namespace Loop.MeshSlicer.EditorTools
{
    /// <summary>
    /// Inspector for <see cref="SlicedMesh"/>: the margin fields, the two buttons that do the
    /// thinking for the artist, and a live readout of what the current scale is doing.
    ///
    /// The scene-view gizmos are the important half. Margins are numbers in mesh-local units, which
    /// nobody can picture; drawing the slice planes on the model turns them into something the
    /// artist can aim at.
    /// </summary>
    [CustomEditor(typeof(SlicedMesh))]
    [CanEditMultipleObjects]
    public sealed class SlicedMeshEditor : UnityEditor.Editor
    {
        private static readonly Color SlicePlaneColor = new Color(0.25f, 0.85f, 1f, 1f);
        private static readonly Color SlicePlaneFill = new Color(0.25f, 0.85f, 1f, 0.08f);

        private static readonly string[] AxisNames = { "X", "Y", "Z" };

        private static readonly Vector3[] s_PlaneCorners = new Vector3[4];

        private SerializedProperty _sourceMesh;
        private SerializedProperty _referenceScale;
        private SerializedProperty _marginMin;
        private SerializedProperty _marginMax;
        private SerializedProperty _uvModes;
        private SerializedProperty _uvChannels;
        private SerializedProperty _uvDensity;
        private SerializedProperty _updateBoxCollider;

        // Held from the last Auto-Fit so the artist can read what the model actually measured,
        // rather than being told a number is fine.
        private SlicedMesh.UvFit[] _lastFit;

        private void OnEnable()
        {
            _sourceMesh = serializedObject.FindProperty("_sourceMesh");
            _referenceScale = serializedObject.FindProperty("_referenceScale");
            _marginMin = serializedObject.FindProperty("_marginMin");
            _marginMax = serializedObject.FindProperty("_marginMax");
            _uvModes = serializedObject.FindProperty("_uvModes");
            _uvChannels = serializedObject.FindProperty("_uvChannels");
            _uvDensity = serializedObject.FindProperty("_uvDensity");
            _updateBoxCollider = serializedObject.FindProperty("_updateBoxCollider");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var slicer = (SlicedMesh)target;

            EditorGUILayout.PropertyField(_sourceMesh);

            if (slicer.SourceMesh == null)
            {
                EditorGUILayout.HelpBox(
                    "No source mesh. Assign one, or put this component on an object whose MeshFilter " +
                    "already has the imported mesh.",
                    MessageType.Warning);
                serializedObject.ApplyModifiedProperties();
                return;
            }

            EditorGUILayout.PropertyField(_referenceScale);
            EditorGUILayout.Space(2f);
            EditorGUILayout.PropertyField(_marginMin);
            EditorGUILayout.PropertyField(_marginMax);
            EditorGUILayout.PropertyField(_updateBoxCollider);

            DrawUvSection(slicer);

            EditorGUILayout.Space(6f);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent(
                        "Auto-Detect Margins",
                        "Finds the widest empty span in the middle of each axis and makes everything " +
                        "outside it rigid.")))
                {
                    foreach (Object each in targets)
                    {
                        var target = (SlicedMesh)each;
                        Undo.RecordObject(target, "Auto-Detect Slice Margins");
                        target.AutoDetectMargins();
                        EditorUtility.SetDirty(target);
                    }
                }

                if (GUILayout.Button(new GUIContent(
                        "Set Reference = Current Scale",
                        "Treats the object's current scale as authored size. Do this if the model " +
                        "looks wrong the moment the component is added.")))
                {
                    foreach (Object each in targets)
                    {
                        var target = (SlicedMesh)each;
                        Undo.RecordObject(target, "Capture Reference Scale");
                        target.CaptureReferenceScale();
                        EditorUtility.SetDirty(target);
                    }
                }
            }

            if (targets.Length == 1)
            {
                DrawStretchReadout(slicer);
            }

            serializedObject.ApplyModifiedProperties();
        }

        /// <summary>
        /// The UV block: what the texture does on each stretched axis, plus the measurements behind
        /// it. Everything here is off by default — a model has to be asked to do this, because
        /// advancing UVs on an atlassed unwrap walks into the neighbouring island.
        /// </summary>
        private void DrawUvSection(SlicedMesh slicer)
        {
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("UV", EditorStyles.boldLabel);

            for (int axis = 0; axis < 3; axis++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.PropertyField(
                        _uvModes.GetArrayElementAtIndex(axis),
                        new GUIContent(AxisNames[axis]));

                    using (new EditorGUI.DisabledScope(
                               slicer.GetUvMode(axis) != UvSliceMode.PreserveDensity))
                    {
                        EditorGUILayout.PropertyField(
                            _uvChannels.GetArrayElementAtIndex(axis), GUIContent.none,
                            GUILayout.Width(50f));
                    }
                }
            }

            EditorGUILayout.PropertyField(_uvDensity, new GUIContent("Density"));

            if (GUILayout.Button(new GUIContent(
                    "Auto-Fit UV Density",
                    "Measures which UV coordinate follows each axis and how fast, straight off the " +
                    "model. Does not switch anything on.")))
            {
                foreach (Object each in targets)
                {
                    var target = (SlicedMesh)each;
                    Undo.RecordObject(target, "Auto-Fit UV Density");
                    SlicedMesh.UvFit[] fit = target.AutoFitUvDensity();
                    EditorUtility.SetDirty(target);
                    if (targets.Length == 1)
                    {
                        _lastFit = fit;
                    }
                }
            }

            DrawFitReadout();
            DrawWrapModeWarning(slicer);
        }

        /// <summary>
        /// Shows both fit numbers, because either one alone misleads. Correlation says which UV
        /// coordinate follows the axis; the residual says whether the model's unwrap follows it
        /// closely enough for the result to look like tiling rather than sliding.
        /// </summary>
        private void DrawFitReadout()
        {
            if (_lastFit == null)
            {
                return;
            }

            bool anyPoor = false;
            for (int axis = 0; axis < _lastFit.Length; axis++)
            {
                SlicedMesh.UvFit fit = _lastFit[axis];
                if (!fit.Valid)
                {
                    EditorGUILayout.LabelField($"{AxisNames[axis]}: no usable fit", EditorStyles.miniLabel);
                    continue;
                }

                bool poor = Mathf.Abs(fit.WorstResidual) > SlicedMesh.PoorFitResidual;
                anyPoor |= poor;

                EditorGUILayout.LabelField(
                    $"{AxisNames[axis]} → {fit.Channel}   density {fit.Density:0.###}   " +
                    $"fit {Mathf.Abs(fit.Correlation):0.00}   off-line {fit.WorstResidual:P0}" +
                    (poor ? "   ← poor" : string.Empty),
                    EditorStyles.miniLabel);
            }

            if (anyPoor)
            {
                EditorGUILayout.HelpBox(
                    "On the axes marked poor, this model's unwrap does not follow the axis closely " +
                    "enough — some faces would slide their texture rather than tile it. The " +
                    "off-line figure is how far the worst vertex sits from the measured line, as a " +
                    "share of the UV range.",
                    MessageType.Warning);
            }
        }

        /// <summary>
        /// Preserving density means letting the texture run past its authored bounds, which only
        /// works if the texture repeats. On a clamped texture it smears the edge pixel instead —
        /// silently, which is why this is worth saying out loud.
        ///
        /// Only the main texture is checked. Walking every texture property flags the wrong things:
        /// ramps, masks and matcaps are commonly Clamp by design and are not sampled by the mesh's
        /// UVs at all, so including them turns this into a warning that is always on and therefore
        /// never read. (Measured in the authoring project: 50 of 50 toon ramps are Clamp.)
        /// </summary>
        private void DrawWrapModeWarning(SlicedMesh slicer)
        {
            if (!slicer.AnyUvSlicing)
            {
                return;
            }

            var renderer = slicer.GetComponent<Renderer>();
            if (renderer == null)
            {
                return;
            }

            foreach (Material material in renderer.sharedMaterials)
            {
                Texture texture = material == null ? null : material.mainTexture;

                // Mirror tiles as well as Repeat does — it just alternates direction, which for a
                // stretched middle is a seamless result too.
                if (texture == null ||
                    texture.wrapMode == TextureWrapMode.Repeat ||
                    texture.wrapMode == TextureWrapMode.Mirror)
                {
                    continue;
                }

                EditorGUILayout.HelpBox(
                    $"'{texture.name}' has wrap mode {texture.wrapMode}. Preserving density needs " +
                    "Repeat or Mirror, or the texture will smear at its edge instead of tiling.",
                    MessageType.Warning);
                return;
            }
        }

        /// <summary>
        /// Says, per axis, how far the middle is being stretched right now. This is the number that
        /// explains a bad-looking result: a middle at 8× is texture-smeared no matter how correct
        /// the geometry is, and the fix is a wider middle, not a different margin.
        /// </summary>
        private static void DrawStretchReadout(SlicedMesh slicer)
        {
            Bounds bounds = slicer.SourceMesh.bounds;
            Vector3 scale = slicer.transform.localScale;
            var text = new System.Text.StringBuilder("Middle stretch:  ");

            for (int axis = 0; axis < 3; axis++)
            {
                SliceMath.Axis solved = SliceMath.SolveAxis(
                    bounds.min[axis], bounds.max[axis],
                    slicer.MarginMin[axis], slicer.MarginMax[axis],
                    scale[axis], slicer.ReferenceScale[axis]);

                float relative = solved.RigidJacobian > Mathf.Epsilon
                    ? solved.MiddleJacobian / solved.RigidJacobian
                    : 0f;

                text.Append("XYZ"[axis]).Append(' ').Append(relative.ToString("0.00")).Append("x   ");
            }

            EditorGUILayout.LabelField(text.ToString(), EditorStyles.miniLabel);
        }

        private void OnSceneGUI()
        {
            var slicer = (SlicedMesh)target;
            if (slicer.SourceMesh == null)
            {
                return;
            }

            Bounds bounds = slicer.SourceMesh.bounds;
            Vector3 scale = slicer.transform.localScale;

            using (new Handles.DrawingScope(SlicePlaneColor, slicer.transform.localToWorldMatrix))
            {
                for (int axis = 0; axis < 3; axis++)
                {
                    float min = bounds.min[axis];
                    float max = bounds.max[axis];

                    // The planes must be drawn where the cut lands in the *generated* mesh, not at
                    // the raw source coordinate. The generated mesh is pre-divided by localScale and
                    // this matrix multiplies it back, so an unmapped coordinate is off by the whole
                    // scale factor — correct only while scale equals reference, i.e. never when the
                    // artist is actually resizing something.
                    SliceMath.Axis solved = SliceMath.SolveAxis(
                        min, max, slicer.MarginMin[axis], slicer.MarginMax[axis],
                        scale[axis], slicer.ReferenceScale[axis]);

                    DrawSlicePlane(bounds, axis, solved.Remap(min + slicer.MarginMin[axis]));
                    DrawSlicePlane(bounds, axis, solved.Remap(max - slicer.MarginMax[axis]));
                }
            }
        }

        /// <summary>
        /// Draws the boundary between a rigid end and the stretchable middle as a rectangle across
        /// the mesh's own bounds — the plane the geometry is actually cut along.
        /// </summary>
        private static void DrawSlicePlane(Bounds bounds, int axis, float position)
        {
            // A plane sitting exactly on the bounds edge means "no margin", and drawing it there is
            // just a box outline that hides the planes that matter.
            if (position <= bounds.min[axis] + Mathf.Epsilon || position >= bounds.max[axis] - Mathf.Epsilon)
            {
                return;
            }

            int a = (axis + 1) % 3;
            int b = (axis + 2) % 3;

            // Reused rather than allocated: this runs six times per scene-view repaint, which is
            // continuous while the artist drags the scale handle.
            Vector3[] corners = s_PlaneCorners;
            for (int i = 0; i < 4; i++)
            {
                var corner = Vector3.zero;
                corner[axis] = position;
                corner[a] = (i == 0 || i == 3) ? bounds.min[a] : bounds.max[a];
                corner[b] = (i < 2) ? bounds.min[b] : bounds.max[b];
                corners[i] = corner;
            }

            Handles.DrawSolidRectangleWithOutline(corners, SlicePlaneFill, SlicePlaneColor);
        }
    }
}

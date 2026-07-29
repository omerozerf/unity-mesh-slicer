using UnityEngine;

namespace Loop.MeshSlicer
{
    /// <summary>What happens to the texture on an axis that stretches.</summary>
    public enum UvSliceMode
    {
        /// <summary>The texture stretches with the geometry. The default, and what every mesh did
        /// before UV slicing existed.</summary>
        Stretch = 0,

        /// <summary>The texture keeps its authored size and repeats to fill the extra length.
        /// Needs a texture whose wrap mode is Repeat, and an unwrap that follows the axis.</summary>
        PreserveDensity = 1
    }

    /// <summary>Which UV coordinate an axis drives.</summary>
    public enum UvChannel
    {
        /// <summary>The first UV coordinate.</summary>
        U = 0,

        /// <summary>The second UV coordinate.</summary>
        V = 1
    }

    /// <summary>
    /// Makes a mesh stretch like a 9-slice sprite: scale the object as usual, and the detailed ends
    /// keep their size while the plain middle absorbs the change.
    ///
    /// **The source asset is never modified.** The component keeps a reference to the imported mesh
    /// and feeds the <see cref="MeshFilter"/> a generated copy marked
    /// <see cref="HideFlags.HideAndDontSave"/>, so nothing is written to the FBX and nothing is
    /// serialized into the scene. Removing or disabling the component puts the original back.
    ///
    /// **Why it reads <c>localScale</c> instead of exposing a size field.** Artists already resize
    /// these objects with the scale handle, and the product prefabs already ship non-unit scales.
    /// Reading the scale means the tool disappears into the workflow the artist has; the generated
    /// mesh is pre-divided by the scale so the transform's own multiply lands on the intended size.
    ///
    /// **Reference scale.** Rigid does not mean "at scale 1" — an FBX node usually imports at some
    /// other scale (this project's Separator sits near 0.71/1.11/1.18). The scale in effect when the
    /// component is attached is captured as the reference, and at that scale the generated mesh
    /// equals the source exactly, so attaching the component changes nothing on screen.
    ///
    /// Out of scope for now, and listed in the package README: skinned meshes and MeshCollider.
    /// A <see cref="BoxCollider"/> on the same object is kept in sync.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter))]
    [AddComponentMenu("Loop/Sliced Mesh")]
    public sealed class SlicedMesh : MonoBehaviour
    {
        #region Variables

        [Header("Source")]
        [Tooltip("The imported mesh to slice. Captured from the MeshFilter on first attach; set it " +
                 "by hand only to point at a different mesh.")]
        [SerializeField] private Mesh _sourceMesh;

        [Tooltip("The scale at which the model looks the way it was authored. The generated mesh is " +
                 "identical to the source at this scale, and the margins keep the size they have " +
                 "here. Captured on attach.")]
        [SerializeField] private Vector3 _referenceScale = Vector3.one;

        [Header("Margins (source units)")]
        [Tooltip("Rigid thickness at the low end of each axis. Everything within this distance of " +
                 "the mesh's minimum keeps its shape; 0 means the axis stretches like plain scaling.")]
        [SerializeField] private Vector3 _marginMin = Vector3.zero;

        [Tooltip("Rigid thickness at the high end of each axis.")]
        [SerializeField] private Vector3 _marginMax = Vector3.zero;

        [Header("UV")]
        [Tooltip("Per axis: what the texture does where the model stretches. Stretch is the " +
                 "default and leaves UVs untouched. Preserve Density keeps the texture at its " +
                 "authored size and lets it repeat instead — only correct when the texture's wrap " +
                 "mode is Repeat and the unwrap follows this axis.")]
        [SerializeField] private UvSliceMode[] _uvModes = { UvSliceMode.Stretch, UvSliceMode.Stretch, UvSliceMode.Stretch };

        [Tooltip("Which UV coordinate each axis drives. Filled in by Auto-Fit.")]
        [SerializeField] private UvChannel[] _uvChannels = { UvChannel.U, UvChannel.V, UvChannel.U };

        [Tooltip("UV units per unit of rendered length, per axis. Measured from the model by " +
                 "Auto-Fit; override by hand if the model's unwrap disagrees with the fit.")]
        [SerializeField] private Vector3 _uvDensity = Vector3.zero;

        [Header("Options")]
        [Tooltip("Keep a BoxCollider on this object in sync with the sliced shape. Off when there " +
                 "is no BoxCollider to update.")]
        [SerializeField] private bool _updateBoxCollider = true;

        // Deliberately NOT [SerializeField]. Unity only serializes private fields that ask for it,
        // so a duplicated object starts with this null and builds its own mesh instead of quietly
        // sharing the original's — which is the failure mode this kind of tool is prone to.
        private Mesh _generatedMesh;

        private MeshFilter _meshFilter;

        // Inputs of the last successful rebuild, so LateUpdate can skip the work when nothing moved.
        private Vector3 _lastScale;
        private Vector3 _lastMarginMin;
        private Vector3 _lastMarginMax;
        private Vector3 _lastReferenceScale;
        private Vector3 _lastUvDensity;
        private int _lastUvSettings = -1;
        private Mesh _lastSourceMesh;
        private bool _hasBuilt;
        private bool _uvsAreSliced;

        // Scratch buffers, reused across rebuilds — a mesh that rebuilds on every scale drag should
        // not allocate on every frame of that drag.
        private Vector3[] _sourceVertices;
        private Vector3[] _sourceNormals;
        private Vector4[] _sourceTangents;
        private Vector2[] _sourceUvs;
        private Vector3[] _targetVertices;
        private Vector3[] _targetNormals;
        private Vector4[] _targetTangents;

        // Only allocated once some axis actually asks for UV slicing; a mesh left on Stretch never
        // pays for a feature it is not using.
        private Vector2[] _targetUvs;

        /// <summary>The imported mesh this component slices.</summary>
        public Mesh SourceMesh => _sourceMesh;

        /// <summary>Scale at which the generated mesh equals the source.</summary>
        public Vector3 ReferenceScale => _referenceScale;

        /// <summary>
        /// Rigid thickness at the low end of each axis, in source units. Clamped to the mesh on
        /// assignment — the inspector path clamps in OnValidate, and code deserves the same
        /// guarantee rather than a value that only makes sense until someone reads it back.
        /// </summary>
        public Vector3 MarginMin
        {
            get => _marginMin;
            set
            {
                _marginMin = value;
                ClampMargins();
            }
        }

        /// <summary>Rigid thickness at the high end of each axis, in source units. Clamped on assignment.</summary>
        public Vector3 MarginMax
        {
            get => _marginMax;
            set
            {
                _marginMax = value;
                ClampMargins();
            }
        }

        /// <summary>What the texture does where this axis stretches.</summary>
        public UvSliceMode GetUvMode(int axis) => _uvModes[axis];

        /// <summary>Sets what the texture does where this axis stretches.</summary>
        public void SetUvMode(int axis, UvSliceMode mode) => _uvModes[axis] = mode;

        /// <summary>Which UV coordinate this axis drives.</summary>
        public UvChannel GetUvChannel(int axis) => _uvChannels[axis];

        /// <summary>Sets which UV coordinate this axis drives.</summary>
        public void SetUvChannel(int axis, UvChannel channel) => _uvChannels[axis] = channel;

        /// <summary>UV units per unit of rendered length, per axis.</summary>
        public Vector3 UvDensity
        {
            get => _uvDensity;
            set => _uvDensity = value;
        }

        /// <summary>True when at least one axis wants its texture kept at authored size.</summary>
        public bool AnyUvSlicing =>
            _uvModes[0] == UvSliceMode.PreserveDensity ||
            _uvModes[1] == UvSliceMode.PreserveDensity ||
            _uvModes[2] == UvSliceMode.PreserveDensity;

        #endregion

        #region Self Methods

        private void OnEnable()
        {
            EnsureUvArrays();
            _meshFilter = GetComponent<MeshFilter>();
            CaptureSourceIfNeeded();
            Rebuild();
        }

        // The per-axis UV arrays are serialized, so they can come back the wrong length — from a
        // component saved before UV slicing existed, or from someone editing the array size in the
        // inspector. Every read of them is indexed by axis, so a short array would throw.
        private void EnsureUvArrays()
        {
            if (_uvModes == null || _uvModes.Length != 3)
            {
                var modes = new UvSliceMode[3];
                for (int i = 0; i < 3 && _uvModes != null && i < _uvModes.Length; i++)
                {
                    modes[i] = _uvModes[i];
                }

                _uvModes = modes;
            }

            if (_uvChannels == null || _uvChannels.Length != 3)
            {
                var channels = new UvChannel[3];
                for (int i = 0; i < 3 && _uvChannels != null && i < _uvChannels.Length; i++)
                {
                    channels[i] = _uvChannels[i];
                }

                _uvChannels = channels;
            }
        }

        private void OnDisable()
        {
            // Disabling should look like the component was never there — including in the scene
            // file, which must never end up referencing the generated mesh.
            RestoreSource();
            DestroyGenerated();
            _hasBuilt = false;
        }

        private void OnDestroy()
        {
            DestroyGenerated();
        }

        private void Reset()
        {
            // Runs when the component is first added. Adopting the current scale and guessing the
            // margins here means attaching the component is a no-op on screen and already useful —
            // the artist scales and it works, instead of scaling and seeing nothing change.
            // Deliberately does NOT clear _sourceMesh first. Unity runs Reset *after* OnEnable, so
            // by this point the MeshFilter is already showing a generated mesh — clearing the field
            // would throw away the only remaining reference to the imported asset, and the
            // "never adopt a generated mesh" guard in CaptureSourceIfNeeded would (correctly)
            // refuse to find it again, leaving the component permanently sourceless.
            _meshFilter = GetComponent<MeshFilter>();
            CaptureSourceIfNeeded();
            _referenceScale = transform.localScale;
            AutoDetectMargins();
        }

        private void OnValidate()
        {
            EnsureUvArrays();

            // Margins are meaningless outside the mesh, and a head+tail wider than the axis would
            // leave a negative middle.
            ClampMargins();

            if (isActiveAndEnabled)
            {
                Rebuild();
            }
        }

        private void LateUpdate()
        {
            // Cheap enough to run unconditionally: four vector compares in the common case. The
            // alternative — reacting to transform changes — has no edit-mode-safe hook that also
            // covers a parent's scale changing.
            if (NeedsRebuild())
            {
                Rebuild();
            }
        }

        #endregion

        #region Util Methods

        /// <summary>
        /// Rebuilds the generated mesh from the source, the current scale and the margins.
        /// Safe to call at any time; does nothing when there is no source mesh.
        /// </summary>
        public void Rebuild()
        {
            if (_meshFilter == null)
            {
                _meshFilter = GetComponent<MeshFilter>();
            }

            CaptureSourceIfNeeded();

            if (_sourceMesh == null || _meshFilter == null)
            {
                return;
            }

            // A new source (or a first build) invalidates every cached buffer.
            if (_sourceVertices == null || _lastSourceMesh != _sourceMesh)
            {
                _sourceVertices = _sourceMesh.vertices;
                _sourceNormals = _sourceMesh.normals;
                _sourceTangents = _sourceMesh.tangents;
                _sourceUvs = _sourceMesh.uv;
                _targetUvs = null;

                int count = _sourceVertices.Length;
                _targetVertices = new Vector3[count];
                _targetNormals = _sourceNormals != null && _sourceNormals.Length == count
                    ? new Vector3[count]
                    : null;
                _targetTangents = _sourceTangents != null && _sourceTangents.Length == count
                    ? new Vector4[count]
                    : null;

                DestroyGenerated();
            }

            Vector3 scale = transform.localScale;
            Bounds bounds = _sourceMesh.bounds;
            Vector3 min = bounds.min;
            Vector3 max = bounds.max;

            SliceMath.Axis axisX = SliceMath.SolveAxis(min.x, max.x, _marginMin.x, _marginMax.x, scale.x, _referenceScale.x);
            SliceMath.Axis axisY = SliceMath.SolveAxis(min.y, max.y, _marginMin.y, _marginMax.y, scale.y, _referenceScale.y);
            SliceMath.Axis axisZ = SliceMath.SolveAxis(min.z, max.z, _marginMin.z, _marginMax.z, scale.z, _referenceScale.z);

            bool sliceUvs = AnyUvSlicing && _sourceUvs != null && _sourceUvs.Length == _sourceVertices.Length;
            if (sliceUvs && (_targetUvs == null || _targetUvs.Length != _sourceUvs.Length))
            {
                _targetUvs = new Vector2[_sourceUvs.Length];
            }

            for (int i = 0; i < _sourceVertices.Length; i++)
            {
                Vector3 v = _sourceVertices[i];
                _targetVertices[i] = new Vector3(axisX.Remap(v.x), axisY.Remap(v.y), axisZ.Remap(v.z));

                // Normals are corrected with the mapping's own derivative at that vertex, so the
                // authored (toon) normals survive stretching. RecalculateNormals would throw the
                // artist's custom split normals away and hard-edge the model.
                float jx = axisX.JacobianAt(v.x);
                float jy = axisY.JacobianAt(v.y);
                float jz = axisZ.JacobianAt(v.z);

                if (_targetNormals != null)
                {
                    Vector3 n = _sourceNormals[i];
                    _targetNormals[i] = SafeNormalize(new Vector3(n.x / jx, n.y / jy, n.z / jz), n);
                }

                if (_targetTangents != null)
                {
                    Vector4 t = _sourceTangents[i];
                    // A tangent points along the surface, so it transforms with the mapping rather
                    // than against it. w carries bitangent handedness and must be copied as-is.
                    Vector3 scaled = SafeNormalize(
                        new Vector3(t.x * jx, t.y * jy, t.z * jz), new Vector3(t.x, t.y, t.z));
                    _targetTangents[i] = new Vector4(scaled.x, scaled.y, scaled.z, t.w);
                }

                if (sliceUvs)
                {
                    // Advance the UV by however far the vertex moved on screen. In the rigid ends
                    // that distance is zero, so their texture is untouched without asking; across
                    // the stretched middle the texture repeats instead of smearing.
                    Vector2 uvOut = _sourceUvs[i];
                    AdvanceUv(ref uvOut, 0, axisX.RenderedDisplacement(v.x));
                    AdvanceUv(ref uvOut, 1, axisY.RenderedDisplacement(v.y));
                    AdvanceUv(ref uvOut, 2, axisZ.RenderedDisplacement(v.z));
                    _targetUvs[i] = uvOut;
                }
            }

            EnsureGenerated();

            _generatedMesh.vertices = _targetVertices;
            if (_targetNormals != null)
            {
                _generatedMesh.normals = _targetNormals;
            }

            if (_targetTangents != null)
            {
                _generatedMesh.tangents = _targetTangents;
            }

            if (sliceUvs)
            {
                _generatedMesh.uv = _targetUvs;
                _uvsAreSliced = true;
            }
            else if (_uvsAreSliced)
            {
                // Switched back to Stretch. Without this the generated mesh would keep whatever UVs
                // the last sliced rebuild left on it, and turning the feature off would look like it
                // had done nothing.
                _generatedMesh.uv = _sourceUvs;
                _uvsAreSliced = false;
            }

            _generatedMesh.RecalculateBounds();

            if (_meshFilter.sharedMesh != _generatedMesh)
            {
                _meshFilter.sharedMesh = _generatedMesh;
            }

            if (_updateBoxCollider)
            {
                UpdateBoxCollider(axisX, axisY, axisZ);
            }

            _lastScale = scale;
            _lastMarginMin = _marginMin;
            _lastMarginMax = _marginMax;
            _lastReferenceScale = _referenceScale;
            _lastUvDensity = _uvDensity;
            _lastUvSettings = UvSettingsFingerprint();
            _lastSourceMesh = _sourceMesh;
            _hasBuilt = true;
        }

        // Six enum values packed into an int, so the dirty check can compare UV settings the same
        // cheap way it compares everything else.
        private int UvSettingsFingerprint()
        {
            int packed = 0;
            for (int axis = 0; axis < 3; axis++)
            {
                packed = (packed << 2) | (int)_uvModes[axis];
                packed = (packed << 2) | (int)_uvChannels[axis];
            }

            return packed;
        }

        /// <summary>
        /// Adopts the object's current scale as the reference — the scale at which the model is
        /// considered "authored size" and the generated mesh matches the source exactly.
        /// </summary>
        public void CaptureReferenceScale()
        {
            _referenceScale = transform.localScale;
            Rebuild();
        }

        /// <summary>
        /// Puts the imported mesh back on the MeshFilter. Called when the component is disabled, and
        /// by the editor before the component is removed.
        /// </summary>
        public void RestoreSource()
        {
            if (_meshFilter == null)
            {
                _meshFilter = GetComponent<MeshFilter>();
            }

            if (_meshFilter != null && _sourceMesh != null && _meshFilter.sharedMesh != _sourceMesh)
            {
                _meshFilter.sharedMesh = _sourceMesh;
            }
        }

        /// <summary>
        /// Result of measuring one axis against the model's unwrap. Handed back by
        /// <see cref="AutoFitUvDensity"/> so the editor can show the artist what it found rather
        /// than silently acting on it.
        /// </summary>
        public readonly struct UvFit
        {
            /// <summary>The UV coordinate that follows this axis most closely.</summary>
            public readonly UvChannel Channel;

            /// <summary>UV units per unit of rendered length.</summary>
            public readonly float Density;

            /// <summary>-1..1. How cleanly the chosen channel tracks the axis.</summary>
            public readonly float Correlation;

            /// <summary>
            /// Largest distance off the fitted line as a fraction of the UV range. This, not the
            /// correlation, is the number that says whether the model is a good candidate:
            /// a real separator model scores a respectable 0.75 correlation while a sixth of its
            /// vertices sit up to 0.45 off the line.
            /// </summary>
            public readonly float WorstResidual;

            /// <summary>False when the axis has no usable fit at all.</summary>
            public readonly bool Valid;

            internal UvFit(UvChannel channel, float density, float correlation, float worstResidual, bool valid)
            {
                Channel = channel;
                Density = density;
                Correlation = correlation;
                WorstResidual = worstResidual;
                Valid = valid;
            }
        }

        /// <summary>
        /// Residual above which a model is reported as a poor candidate for UV slicing. Placed
        /// between the only two real measurements available: a flat tiled plane fits at 0.0, and a
        /// sculpted separator at 0.45.
        /// </summary>
        public const float PoorFitResidual = 0.1f;

        /// <summary>
        /// Works out, for each axis, which UV coordinate follows it and how fast — the numbers UV
        /// slicing runs on. Reads them off the model so the artist never types a density.
        ///
        /// Does not switch anything on: the modes stay where they are, and the caller decides what
        /// to do with a poor fit.
        /// </summary>
        public UvFit[] AutoFitUvDensity()
        {
            var results = new UvFit[3];
            if (_sourceMesh == null)
            {
                return results;
            }

            Vector3[] vertices = _sourceVertices ?? _sourceMesh.vertices;
            Vector2[] uvs = _sourceUvs ?? _sourceMesh.uv;

            if (uvs == null || uvs.Length != vertices.Length)
            {
                return results;
            }

            for (int axis = 0; axis < 3; axis++)
            {
                bool okU = SliceMath.TryFitUvDensity(vertices, uvs, axis, 0, out float slopeU, out float rU, out float residU);
                bool okV = SliceMath.TryFitUvDensity(vertices, uvs, axis, 1, out float slopeV, out float rV, out float residV);

                if (!okU && !okV)
                {
                    results[axis] = new UvFit(_uvChannels[axis], 0f, 0f, 0f, false);
                    _uvDensity[axis] = 0f;
                    continue;
                }

                // Whichever coordinate tracks the axis more closely is the one it drives. Measured
                // on real art this is not a close call — the tracking channel scores 1.0 and the
                // other 0.0.
                bool pickU = okU && (!okV || Mathf.Abs(rU) >= Mathf.Abs(rV));

                float slope = pickU ? slopeU : slopeV;
                float correlation = pickU ? rU : rV;
                float residual = pickU ? residU : residV;

                // The fit is in UV per *source* unit; the mapping works in rendered units, and the
                // model renders at its reference scale when it is authored size.
                float reference = Mathf.Max(Mathf.Abs(_referenceScale[axis]), SliceMath.MinScale);
                float density = slope / reference;

                _uvChannels[axis] = pickU ? UvChannel.U : UvChannel.V;
                _uvDensity[axis] = density;
                results[axis] = new UvFit(_uvChannels[axis], density, correlation, residual, true);
            }

            Rebuild();
            return results;
        }

        /// <summary>
        /// Number of histogram bins used when guessing margins. Ten is enough resolution to find the
        /// empty middle of a hand-modelled prop without turning every gap between two vertex rings
        /// into a false positive.
        /// </summary>
        public const int HistogramBins = 10;

        /// <summary>
        /// Fraction of each end treated as rigid when no empty middle can be found — a defensible
        /// "looks like a frame" default rather than a refusal to do anything.
        /// </summary>
        public const float FallbackMarginFraction = 0.25f;

        /// <summary>
        /// Guesses margins for all three axes from where the vertices actually sit: detail clusters
        /// at the ends of a sliceable model, and the empty span between the clusters is what should
        /// stretch. Falls back to <see cref="FallbackMarginFraction"/> on axes with no clear gap.
        /// </summary>
        public void AutoDetectMargins()
        {
            if (_sourceMesh == null)
            {
                return;
            }

            Vector3 size = _sourceMesh.bounds.size;
            var bins = new int[HistogramBins];

            for (int axis = 0; axis < 3; axis++)
            {
                float length = size[axis];
                if (length <= Mathf.Epsilon)
                {
                    _marginMin[axis] = 0f;
                    _marginMax[axis] = 0f;
                    continue;
                }

                if (TryBuildHistogram(axis, HistogramBins, bins) &&
                    SliceMath.TrySuggestMargins(bins, length, out float low, out float high))
                {
                    _marginMin[axis] = low;
                    _marginMax[axis] = high;
                }
                else
                {
                    _marginMin[axis] = length * FallbackMarginFraction;
                    _marginMax[axis] = length * FallbackMarginFraction;
                }
            }

            ClampMargins();
            Rebuild();
        }

        /// <summary>
        /// Bins the source vertices along an axis. The editor uses this to find the empty middle a
        /// sliceable model has; exposed here so the logic sits next to the mesh it describes.
        /// </summary>
        public bool TryBuildHistogram(int axis, int binCount, int[] bins)
        {
            if (_sourceMesh == null || bins == null || bins.Length < binCount || binCount <= 0)
            {
                return false;
            }

            Vector3[] vertices = _sourceVertices ?? _sourceMesh.vertices;
            Bounds bounds = _sourceMesh.bounds;
            float min = bounds.min[axis];
            float length = bounds.size[axis];

            if (length <= Mathf.Epsilon)
            {
                return false;
            }

            for (int i = 0; i < binCount; i++)
            {
                bins[i] = 0;
            }

            for (int i = 0; i < vertices.Length; i++)
            {
                int index = Mathf.Clamp((int)((vertices[i][axis] - min) / length * binCount), 0, binCount - 1);
                bins[index]++;
            }

            return true;
        }

        private void CaptureSourceIfNeeded()
        {
            if (_sourceMesh != null || _meshFilter == null)
            {
                return;
            }

            Mesh current = _meshFilter.sharedMesh;

            // Never adopt a generated mesh as the source — that would bake one rebuild's output in
            // as the new "authored" shape and compound it on every subsequent build.
            if (current == null || current == _generatedMesh || (current.hideFlags & HideFlags.DontSave) != 0)
            {
                return;
            }

            _sourceMesh = current;
        }

        private void EnsureGenerated()
        {
            if (_generatedMesh != null)
            {
                return;
            }

            _generatedMesh = new Mesh
            {
                name = _sourceMesh.name + " (Sliced)",
                // Never saved to the scene or a prefab: this mesh is derived data, rebuilt on load.
                hideFlags = HideFlags.HideAndDontSave,
                indexFormat = _sourceMesh.indexFormat
            };

            // Rewritten every time the scale changes, which for a hand-dragged gizmo is every frame
            // of the drag. Telling Unity that up front lets it pick the right buffer strategy.
            _generatedMesh.MarkDynamic();

            // Vertex data is written by Rebuild; everything else is carried over untouched so the
            // sliced mesh renders with the same materials, UVs and vertex colours as the original.
            _generatedMesh.vertices = _sourceMesh.vertices;
            _generatedMesh.subMeshCount = _sourceMesh.subMeshCount;

            for (int i = 0; i < _sourceMesh.subMeshCount; i++)
            {
                _generatedMesh.SetTriangles(_sourceMesh.GetTriangles(i), i, false);
            }

            _generatedMesh.uv = _sourceMesh.uv;
            _generatedMesh.uv2 = _sourceMesh.uv2;
            _generatedMesh.uv3 = _sourceMesh.uv3;
            _generatedMesh.uv4 = _sourceMesh.uv4;
            _generatedMesh.colors = _sourceMesh.colors;
        }

        private void DestroyGenerated()
        {
            if (_generatedMesh == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Destroy(_generatedMesh);
            }
            else
            {
                DestroyImmediate(_generatedMesh);
            }

            _generatedMesh = null;
        }

        private bool NeedsRebuild()
        {
            if (!_hasBuilt || _generatedMesh == null || _sourceMesh != _lastSourceMesh)
            {
                return true;
            }

            return transform.localScale != _lastScale
                   || _marginMin != _lastMarginMin
                   || _marginMax != _lastMarginMax
                   || _referenceScale != _lastReferenceScale
                   || _uvDensity != _lastUvDensity
                   || UvSettingsFingerprint() != _lastUvSettings;
        }

        private void ClampMargins()
        {
            if (_sourceMesh == null)
            {
                return;
            }

            Vector3 size = _sourceMesh.bounds.size;
            for (int axis = 0; axis < 3; axis++)
            {
                float length = size[axis];
                float low = Mathf.Clamp(_marginMin[axis], 0f, length);
                float high = Mathf.Clamp(_marginMax[axis], 0f, length);

                // Head and tail may meet, but they may not overlap — past that point there is no
                // middle left and the mapping stops being meaningful.
                if (low + high > length)
                {
                    high = length - low;
                }

                _marginMin[axis] = low;
                _marginMax[axis] = high;
            }
        }

        private void UpdateBoxCollider(SliceMath.Axis axisX, SliceMath.Axis axisY, SliceMath.Axis axisZ)
        {
            var box = GetComponent<BoxCollider>();
            if (box == null)
            {
                return;
            }

            // The collider is remapped through the same mapping as the vertices, so a physical edge
            // stays on the visual edge it was placed against.
            Bounds source = _sourceMesh.bounds;
            Vector3 min = new Vector3(
                axisX.Remap(source.min.x), axisY.Remap(source.min.y), axisZ.Remap(source.min.z));
            Vector3 max = new Vector3(
                axisX.Remap(source.max.x), axisY.Remap(source.max.y), axisZ.Remap(source.max.z));

            box.center = (min + max) * 0.5f;
            box.size = max - min;
        }

        // Moves one UV coordinate by the distance this axis displaced the vertex. A no-op unless the
        // axis was asked to preserve density, which is what makes the feature opt-in per axis.
        private void AdvanceUv(ref Vector2 uv, int axis, float renderedDisplacement)
        {
            if (_uvModes[axis] != UvSliceMode.PreserveDensity)
            {
                return;
            }

            int channel = (int)_uvChannels[axis];
            uv[channel] += renderedDisplacement * _uvDensity[axis];
        }

        private static Vector3 SafeNormalize(Vector3 value, Vector3 fallback)
        {
            // A zero-length result means the source vector was degenerate or an axis collapsed;
            // keeping the original beats handing the renderer a zero normal.
            return value.sqrMagnitude > 1e-12f ? value.normalized : fallback;
        }

        #endregion
    }
}

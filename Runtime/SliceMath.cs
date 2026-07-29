using UnityEngine;

namespace Loop.MeshSlicer
{
    /// <summary>
    /// The whole idea of this package, on one axis, with no Unity objects involved.
    ///
    /// A source coordinate is remapped through three zones: a rigid head, a stretchable middle, and
    /// a rigid tail. Rigid means "keeps the size it has at the reference scale" — so when the object
    /// is scaled up, the ends do not grow and the middle absorbs the whole difference. That is the
    /// 9-slice contract, applied to mesh vertices instead of sprite pixels.
    ///
    /// Everything here is deliberately engine-agnostic and allocation-free: it is the only part with
    /// real logic to get wrong, so it is the part that has to be testable without a scene. It is also
    /// LINQ/reflection-free so a Luna playable could adopt the runtime folder unchanged.
    ///
    /// **Compensation, not layout.** The values produced here are *pre-divided by the scale the
    /// transform is about to apply*. Unity multiplies the mesh by <c>localScale</c> after we hand it
    /// over, so to make a 1-unit end cap render as 1 unit at scale 3 we must store 1/3 in the mesh.
    /// Every "target" length below is therefore in mesh-local units *after* compensation, which is
    /// why <see cref="Axis.Remap"/> divides by <c>scale</c> at the very end.
    /// </summary>
    public static class SliceMath
    {
        /// <summary>Scale magnitudes below this are treated as this, so an axis scaled to zero
        /// produces a flat mesh instead of NaNs (dividing by the scale is the last step).</summary>
        public const float MinScale = 1e-5f;

        /// <summary>
        /// A solved single-axis mapping. Build it once per axis per rebuild, then call
        /// <see cref="Remap"/> per vertex — the per-vertex path is three compares and a lerp.
        /// </summary>
        public readonly struct Axis
        {
            /// <summary>Source-space lower bound of the mesh on this axis.</summary>
            public readonly float SourceMin;

            /// <summary>Source coordinate where the rigid head ends.</summary>
            public readonly float HeadEnd;

            /// <summary>Source coordinate where the rigid tail begins.</summary>
            public readonly float TailStart;

            /// <summary>Source-space upper bound of the mesh on this axis.</summary>
            public readonly float SourceMax;

            /// <summary>Output coordinate the head maps to at <see cref="HeadEnd"/>.</summary>
            public readonly float TargetHeadEnd;

            /// <summary>Output coordinate the tail starts at, mapped from <see cref="TailStart"/>.</summary>
            public readonly float TargetTailStart;

            /// <summary>Output-space lower bound.</summary>
            public readonly float TargetMin;

            /// <summary>Output-space upper bound.</summary>
            public readonly float TargetMax;

            /// <summary>
            /// Derivative of the mapping inside the rigid zones — how much a normal must be
            /// counter-scaled there. Equals the reference scale in the normal case, and shrinks
            /// with the margins when they have to be compressed.
            /// </summary>
            public readonly float RigidJacobian;

            /// <summary>Derivative of the mapping inside the stretchable middle.</summary>
            public readonly float MiddleJacobian;

            /// <summary>Magnitude of the scale the transform will apply.</summary>
            public readonly float Scale;

            /// <summary>Magnitude of the scale at which the model is authored size.</summary>
            public readonly float ReferenceScale;

            internal Axis(
                float sourceMin, float headEnd, float tailStart, float sourceMax,
                float targetMin, float targetHeadEnd, float targetTailStart, float targetMax,
                float rigidJacobian, float middleJacobian,
                float scale, float referenceScale)
            {
                Scale = scale;
                ReferenceScale = referenceScale;
                SourceMin = sourceMin;
                HeadEnd = headEnd;
                TailStart = tailStart;
                SourceMax = sourceMax;
                TargetMin = targetMin;
                TargetHeadEnd = targetHeadEnd;
                TargetTailStart = targetTailStart;
                TargetMax = targetMax;
                RigidJacobian = rigidJacobian;
                MiddleJacobian = middleJacobian;
            }

            /// <summary>Maps one source coordinate to its compensated output coordinate.</summary>
            public float Remap(float value)
            {
                if (value <= HeadEnd)
                {
                    // Rigid head: anchored to the target minimum, so the cap keeps its shape and
                    // stays flush with the object's edge.
                    return TargetMin + (value - SourceMin) * RigidJacobian;
                }

                if (value >= TailStart)
                {
                    // Rigid tail: anchored to the target maximum for the same reason, mirrored.
                    return TargetMax - (SourceMax - value) * RigidJacobian;
                }

                return TargetHeadEnd + (value - HeadEnd) * MiddleJacobian;
            }

            /// <summary>
            /// The mapping's derivative at a source coordinate. Normals are divided by this
            /// (component-wise) so a stretched surface still shades correctly.
            /// </summary>
            public float JacobianAt(float value)
            {
                return (value <= HeadEnd || value >= TailStart) ? RigidJacobian : MiddleJacobian;
            }

            /// <summary>
            /// How far slicing moved this coordinate, measured on screen — the distance between
            /// where it renders now and where it rendered at authored size, both taken from the
            /// object's minimum edge.
            ///
            /// This is what UV slicing needs, and it falls out with no special cases: inside the
            /// rigid zones the two positions advance at the same rate, so the answer is exactly
            /// zero; across the tail it is the constant amount the middle grew by; and at
            /// <see cref="Scale"/> == <see cref="ReferenceScale"/> it is zero everywhere, which is
            /// what keeps "attaching the component changes nothing" true even with UV slicing on.
            /// </summary>
            public float RenderedDisplacement(float value)
            {
                return (Remap(value) - TargetMin) * Scale - (value - SourceMin) * ReferenceScale;
            }
        }

        /// <summary>
        /// Solves one axis.
        /// </summary>
        /// <param name="sourceMin">Mesh-space lower bound on this axis.</param>
        /// <param name="sourceMax">Mesh-space upper bound on this axis.</param>
        /// <param name="marginMin">Rigid head thickness, in source units.</param>
        /// <param name="marginMax">Rigid tail thickness, in source units.</param>
        /// <param name="scale">The scale the transform will apply (sign ignored — mirroring is the
        /// transform's job, exactly as with plain scaling).</param>
        /// <param name="referenceScale">The scale at which the model looks the way the artist
        /// authored it. At <paramref name="scale"/> == this, the mapping is the identity. This is
        /// not cosmetic: FBX nodes routinely import at a non-unit scale (Separator sits at
        /// ~0.71/1.11/1.18), so baselining on 1 would change the model the moment the component
        /// is attached.</param>
        public static Axis SolveAxis(
            float sourceMin, float sourceMax,
            float marginMin, float marginMax,
            float scale, float referenceScale)
        {
            float sourceLength = sourceMax - sourceMin;
            float absScale = Mathf.Max(Mathf.Abs(scale), MinScale);
            float absReference = Mathf.Max(Mathf.Abs(referenceScale), MinScale);

            // Target length in mesh-local units. The rendered length is sourceLength * absScale;
            // dividing by absScale is the compensation that lets the transform re-apply it.
            float targetMin = sourceMin;
            float targetMax = sourceMin + sourceLength;

            // Degenerate mesh (flat on this axis): nothing to slice, and every ratio below would
            // divide by zero.
            if (sourceLength <= Mathf.Epsilon)
            {
                return new Axis(
                    sourceMin, sourceMin, sourceMax, sourceMax,
                    targetMin, targetMin, targetMax, targetMax,
                    1f, 1f, absScale, absReference);
            }

            float head = Mathf.Max(0f, marginMin);
            float tail = Mathf.Max(0f, marginMax);

            // Rendered size the rigid zones want to keep: their source thickness at reference scale.
            float renderedHead = head * absReference;
            float renderedTail = tail * absReference;
            float renderedTotal = sourceLength * absScale;

            // Shrunk below the sum of the caps there is no room for both at full size. Sprites
            // compress the margins proportionally rather than overlapping them; do the same.
            float renderedMargins = renderedHead + renderedTail;
            float rigidScale = absReference;
            if (renderedMargins > renderedTotal && renderedMargins > Mathf.Epsilon)
            {
                float squeeze = renderedTotal / renderedMargins;
                renderedHead *= squeeze;
                renderedTail *= squeeze;
                rigidScale *= squeeze;
            }

            float sourceMiddle = sourceLength - head - tail;
            float renderedMiddle = renderedTotal - renderedHead - renderedTail;

            // Jacobians are expressed in compensated (mesh-local) units: rendered / absScale.
            float rigidJacobian = rigidScale / absScale;
            float middleJacobian = sourceMiddle > Mathf.Epsilon
                ? (renderedMiddle / absScale) / sourceMiddle
                // No middle to stretch (margins cover the whole axis): the rigid map is the map.
                : rigidJacobian;

            float targetHeadEnd = targetMin + (renderedHead / absScale);
            float targetTailStart = targetMax - (renderedTail / absScale);

            return new Axis(
                sourceMin, sourceMin + head, sourceMax - tail, sourceMax,
                targetMin, targetHeadEnd, targetTailStart, targetMax,
                rigidJacobian, middleJacobian, absScale, absReference);
        }

        /// <summary>
        /// Measures how fast a UV channel advances along one geometry axis, by fitting a line
        /// through the mesh's own (position, uv) pairs.
        ///
        /// UV slicing needs a number — "this many UV units per unit of model" — and only the model
        /// knows it. A least-squares fit reads it straight off the vertices, which also means the
        /// artist never has to type it.
        ///
        /// It returns two quality numbers because **one of them is not enough**, measured on real
        /// art: a separator model's best-fitting axis scores <c>r = 0.75</c>, which reads as
        /// acceptable, while 16 of its 139 vertices sit up to 44.8% of the UV range off the line.
        /// So <paramref name="correlation"/> is for *choosing* between U and V, and
        /// <paramref name="worstResidual"/> is for deciding whether to use either — a model whose
        /// unwrap does not follow the axis will smear rather than tile, and the residual is what
        /// says so.
        /// </summary>
        /// <param name="positions">Source vertex positions.</param>
        /// <param name="uvs">Source UVs, one per vertex.</param>
        /// <param name="axis">Geometry axis: 0 = X, 1 = Y, 2 = Z.</param>
        /// <param name="channel">UV channel: 0 = U, 1 = V.</param>
        /// <param name="slope">UV units per source unit along the axis. Signed.</param>
        /// <param name="correlation">-1..1. Magnitude near 1 means the pairing is the right one.</param>
        /// <param name="worstResidual">Largest distance off the line, as a fraction of the UV range
        /// (0 = every vertex on the line). Above roughly 0.1 the model is a poor candidate.</param>
        /// <returns>False when the data cannot support a fit — too few vertices, or no variation on
        /// one of the two sides — in which case the outputs are zeroed.</returns>
        public static bool TryFitUvDensity(
            Vector3[] positions, Vector2[] uvs, int axis, int channel,
            out float slope, out float correlation, out float worstResidual)
        {
            slope = 0f;
            correlation = 0f;
            worstResidual = 0f;

            if (positions == null || uvs == null || positions.Length != uvs.Length || positions.Length < 2)
            {
                return false;
            }

            int count = positions.Length;

            double sumX = 0d;
            double sumY = 0d;
            for (int i = 0; i < count; i++)
            {
                sumX += positions[i][axis];
                sumY += uvs[i][channel];
            }

            double meanX = sumX / count;
            double meanY = sumY / count;

            double sxy = 0d;
            double sxx = 0d;
            double syy = 0d;
            for (int i = 0; i < count; i++)
            {
                double dx = positions[i][axis] - meanX;
                double dy = uvs[i][channel] - meanY;
                sxy += dx * dy;
                sxx += dx * dx;
                syy += dy * dy;
            }

            // Flat on either side: the mesh does not extend along this axis, or the UV does not vary
            // on this channel. Either way there is no density to measure.
            if (sxx < 1e-12d || syy < 1e-12d)
            {
                return false;
            }

            double fittedSlope = sxy / sxx;
            double intercept = meanY - fittedSlope * meanX;

            double uvMin = double.MaxValue;
            double uvMax = double.MinValue;
            for (int i = 0; i < count; i++)
            {
                double uv = uvs[i][channel];
                if (uv < uvMin) uvMin = uv;
                if (uv > uvMax) uvMax = uv;
            }

            double uvRange = uvMax - uvMin;
            double worst = 0d;
            for (int i = 0; i < count; i++)
            {
                double predicted = fittedSlope * positions[i][axis] + intercept;
                double residual = uvs[i][channel] - predicted;
                if (residual < 0d) residual = -residual;
                if (residual > worst) worst = residual;
            }

            slope = (float)fittedSlope;
            correlation = (float)(sxy / System.Math.Sqrt(sxx * syy));
            worstResidual = uvRange > 1e-12d ? (float)(worst / uvRange) : 0f;
            return true;
        }

        /// <summary>
        /// Suggests margins for one axis by finding the widest interior run of empty histogram bins
        /// — the "plain middle" a sliceable model has by construction. The detail clusters at the
        /// ends; the span between them is what should stretch.
        ///
        /// Returns false when the vertices are spread evenly enough that no honest gap exists, so
        /// the caller can fall back rather than invent a split.
        /// </summary>
        /// <param name="binCounts">Vertex counts per equal-width bin across the axis extent.</param>
        /// <param name="axisLength">Source length of the axis.</param>
        /// <param name="marginMin">Suggested rigid head thickness, in source units.</param>
        /// <param name="marginMax">Suggested rigid tail thickness, in source units.</param>
        public static bool TrySuggestMargins(
            int[] binCounts, float axisLength, out float marginMin, out float marginMax)
        {
            marginMin = 0f;
            marginMax = 0f;

            if (binCounts == null || binCounts.Length < 3 || axisLength <= Mathf.Epsilon)
            {
                return false;
            }

            // Narrow the search to the span that actually contains geometry. A leading or trailing
            // run of empty bins is empty space, not a middle, and a gap merely *adjacent* to one is
            // the same empty space seen from one bin further in — scanning from a fixed index 1
            // would report it as an interior gap.
            int first = 0;
            while (first < binCounts.Length && binCounts[first] == 0)
            {
                first++;
            }

            int last = binCounts.Length - 1;
            while (last >= 0 && binCounts[last] == 0)
            {
                last--;
            }

            if (first >= last)
            {
                return false;
            }

            int bestStart = -1;
            int bestLength = 0;
            int runStart = -1;

            // Interior bins only: the gap has to have geometry on both sides of it.
            for (int i = first + 1; i < last; i++)
            {
                if (binCounts[i] == 0)
                {
                    if (runStart < 0)
                    {
                        runStart = i;
                    }

                    int length = i - runStart + 1;
                    if (length > bestLength)
                    {
                        bestLength = length;
                        bestStart = runStart;
                    }
                }
                else
                {
                    runStart = -1;
                }
            }

            if (bestStart < 0)
            {
                return false;
            }

            float binWidth = axisLength / binCounts.Length;
            marginMin = bestStart * binWidth;
            marginMax = (binCounts.Length - (bestStart + bestLength)) * binWidth;
            return true;
        }
    }
}

using Loop.MeshSlicer;
using NUnit.Framework;
using UnityEngine;

namespace Loop.MeshSlicer.Tests
{
    /// <summary>
    /// Tests for the mapping itself. Everything the package promises is a property of these
    /// functions, so every promise gets an assertion that fails when the promise is broken —
    /// identity at reference scale, rigid ends that keep their rendered size, and a middle that
    /// absorbs the rest.
    ///
    /// Bounds are deliberately asymmetric in most cases (0.2 .. 1.4 rather than -0.6 .. 0.6): the
    /// art in this project bakes pivot offsets into the vertex data, and a symmetric fixture would
    /// pass even if the code assumed the mesh was centred on its pivot.
    /// </summary>
    public class SliceMathTests
    {
        private const float Tolerance = 1e-4f;

        private const float Min = 0.2f;
        private const float Max = 1.4f;
        private const float Head = 0.3f;
        private const float Tail = 0.4f;

        [Test]
        public void Remap_IsIdentity_AtReferenceScale()
        {
            // The one that keeps attaching the component from changing what is on screen.
            SliceMath.Axis axis = SliceMath.SolveAxis(Min, Max, Head, Tail, 1.7f, 1.7f);

            Assert.AreEqual(Min, axis.Remap(Min), Tolerance);
            Assert.AreEqual(0.55f, axis.Remap(0.55f), Tolerance);
            Assert.AreEqual(Max, axis.Remap(Max), Tolerance);
        }

        [Test]
        public void Remap_IsIdentity_AtReferenceScale_WhenReferenceIsNotOne()
        {
            // Same claim, stated where it actually bites: an FBX node that imports at 0.71.
            SliceMath.Axis axis = SliceMath.SolveAxis(Min, Max, Head, Tail, 0.71f, 0.71f);

            for (float v = Min; v <= Max; v += 0.1f)
            {
                Assert.AreEqual(v, axis.Remap(v), Tolerance, $"Identity broken at {v}");
            }
        }

        [Test]
        public void RigidHead_KeepsItsRenderedSize_WhenStretched()
        {
            const float scale = 3f;
            const float reference = 1f;
            SliceMath.Axis axis = SliceMath.SolveAxis(Min, Max, Head, Tail, scale, reference);

            // Rendered thickness = mesh-local thickness * scale. The head must still measure `Head`
            // on screen after the transform multiplies it.
            float renderedHead = (axis.Remap(Min + Head) - axis.Remap(Min)) * scale;
            Assert.AreEqual(Head * reference, renderedHead, Tolerance);
        }

        [Test]
        public void RigidTail_KeepsItsRenderedSize_WhenStretched()
        {
            const float scale = 3f;
            const float reference = 1f;
            SliceMath.Axis axis = SliceMath.SolveAxis(Min, Max, Head, Tail, scale, reference);

            float renderedTail = (axis.Remap(Max) - axis.Remap(Max - Tail)) * scale;
            Assert.AreEqual(Tail * reference, renderedTail, Tolerance);
        }

        [Test]
        public void Middle_AbsorbsTheWholeDifference_WhenStretched()
        {
            const float scale = 3f;
            const float reference = 1f;
            SliceMath.Axis axis = SliceMath.SolveAxis(Min, Max, Head, Tail, scale, reference);

            float renderedMiddle = (axis.Remap(Max - Tail) - axis.Remap(Min + Head)) * scale;
            float expected = (Max - Min) * scale - (Head + Tail) * reference;
            Assert.AreEqual(expected, renderedMiddle, Tolerance);
        }

        [Test]
        public void Remap_PreservesOverallFootprint_SoSlicingNeverMovesTheEdges()
        {
            // The sliced object must occupy exactly the space plain scaling would. If this fails,
            // adding the component nudges the model relative to everything around it.
            SliceMath.Axis axis = SliceMath.SolveAxis(Min, Max, Head, Tail, 4.2f, 1f);

            Assert.AreEqual(Min, axis.Remap(Min), Tolerance);
            Assert.AreEqual(Max, axis.Remap(Max), Tolerance);
        }

        [Test]
        public void Remap_IsMonotonic_SoGeometryNeverFoldsThroughItself()
        {
            SliceMath.Axis axis = SliceMath.SolveAxis(Min, Max, Head, Tail, 2.5f, 1f);

            float previous = float.NegativeInfinity;
            for (float v = Min; v <= Max; v += 0.02f)
            {
                float mapped = axis.Remap(v);
                Assert.Greater(mapped, previous, $"Mapping went backwards at {v}");
                previous = mapped;
            }
        }

        [Test]
        public void ZeroMargins_BehaveExactlyLikePlainScaling()
        {
            // The escape hatch: an axis with no margins must be indistinguishable from not using
            // the component at all.
            SliceMath.Axis axis = SliceMath.SolveAxis(Min, Max, 0f, 0f, 3f, 1f);

            for (float v = Min; v <= Max; v += 0.1f)
            {
                Assert.AreEqual(v, axis.Remap(v), Tolerance, $"Zero-margin axis distorted at {v}");
            }
        }

        [Test]
        public void Margins_CompressProportionally_WhenTheObjectIsSmallerThanTheirSum()
        {
            // Squeezed to a third of reference, the caps cannot both fit. Sprites shrink them
            // rather than letting them overlap; so does this. Overlapping would fold the mesh.
            const float scale = 0.2f;
            SliceMath.Axis axis = SliceMath.SolveAxis(Min, Max, Head, Tail, scale, 1f);

            float renderedHead = (axis.Remap(Min + Head) - axis.Remap(Min)) * scale;
            float renderedTail = (axis.Remap(Max) - axis.Remap(Max - Tail)) * scale;
            float renderedTotal = (Max - Min) * scale;

            Assert.AreEqual(renderedTotal, renderedHead + renderedTail, Tolerance,
                "Compressed margins should exactly fill the object.");
            Assert.AreEqual(Head / Tail, renderedHead / renderedTail, Tolerance,
                "Compression should keep the head/tail ratio.");
        }

        [Test]
        public void RigidJacobian_EqualsReference_AndMiddleJacobianGrows_WhenStretched()
        {
            // Normals are divided by these, so a wrong Jacobian shades a correct mesh wrongly.
            SliceMath.Axis axis = SliceMath.SolveAxis(Min, Max, Head, Tail, 3f, 1f);

            Assert.AreEqual(1f / 3f, axis.RigidJacobian, Tolerance);
            Assert.Greater(axis.MiddleJacobian, axis.RigidJacobian);
        }

        [Test]
        public void JacobianAt_IsRigidOnTheEnds_AndMiddleInBetween()
        {
            SliceMath.Axis axis = SliceMath.SolveAxis(Min, Max, Head, Tail, 3f, 1f);

            Assert.AreEqual(axis.RigidJacobian, axis.JacobianAt(Min), Tolerance);
            Assert.AreEqual(axis.RigidJacobian, axis.JacobianAt(Max), Tolerance);
            Assert.AreEqual(axis.MiddleJacobian, axis.JacobianAt((Min + Max) * 0.5f), Tolerance);
        }

        [Test]
        public void NegativeScale_IsTreatedAsItsMagnitude_BecauseMirroringIsTheTransformsJob()
        {
            SliceMath.Axis positive = SliceMath.SolveAxis(Min, Max, Head, Tail, 3f, 1f);
            SliceMath.Axis negative = SliceMath.SolveAxis(Min, Max, Head, Tail, -3f, 1f);

            Assert.AreEqual(positive.Remap(0.7f), negative.Remap(0.7f), Tolerance);
        }

        [Test]
        public void ZeroScale_ProducesFiniteNumbers_InsteadOfNaN()
        {
            SliceMath.Axis axis = SliceMath.SolveAxis(Min, Max, Head, Tail, 0f, 1f);

            for (float v = Min; v <= Max; v += 0.2f)
            {
                Assert.IsFalse(float.IsNaN(axis.Remap(v)) || float.IsInfinity(axis.Remap(v)),
                    $"Non-finite output at {v}");
            }
        }

        [Test]
        public void FlatAxis_MapsToItself_InsteadOfDividingByZero()
        {
            SliceMath.Axis axis = SliceMath.SolveAxis(0.5f, 0.5f, 0.1f, 0.1f, 4f, 1f);

            Assert.AreEqual(0.5f, axis.Remap(0.5f), Tolerance);
        }

        [Test]
        public void MarginsCoveringTheWholeAxis_LeaveNoMiddle_AndStayFinite()
        {
            // Head + tail == length: legal (they meet), and there is no middle to stretch.
            SliceMath.Axis axis = SliceMath.SolveAxis(Min, Max, 0.6f, 0.6f, 3f, 1f);

            Assert.AreEqual(axis.RigidJacobian, axis.MiddleJacobian, Tolerance);
            Assert.IsFalse(float.IsNaN(axis.Remap(0.8f)));
        }

        [Test]
        public void RenderedDisplacement_IsExactlyZero_InTheRigidHead()
        {
            // This is what makes UV slicing leave the ends alone without any special case for them.
            SliceMath.Axis axis = SliceMath.SolveAxis(Min, Max, Head, Tail, 4.25f, 1.7f);

            Assert.AreEqual(0f, axis.RenderedDisplacement(Min), Tolerance);
            Assert.AreEqual(0f, axis.RenderedDisplacement(Min + Head * 0.5f), Tolerance);
            Assert.AreEqual(0f, axis.RenderedDisplacement(Min + Head), Tolerance);
        }

        [Test]
        public void RenderedDisplacement_IsConstantAcrossTheTail_AndEqualsTheMiddlesGrowth()
        {
            const float scale = 4.25f;
            const float reference = 1.7f;
            SliceMath.Axis axis = SliceMath.SolveAxis(Min, Max, Head, Tail, scale, reference);

            float growth = (Max - Min) * (scale - reference);

            Assert.AreEqual(growth, axis.RenderedDisplacement(Max - Tail), Tolerance);
            Assert.AreEqual(growth, axis.RenderedDisplacement(Max - Tail * 0.5f), Tolerance);
            Assert.AreEqual(growth, axis.RenderedDisplacement(Max), Tolerance);
        }

        [Test]
        public void RenderedDisplacement_IsZeroEverywhere_AtReferenceScale()
        {
            // Keeps "attaching the component changes nothing" true with UV slicing switched on.
            SliceMath.Axis axis = SliceMath.SolveAxis(Min, Max, Head, Tail, 1.7f, 1.7f);

            for (float v = Min; v <= Max; v += 0.05f)
            {
                Assert.AreEqual(0f, axis.RenderedDisplacement(v), Tolerance, $"Non-zero at {v}");
            }
        }

        [Test]
        public void RenderedDisplacement_GrowsMonotonically_AcrossTheMiddle()
        {
            SliceMath.Axis axis = SliceMath.SolveAxis(Min, Max, Head, Tail, 3f, 1f);

            float previous = -1f;
            for (float v = Min + Head; v <= Max - Tail; v += 0.02f)
            {
                float d = axis.RenderedDisplacement(v);
                Assert.GreaterOrEqual(d, previous, $"Displacement went backwards at {v}");
                previous = d;
            }
        }

        [Test]
        public void TryFitUvDensity_RecoversAKnownSlope_Exactly()
        {
            // Modelled on the real tiled text strip: V advances 12.255 per unit of Y.
            const float slope = 12.255f;
            var positions = new Vector3[24];
            var uvs = new Vector2[positions.Length];
            for (int i = 0; i < positions.Length; i++)
            {
                float y = i * 0.25f;
                positions[i] = new Vector3(0f, y, 0f);
                uvs[i] = new Vector2(0.5f, y * slope - 51.363f);
            }

            Assert.IsTrue(SliceMath.TryFitUvDensity(positions, uvs, 1, 1, out float fitted, out float r, out float residual));
            Assert.AreEqual(slope, fitted, 1e-3f);
            Assert.AreEqual(1f, Mathf.Abs(r), 1e-4f);
            Assert.AreEqual(0f, residual, 1e-4f);
        }

        [Test]
        public void TryFitUvDensity_ReportsAHighResidual_EvenWhenTheCorrelationLooksAcceptable()
        {
            // The case that made this method return two numbers instead of one. A real separator
            // model fits its best axis at r = 0.75 while a sixth of its vertices sit far off the
            // line — reading only the correlation would have called that model a good candidate.
            var positions = new Vector3[20];
            var uvs = new Vector2[positions.Length];
            for (int i = 0; i < positions.Length; i++)
            {
                float y = i * 0.1f;
                positions[i] = new Vector3(0f, y, 0f);
                // Every fifth vertex belongs to a cap whose UV does not follow Y at all.
                uvs[i] = new Vector2(0f, i % 5 == 0 ? 0.5f : y);
            }

            Assert.IsTrue(SliceMath.TryFitUvDensity(positions, uvs, 1, 1, out _, out float r, out float residual));
            Assert.Greater(Mathf.Abs(r), 0.6f, "Fixture should still correlate respectably.");
            Assert.Greater(residual, SlicedMesh.PoorFitResidual,
                "The residual is what has to catch this; the correlation does not.");
        }

        [Test]
        public void TryFitUvDensity_PicksTheTrackingChannel_Unambiguously()
        {
            // On real art this is not a close call: the tracking channel scores 1.0, the other 0.0.
            var positions = new Vector3[12];
            var uvs = new Vector2[positions.Length];
            for (int i = 0; i < positions.Length; i++)
            {
                positions[i] = new Vector3(0f, i * 0.5f, 0f);
                uvs[i] = new Vector2(0.25f, i * 0.5f * 3f);
            }

            SliceMath.TryFitUvDensity(positions, uvs, 1, 0, out _, out float rU, out _);
            Assert.IsTrue(SliceMath.TryFitUvDensity(positions, uvs, 1, 1, out _, out float rV, out _));
            Assert.Greater(Mathf.Abs(rV), Mathf.Abs(rU));
        }

        [Test]
        public void TryFitUvDensity_Fails_WhenThereIsNothingToMeasure()
        {
            var positions = new Vector3[8];
            var uvs = new Vector2[positions.Length];
            for (int i = 0; i < positions.Length; i++)
            {
                // Mesh spreads along Y but the UV never moves: no density exists.
                positions[i] = new Vector3(0f, i, 0f);
                uvs[i] = new Vector2(0.5f, 0.5f);
            }

            Assert.IsFalse(SliceMath.TryFitUvDensity(positions, uvs, 1, 1, out _, out _, out _));
        }

        [Test]
        public void TrySuggestMargins_FindsTheEmptyMiddle_OfARealSeparatorHistogram()
        {
            // Measured from Separator.fbx in Blender: detail at both ends, nothing in the middle.
            var bins = new[] { 6, 24, 12, 0, 0, 0, 0, 12, 24, 6 };

            bool found = SliceMath.TrySuggestMargins(bins, 1.0f, out float low, out float high);

            Assert.IsTrue(found);
            Assert.AreEqual(0.3f, low, Tolerance);
            Assert.AreEqual(0.3f, high, Tolerance);
        }

        [Test]
        public void TrySuggestMargins_ReportsFailure_WhenVerticesAreSpreadEvenly()
        {
            // No gap means no honest answer; the caller falls back rather than inventing a split.
            var bins = new[] { 5, 5, 5, 5, 5, 5, 5, 5, 5, 5 };

            Assert.IsFalse(SliceMath.TrySuggestMargins(bins, 1.0f, out _, out _));
        }

        [Test]
        public void TrySuggestMargins_PicksTheWidestGap_WhenThereAreSeveral()
        {
            var bins = new[] { 8, 0, 6, 0, 0, 0, 4, 0, 7, 3 };

            Assert.IsTrue(SliceMath.TrySuggestMargins(bins, 1.0f, out float low, out float high));
            Assert.AreEqual(0.3f, low, Tolerance);
            Assert.AreEqual(0.4f, high, Tolerance);
        }

        [Test]
        public void TrySuggestMargins_IgnoresGapsTouchingTheEnds()
        {
            // A leading run of empty bins is empty space before the geometry, not a middle.
            var bins = new[] { 0, 0, 0, 9, 9, 9, 9, 0, 0, 9 };

            Assert.IsTrue(SliceMath.TrySuggestMargins(bins, 1.0f, out float low, out float high));
            Assert.AreEqual(0.7f, low, Tolerance);
            Assert.AreEqual(0.1f, high, Tolerance);
        }

        [Test]
        public void SuggestedMargins_AreUsable_EndToEnd()
        {
            // The suggestion has to be a valid input to the mapping, not just a pair of numbers.
            var bins = new[] { 6, 24, 12, 0, 0, 0, 0, 12, 24, 6 };
            float length = Max - Min;

            Assert.IsTrue(SliceMath.TrySuggestMargins(bins, length, out float low, out float high));

            SliceMath.Axis axis = SliceMath.SolveAxis(Min, Max, low, high, 2.5f, 1f);
            Assert.AreEqual(Min, axis.Remap(Min), Tolerance);
            Assert.AreEqual(Max, axis.Remap(Max), Tolerance);
            Assert.Greater(axis.MiddleJacobian, axis.RigidJacobian);
        }
    }
}

# What is left to build

Nothing here is broken. As of v0.2.1 the package has no known defects — a full review was done and
everything it found is fixed, with a test each. This file is the backlog: what a future version could
add, what it would cost, and what evidence exists for each.

Two things this document is for, beyond a list:

- **Sizes are honest.** "Small" means a day or less; "large" means the mapping has to be rebuilt in a
  different coordinate space. Do not take an item because it sounds next in line.
- **The deliberately-unfixed section is load-bearing.** Two known behaviours look like bugs and are
  not. Read that section before "fixing" either of them, because one of them was already tried and
  reverted with a failing test to show why.

---

## Adoption gaps — the package works, but a newcomer cannot see it work

These are not features. They are the difference between someone installing the package and someone
using it. They became the top of the list the moment this package started being shared beyond the
project it was written in.

### A sample scene (`Samples~/`) — highest value here

A person who installs this today has to find a suitable model, add the component, and guess whether
what they are seeing is right. There is nothing to open.

For comparison, the commercial 27 Slicer ships six demo scenes and its documentation opens with
"explore the demos" before it explains anything. That is the shape to copy, at a smaller scale: one
scene with a stretchable prop, a slider or two, and the same object next to a plain-scaled twin so
the difference is visible at a glance rather than described.

Unity excludes any folder ending in `~` from compilation and import, and the Package Manager offers
its contents as an importable sample when `package.json` lists it under `"samples"`. So this costs a
scene, a model, and four lines of manifest — no code.

**Size:** small. **Blocked on:** picking a model that can ship (the ones used to develop this belong
to a game project).

### `CHANGELOG.md`

Four tags exist and there is no way to see what changed between them except by reading commit
messages. Unity's Package Manager renders this file in its own UI, so it is the natural place for
"what did I get when I bumped the tag".

Follow *Keep a Changelog* headings — Unity's package validator expects that shape, and it costs
nothing to match it.

**Size:** small, and it gets easier the sooner it starts.

### `package.json` metadata

Missing `documentationUrl` (the Package Manager's "Documentation" link comes from this field, and
without it the README is invisible from the UI), an author email, and a `unity` release qualifier.

**Size:** trivial.

---

## Features

Ordered by what they actually buy. The reasoning behind this order, and how the commercial 27 Slicer
solves each, is in [NOTES-27slicer.md](NOTES-27slicer.md).

### 1. Per-material UV conditions — small, closes a real correctness gap

Today UV slicing is one decision per axis for the whole mesh, so a model with several materials is
all-or-nothing. 27 Slicer decides per material against explicit conditions (material count equals
submesh count, main texture wraps Repeat or Mirror, submesh topology is Triangles), and their example
is exactly the case that matters: a wooden door tiles its wood while the knob and hinges stay put.

Most of the machinery is already here — the wrap-mode check, the density fit, the per-axis modes. The
work is moving the decision from "the mesh" to "each submesh's material" and slicing the UV array in
those ranges only.

**Do this when a real multi-material model needs it.** Building it speculatively would be guessing at
which conditions matter.

### 2. Export the sliced mesh as a project asset — small

`FinalizeSlicing()` already bakes the slicing into a scene object. The remaining step is writing that
mesh out as a `.asset` so it can be referenced from prefabs and shared between scenes, which is what
turns "this scene is cheap now" into "this prop is cheap everywhere".

27 Slicer's exporter builds output paths from a token pattern (`{meshAssetFileName}_{meshName}_…`);
worth copying, because naming twenty exported meshes by hand is how the feature stops being used.

### 3. Per-island UV fit — medium

The density fit is one straight line per axis across the whole mesh, so faces whose UVs do not follow
that axis — caps, mostly — get moved along with everything else. That is why the inspector reports a
worst-residual and calls some models poor candidates rather than slicing them badly.

Fitting per UV island would let those models use the feature instead of being warned off. Measured on
a real sculpted prop: 16 of 139 vertices sat up to 45% of the UV range off the fitted line while the
correlation still read a respectable 0.75. Those 16 are the islands this would separate.

### 4. Hierarchy slicing — large, and everything else depends on it

One component slicing several child meshes as one combined shape, with children repositioned to
match. This is the biggest capability gap against a mature slicer, and it is the foundation the rest
of their feature list stands on (composite slicing, position/scale modifiers for children, nested
slicers, an ignore marker to prune a branch).

The whole of it rests on one pair of functions: push a descendant's vertices into the controller's
space, slice there, push them back. 27 Slicer's `MatrixUtility.BuildTransformMatrix` /
`BuildInverseTransformMatrix` is exactly that, and it is the difference between their scope and ours.

**Size:** large — the mapping currently assumes one object's local space throughout. Do not start
this without a concrete need; it is bigger than everything above it combined.

### 5. Skinned meshes and `MeshCollider`

`MeshCollider` is the smaller half and has a known shape: 27 Slicer slices the collider mesh, and
where a `MeshCollider` shares its mesh with a `MeshSlicerComponent` it slices once and hands the same
result to both. Their `UnsupportedColliderDetails` — a named "I know this type and cannot do it" —
is worth copying as a pattern regardless.

Skinned meshes are a different problem (bone weights and bind poses would have to survive the remap)
and there is no demand for it here.

---

## Deliberately not fixed — read before "fixing" either

### One stranded mesh per sliced object per domain reload

After a script recompile the component builds a fresh mesh and the previous one is left for Unity to
reclaim when the scene closes.

**This was fixed once and reverted.** The cleanup looked obvious — on rebuild, destroy a mesh on the
filter that carries `DontSave` and is not ours — and it broke duplication: a copied object (Ctrl+D)
inherits a reference to the *original's* generated mesh, so "orphaned by a reload" and "still being
rendered by a sibling" are indistinguishable from inside the component. The copy destroyed the
original's mesh. `Duplicating_GivesTheCopyItsOwnMesh_AndTheRealSource` is the test that caught it.

A real fix needs ownership the component can prove across a domain reload — a serialized owner id, or
a registry rebuilt on load. That is more machinery than the leak deserves.

### The Unity 2021.3 floor is declared, not tested

`package.json` says `2021.3`; the package has only ever run on Unity 6000.3. Nothing in it is version
sensitive — but that is a belief, not a measurement, and the README says so plainly.

If someone does test it lower, note it in the README rather than quietly trusting the number. If it
turns out to break, raising the floor is the honest fix, not patching around it. (One trap already
avoided: `float.IsFinite` is a .NET Standard 2.1 API and would not compile against the 2.0 profile
that Unity 2021.3 ships — the code uses `IsNaN`/`IsInfinity` for exactly this reason.)

---

## Picking something up

The package is small enough to read end to end in one sitting — about 1,200 lines of runtime and
editor code plus 57 tests. Two things worth knowing before editing:

- **`Runtime/` must stay free of `UnityEditor`** except behind `#if UNITY_EDITOR`, and free of APIs
  newer than the declared Unity floor. It compiles into player builds; `Editor/` and `Tests/` do not.
- **It is a git package.** Consumers resolve it read-only into `Library/PackageCache/`, so edits go
  through commit → tag → push → bump the tag in the consuming project's manifest. The README's
  *Working on the package itself* section has the local-override shortcut for iterating.

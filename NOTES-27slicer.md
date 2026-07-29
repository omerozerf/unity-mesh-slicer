# What 27 Slicer does, and what it means for this package

Notes from reading the full 27 Slicer manual (24 pages), its DocFX API map (569 entries) and its
shipped file listing. 27 Slicer is the $30 store asset this package was written as an alternative to;
it has been maintained since 2021 and its documentation is unusually candid about its own limits, so
it is worth treating as a map of the problem rather than as a competitor.

Read this before adding a feature here — several of the obvious ideas have a known shape, and two of
its documented behaviours confirm things we worked out the hard way.

## Their architecture

```
SlicerController          the coordinator. Holds Size + Slices, scans descendants,
  ├── SlicerComponent     …hands each descendant GameObject to the components:
  │     ├── MeshSlicerComponent        slices MeshFilter meshes
  │     └── ColliderSlicerComponent    slices Box and Mesh colliders
  ├── SliceModifier       …and runs modifiers found on the way down:
  │     ├── PositionSliceModifier          moves a child as if it were a vertex
  │     ├── ScaleSliceModifier             scales a child
  │     └── SlicerControllerSliceModifier  drives a *nested* controller's Size
  └── SlicerIgnore        prunes a whole branch from the scan
```

`Core` is a separate assembly holding the pure math: `SliceUtility`, `UvUtility`, `MeshUtility`,
`BoundsUtility`, `MatrixUtility`, `HashUtility`, `TempCollections`. Same split we have between
`SliceMath` and `SlicedMesh`, one layer bigger.

**The primitive is `SliceUtility.SliceVector`.** Vertices, box-collider corners, child positions and
child scales all go through that one function — which is why "slice a collider" and "slice a child's
position" cost them almost nothing once vertices worked. Our `SliceMath.Axis.Remap` is the same
primitive at scalar granularity; anything we add that needs the mapping should route through it
rather than re-deriving.

**`MatrixUtility.BuildTransformMatrix` / `BuildInverseTransformMatrix` is what makes hierarchies
work.** A descendant's vertices are pushed into the controller's space, sliced there, and pushed
back. That single pair of functions is the difference between their composite slicing and our
single-object scope.

## Where they are ahead of us

Ranked by what it would actually buy this project.

1. **Per-material UV control.** Their UV scaling is decided *per material*, with stated conditions:
   material count must equal submesh count, the main texture's wrap must be Repeat **or Mirror**, U
   and V wrap must match, and the submesh topology must be Triangles. A material failing a condition
   simply is not UV-sliced. Their example is the right one: a wooden door tiles its wood while the
   knob and hinges do not. **Ours is per-axis and global**, so a multi-material model is all-or-
   nothing. This is the most valuable gap and the cheapest of the big ones.

2. **Composite slicing over a hierarchy.** One controller, many descendant meshes, bounds combined
   and sliced as one — their wall-and-window demo. Ours is one component per mesh, so a model built
   from parts has to be sliced part by part and the parts do not know about each other. This is the
   largest genuine capability gap.

3. **Collider slicing beyond BoxCollider.** They handle MeshCollider too, and when a MeshCollider
   shares its mesh with a MeshSlicerComponent they slice once and share the result
   (`MeshSlicerManagedMeshColliderDetails`). They also ship an explicit `UnsupportedColliderDetails`
   — a named "we know this type and cannot do it" rather than silence. Worth copying the idea, not
   just the feature.

4. **Finalization and export.** `FinalizeSlicing()` destroys the slicing components and leaves the
   sliced meshes; the exporter window writes them out as real `.asset` meshes with a token-based path
   pattern, optionally marking them non-readable and calling `Mesh.Optimize()`. For a recording
   branch this is the "bake it and stop paying for it" path.

5. **Slice modifiers for children.** Their table demo keeps a mug pinned to the corner and a
   tablecloth scaled to the top as the table resizes. Needs the matrix work from (2) first.

6. **`SlicerIgnore`.** A one-line component that prunes a branch. Trivial to add once we have
   hierarchy scanning; meaningless before that.

7. **Project-level configuration asset** and **hotkeys** (Alt+S edit mode, Alt+R refresh, Alt+/ swap
   scale into size, each with a Ctrl+Alt all-objects variant).

## Where we differ, deliberately

- **We read `transform.localScale`; they use their own `Size` field** and expect the transform to
  stay at 1. They shipped a "Swap Scale With Size" utility in v1.1 to convert existing objects, which
  says something about how artists actually work. Our approach needs no conversion step — but it also
  means anything else animating the transform drives the slicer, which their design isolates.
- **Their `Slices` is normalised 0–1 from the centre; our margins are absolute distances from the
  bounds.** Theirs transfers between models of different size, ours is direct to read off a model.
- **Auto-detect margins from a vertex histogram appears nowhere in their docs.** Neither does a
  measured UV density with a quality report. Those are ours, and they exist because we chose to read
  the model instead of asking the artist to dial numbers.
- **Identity at reference scale** is a guarantee we state and test. They have no equivalent because
  `Size` defaults to 1 and the question does not arise the same way.

## Things they document that we found independently — worth knowing we agree

- **"A mesh slicer does not create any new mesh vertices, it simply moves the vertices around. On
  meshes with very few vertices you may find that the triangles don't quite sit right… try adding a
  loop cut just on the inside of where the slice will be."** This is exactly the 4-vertex plane
  result: with vertices only at the bounds, slicing degenerates to plain scaling. Same architecture,
  same limitation, and their fix is the modelling fix, not a code fix. Belongs in our README as
  advice rather than as an apology.
- **Read/Write Enabled is required for slicing in a player build.** Matches our note. The Editor can
  read any mesh, which is why this branch never hits it.
- **`MeshUtility.ResetUV` exists in their Core.** They need the same "put the original UVs back when
  the feature is switched off" that we shipped a fix for — it is not an edge case we invented.
- **Duplicating a GameObject broke sliced meshes twice** (v1.3.1 shared mesh, v1.4.1 disappearing
  mesh). We are immune by construction and now have a test pinning it.
- **`Mesh.MarkDynamic()`** — they added it in v1.2.0; we took it from their changelog.

## One line that is about this project specifically

Their optimisation page lists **"Video recording — if the whole project is dedicated to non-real time
applications such as video recording, this option can be useful to ensure all slices are kept up to
date"** as the reason to disable their skip-unmodified optimisation. That is precisely what this
marketing branch is for, and our `LateUpdate` dirty check already behaves that way: it re-checks
every frame and rebuilds whenever anything it tracks moved. No configuration needed.

The corollary is their warning, which applies to us too: their optimisation misses changes to
properties it does not hash. Our dirty check compares scale, margins, reference scale, UV settings,
density and the source mesh. **Anything added later that affects the output must be added to
`NeedsRebuild` in the same change**, or it will silently not take effect until something else moves.

## Also documented, and true for us

- **Static GameObjects are incompatible.** Batching a mesh that is about to be rewritten cannot work.
  We have never said so; we should.
- **No shader modifications**, so any shader works. Same here.
- **Prefabs, nested prefabs, prefab variants, LOD groups** are all supported by them. We have tested
  none of these. If a slicer ever goes into a prefab here, test the variant case first — and note our
  README already warns against applying a sliced instance back to its prefab.

## Recommendation

If this package gets more work, the order that pays: **per-material UV conditions** (small, closes a
real correctness gap for multi-material models), then **export/finalize** (small, and it is the
honest answer to "do not pay for slicing at runtime"), then **hierarchy + matrix space** (large, and
everything in their feature list from composite slicing to modifiers depends on it).

# Loop Mesh Slicer

9-slice stretching for 3D meshes. Scale an object the way you always do; the detailed ends keep
their size and the plain middle absorbs the change.

## Install

Add one line to the consuming project's `Packages/manifest.json`:

```json
"com.loop.meshslicer": "file:../../../../MyPackages/MeshSlicer"
```

The `../` depth depends on where the project sits. From
`UnityProjects/LoopProjects/<game>/<clone>/Packages/` it is four levels up, as above.

To get the package's tests into the Test Runner, also add:

```json
"testables": [ "com.loop.meshslicer" ]
```

Unity only lists tests from packages **outside** the `Packages` folder when they are named in
`testables` — without it the package works fine but its tests are invisible.

## Use

1. Select the object whose mesh should stretch.
2. **Add Component → Loop → Sliced Mesh**.
3. Nothing changes on screen — that is intentional. The component adopts the current scale as the
   reference and guesses the margins from the model.
4. Scale the object. The ends hold; the middle stretches.

If the guess is wrong, set **Margin Min / Margin Max** by hand — they are thicknesses in the mesh's
own units, one per axis, and the blue planes in the scene view show where the cuts land. **Auto-Detect
Margins** re-runs the guess. `0` on an axis means that axis scales normally.

### If the model looks wrong the moment you add the component

Press **Set Reference = Current Scale**. Reference scale is the scale at which the model is
considered authored-size, and it is captured on attach — but if the object was already stretched
before you added the component, that stretched state is what got captured.

## How it works

Each axis is split into a rigid head, a stretchable middle and a rigid tail. Vertices are remapped
through that piecewise function and **divided by the scale the transform is about to apply**, so the
transform's own multiply lands the ends on their authored size. The object occupies exactly the space
plain scaling would — only the interior distribution differs.

Normals and tangents are corrected with the mapping's derivative, so authored (toon / custom split)
normals survive. `RecalculateNormals` is never called.

**The source asset is never modified.** The generated mesh is marked `HideAndDontSave`, so it cannot
be written into a scene or a prefab; disabling or removing the component restores the imported mesh.

## Auto-detect

The margin guess bins the vertices along each axis and finds the widest empty interior span — the
plain middle a sliceable model has by construction. Axes with no clear gap fall back to 25% at each
end.

## UV slicing — keeping the texture at its authored size

Slicing holds the *shape* of the ends but the middle's texture still stretches with the geometry. Set
an axis's **UV** mode to **Preserve Density** and it stops: the texture keeps its authored size and
repeats to fill the extra length instead.

Press **Auto-Fit UV Density** first. It measures, per axis, which UV coordinate follows that axis and
how fast, straight off the model — you never type a density. It reports two numbers, and both matter:

- **fit** (correlation) picks U or V. On a model that suits this it is not a close call: the tracking
  coordinate scores 1.00 and the other 0.00.
- **off-line** (worst residual) is the go/no-go. It is how far the worst vertex sits from the measured
  line, as a share of the UV range. **Read this one, not the fit.** A real sculpted model measured a
  respectable 0.75 fit while a sixth of its vertices sat up to 45% off the line — those are faces
  whose UVs do not follow the axis at all (caps, mostly), and their texture would slide rather than
  tile. Anything above ~10% is flagged as poor, and the number is printed so you can judge it.

**Requirements.** The texture's wrap mode must be **Repeat** — density preservation works by letting
the UV run past its authored range, which on a Clamp texture smears the edge pixel and on an atlas
samples whatever is next door. The inspector warns when it finds a bound texture that is not Repeat,
but it cannot detect an atlas; that one is on you.

Off by default, per axis. An axis left on **Stretch** produces byte-identical UVs, and at reference
scale even an axis set to Preserve Density does — attaching the component still changes nothing.

Only the first UV channel is touched. `uv2` is the lightmap channel and is left alone.

## Limitations

- **A model needs vertices near the slice, or there is nothing to hold.** Slicing moves the vertices a
  model already has; it never adds any. On a mesh whose only vertices sit at its outer bounds — a
  plain quad, say — every vertex lands in a rigid zone and the result is identical to plain scaling.
  The fix is in the model: add a loop cut just inside where the slice should fall. (The commercial
  27 Slicer documents the same limitation and the same remedy — this is the shape of the technique,
  not a shortcoming of this implementation.)
- **Skinned meshes and `MeshCollider` are not supported.** A `BoxCollider` on the same object is kept
  in sync.
- **Static GameObjects are incompatible.** A mesh that is rewritten at runtime cannot also be batched
  as static geometry.
- **One material's worth of UV control.** UV slicing is set per axis for the whole mesh, so a model
  with several materials is all-or-nothing. See NOTES-27slicer.md — per-material conditions are the
  first thing worth adding.
- **Texture in the middle stretches** unless you turn on UV slicing (above), and even then only on
  models whose unwrap follows the axis. The inspector prints the middle's stretch factor.
- **Player builds need Read/Write Enabled** on the model. The Editor can read any mesh, so editor-only
  projects need nothing; a mesh sliced in a build must have `isReadable` on in its import settings.
- **`localScale` is read every frame** and the mesh is rebuilt when it changes. Intended for
  hand-placed scenery. Driving the scale from a tween rebuilds every frame — cheap for small props,
  worth measuring on a heavy mesh.
- **Do not "Apply All" a sliced object to a prefab.** The generated mesh cannot be saved, so the
  prefab's mesh field would come out empty. Slice the instance, not the asset.

Duplicating a sliced object (Ctrl+D) is safe — the copy builds its own mesh and keeps pointing at the
imported asset. There is a test for it, because it is the failure this kind of tool is prone to.

## Not built yet

Ordered by what they would actually buy; the reasoning, and how the commercial 27 Slicer solves each,
is in **NOTES-27slicer.md**.

- **Per-material UV conditions.** Decide UV slicing per material rather than per mesh, so a door can
  tile its wood while the knob and hinges stay put. Small, and it closes a real correctness gap.
- **Export the sliced mesh as an asset.** Bakes one size into a normal mesh, so a finished prop needs
  no component and no per-frame check at all.
- **Hierarchy slicing.** One component slicing several child meshes as one combined shape, with
  children repositioned to match. Large — it needs the whole mapping moved into matrix space — but
  everything else on a mature slicer's feature list depends on it.
- **Per-island UV slicing.** Today the density fit is one line per axis for the whole mesh, so faces
  that do not follow the axis get moved along with the rest. Fitting per UV island would let a model
  with caps use the feature instead of being told it is a poor candidate.
- **Mirror wrap mode** counts as tileable too; the warning currently only accepts Repeat.
- **Child transforms.** Slicing only reshapes this object's mesh. Children keep their local positions,
  so a model built from several parts needs each part sliced, or the parts repositioned by hand.
- **Skinned meshes and MeshCollider.**

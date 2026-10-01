# Loop Mesh Slicer

9-slice stretching for 3D meshes. Scale an object the way you always do; the detailed ends keep
their size and the plain middle absorbs the change.

## Install

Add one line to the consuming project's `Packages/manifest.json`:

```json
"com.loop.meshslicer": "https://github.com/omerozerf/unity-mesh-slicer.git#v0.2.0"
```

Pin a tag rather than tracking `main`, so a project never changes underneath you.

The repository is **private**, so Unity can only resolve that URL on a machine with GitHub
credentials that have access to it. If the Package Manager reports that it cannot fetch the package,
that is almost always what it means — `git clone https://github.com/omerozerf/unity-mesh-slicer.git`
in a terminal is the one-command test, and if it prompts or fails, so will Unity. Ask the repo owner
for access rather than working around it.

Requires Unity **2021.3 or newer**. Developed and tested on Unity 6000.3; nothing in it is version
sensitive, but that is what "tested" honestly covers.

To get the package's tests into the Test Runner, also add:

```json
"testables": [ "com.loop.meshslicer" ]
```

Unity only lists tests from packages **outside** the `Packages` folder when they are named in
`testables` — without it the package works fine but its tests are invisible.

## Working on the package itself

A git package resolves into `Library/PackageCache/` and is **read-only** there. To change it, edit
this repo, then release:

```bash
git commit -am "..." && git tag -a v0.2.0 -m "..." && git push --follow-tags
```

…and bump the tag in the consuming project's manifest.

While iterating, point that manifest at a local checkout instead — `"file:../relative/path"` — so
edits appear immediately. **Do not commit that line**: a `file:` path only resolves on a machine that
has the folder at exactly that depth.

## Vendoring as an embedded package

For a project whose people cannot reach this private repo, copy the package into the project instead
of pointing the manifest at git:

```bash
rsync -a --exclude='.git' --exclude='.DS_Store' --exclude='.gitignore' <this repo>/ <project>/Packages/com.loop.meshslicer/
```

Re-running the same command is how to update it. Make the change here first, commit and tag, then
copy: the vendored folder is a copy, and the next rsync overwrites anything edited there.

- **No manifest entry and no `testables` entry.** Unity picks up anything under `Packages/` as an
  embedded package, and an embedded package's tests reach the Test Runner without `testables`.
- **Name the folder `com.loop.meshslicer`.** Unity wants an embedded package's directory to match its
  package name. Under any other name every reload logs *"Embedded package [com.loop.meshslicer] was
  found in the directory […]. Please consider changing the directory name to match the package
  name."* to `Editor.log`, not to the Console.
- **Nothing records which version was copied.** `packages-lock.json` lists the package with
  `"source": "embedded"` and no hash, so the `version` field in `package.json` is the only marker —
  compare it on both sides after a sync.
- The package has no project of its own, so run its tests from the consuming project after each sync.

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

**The source asset is never modified.** The generated mesh is marked `HideAndDontSave`, so its data
cannot be written into a scene or a prefab; disabling or removing the component restores the imported
mesh, and a `BoxCollider` on the same object is restored with it.

Saving a scene would otherwise write the MeshFilter's *reference* to that unsaveable mesh as empty, so
the package swaps the real mesh back in for the duration of the save and re-slices immediately after.
You will not notice it happen; the point is that a sliced object produces no diff in the scene file
and no override on a prefab instance.

That swap is tracked **per scene**, which matters if you edit several scenes at once. Unity batches the
save callbacks — measured on 6000.3, two scenes come through as `saving:A → saving:B → saved:A →
saved:B`, not one scene at a time — so a single shared list would lose the first scene's objects when
the second one starts saving, leaving them un-sliced in the editor until something else touched them.
Fixed in 0.2.3, with tests that drive a real two-scene save.

## Using it in a build

Nothing here is editor-only — the component works at runtime — but two things are worth deciding
before you ship a scene that uses it.

**Read/Write Enabled.** Slicing reads the model's vertices. In the Editor that always works; in a
player, a model without *Read/Write Enabled* has had its mesh data freed and the component will log
one error naming the model and disable itself. Either enable that setting (it costs memory), or bake
the slicing at edit time — next paragraph.

**Bake it if the size never changes at runtime.** The component checks for changes every frame, for
every sliced object. If a prop is sliced once to a fixed size and then never resized, call
`FinalizeSlicing()`: it keeps the sliced mesh, drops the don't-save flag so the mesh is a normal
saveable object, and removes the component. After that the object is an ordinary MeshFilter with no
per-frame cost and no Read/Write requirement.

```csharp
var slicer = GetComponent<SlicedMesh>();
transform.localScale = finalSize;
slicer.Rebuild();
slicer.FinalizeSlicing();   // component removes itself; the sliced mesh stays
```

It is a manual call on purpose — a component that finalized itself would quietly break anyone who
meant to keep resizing at runtime.

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
  line, as a share of the UV range. **Read this one, not the fit.** Measured on a real sculpted prop:
  a respectable 0.75 fit while a sixth of its vertices sat up to 45% off the line — those are faces
  whose UVs do not follow the axis at all (caps, mostly), and their texture would slide rather than
  tile. Anything above ~10% is flagged as poor, and the number is printed so you can judge it.

**Requirements.** The material's main texture must wrap **Repeat** or **Mirror** — density
preservation works by letting the UV run past its authored range, which on a Clamp texture smears the
edge pixel and on an atlas samples whatever is next door. The inspector warns about the main texture's
wrap mode; it cannot detect an atlas, so that one is on you. (Only the main texture is checked —
ramps, masks and matcaps are routinely Clamp by design and are not sampled by the mesh's UVs, so
flagging them would make the warning permanent and therefore useless.)

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
  in sync — its authored centre and size are remapped, not replaced, and restored when the component
  goes away.
- **Static GameObjects are incompatible.** A mesh that is rewritten at runtime cannot also be batched
  as static geometry. (Bake with `FinalizeSlicing()` and the object can be static again.)
- **Only triangle submeshes are sliced.** A quad, line or point submesh is copied through unchanged
  and logs a warning naming it, rather than silently disappearing.
- **Only this object's own `localScale` drives the slicing.** A parent's scale still stretches the
  result the ordinary way, because that is the transform doing its job after the mesh is handed over.
- **One material's worth of UV control.** UV slicing is set per axis for the whole mesh, so a model
  with several materials is all-or-nothing. See NOTES-27slicer.md — per-material conditions are the
  first thing worth adding.
- **Texture in the middle stretches** unless you turn on UV slicing (above), and even then only on
  models whose unwrap follows the axis. The inspector prints the middle's stretch factor.
- **Player builds need Read/Write Enabled**, or `FinalizeSlicing()` at edit time. See *Using it in a
  build* above. (Note the Editor reads mesh data regardless of that import setting, so this never
  shows up until someone builds.)
- **`localScale` is read every frame** and the mesh is rebuilt when it changes. Intended for
  hand-placed scenery. Driving the scale from a tween rebuilds every frame — cheap for small props,
  worth measuring on a heavy mesh.
- **One stranded mesh per sliced object per domain reload.** After a script recompile the component
  builds a fresh mesh and the previous one is left for Unity to reclaim when the scene closes. It is
  not cleaned up on purpose: a leftover mesh is indistinguishable from the mesh a *duplicated* object
  is still rendering, and destroying that would break the sibling. There is a test for the duplicate
  case; this is the price of it.
- **Do not "Apply All" a sliced object to a prefab.** The generated mesh cannot be saved, so the
  prefab's mesh field would come out empty. Slice the instance, not the asset.

Duplicating a sliced object (Ctrl+D) is safe — the copy builds its own mesh and keeps pointing at the
imported asset. There is a test for it, because it is the failure this kind of tool is prone to.

## Not built yet

The backlog lives in **[ROADMAP.md](ROADMAP.md)** — what a future version could add, what each would
cost, and which two known behaviours are deliberate rather than defects. In short: a sample scene and
a changelog are the most useful things missing; per-material UV control is the first real feature;
hierarchy slicing is the large one everything else would build on.

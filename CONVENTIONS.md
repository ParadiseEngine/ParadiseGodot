# Export Contract Conventions (pinned)

These are the conventions the Godot export tools **must** reproduce so their output stays
byte-comparable with the original Unity tools (`ParadiseUnityEditor`). Pinned in Phase 1 against
the real Unity baseline `data/scenes/SampleScene.json`, enforced by `ParadiseExport.Core.Tests`.

## Handedness — the contract is Unity left-handed

The export contract stores transforms in **Unity's convention: Y-up, left-handed** (+X right,
+Y up, **+Z forward**). The Unity tools wrote transform values verbatim, with **no** handedness
conversion. (Note: `MIGRATION.md` originally described the contract as "right-handed" — that was
aspirational; the actual on-disk baseline is left-handed.)

Godot is Y-up, **right-handed** (+X right, +Y up, **−Z** forward). Because the contract is fixed
and the runtime already consumes Unity-convention data, the Godot exporter converts Godot's
right-handed values to the contract's left-handed values at export time, via a **Z-axis mirror**
(`CoordinateConversion`):

| Quantity | Conversion |
|---|---|
| position / direction `(x, y, z)` | `(x, y, −z)` |
| rotation quaternion `(x, y, z, w)` | `(−x, −y, z, w)` |
| transform matrix `M` | `S · M · S`, `S = diag(1, 1, −1)` |

Validation: Godot's default camera authored at `(0, 1, 10)` looking toward −Z maps to the
contract's `(0, 1, −10)`, matching the baseline.

## Color — linear, packed 8-bit

Colors are emitted as `{ "r", "g", "b", "a" }` objects with **linear** float channels packed to
8 bits per channel (`Color32`). The Unity exporter linearized sRGB authoring colors before
packing. Godot color-space parity is finalized with material/lighting fidelity in Phase 3.

## Matrices — column-major

`Matrix4x4` is serialized as a flat `float[16]` in **column-major** order. (Translation from
`Matrix4x4.CreateTranslation(x,y,z)` lands at flat indices 3, 7, 11.)

## Float formatting — emulate Mono's `"R"`

The Unity tools ran on **Mono**, whose `float.ToString("R")` formats with 7 significant digits and
falls back to 9 only when 7 doesn't round-trip. Modern .NET's `"R"` instead emits the *shortest*
round-trippable string (often 8 digits), e.g. Mono `0.766044438` vs modern `0.76604444`. To keep
output byte-identical, `ExportJsonWriter.FormatFloat` reproduces Mono's **G7-then-G9** behavior.

This applies only to vector/quaternion/matrix/color floats (the custom converter). Scalar float
properties go through Newtonsoft's default formatting (e.g. whole numbers render as `5.0`).

## Enums & nulls

Enums serialize **by name** (`StringEnumConverter`). Null properties are **included** in the JSON
(`NullValueHandling.Include`).

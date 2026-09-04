"""Convert a committed v5 level document to v6, in place.

v6 refuses v5 by design and the Godot exporter that would re-export these was deleted with the
contract change, so the committed sample scenes are converted rather than regenerated.

The conversion is faithful because v5 was FLATTENED: every entity carried a baked WORLD matrix and
no parent link, so local == world and no hierarchy is being invented. What changes:

  NameComponentData      -> the format's `meta`      (Guid + Name; guid minted, stable per name)
  TransformComponentData -> the format's `transform` (local TRS, decomposed from the world matrix)
  SchemaVersion 5        -> 6
  Type "Paradise.Export.Data.X" -> "Paradise.Sample.Runtime.X"   (ids are unchanged)
"""
import json, math, sys, uuid

META_ID = "0f1d4b3a-8c27-4a55-9b6e-2f7c1d40a913"
TRANSFORM_ID = "7e55c210-3d41-4b8a-8f26-9c0a5e71b4d2"
NAME_ID = "5bb6c0eb-1a3b-4a2e-9a25-4b0f4d4bb9e0"        # resolved below from the document
TRANSFORM_OLD_ID = None

# Deterministic ids: a re-run must not churn the documents.
NAMESPACE = uuid.UUID("6f1e4c2a-9d38-4b17-8c05-2a7e3f9b1d64")


def decompose(flat):
    """Flat contract float[16] -> (translation, quaternion xyzw, scale).

    Matrix4x4Converter.Read builds a column-vector matrix that consumers TRANSPOSE to get the
    System.Numerics model matrix; composing those two steps means the flat array is already the
    numerics matrix in row-major order.
    """
    rows = [flat[0:3], flat[4:7], flat[8:11]]
    translation = flat[12:15]

    scale = [math.sqrt(sum(c * c for c in r)) for r in rows]
    # A negative determinant is a mirrored basis; the convention is to carry it on X.
    det = (
        rows[0][0] * (rows[1][1] * rows[2][2] - rows[1][2] * rows[2][1])
        - rows[0][1] * (rows[1][0] * rows[2][2] - rows[1][2] * rows[2][0])
        + rows[0][2] * (rows[1][0] * rows[2][1] - rows[1][1] * rows[2][0])
    )
    if det < 0:
        scale[0] = -scale[0]

    r = [[rows[i][j] / scale[i] if scale[i] else (1.0 if i == j else 0.0) for j in range(3)] for i in range(3)]

    # Shepperd's method: pick the largest diagonal term so the divisor is never near zero.
    trace = r[0][0] + r[1][1] + r[2][2]
    if trace > 0:
        s = math.sqrt(trace + 1.0) * 2
        w, x, y, z = 0.25 * s, (r[1][2] - r[2][1]) / s, (r[2][0] - r[0][2]) / s, (r[0][1] - r[1][0]) / s
    elif r[0][0] > r[1][1] and r[0][0] > r[2][2]:
        s = math.sqrt(1.0 + r[0][0] - r[1][1] - r[2][2]) * 2
        w, x, y, z = (r[1][2] - r[2][1]) / s, 0.25 * s, (r[1][0] + r[0][1]) / s, (r[2][0] + r[0][2]) / s
    elif r[1][1] > r[2][2]:
        s = math.sqrt(1.0 + r[1][1] - r[0][0] - r[2][2]) * 2
        w, x, y, z = (r[2][0] - r[0][2]) / s, (r[1][0] + r[0][1]) / s, 0.25 * s, (r[2][1] + r[1][2]) / s
    else:
        s = math.sqrt(1.0 + r[2][2] - r[0][0] - r[1][1]) * 2
        w, x, y, z = (r[0][1] - r[1][0]) / s, (r[2][0] + r[0][2]) / s, (r[2][1] + r[1][2]) / s, 0.25 * s

    return translation, [x, y, z, w], scale


def convert(path):
    doc = json.load(open(path))
    if doc.get("SchemaVersion") != 5:
        print(f"  {path}: SchemaVersion {doc.get('SchemaVersion')} — skipped")
        return

    name_ids, transform_ids = set(), set()
    for entity in doc["Entities"]:
        for c in entity:
            if c.get("Type", "").endswith("NameComponentData"):
                name_ids.add(c["Id"])
            if c.get("Type", "").endswith("TransformComponentData"):
                transform_ids.add(c["Id"])

    converted = []
    for index, entity in enumerate(doc["Entities"]):
        name, trs, rest = None, None, []
        for c in entity:
            if c["Id"] in name_ids:
                name = c["Data"].get("Value")
            elif c["Id"] in transform_ids:
                trs = decompose(c["Data"]["World"])
            else:
                c["Type"] = c.get("Type", "").replace("Paradise.Export.Data.", "Paradise.Sample.Runtime.")
                rest.append(c)

        label = name if name is not None else f"entity-{index}"
        meta = {"Guid": str(uuid.uuid5(NAMESPACE, f"{path}#{index}#{label}"))}
        if name is not None:
            meta["Name"] = name
        out = [{"Id": META_ID, "Type": "meta", "Data": meta}]
        if trs is not None:
            translation, rotation, scale = trs
            out.append({
                "Id": TRANSFORM_ID,
                "Type": "transform",
                "Data": {"Position": translation, "Rotation": rotation, "Scale": scale},
            })
        converted.append(out + rest)

    doc["SchemaVersion"] = 6
    doc["Entities"] = converted
    with open(path, "w") as f:
        json.dump(doc, f, indent=2)
        f.write("\n")
    print(f"  {path}: {len(converted)} entities -> v6")


for path in sys.argv[1:]:
    convert(path)

#nullable enable
using System;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using ParadiseExport.Core.Data;

namespace ParadiseExport.Core.Serialization.Converters
{
    // Hand-written (AOT-safe) converters for the contract's structural shapes: System.Numerics
    // vectors/quaternions/matrices as flat float arrays (matrices column-major), and Color32 as an
    // { r, g, b, a } object. Serialize-only — the export tools never deserialize these.

    public sealed class Color32Converter : JsonConverter<Color32>
    {
        public override Color32 Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o) =>
            throw new NotSupportedException("Color32 is serialize-only.");

        public override void Write(Utf8JsonWriter writer, Color32 value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("r", value.R);
            writer.WriteNumber("g", value.G);
            writer.WriteNumber("b", value.B);
            writer.WriteNumber("a", value.A);
            writer.WriteEndObject();
        }
    }

    public sealed class Vector2Converter : JsonConverter<Vector2>
    {
        public override Vector2 Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, Vector2 v, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(v.X);
            writer.WriteNumberValue(v.Y);
            writer.WriteEndArray();
        }
    }

    public sealed class Vector3Converter : JsonConverter<Vector3>
    {
        public override Vector3 Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, Vector3 v, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(v.X);
            writer.WriteNumberValue(v.Y);
            writer.WriteNumberValue(v.Z);
            writer.WriteEndArray();
        }
    }

    public sealed class Vector4Converter : JsonConverter<Vector4>
    {
        public override Vector4 Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, Vector4 v, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(v.X);
            writer.WriteNumberValue(v.Y);
            writer.WriteNumberValue(v.Z);
            writer.WriteNumberValue(v.W);
            writer.WriteEndArray();
        }
    }

    public sealed class QuaternionConverter : JsonConverter<Quaternion>
    {
        public override Quaternion Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, Quaternion q, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(q.X);
            writer.WriteNumberValue(q.Y);
            writer.WriteNumberValue(q.Z);
            writer.WriteNumberValue(q.W);
            writer.WriteEndArray();
        }
    }

    public sealed class Matrix4x4Converter : JsonConverter<Matrix4x4>
    {
        public override Matrix4x4 Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o) =>
            throw new NotSupportedException();

        // Column-major flat float[16], matching the original Newtonsoft converter order.
        public override void Write(Utf8JsonWriter writer, Matrix4x4 m, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(m.M11); writer.WriteNumberValue(m.M21); writer.WriteNumberValue(m.M31); writer.WriteNumberValue(m.M41);
            writer.WriteNumberValue(m.M12); writer.WriteNumberValue(m.M22); writer.WriteNumberValue(m.M32); writer.WriteNumberValue(m.M42);
            writer.WriteNumberValue(m.M13); writer.WriteNumberValue(m.M23); writer.WriteNumberValue(m.M33); writer.WriteNumberValue(m.M43);
            writer.WriteNumberValue(m.M14); writer.WriteNumberValue(m.M24); writer.WriteNumberValue(m.M34); writer.WriteNumberValue(m.M44);
            writer.WriteEndArray();
        }
    }
}

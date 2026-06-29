#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;
using LevelColor32 = ParadiseExport.Core.Data.Color32;

namespace ParadiseExport.Core.Serialization
{
    /// <summary>
    /// General-purpose JSON serializer for exported documents (scenes, materials, prefabs,
    /// project settings). Serializes plain DTOs with Newtonsoft using the C# property names
    /// as JSON keys, string enum names, and a custom converter that emits System.Numerics
    /// vectors/quaternions/matrices as float arrays (matrices column-major) and Color32 as
    /// an { r, g, b, a } object. Writes are atomic (temp file + rename).
    ///
    /// Ported verbatim from ParadiseUnityEditor (Editor/Export/ExportJsonWriter.cs); this is
    /// the format seam of the fixed export contract.
    /// </summary>
    public static class ExportJsonWriter
    {
        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            ContractResolver = new DefaultContractResolver(),
            Converters =
            {
                new StringEnumConverter(),
                new SystemNumericsJsonConverter(),
            },
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Include,
        };

        public static void WriteJsonDocument(string outputPath, object document)
        {
            string json = JsonConvert.SerializeObject(document, JsonSettings) + Environment.NewLine;
            WriteTextAtomically(outputPath, json);
        }

        public static string SerializeToString(object document) =>
            JsonConvert.SerializeObject(document, JsonSettings);

        public static void AppendJsonString(StringBuilder json, string name, string value, bool trailingComma)
        {
            json.Append("    \"");
            json.Append(EscapeJson(name));
            json.Append("\": \"");
            json.Append(EscapeJson(value));
            json.Append('"');
            if (trailingComma)
            {
                json.Append(',');
            }

            json.AppendLine();
        }

        // The Unity tools ran on Mono, whose float.ToString("R") uses the classic .NET Framework
        // algorithm: format with 7 significant digits and, only if that does not round-trip, fall
        // back to 9. Modern .NET's "R" instead emits the *shortest* round-trippable string (often
        // 8 digits), which would diverge from the committed Unity baselines (e.g. Mono
        // "0.766044438" vs modern "0.76604444"). Reproduce Mono's G7-then-G9 behavior so the export
        // contract stays byte-identical across both toolchains. (Verified by the SampleScene golden test.)
        public static string FormatFloat(float value)
        {
            string g7 = value.ToString("G7", CultureInfo.InvariantCulture);
            if (float.TryParse(g7, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) &&
                parsed == value)
            {
                return g7;
            }

            return value.ToString("G9", CultureInfo.InvariantCulture);
        }

        public static void WriteTextAtomically(string outputPath, string text)
        {
            string directory = Path.GetDirectoryName(outputPath) ?? ".";
            Directory.CreateDirectory(directory);
            string tempPath = Path.Combine(directory, $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                File.WriteAllText(tempPath, text);
                if (File.Exists(outputPath))
                {
                    File.Replace(tempPath, outputPath, null);
                }
                else
                {
                    File.Move(tempPath, outputPath);
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }

        private static string EscapeJson(string value) =>
            value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\b", "\\b")
                .Replace("\f", "\\f")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");

        private sealed class SystemNumericsJsonConverter : JsonConverter
        {
            public override bool CanRead => false;

            public override bool CanConvert(Type objectType)
            {
                Type type = Nullable.GetUnderlyingType(objectType) ?? objectType;
                return type == typeof(System.Numerics.Vector2) ||
                       type == typeof(System.Numerics.Vector3) ||
                       type == typeof(System.Numerics.Vector4) ||
                       type == typeof(System.Numerics.Quaternion) ||
                       type == typeof(System.Numerics.Matrix4x4) ||
                       type == typeof(LevelColor32);
            }

            public override object ReadJson(
                JsonReader reader,
                Type objectType,
                object? existingValue,
                JsonSerializer serializer) =>
                throw new NotSupportedException();

            public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
            {
                if (value == null)
                {
                    writer.WriteNull();
                    return;
                }

                if (value is LevelColor32 color)
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("r");
                    WriteFloat(writer, color.R);
                    writer.WritePropertyName("g");
                    WriteFloat(writer, color.G);
                    writer.WritePropertyName("b");
                    WriteFloat(writer, color.B);
                    writer.WritePropertyName("a");
                    WriteFloat(writer, color.A);
                    writer.WriteEndObject();
                    return;
                }

                writer.WriteStartArray();
                switch (value)
                {
                    case System.Numerics.Vector2 vector:
                        WriteFloat(writer, vector.X);
                        WriteFloat(writer, vector.Y);
                        break;
                    case System.Numerics.Vector3 vector:
                        WriteFloat(writer, vector.X);
                        WriteFloat(writer, vector.Y);
                        WriteFloat(writer, vector.Z);
                        break;
                    case System.Numerics.Vector4 vector:
                        WriteFloat(writer, vector.X);
                        WriteFloat(writer, vector.Y);
                        WriteFloat(writer, vector.Z);
                        WriteFloat(writer, vector.W);
                        break;
                    case System.Numerics.Quaternion quaternion:
                        WriteFloat(writer, quaternion.X);
                        WriteFloat(writer, quaternion.Y);
                        WriteFloat(writer, quaternion.Z);
                        WriteFloat(writer, quaternion.W);
                        break;
                    case System.Numerics.Matrix4x4 matrix:
                        WriteFloat(writer, matrix.M11);
                        WriteFloat(writer, matrix.M21);
                        WriteFloat(writer, matrix.M31);
                        WriteFloat(writer, matrix.M41);
                        WriteFloat(writer, matrix.M12);
                        WriteFloat(writer, matrix.M22);
                        WriteFloat(writer, matrix.M32);
                        WriteFloat(writer, matrix.M42);
                        WriteFloat(writer, matrix.M13);
                        WriteFloat(writer, matrix.M23);
                        WriteFloat(writer, matrix.M33);
                        WriteFloat(writer, matrix.M43);
                        WriteFloat(writer, matrix.M14);
                        WriteFloat(writer, matrix.M24);
                        WriteFloat(writer, matrix.M34);
                        WriteFloat(writer, matrix.M44);
                        break;
                }

                writer.WriteEndArray();
            }

            private static void WriteFloat(JsonWriter writer, float value) =>
                writer.WriteRawValue(FormatFloat(value));
        }
    }
}

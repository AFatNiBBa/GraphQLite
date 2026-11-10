using System.Buffers;
using System.Text;
using System.Text.Json;

namespace GraphQLite;

/// <summary>JSON conversion of values (maps ⇄ objects, lists ⇄ arrays). Reflection-free, AOT friendly.</summary>
internal static class Json
{
    public static string Serialize(in Value v)
    {
        var buf = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buf))
            Write(w, v);
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    private static void Write(Utf8JsonWriter w, in Value v)
    {
        switch (v.Kind)
        {
            case ValueKind.Null: w.WriteNullValue(); break;
            case ValueKind.Boolean: w.WriteBooleanValue(v.Num != 0); break;
            case ValueKind.Integer: w.WriteNumberValue(v.Num); break;
            case ValueKind.Float:
                {
                    double d = v.FloatBits;
                    if (double.IsFinite(d)) w.WriteNumberValue(d);
                    else w.WriteStringValue(Value.FormatFloat(d));
                    break;
                }
            case ValueKind.String: w.WriteStringValue(v.Str); break;
            case ValueKind.List:
                w.WriteStartArray();
                foreach (var x in v.ListItems) Write(w, x);
                w.WriteEndArray();
                break;
            case ValueKind.Map:
                w.WriteStartObject();
                foreach (var kv in v.MapItems)
                {
                    w.WritePropertyName(kv.Key);
                    Write(w, kv.Value);
                }
                w.WriteEndObject();
                break;
            case ValueKind.Node when v.Obj is Node n:
                w.WriteStartObject();
                w.WriteNumber("id", n.Id);
                w.WriteStartArray("labels");
                foreach (var l in n.Labels) w.WriteStringValue(l);
                w.WriteEndArray();
                w.WritePropertyName("properties");
                Write(w, Value.FromMap(n.Properties.ToDictionary(k => k.Key, k => k.Value)));
                w.WriteEndObject();
                break;
            case ValueKind.Relationship when v.Obj is Relationship r:
                w.WriteStartObject();
                w.WriteNumber("id", r.Id);
                w.WriteString("type", r.Type);
                w.WriteNumber("start", r.StartNodeId);
                w.WriteNumber("end", r.EndNodeId);
                w.WritePropertyName("properties");
                Write(w, Value.FromMap(r.Properties.ToDictionary(k => k.Key, k => k.Value)));
                w.WriteEndObject();
                break;
            case ValueKind.Node:
            case ValueKind.Relationship:
                w.WriteNumberValue(v.Num);
                break;
            case ValueKind.Point:
                {
                    var p = v.AsPoint();
                    w.WriteStartObject();
                    w.WriteNumber("srid", p.Srid);
                    w.WriteNumber("x", p.X);
                    w.WriteNumber("y", p.Y);
                    if (p.Z.HasValue) w.WriteNumber("z", p.Z.Value);
                    w.WriteEndObject();
                    break;
                }
            default:
                w.WriteStringValue(v.Kind == ValueKind.Path ? v.ToString() : Temporal.Format(v));
                break;
        }
    }

    public static Value Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return FromElement(doc.RootElement);
        }
        catch (JsonException ex)
        {
            throw new CypherRuntimeException("Invalid JSON: " + ex.Message);
        }
    }

    public static Value FromElement(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return Value.Null;
            case JsonValueKind.True: return Value.True;
            case JsonValueKind.False: return Value.False;
            case JsonValueKind.String: return Value.FromString(e.GetString());
            case JsonValueKind.Number:
                return e.TryGetInt64(out var l) ? Value.FromInt(l) : Value.FromFloat(e.GetDouble());
            case JsonValueKind.Array:
                {
                    var arr = new Value[e.GetArrayLength()];
                    int i = 0;
                    foreach (var x in e.EnumerateArray()) arr[i++] = FromElement(x);
                    return Value.FromList(arr);
                }
            default:
                {
                    var d = new Dictionary<string, Value>();
                    foreach (var p in e.EnumerateObject()) d[p.Name] = FromElement(p.Value);
                    return Value.FromMap(d);
                }
        }
    }
}

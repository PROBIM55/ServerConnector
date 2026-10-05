namespace Platform.Contracts.Geometry;

// Vec3 stays as struct + JSON-array shape on the wire; mirror of TS
// `readonly [number, number, number]`. JsonConverter at the bottom of
// this file handles that mapping — without it System.Text.Json would
// emit { "x": …, "y": …, "z": … } and break round-trip with TS.

[System.Text.Json.Serialization.JsonConverter(typeof(Vec3JsonConverter))]
public readonly record struct Vec3(double X, double Y, double Z)
{
    public static Vec3 Zero => new(0, 0, 0);
    public static Vec3 UnitX => new(1, 0, 0);
    public static Vec3 UnitY => new(0, 1, 0);
    public static Vec3 UnitZ => new(0, 0, 1);
}

[System.Text.Json.Serialization.JsonConverter(typeof(Vec2JsonConverter))]
public readonly record struct Vec2(double X, double Y);

public sealed record Transform(Vec3 Position, Vec3 Rotation, Vec3 Scale)
{
    public static Transform Identity { get; } = new(Vec3.Zero, Vec3.Zero, new Vec3(1, 1, 1));
}

internal sealed class Vec3JsonConverter : System.Text.Json.Serialization.JsonConverter<Vec3>
{
    public override Vec3 Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        if (reader.TokenType != System.Text.Json.JsonTokenType.StartArray)
            throw new System.Text.Json.JsonException("Vec3 expected JSON array of 3 numbers");
        reader.Read(); var x = reader.GetDouble();
        reader.Read(); var y = reader.GetDouble();
        reader.Read(); var z = reader.GetDouble();
        reader.Read();
        if (reader.TokenType != System.Text.Json.JsonTokenType.EndArray)
            throw new System.Text.Json.JsonException("Vec3 expected end of array after 3 numbers");
        return new Vec3(x, y, z);
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, Vec3 value, System.Text.Json.JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.X);
        writer.WriteNumberValue(value.Y);
        writer.WriteNumberValue(value.Z);
        writer.WriteEndArray();
    }
}

internal sealed class Vec2JsonConverter : System.Text.Json.Serialization.JsonConverter<Vec2>
{
    public override Vec2 Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        if (reader.TokenType != System.Text.Json.JsonTokenType.StartArray)
            throw new System.Text.Json.JsonException("Vec2 expected JSON array of 2 numbers");
        reader.Read(); var x = reader.GetDouble();
        reader.Read(); var y = reader.GetDouble();
        reader.Read();
        if (reader.TokenType != System.Text.Json.JsonTokenType.EndArray)
            throw new System.Text.Json.JsonException("Vec2 expected end of array after 2 numbers");
        return new Vec2(x, y);
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, Vec2 value, System.Text.Json.JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.X);
        writer.WriteNumberValue(value.Y);
        writer.WriteEndArray();
    }
}

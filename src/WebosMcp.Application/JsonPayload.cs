using System.Text.Json;

namespace WebosMcp.Application;

internal static class JsonPayload
{
    public static string? String(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty(name, out var value))
            {
                switch (value.ValueKind)
                {
                    case JsonValueKind.String:
                        return value.GetString();
                    case JsonValueKind.Number:
                        return value.ToString();
                    case JsonValueKind.True:
                    case JsonValueKind.False:
                        return value.GetBoolean() ? "true" : "false";
                }
            }
        }

        return null;
    }

    public static int? Int(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty(name, out var value))
            {
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                {
                    return number;
                }

                if (value.ValueKind == JsonValueKind.String &&
                    int.TryParse(value.GetString(), out var parsed))
                {
                    return parsed;
                }
            }
        }

        return null;
    }

    public static bool? Bool(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty(name, out var value))
            {
                if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    return value.GetBoolean();
                }

                if (value.ValueKind == JsonValueKind.String &&
                    bool.TryParse(value.GetString(), out var parsed))
                {
                    return parsed;
                }
            }
        }

        return null;
    }

    public static JsonElement? Object(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty(name, out var value) &&
                value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                return value;
            }
        }

        return null;
    }

    public static IEnumerable<JsonElement> Array(JsonElement element, params string[] names)
    {
        var found = Object(element, names);
        if (found is { ValueKind: JsonValueKind.Array } array)
        {
            foreach (var item in array.EnumerateArray())
            {
                yield return item;
            }
        }
    }
}

using System.Globalization;
using System.Text.Json;
using UnityEngine;

namespace BepInExMCP.IL2CPP;

internal static class ValueConverter
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    internal static IReadOnlyList<JsonElement> ParseArguments(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<JsonElement>();
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("Method arguments must be a JSON array.", nameof(json));
        }

        return document.RootElement
            .EnumerateArray()
            .Select(element => element.Clone())
            .ToArray();
    }

    internal static object? ConvertJson(JsonElement element, Type targetType)
    {
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            if (IsNullable(targetType))
            {
                return null;
            }

            throw new InvalidCastException(
                $"Null cannot be assigned to non-nullable type '{targetType.FullName}'.");
        }

        if (targetType == typeof(object))
        {
            return ConvertUntyped(element);
        }

        if (targetType == typeof(string))
        {
            return element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : element.GetRawText();
        }

        if (element.ValueKind == JsonValueKind.Array && TryConvertVector(element, targetType, out var vector))
        {
            return vector;
        }

        var text = element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : element.GetRawText();
        return ConvertString(text, targetType);
    }

    internal static object? ConvertString(string value, Type targetType)
    {
        ArgumentNullException.ThrowIfNull(targetType);

        var nullableType = Nullable.GetUnderlyingType(targetType);
        if (nullableType is not null)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                value.Equals("null", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            targetType = nullableType;
        }

        if (targetType == typeof(string))
        {
            return value;
        }

        if (targetType == typeof(char))
        {
            if (value.Length != 1)
            {
                throw new FormatException("A char value must contain exactly one character.");
            }

            return value[0];
        }

        if (targetType.IsEnum)
        {
            return Enum.Parse(targetType, value, ignoreCase: true);
        }

        if (targetType == typeof(Guid))
        {
            return Guid.Parse(value);
        }

        if (TryConvertVector(value, targetType, out var vector))
        {
            return vector;
        }

        if (targetType == typeof(bool))
        {
            if (bool.TryParse(value, out var boolean))
            {
                return boolean;
            }

            if (value == "0")
            {
                return false;
            }

            if (value == "1")
            {
                return true;
            }
        }

        if (typeof(IConvertible).IsAssignableFrom(targetType))
        {
            return Convert.ChangeType(value, targetType, Invariant);
        }

        throw new NotSupportedException(
            $"Conversion from text to '{targetType.FullName}' is not supported.");
    }

    internal static string SafeToString(object? value)
    {
        if (value is null)
        {
            return "null";
        }

        try
        {
            return Convert.ToString(value, Invariant) ?? "null";
        }
        catch
        {
            return $"<{value.GetType().FullName}>";
        }
    }

    private static bool IsNullable(Type type) =>
        !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;

    private static object? ConvertUntyped(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertUntyped).ToArray(),
            _ => element.GetRawText()
        };

    private static bool TryConvertVector(string value, Type targetType, out object? result)
    {
        if (!IsSupportedVectorType(targetType))
        {
            result = null;
            return false;
        }

        var values = value
            .Trim()
            .Trim('(', ')', '[', ']')
            .Split(
                new[] { ',', ';', ' ' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => float.Parse(part, Invariant))
            .ToArray();

        return TryCreateVector(values, targetType, out result);
    }

    private static bool TryConvertVector(JsonElement element, Type targetType, out object? result)
    {
        if (!IsSupportedVectorType(targetType))
        {
            result = null;
            return false;
        }

        var values = element
            .EnumerateArray()
            .Select(item => item.GetSingle())
            .ToArray();
        return TryCreateVector(values, targetType, out result);
    }

    private static bool TryCreateVector(
        IReadOnlyList<float> values,
        Type targetType,
        out object? result)
    {
        result = null;

        if (targetType == typeof(Vector2) && values.Count == 2)
        {
            result = new Vector2(values[0], values[1]);
            return true;
        }

        if (targetType == typeof(Vector3) && values.Count == 3)
        {
            result = new Vector3(values[0], values[1], values[2]);
            return true;
        }

        if (targetType == typeof(Vector4) && values.Count == 4)
        {
            result = new Vector4(values[0], values[1], values[2], values[3]);
            return true;
        }

        if (targetType == typeof(Quaternion) && values.Count == 4)
        {
            result = new Quaternion(values[0], values[1], values[2], values[3]);
            return true;
        }

        if (targetType == typeof(Color) && values.Count is 3 or 4)
        {
            result = new Color(
                values[0],
                values[1],
                values[2],
                values.Count == 4 ? values[3] : 1f);
            return true;
        }

        return false;
    }

    private static bool IsSupportedVectorType(Type type) =>
        type == typeof(Vector2) ||
        type == typeof(Vector3) ||
        type == typeof(Vector4) ||
        type == typeof(Quaternion) ||
        type == typeof(Color);
}

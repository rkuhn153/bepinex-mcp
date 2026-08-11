using System.Collections.Concurrent;

namespace BepInExMCP.IL2CPP;

/// <summary>
/// Shared registration-ID allocator so watchers and patches cannot collide.
/// </summary>
internal sealed class RegistrationIdStore
{
    private readonly ConcurrentDictionary<string, byte> ids =
        new(StringComparer.Ordinal);

    internal string Allocate(string? requestedId, string prefix)
    {
        var id = string.IsNullOrWhiteSpace(requestedId)
            ? $"{prefix}-{Guid.NewGuid():N}"
            : requestedId.Trim();

        if (id.Length > 128 ||
            id.Any(character =>
                !(char.IsLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            throw new ArgumentException(
                "Registration IDs may contain only letters, digits, '.', '_' and '-' " +
                "and must be at most 128 characters.");
        }

        if (!ids.TryAdd(id, 0))
        {
            throw new InvalidOperationException($"Registration ID '{id}' already exists.");
        }

        return id;
    }

    internal void Release(string id) => ids.TryRemove(id, out _);

    internal void Clear() => ids.Clear();
}

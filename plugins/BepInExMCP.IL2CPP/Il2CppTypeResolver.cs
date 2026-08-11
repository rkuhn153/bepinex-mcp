using System.Collections.Concurrent;
using System.Reflection;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace BepInExMCP.IL2CPP;

internal sealed class Il2CppTypeResolver : IDisposable
{
    private static readonly MethodInfo CastMethod = typeof(Il2CppObjectBase)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .Single(method =>
            method.Name == nameof(Il2CppObjectBase.Cast) &&
            method.IsGenericMethodDefinition &&
            method.GetParameters().Length == 0);

    private static readonly IReadOnlyDictionary<string, Type> Aliases =
        new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
        {
            ["bool"] = typeof(bool),
            ["byte"] = typeof(byte),
            ["sbyte"] = typeof(sbyte),
            ["char"] = typeof(char),
            ["decimal"] = typeof(decimal),
            ["double"] = typeof(double),
            ["float"] = typeof(float),
            ["int"] = typeof(int),
            ["uint"] = typeof(uint),
            ["long"] = typeof(long),
            ["ulong"] = typeof(ulong),
            ["short"] = typeof(short),
            ["ushort"] = typeof(ushort),
            ["string"] = typeof(string),
            ["object"] = typeof(object),
            ["void"] = typeof(void)
        };

    private readonly ConcurrentDictionary<string, Type> fullNames =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Type, byte>> simpleNames =
        new(StringComparer.Ordinal);
    private readonly ManualLogSource log;

    internal Il2CppTypeResolver(ManualLogSource log)
    {
        this.log = log;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            IndexAssembly(assembly);
        }

        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
    }

    public void Dispose()
    {
        AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
    }

    internal Type ResolveRequired(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            throw new ArgumentException("A type name is required.", nameof(typeName));
        }

        typeName = typeName.Trim();

        if (Aliases.TryGetValue(typeName, out var alias))
        {
            return alias;
        }

        var direct = Type.GetType(typeName, throwOnError: false, ignoreCase: false);
        if (direct is not null)
        {
            return direct;
        }

        if (fullNames.TryGetValue(typeName, out var exact))
        {
            return exact;
        }

        if (simpleNames.TryGetValue(typeName, out var candidates))
        {
            var matches = candidates.Keys.ToArray();
            if (matches.Length == 1)
            {
                return matches[0];
            }

            if (matches.Length > 1)
            {
                var descriptions = string.Join(
                    ", ",
                    matches.Select(type => type.AssemblyQualifiedName ?? type.FullName ?? type.Name));
                throw new AmbiguousMatchException(
                    $"Type name '{typeName}' is ambiguous. Use a full name. Matches: {descriptions}");
            }
        }

        throw new TypeLoadException($"Type '{typeName}' was not found in loaded interop assemblies.");
    }

    internal Type GetActualType(Il2CppObjectBase value)
    {
        ArgumentNullException.ThrowIfNull(value);

        try
        {
            var il2CppType = Il2CppType.TypeFromPointer(value.ObjectClass);
            var fullName = il2CppType.FullName;

            if (!string.IsNullOrWhiteSpace(fullName))
            {
                if (fullNames.TryGetValue(fullName, out var resolved))
                {
                    return resolved;
                }

                if (fullName.StartsWith("System.", StringComparison.Ordinal))
                {
                    var redirectedName = "Il2Cpp" + fullName;
                    if (fullNames.TryGetValue(redirectedName, out resolved))
                    {
                        return resolved;
                    }
                }
            }
        }
        catch (Exception exception)
        {
            log.LogDebug($"Could not resolve an IL2CPP runtime type: {exception.Message}");
        }

        return value.GetType();
    }

    internal object Cast(Il2CppObjectBase value, Type targetType)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(targetType);

        if (targetType.IsInstanceOfType(value))
        {
            return value;
        }

        if (!typeof(Il2CppObjectBase).IsAssignableFrom(targetType))
        {
            throw new InvalidCastException(
                $"Type '{targetType.FullName}' is not an Il2CppInterop reference type.");
        }

        try
        {
            return CastMethod.MakeGenericMethod(targetType).Invoke(value, null)
                   ?? throw new InvalidCastException(
                       $"Could not cast IL2CPP object to '{targetType.FullName}'.");
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw new InvalidCastException(
                $"Could not cast IL2CPP object to '{targetType.FullName}': " +
                exception.InnerException.Message,
                exception.InnerException);
        }
    }

    internal string GetActualTypeName(Il2CppObjectBase value)
    {
        var type = GetActualType(value);
        return type.FullName ?? type.Name;
    }

    internal IReadOnlyList<Type> Search(string query, int offset, int limit)
    {
        query ??= string.Empty;
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 500);

        return fullNames.Values
            .Distinct()
            .Where(type =>
                string.IsNullOrEmpty(query) ||
                (type.FullName ?? type.Name).Contains(
                    query,
                    StringComparison.OrdinalIgnoreCase))
            .OrderBy(type => type.FullName ?? type.Name, StringComparer.Ordinal)
            .Skip(offset)
            .Take(limit)
            .ToArray();
    }

    private void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
    {
        IndexAssembly(args.LoadedAssembly);
    }

    private void IndexAssembly(Assembly assembly)
    {
        foreach (var type in GetLoadableTypes(assembly))
        {
            if (!string.IsNullOrEmpty(type.FullName))
            {
                fullNames.TryAdd(type.FullName, type);
            }

            var matches = simpleNames.GetOrAdd(
                type.Name,
                _ => new ConcurrentDictionary<Type, byte>());
            matches.TryAdd(type, 0);
        }
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(type => type is not null)!;
        }
        catch
        {
            return Array.Empty<Type>();
        }
    }
}

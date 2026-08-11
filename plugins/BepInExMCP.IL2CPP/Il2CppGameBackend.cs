using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BepInExMCP.IL2CPP;

internal sealed class Il2CppGameBackend
{
    private const BindingFlags InstanceMembers =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags AllMethods =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private readonly Il2CppTypeResolver typeResolver;
    private readonly Func<int?> getSelfTestObjectId;

    internal Il2CppGameBackend(
        Il2CppTypeResolver typeResolver,
        Func<int?> getSelfTestObjectId)
    {
        this.typeResolver = typeResolver;
        this.getSelfTestObjectId = getSelfTestObjectId;
    }

    internal CapabilitiesResponse GetCapabilities()
    {
        var bepinexAssembly = typeof(BasePlugin).Assembly;
        var bepinexVersion = bepinexAssembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? bepinexAssembly.GetName().Version?.ToString()
            ?? "unknown";

        return new CapabilitiesResponse(
            Protocol.Version,
            "il2cpp",
            Application.unityVersion,
            bepinexVersion,
            RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            new[]
            {
                "system/capabilities",
                "system/ping",
                "system/paths",
                "scene/list_root_gameobjects",
                "scene/search_gameobjects",
                "scene/hierarchy_snapshot",
                "scene/resolve_selector",
                "gameobject/list_children",
                "gameobject/inspect_components",
                "component/get_details",
                "component/get_member",
                "component/set_value",
                "component/call_method",
                "component/list_methods",
                "type/search",
                "type/describe",
                "scene:find_objects_with_component",
                "network/diagnostics",
                "watch/member",
                "watch/scene",
                "watch/list",
                "watch/remove",
                "mod:subscribe_to_method",
                "mod:patch_method",
                "mod:list_patches",
                "mod:remove_patch",
                "batch",
                "events/webhook"
            },
            new[] { "prefix", "postfix" },
            new[]
            {
                "Members removed by IL2CPP stripping cannot be inspected or invoked.",
                "Obfuscated names must be addressed by their generated interop names.",
                "Harmony transpilers and finalizers are not exposed by this IL2CPP bridge."
            },
            getSelfTestObjectId(),
            new[]
            {
                "stableSelectors",
                "hierarchySnapshots",
                "targetedMemberReads",
                "typeDiscovery",
                "batchPost",
                "memberWatchers",
                "sceneWatchers",
                "patchLifecycle",
                "networkDiagnostics"
            },
            new Dictionary<string, int>
            {
                ["maxBatchOperations"] = Protocol.MaxBatchOperations,
                ["maxRequestBodyBytes"] = Protocol.MaxRequestBodyBytes,
                ["maxWatchers"] = Protocol.MaxWatchers,
                ["maxPatchRegistrations"] = Protocol.MaxPatchRegistrations,
                ["maxSnapshotNodes"] = Protocol.MaxSnapshotNodes
            });
    }

    internal IReadOnlyList<SimpleGameObject> ListRootGameObjects()
    {
        var roots = SceneManager.GetActiveScene().GetRootGameObjects();
        var result = new List<SimpleGameObject>(roots.Length);

        foreach (var gameObject in roots)
        {
            if (gameObject is not null)
            {
                result.Add(ToSimpleObject(gameObject));
            }
        }

        return result;
    }

    internal IReadOnlyList<SimpleGameObject> ListChildren(int parentId)
    {
        var parent = FindObjectById(parentId);
        var transform = parent.transform;
        var result = new List<SimpleGameObject>(transform.childCount);

        for (var index = 0; index < transform.childCount; index++)
        {
            var child = transform.GetChild(index);
            if (child is not null && child.gameObject is not null)
            {
                result.Add(ToSimpleObject(child.gameObject));
            }
        }

        return result;
    }

    internal IReadOnlyList<string> InspectComponents(int gameObjectId)
    {
        var gameObject = FindObjectById(gameObjectId);
        var result = new List<string>();

        foreach (var component in gameObject.GetComponents<Component>())
        {
            if (component is not null)
            {
                result.Add(typeResolver.GetActualTypeName(component));
            }
        }

        return result;
    }

    internal IReadOnlyDictionary<string, ComponentMemberInfo> GetComponentDetails(
        int gameObjectId,
        string componentName)
    {
        var component = ResolveComponent(gameObjectId, componentName);
        var details = new SortedDictionary<string, ComponentMemberInfo>(StringComparer.Ordinal);

        foreach (var field in component.Type.GetFields(InstanceMembers))
        {
            try
            {
                var value = field.GetValue(component.Instance);
                details[field.Name] = new ComponentMemberInfo(
                    FormatType(field.FieldType),
                    ValueConverter.SafeToString(value));
            }
            catch
            {
                // Some generated or stripped field accessors cannot be read safely.
            }
        }

        foreach (var property in component.Type.GetProperties(InstanceMembers))
        {
            if (!property.CanRead || property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            try
            {
                var value = property.GetValue(component.Instance);
                details[property.Name] = new ComponentMemberInfo(
                    FormatType(property.PropertyType),
                    ValueConverter.SafeToString(value));
            }
            catch
            {
                // Properties can contain game logic or represent stripped IL2CPP fields.
            }
        }

        return details;
    }

    internal StatusResponse SetComponentValue(
        int gameObjectId,
        string componentName,
        string memberName,
        string value)
    {
        var component = ResolveComponent(gameObjectId, componentName);

        var field = component.Type.GetField(
            memberName,
            InstanceMembers | BindingFlags.IgnoreCase);
        if (field is not null)
        {
            var converted = ValueConverter.ConvertString(value, field.FieldType);
            field.SetValue(component.Instance, converted);
            return new StatusResponse(
                "ok",
                $"Set field '{field.Name}' on '{FormatType(component.Type)}'.");
        }

        var property = component.Type.GetProperty(
            memberName,
            InstanceMembers | BindingFlags.IgnoreCase);
        if (property is null)
        {
            throw new MissingMemberException(
                component.Type.FullName,
                memberName);
        }

        if (!property.CanWrite || property.GetIndexParameters().Length != 0)
        {
            throw new InvalidOperationException($"Property '{property.Name}' is read-only.");
        }

        var propertyValue = ValueConverter.ConvertString(value, property.PropertyType);
        property.SetValue(component.Instance, propertyValue);
        return new StatusResponse(
            "ok",
            $"Set property '{property.Name}' on '{FormatType(component.Type)}'.");
    }

    internal StatusResponse CallComponentMethod(
        int gameObjectId,
        string componentName,
        string methodName,
        string argumentsJson)
    {
        var component = ResolveComponent(gameObjectId, componentName);
        var arguments = ValueConverter.ParseArguments(argumentsJson);
        var methodAndArguments = SelectInvocableMethod(
            component.Type,
            methodName,
            arguments);

        object? returnValue;
        try
        {
            returnValue = methodAndArguments.Method.Invoke(
                component.Instance,
                methodAndArguments.Arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw new InvalidOperationException(
                $"Method '{methodAndArguments.Method.Name}' threw: " +
                exception.InnerException.Message,
                exception.InnerException);
        }

        return new StatusResponse(
            "ok",
            $"Called '{FormatMethod(methodAndArguments.Method)}'. " +
            $"ReturnValue: {ValueConverter.SafeToString(returnValue)}");
    }

    internal IReadOnlyList<string> ListComponentMethods(
        int gameObjectId,
        string componentName)
    {
        var component = ResolveComponent(gameObjectId, componentName);
        return component.Type
            .GetMethods(InstanceMembers)
            .Where(method => !method.IsSpecialName)
            .Select(FormatMethod)
            .OrderBy(signature => signature, StringComparer.Ordinal)
            .ToArray();
    }

    internal IReadOnlyList<SimpleGameObject> FindObjectsWithComponent(string componentName)
    {
        var requestedType = typeResolver.ResolveRequired(componentName);
        var result = new List<SimpleGameObject>();
        var seen = new HashSet<int>();

        foreach (var gameObject in Resources.FindObjectsOfTypeAll<GameObject>())
        {
            if (gameObject is null ||
                (!gameObject.scene.IsValid() && gameObject.hideFlags == HideFlags.None))
            {
                continue;
            }

            foreach (var component in gameObject.GetComponents<Component>())
            {
                if (component is null)
                {
                    continue;
                }

                var actualType = typeResolver.GetActualType(component);
                if (actualType != requestedType && !requestedType.IsAssignableFrom(actualType))
                {
                    continue;
                }

                var id = gameObject.GetInstanceID();
                if (seen.Add(id))
                {
                    result.Add(ToSimpleObject(gameObject));
                }

                break;
            }
        }

        return result;
    }

    internal SearchResponse SearchGameObjects(
        string name,
        string componentName,
        string tag,
        string sceneName,
        bool includeInactive,
        int limit)
    {
        limit = Math.Clamp(limit, 1, 1_000);
        var result = new List<GameObjectSearchResult>(Math.Min(limit, 128));
        var truncated = false;

        foreach (var gameObject in Resources.FindObjectsOfTypeAll<GameObject>())
        {
            if (gameObject is null ||
                (!gameObject.scene.IsValid() && gameObject.hideFlags == HideFlags.None))
            {
                continue;
            }

            if (!includeInactive && !gameObject.activeInHierarchy)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(name) &&
                !gameObject.name.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(tag) &&
                !string.Equals(gameObject.tag, tag, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(sceneName) &&
                !string.Equals(
                    GetSceneName(gameObject),
                    sceneName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(componentName) &&
                !HasComponent(gameObject, componentName))
            {
                continue;
            }

            if (result.Count == limit)
            {
                truncated = true;
                break;
            }

            result.Add(ToSearchResult(gameObject));
        }

        return new SearchResponse(result, truncated);
    }

    internal IReadOnlyList<HierarchyNode> GetHierarchySnapshot(
        int? rootId,
        int depth,
        int maxNodes)
    {
        depth = Math.Clamp(depth, 0, 16);
        maxNodes = Math.Clamp(maxNodes, 1, Protocol.MaxSnapshotNodes);
        var remaining = maxNodes;
        var result = new List<HierarchyNode>();

        if (rootId.HasValue)
        {
            result.Add(BuildHierarchyNode(FindObjectById(rootId.Value), depth, ref remaining));
            return result;
        }

        foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
        {
            if (root is null || remaining <= 0)
            {
                break;
            }

            result.Add(BuildHierarchyNode(root, depth, ref remaining));
        }

        return result;
    }

    internal ObjectSelector GetSelector(int instanceId)
    {
        var gameObject = FindObjectById(instanceId);
        return new ObjectSelector(
            GetSceneName(gameObject),
            GetObjectPath(gameObject),
            null,
            gameObject.name);
    }

    internal GameObjectSearchResult ResolveSelector(ObjectSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        var matches = new List<GameObject>();

        foreach (var gameObject in Resources.FindObjectsOfTypeAll<GameObject>())
        {
            if (gameObject is null ||
                (!gameObject.scene.IsValid() && gameObject.hideFlags == HideFlags.None))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(selector.Scene) &&
                !string.Equals(
                    GetSceneName(gameObject),
                    selector.Scene,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(selector.Path) &&
                !string.Equals(GetObjectPath(gameObject), selector.Path, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(selector.Name) &&
                !string.Equals(gameObject.name, selector.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(selector.Component) &&
                !HasComponent(gameObject, selector.Component))
            {
                continue;
            }

            matches.Add(gameObject);
            if (matches.Count > 1)
            {
                break;
            }
        }

        return matches.Count switch
        {
            1 => ToSearchResult(matches[0]),
            0 => throw new KeyNotFoundException("No GameObject matched the supplied selector."),
            _ => throw new AmbiguousMatchException(
                "The selector matched more than one GameObject. Add a scene or hierarchy path.")
        };
    }

    internal MemberValueResponse GetComponentMember(
        int gameObjectId,
        string componentName,
        string memberName)
    {
        var component = ResolveComponent(gameObjectId, componentName);
        return ReadMember(component, memberName);
    }

    internal IReadOnlyList<TypeSummary> SearchTypes(string query, int offset, int limit) =>
        typeResolver.Search(query, offset, limit)
            .Select(ToTypeSummary)
            .ToArray();

    internal TypeDescription DescribeType(string typeName, int offset, int limit)
    {
        var type = typeResolver.ResolveRequired(typeName);
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 500);

        var members = type
            .GetFields(AllMethods)
            .Select(field => new MemberDescription(
                field.Name,
                FormatType(field.FieldType),
                "field",
                true,
                !field.IsInitOnly && !field.IsLiteral,
                field.IsStatic))
            .Concat(type.GetProperties(AllMethods)
                .Where(property => property.GetIndexParameters().Length == 0)
                .Select(property => new MemberDescription(
                    property.Name,
                    FormatType(property.PropertyType),
                    "property",
                    property.CanRead,
                    property.CanWrite,
                    (property.GetMethod ?? property.SetMethod)?.IsStatic ?? false)))
            .OrderBy(member => member.Name, StringComparer.Ordinal)
            .Skip(offset)
            .Take(limit)
            .ToArray();

        var methods = type
            .GetMethods(AllMethods)
            .Where(method => !method.IsSpecialName)
            .OrderBy(method => method.Name, StringComparer.Ordinal)
            .ThenBy(method => method.GetParameters().Length)
            .Skip(offset)
            .Take(limit)
            .Select(method => new MethodDescription(
                method.Name,
                FormatMethod(method),
                FormatType(method.ReturnType),
                method.GetParameters()
                    .Select(parameter => FormatType(parameter.ParameterType))
                    .ToArray(),
                method.IsStatic))
            .ToArray();

        return new TypeDescription(ToTypeSummary(type), members, methods);
    }

    internal NetworkDiagnosticsResponse GetNetworkDiagnostics(int gameObjectId)
    {
        var gameObject = FindObjectById(gameObjectId);
        var diagnostics = new List<NetworkObjectDiagnostic>();
        var propertyNames = new[]
        {
            "IsOwner", "HasAuthority", "IsServer", "IsClient", "IsHost",
            "IsSpawned", "OwnerId", "ObjectId", "NetworkObjectId", "ViewID"
        };

        foreach (var component in gameObject.GetComponents<Component>())
        {
            if (component is null)
            {
                continue;
            }

            var type = typeResolver.GetActualType(component);
            var typed = typeResolver.Cast(component, type);
            var values = new SortedDictionary<string, string>(StringComparer.Ordinal);

            foreach (var propertyName in propertyNames)
            {
                var property = type.GetProperty(propertyName, InstanceMembers);
                if (property is null || !property.CanRead || property.GetIndexParameters().Length != 0)
                {
                    continue;
                }

                try
                {
                    values[propertyName] = ValueConverter.SafeToString(property.GetValue(typed));
                }
                catch
                {
                }
            }

            if (values.Count == 0)
            {
                continue;
            }

            diagnostics.Add(new NetworkObjectDiagnostic(
                FormatType(type),
                DetectNetworkFramework(type),
                values));
        }

        return new NetworkDiagnosticsResponse(gameObjectId, diagnostics);
    }

    internal string ReadMemberValue(ObjectSelector selector, string componentName, string memberName)
    {
        var target = ResolveSelector(selector);
        return GetComponentMember(target.Id, componentName, memberName).Value;
    }

    internal ResolvedComponent ResolveComponent(int gameObjectId, string componentName)
    {
        var gameObject = FindObjectById(gameObjectId);
        var exactMatches = new List<(Component Component, Type Type)>();
        var simpleMatches = new List<(Component Component, Type Type)>();

        foreach (var component in gameObject.GetComponents<Component>())
        {
            if (component is null)
            {
                continue;
            }

            var actualType = typeResolver.GetActualType(component);
            if (string.Equals(actualType.FullName, componentName, StringComparison.Ordinal) ||
                string.Equals(
                    actualType.AssemblyQualifiedName,
                    componentName,
                    StringComparison.Ordinal))
            {
                exactMatches.Add((component, actualType));
            }
            else if (string.Equals(actualType.Name, componentName, StringComparison.Ordinal) ||
                     string.Equals(
                         actualType.FullName,
                         componentName,
                         StringComparison.OrdinalIgnoreCase))
            {
                simpleMatches.Add((component, actualType));
            }
        }

        var matches = exactMatches.Count > 0 ? exactMatches : simpleMatches;
        if (matches.Count == 0)
        {
            throw new KeyNotFoundException(
                $"Component '{componentName}' was not found on GameObject {gameObjectId}.");
        }

        var distinctTypes = matches
            .Select(match => match.Type)
            .Distinct()
            .ToArray();
        if (distinctTypes.Length > 1)
        {
            throw new AmbiguousMatchException(
                $"Component name '{componentName}' is ambiguous on GameObject {gameObjectId}: " +
                string.Join(", ", distinctTypes.Select(FormatType)));
        }

        var selected = matches[0];
        var typedInstance = typeResolver.Cast(selected.Component, selected.Type);
        return new ResolvedComponent(gameObject, selected.Component, typedInstance, selected.Type);
    }

    internal Type ResolveType(string typeName) => typeResolver.ResolveRequired(typeName);

    internal MethodInfo ResolveMethod(
        Type targetType,
        string methodName,
        string parameterTypeNames)
    {
        var methods = targetType
            .GetMethods(AllMethods)
            .Where(method =>
                !method.IsGenericMethodDefinition &&
                string.Equals(method.Name, methodName, StringComparison.Ordinal))
            .ToArray();

        if (!string.IsNullOrWhiteSpace(parameterTypeNames))
        {
            var parameterTypes = parameterTypeNames
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(typeResolver.ResolveRequired)
                .ToArray();
            methods = methods
                .Where(method => method
                    .GetParameters()
                    .Select(parameter => parameter.ParameterType)
                    .SequenceEqual(parameterTypes))
                .ToArray();
        }

        return methods.Length switch
        {
            1 => methods[0],
            0 => throw new MissingMethodException(
                targetType.FullName,
                $"{methodName}({parameterTypeNames})"),
            _ => throw new AmbiguousMatchException(
                $"Method '{targetType.FullName}.{methodName}' is overloaded. " +
                "Supply parameterTypes. Matches: " +
                string.Join("; ", methods.Select(FormatMethod)))
        };
    }

    internal GameObject FindObjectById(int instanceId)
    {
        foreach (var gameObject in Resources.FindObjectsOfTypeAll<GameObject>())
        {
            if (gameObject is not null && gameObject.GetInstanceID() == instanceId)
            {
                return gameObject;
            }
        }

        throw new KeyNotFoundException($"GameObject with ID {instanceId} was not found.");
    }

    private static SelectedMethod SelectInvocableMethod(
        Type componentType,
        string methodName,
        IReadOnlyList<System.Text.Json.JsonElement> arguments)
    {
        var candidates = componentType
            .GetMethods(InstanceMembers)
            .Where(method =>
                !method.IsSpecialName &&
                !method.IsGenericMethodDefinition &&
                string.Equals(method.Name, methodName, StringComparison.OrdinalIgnoreCase) &&
                method.GetParameters().Length == arguments.Count)
            .ToArray();

        var matches = new List<SelectedMethod>();
        foreach (var candidate in candidates)
        {
            var parameters = candidate.GetParameters();
            var converted = new object?[parameters.Length];
            var valid = true;

            for (var index = 0; index < parameters.Length; index++)
            {
                try
                {
                    converted[index] = ValueConverter.ConvertJson(
                        arguments[index],
                        parameters[index].ParameterType);
                }
                catch
                {
                    valid = false;
                    break;
                }
            }

            if (valid)
            {
                matches.Add(new SelectedMethod(candidate, converted));
            }
        }

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new MissingMethodException(
                $"No overload of '{componentType.FullName}.{methodName}' accepts " +
                $"{arguments.Count} supplied argument(s)."),
            _ => throw new AmbiguousMatchException(
                $"More than one overload of '{componentType.FullName}.{methodName}' accepts " +
                "the supplied arguments. Matches: " +
                string.Join("; ", matches.Select(match => FormatMethod(match.Method))))
        };
    }

    private static SimpleGameObject ToSimpleObject(GameObject gameObject) =>
        new(gameObject.name, gameObject.GetInstanceID());

    private bool HasComponent(GameObject gameObject, string componentName)
    {
        foreach (var component in gameObject.GetComponents<Component>())
        {
            if (component is null)
            {
                continue;
            }

            var type = typeResolver.GetActualType(component);
            if (string.Equals(type.FullName, componentName, StringComparison.Ordinal) ||
                string.Equals(type.Name, componentName, StringComparison.Ordinal) ||
                string.Equals(type.FullName, componentName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private HierarchyNode BuildHierarchyNode(GameObject gameObject, int depth, ref int remaining)
    {
        remaining--;
        var children = new List<HierarchyNode>();

        if (depth > 0 && remaining > 0)
        {
            var transform = gameObject.transform;
            for (var index = 0; index < transform.childCount && remaining > 0; index++)
            {
                var child = transform.GetChild(index);
                if (child is not null && child.gameObject is not null)
                {
                    children.Add(BuildHierarchyNode(child.gameObject, depth - 1, ref remaining));
                }
            }
        }

        return new HierarchyNode(
            gameObject.name,
            gameObject.GetInstanceID(),
            GetSceneName(gameObject),
            GetObjectPath(gameObject),
            gameObject.activeInHierarchy,
            children);
    }

    private static string GetObjectPath(GameObject gameObject)
    {
        var segments = new Stack<string>();
        var current = gameObject.transform;

        while (current is not null)
        {
            segments.Push(
                $"{Uri.EscapeDataString(current.gameObject.name)}[{current.GetSiblingIndex()}]");
            current = current.parent;
        }

        return "/" + string.Join("/", segments);
    }

    private static GameObjectSearchResult ToSearchResult(GameObject gameObject) =>
        new(
            gameObject.name,
            gameObject.GetInstanceID(),
            GetSceneName(gameObject),
            GetObjectPath(gameObject),
            gameObject.activeInHierarchy);

    private static string GetSceneName(GameObject gameObject) =>
        gameObject.scene.IsValid() && !string.IsNullOrEmpty(gameObject.scene.name)
            ? gameObject.scene.name
            : "<persistent>";

    private static MemberValueResponse ReadMember(
        ResolvedComponent component,
        string memberName)
    {
        var field = component.Type.GetField(
            memberName,
            InstanceMembers | BindingFlags.IgnoreCase);
        if (field is not null)
        {
            return new MemberValueResponse(
                field.Name,
                FormatType(field.FieldType),
                ValueConverter.SafeToString(field.GetValue(component.Instance)),
                !field.IsInitOnly && !field.IsLiteral,
                "field");
        }

        var property = component.Type.GetProperty(
            memberName,
            InstanceMembers | BindingFlags.IgnoreCase);
        if (property is null || property.GetIndexParameters().Length != 0)
        {
            throw new MissingMemberException(component.Type.FullName, memberName);
        }

        if (!property.CanRead)
        {
            throw new InvalidOperationException($"Property '{property.Name}' is write-only.");
        }

        return new MemberValueResponse(
            property.Name,
            FormatType(property.PropertyType),
            ValueConverter.SafeToString(property.GetValue(component.Instance)),
            property.CanWrite,
            "property");
    }

    private static TypeSummary ToTypeSummary(Type type) =>
        new(
            type.Name,
            FormatType(type),
            type.Assembly.GetName().Name ?? "unknown",
            typeof(Component).IsAssignableFrom(type));

    private static string? DetectNetworkFramework(Type type)
    {
        var name = type.FullName ?? type.Name;
        if (name.Contains("FishNet.", StringComparison.Ordinal))
        {
            return "FishNet";
        }

        if (name.Contains("Mirror.", StringComparison.Ordinal))
        {
            return "Mirror";
        }

        if (name.Contains("Unity.Netcode.", StringComparison.Ordinal))
        {
            return "Unity.Netcode";
        }

        if (name.Contains("Photon.", StringComparison.Ordinal))
        {
            return "Photon";
        }

        return null;
    }

    private static string FormatType(Type type) => type.FullName ?? type.Name;

    private static string FormatMethod(MethodInfo method)
    {
        var parameters = string.Join(
            ", ",
            method.GetParameters().Select(parameter =>
                $"{FormatType(parameter.ParameterType)} {parameter.Name}"));
        return $"{FormatType(method.ReturnType)} {method.Name}({parameters})";
    }

    internal sealed record ResolvedComponent(
        GameObject GameObject,
        Component RawComponent,
        object Instance,
        Type Type);

    private sealed record SelectedMethod(MethodInfo Method, object?[] Arguments);
}

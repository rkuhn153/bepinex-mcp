using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

public partial class McpServerMod
{
    private const string ProductivityProtocolVersion = "2.0";
    private const int ProductivityMaxBatchOperations = 100;
    private const int ProductivityMaxRequestBodyBytes = 256 * 1024;
    private const int ProductivityMaxWatchers = 128;
    private const int ProductivityMaxPatchRegistrations = 128;
    private const int ProductivityMaxSnapshotNodes = 1000;

    private readonly Dictionary<string, MonoWatchState> productivityWatchers =
        new Dictionary<string, MonoWatchState>(StringComparer.Ordinal);
    private readonly Dictionary<string, MonoPatchState> productivityPatches =
        new Dictionary<string, MonoPatchState>(StringComparer.Ordinal);

    private void InitializeProductivity()
    {
    }

    private void ShutdownProductivity()
    {
        productivityWatchers.Clear();
        productivityPatches.Clear();
    }

    private bool TryCreateProductivityJob(
        string command,
        HttpListenerRequest request,
        string requestBody,
        out Func<string> job,
        out int statusCode,
        out string response)
    {
        job = null;
        statusCode = 200;
        response = string.Empty;

        try
        {
            switch (command)
            {
                case "system/ping":
                    job = () => Json(new
                    {
                        status = "ok",
                        protocolVersion = ProductivityProtocolVersion,
                        timestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    });
                    return true;
                case "scene/search_gameobjects":
                    job = () => Json(SearchGameObjectsV2(
                        Optional(request, "name"),
                        Optional(request, "componentName"),
                        Optional(request, "tag"),
                        Optional(request, "scene"),
                        OptionalBool(request, "includeInactive", true),
                        OptionalInt(request, "limit", 100, 1, 1000)));
                    return true;
                case "scene/hierarchy_snapshot":
                    job = () => Json(HierarchySnapshotV2(
                        OptionalNullableInt(request, "id"),
                        OptionalInt(request, "depth", 3, 0, 16),
                        OptionalInt(
                            request,
                            "maxNodes",
                            500,
                            1,
                            ProductivityMaxSnapshotNodes)));
                    return true;
                case "scene/resolve_selector":
                    job = () => Json(ToSearchResult(ResolveSelectorV2(ReadSelector(request))));
                    return true;
                case "component/get_member":
                    job = () => Json(GetMemberV2(
                        RequiredInt(request, "id"),
                        Required(request, "componentName"),
                        Required(request, "memberName")));
                    return true;
                case "type/search":
                    job = () => Json(SearchTypesV2(
                        Optional(request, "query"),
                        OptionalInt(request, "offset", 0, 0, int.MaxValue),
                        OptionalInt(request, "limit", 100, 1, 500)));
                    return true;
                case "type/describe":
                    job = () => Json(DescribeTypeV2(
                        Required(request, "typeName"),
                        OptionalInt(request, "offset", 0, 0, int.MaxValue),
                        OptionalInt(request, "limit", 200, 1, 500)));
                    return true;
                case "network/diagnostics":
                    job = () => Json(NetworkDiagnosticsV2(RequiredInt(request, "id")));
                    return true;
                case "watch/member":
                    job = () => Json(CreateMemberWatchV2(
                        Optional(request, "registrationId"),
                        ReadSelector(request),
                        Required(request, "componentName"),
                        Required(request, "memberName"),
                        OptionalInt(request, "intervalMs", 500, 100, 60000)));
                    return true;
                case "watch/scene":
                    job = () => Json(CreateSceneWatchV2(
                        Optional(request, "registrationId"),
                        OptionalInt(request, "intervalMs", 500, 100, 60000)));
                    return true;
                case "watch/list":
                    job = () => Json(productivityWatchers.Values
                        .Select(state => state.Registration)
                        .OrderBy(item => item.id)
                        .ToArray());
                    return true;
                case "watch/remove":
                    job = () => Json(RemoveWatchV2(Required(request, "registrationId")));
                    return true;
                case "mod:list_patches":
                    job = () => Json(productivityPatches.Values
                        .Select(state => state.Info)
                        .OrderBy(item => item.id)
                        .ToArray());
                    return true;
                case "mod:remove_patch":
                    job = () => Json(RemovePatchV2(Required(request, "registrationId")));
                    return true;
                case "batch":
                    if (!string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                    {
                        statusCode = 405;
                        response = JsonError("Batch requires HTTP POST.", "method_not_allowed");
                        return true;
                    }

                    job = () => ExecuteBatchV2(requestBody);
                    return true;
                default:
                    return false;
            }
        }
        catch (Exception exception)
        {
            statusCode = 400;
            response = JsonError(exception.Message, "invalid_request");
            return true;
        }
    }

    private object SearchGameObjectsV2(
        string name,
        string componentName,
        string tag,
        string scene,
        bool includeInactive,
        int limit)
    {
        var items = new List<object>();
        var truncated = false;

        foreach (var gameObject in Resources.FindObjectsOfTypeAll<GameObject>())
        {
            if (gameObject == null || !gameObject.scene.IsValid())
                continue;
            if (!includeInactive && !gameObject.activeInHierarchy)
                continue;
            if (!string.IsNullOrWhiteSpace(name) &&
                gameObject.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            if (!string.IsNullOrWhiteSpace(tag) &&
                !string.Equals(gameObject.tag, tag, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.IsNullOrWhiteSpace(scene) &&
                !string.Equals(gameObject.scene.name, scene, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.IsNullOrWhiteSpace(componentName) &&
                !HasComponentV2(gameObject, componentName))
                continue;

            if (items.Count == limit)
            {
                truncated = true;
                break;
            }

            items.Add(ToSearchResult(gameObject));
        }

        return new { items, truncated };
    }

    private object[] HierarchySnapshotV2(int? rootId, int depth, int maxNodes)
    {
        var remaining = maxNodes;
        if (rootId.HasValue)
        {
            var root = FindObjectById(rootId.Value)
                       ?? throw new KeyNotFoundException(
                           $"GameObject with ID {rootId.Value} was not found.");
            return new[] { BuildHierarchyNodeV2(root, depth, ref remaining) };
        }

        var result = new List<object>();
        foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
        {
            if (remaining <= 0)
                break;
            result.Add(BuildHierarchyNodeV2(root, depth, ref remaining));
        }

        return result.ToArray();
    }

    private object BuildHierarchyNodeV2(GameObject gameObject, int depth, ref int remaining)
    {
        remaining--;
        var children = new List<object>();
        if (depth > 0 && remaining > 0)
        {
            for (var i = 0; i < gameObject.transform.childCount && remaining > 0; i++)
            {
                children.Add(BuildHierarchyNodeV2(
                    gameObject.transform.GetChild(i).gameObject,
                    depth - 1,
                    ref remaining));
            }
        }

        return new
        {
            name = gameObject.name,
            id = gameObject.GetInstanceID(),
            scene = gameObject.scene.name,
            path = ObjectPathV2(gameObject),
            active = gameObject.activeInHierarchy,
            children
        };
    }

    private MonoMemberValue GetMemberV2(int id, string componentName, string memberName)
    {
        var gameObject = FindObjectById(id)
                         ?? throw new KeyNotFoundException(
                             $"GameObject with ID {id} was not found.");
        var type = FindType(componentName)
                   ?? throw new TypeLoadException($"Type '{componentName}' was not found.");
        var component = gameObject.GetComponent(type)
                        ?? throw new KeyNotFoundException(
                            $"Component '{componentName}' was not found on GameObject {id}.");
        const BindingFlags flags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase;
        var field = type.GetField(memberName, flags);
        if (field != null)
        {
            return new MonoMemberValue
            {
                name = field.Name,
                type = TypeName(field.FieldType),
                value = SafeString(field.GetValue(component)),
                writable = !field.IsInitOnly && !field.IsLiteral,
                kind = "field"
            };
        }

        var property = type.GetProperty(memberName, flags);
        if (property == null || property.GetIndexParameters().Length != 0)
            throw new MissingMemberException(type.FullName, memberName);
        if (!property.CanRead)
            throw new InvalidOperationException($"Property '{property.Name}' is write-only.");

        return new MonoMemberValue
        {
            name = property.Name,
            type = TypeName(property.PropertyType),
            value = SafeString(property.GetValue(component, null)),
            writable = property.CanWrite,
            kind = "property"
        };
    }

    private object[] SearchTypesV2(string query, int offset, int limit)
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(GetLoadableTypesV2)
            .Where(type =>
                string.IsNullOrWhiteSpace(query) ||
                TypeName(type).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(TypeName, StringComparer.Ordinal)
            .Skip(offset)
            .Take(limit)
            .Select(type => (object)TypeSummaryV2(type))
            .ToArray();
    }

    private object DescribeTypeV2(string typeName, int offset, int limit)
    {
        var type = FindType(typeName)
                   ?? throw new TypeLoadException($"Type '{typeName}' was not found.");
        const BindingFlags flags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var members = type.GetFields(flags)
            .Select(field => new MonoTypeMember
            {
                name = field.Name,
                type = TypeName(field.FieldType),
                kind = "field",
                readable = true,
                writable = !field.IsInitOnly && !field.IsLiteral,
                isStatic = field.IsStatic
            })
            .Concat(type.GetProperties(flags)
                .Where(property => property.GetIndexParameters().Length == 0)
                .Select(property => new MonoTypeMember
                {
                    name = property.Name,
                    type = TypeName(property.PropertyType),
                    kind = "property",
                    readable = property.CanRead,
                    writable = property.CanWrite,
                    isStatic = (property.GetGetMethod(true) ?? property.GetSetMethod(true))?.IsStatic ?? false
                }))
            .OrderBy(item => item.name, StringComparer.Ordinal)
            .Skip(offset)
            .Take(limit)
            .ToArray();
        var methods = type.GetMethods(flags)
            .Where(method => !method.IsSpecialName)
            .OrderBy(method => method.Name)
            .ThenBy(method => method.GetParameters().Length)
            .Skip(offset)
            .Take(limit)
            .Select(method => new
            {
                name = method.Name,
                signature = FormatMethodV2(method),
                returnType = TypeName(method.ReturnType),
                parameterTypes = method.GetParameters()
                    .Select(parameter => TypeName(parameter.ParameterType))
                    .ToArray(),
                isStatic = method.IsStatic
            })
            .ToArray();
        return new { type = TypeSummaryV2(type), members, methods };
    }

    private object NetworkDiagnosticsV2(int id)
    {
        var gameObject = FindObjectById(id)
                         ?? throw new KeyNotFoundException(
                             $"GameObject with ID {id} was not found.");
        var propertyNames = new[]
        {
            "IsOwner", "HasAuthority", "IsServer", "IsClient", "IsHost",
            "IsSpawned", "OwnerId", "ObjectId", "NetworkObjectId", "ViewID"
        };
        var components = new List<object>();

        foreach (var component in gameObject.GetComponents<Component>())
        {
            if (component == null)
                continue;
            var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in propertyNames)
            {
                var property = component.GetType().GetProperty(
                    name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (property == null || !property.CanRead || property.GetIndexParameters().Length != 0)
                    continue;
                try { values[name] = SafeString(property.GetValue(component, null)); }
                catch { }
            }

            if (values.Count > 0)
            {
                components.Add(new
                {
                    component = TypeName(component.GetType()),
                    framework = DetectNetworkFrameworkV2(component.GetType()),
                    values
                });
            }
        }

        return new { instanceId = id, components };
    }

    private MonoWatchRegistration CreateMemberWatchV2(
        string requestedId,
        MonoObjectSelector selector,
        string component,
        string member,
        int intervalMs)
    {
        EnsureWatchCapacityV2();
        var id = RegistrationIdV2(requestedId, "watch");
        var target = ResolveSelectorV2(selector);
        var initial = GetMemberV2(target.GetInstanceID(), component, member).value;
        var registration = new MonoWatchRegistration
        {
            id = id,
            kind = "member",
            selector = selector,
            component = component,
            member = member,
            intervalMs = intervalMs,
            active = true,
            instanceId = target.GetInstanceID()
        };
        productivityWatchers.Add(id, new MonoWatchState
        {
            Registration = registration,
            LastValue = initial,
            NextPollAt = Time.realtimeSinceStartup
        });
        return registration;
    }

    private MonoWatchRegistration CreateSceneWatchV2(string requestedId, int intervalMs)
    {
        EnsureWatchCapacityV2();
        var id = RegistrationIdV2(requestedId, "scene");
        var scene = SceneManager.GetActiveScene();
        var registration = new MonoWatchRegistration
        {
            id = id,
            kind = "scene",
            intervalMs = intervalMs,
            active = true
        };
        productivityWatchers.Add(id, new MonoWatchState
        {
            Registration = registration,
            LastValue = $"{scene.handle}:{scene.name}",
            NextPollAt = Time.realtimeSinceStartup
        });
        return registration;
    }

    private object RemoveWatchV2(string id)
    {
        if (!productivityWatchers.Remove(id))
            throw new KeyNotFoundException($"Watcher '{id}' was not found.");
        return new { status = "ok", message = $"Removed watcher '{id}'.", id };
    }

    private void TickProductivity()
    {
        var now = Time.realtimeSinceStartup;
        foreach (var state in productivityWatchers.Values.ToArray())
        {
            if (now < state.NextPollAt)
                continue;
            state.NextPollAt = now + state.Registration.intervalMs / 1000f;

            if (state.Registration.kind == "scene")
            {
                var scene = SceneManager.GetActiveScene();
                var current = $"{scene.handle}:{scene.name}";
                if (current != state.LastValue)
                {
                    SendProductivityEventV2(new
                    {
                        kind = "scene.changed",
                        registrationId = state.Registration.id,
                        timestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        oldValue = state.LastValue,
                        newValue = current
                    });
                    state.LastValue = current;
                }
                continue;
            }

            try
            {
                var target = ResolveSelectorV2(state.Registration.selector);
                var current = GetMemberV2(
                    target.GetInstanceID(),
                    state.Registration.component,
                    state.Registration.member).value;
                state.TargetLostReported = false;
                if (current == state.LastValue)
                    continue;
                SendProductivityEventV2(new
                {
                    kind = "watch.changed",
                    registrationId = state.Registration.id,
                    timestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    selector = state.Registration.selector,
                    instanceId = target.GetInstanceID(),
                    component = state.Registration.component,
                    member = state.Registration.member,
                    oldValue = state.LastValue,
                    newValue = current
                });
                state.LastValue = current;
                state.Registration.instanceId = target.GetInstanceID();
            }
            catch (Exception exception)
            {
                if (state.TargetLostReported)
                    continue;
                state.TargetLostReported = true;
                SendProductivityEventV2(new
                {
                    kind = "watch.target_lost",
                    registrationId = state.Registration.id,
                    timestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    selector = state.Registration.selector,
                    component = state.Registration.component,
                    member = state.Registration.member,
                    newValue = exception.Message
                });
            }
        }
    }

    private string ExecuteBatchV2(string requestBody)
    {
        if (string.IsNullOrWhiteSpace(requestBody))
            throw new ArgumentException("A JSON batch body is required.");
        var request = JObject.Parse(requestBody);
        var operations = request["operations"] as JArray
                         ?? throw new ArgumentException("'operations' must be an array.");
        if (operations.Count < 1 || operations.Count > ProductivityMaxBatchOperations)
            throw new ArgumentException(
                $"A batch must contain 1 to {ProductivityMaxBatchOperations} operations.");
        var stopOnError = request.Value<bool?>("stopOnError") ?? false;
        var results = new JArray();

        foreach (var token in operations)
        {
            var operation = token as JObject
                            ?? throw new ArgumentException("Each batch operation must be an object.");
            var operationId = operation.Value<string>("id") ?? string.Empty;
            var command = operation.Value<string>("command") ?? string.Empty;
            var parameters = operation["parameters"] as JObject ?? new JObject();
            try
            {
                object result;
                switch (command)
                {
                    case "scene/list_root_gameobjects":
                        result = SceneManager.GetActiveScene().GetRootGameObjects()
                            .Select(ToSearchResult).ToArray();
                        break;
                    case "scene/search_gameobjects":
                        result = SearchGameObjectsV2(
                            parameters.Value<string>("name") ?? string.Empty,
                            parameters.Value<string>("componentName") ?? string.Empty,
                            parameters.Value<string>("tag") ?? string.Empty,
                            parameters.Value<string>("scene") ?? string.Empty,
                            parameters.Value<bool?>("includeInactive") ?? true,
                            ClampV2(parameters.Value<int?>("limit") ?? 100, 1, 1000));
                        break;
                    case "scene/hierarchy_snapshot":
                        result = HierarchySnapshotV2(
                            parameters.Value<int?>("id"),
                            ClampV2(parameters.Value<int?>("depth") ?? 3, 0, 16),
                            ClampV2(parameters.Value<int?>("maxNodes") ?? 500, 1, ProductivityMaxSnapshotNodes));
                        break;
                    case "scene/resolve_selector":
                        result = ToSearchResult(ResolveSelectorV2(
                            parameters["selector"]?.ToObject<MonoObjectSelector>()
                            ?? throw new ArgumentException("Batch selector is missing.")));
                        break;
                    case "scene:find_objects_with_component":
                        result = JToken.Parse(FindObjectsWithComponent(
                            RequiredTokenV2(parameters, "componentName"))());
                        break;
                    case "gameobject/list_children":
                        result = JToken.Parse(ListChildren(parameters.Value<int>("id"))());
                        break;
                    case "gameobject/inspect_components":
                        result = JToken.Parse(InspectGameObject(parameters.Value<int>("id"))());
                        break;
                    case "component/get_details":
                        result = JToken.Parse(InspectComponentDetails(
                            parameters.Value<int>("id"),
                            RequiredTokenV2(parameters, "componentName"))());
                        break;
                    case "component/get_member":
                        result = GetMemberV2(
                            parameters.Value<int>("id"),
                            RequiredTokenV2(parameters, "componentName"),
                            RequiredTokenV2(parameters, "memberName"));
                        break;
                    case "component/set_value":
                        result = JToken.Parse(SetComponentValue(
                            parameters.Value<int>("id"),
                            RequiredTokenV2(parameters, "componentName"),
                            RequiredTokenV2(parameters, "memberName"),
                            parameters.Value<string>("value") ?? string.Empty)());
                        break;
                    case "component/call_method":
                        result = JToken.Parse(CallComponentMethod(
                            parameters.Value<int>("id"),
                            RequiredTokenV2(parameters, "componentName"),
                            RequiredTokenV2(parameters, "methodName"),
                            parameters.Value<string>("args") ?? "[]")());
                        break;
                    case "component/list_methods":
                        result = JToken.Parse(ListComponentMethods(
                            parameters.Value<int>("id"),
                            RequiredTokenV2(parameters, "componentName"))());
                        break;
                    case "type/search":
                        result = SearchTypesV2(
                            parameters.Value<string>("query") ?? string.Empty,
                            ClampV2(parameters.Value<int?>("offset") ?? 0, 0, 100000),
                            ClampV2(parameters.Value<int?>("limit") ?? 50, 1, 500));
                        break;
                    case "type/describe":
                        result = DescribeTypeV2(
                            RequiredTokenV2(parameters, "typeName"),
                            ClampV2(parameters.Value<int?>("offset") ?? 0, 0, 100000),
                            ClampV2(parameters.Value<int?>("limit") ?? 100, 1, 1000));
                        break;
                    case "network/diagnostics":
                        result = NetworkDiagnosticsV2(parameters.Value<int>("id"));
                        break;
                    case "watch/list":
                        result = productivityWatchers.Values
                            .Select(state => state.Registration)
                            .OrderBy(item => item.id, StringComparer.Ordinal)
                            .ToArray();
                        break;
                    case "mod:list_patches":
                        result = productivityPatches.Values
                            .Select(state => state.Info)
                            .OrderBy(item => item.id, StringComparer.Ordinal)
                            .ToArray();
                        break;
                    default:
                        throw new NotSupportedException(
                            $"Command '{command}' is not allowed in a batch.");
                }
                results.Add(JObject.FromObject(new { id = operationId, ok = true, result }));
            }
            catch (Exception exception)
            {
                results.Add(JObject.FromObject(new
                {
                    id = operationId,
                    ok = false,
                    error = new { error = exception.Message, code = "operation_failed" }
                }));
                if (stopOnError)
                    break;
            }
        }

        return Json(new { results });
    }

    private object RemovePatchV2(string id)
    {
        if (!productivityPatches.TryGetValue(id, out var state))
            throw new KeyNotFoundException($"Patch or subscription '{id}' was not found.");
        harmony.Unpatch(state.Original, state.PatchMethod);
        productivityPatches.Remove(id);
        patchedMethods.TryRemove(state.LegacyId ?? string.Empty, out _);
        return new { status = "ok", message = $"Removed patch '{id}'.", id };
    }

    private MonoPatchInfo RegisterPatchV2(
        string requestedId,
        string kind,
        string target,
        string patchType,
        MethodBase original,
        MethodInfo patchMethod,
        string legacyId = null,
        string fingerprint = null)
    {
        if (productivityPatches.Count >= ProductivityMaxPatchRegistrations)
            throw new InvalidOperationException(
                $"The patch registration limit of {ProductivityMaxPatchRegistrations} has been reached.");
        var id = RegistrationIdV2(requestedId, kind == "subscription" ? "subscription" : "patch");
        var info = new MonoPatchInfo
        {
            id = id,
            kind = kind,
            target = target,
            patchType = patchType,
            active = true
        };
        productivityPatches.Add(id, new MonoPatchState
        {
            Info = info,
            Original = original,
            PatchMethod = patchMethod,
            LegacyId = legacyId,
            Fingerprint = fingerprint
        });
        return info;
    }

    private static void SendProductivityEventV2(object value)
    {
        var payload = JsonConvert.SerializeObject(value);
        _ = System.Threading.Tasks.Task.Run(() => SendWebhook(payload));
    }

    private MonoObjectSelector ReadSelector(HttpListenerRequest request)
    {
        var json = Optional(request, "selector");
        if (!string.IsNullOrWhiteSpace(json))
            return JsonConvert.DeserializeObject<MonoObjectSelector>(json)
                   ?? throw new ArgumentException("Selector JSON is invalid.");
        var id = OptionalNullableInt(request, "id");
        if (id.HasValue)
            return SelectorForV2(FindObjectById(id.Value));
        throw new ArgumentException("Supply either 'selector' JSON or an 'id'.");
    }

    private GameObject ResolveSelectorV2(MonoObjectSelector selector)
    {
        if (selector == null)
            throw new ArgumentNullException(nameof(selector));
        var matches = Resources.FindObjectsOfTypeAll<GameObject>()
            .Where(gameObject => gameObject != null && gameObject.scene.IsValid())
            .Where(gameObject =>
                string.IsNullOrWhiteSpace(selector.scene) ||
                string.Equals(gameObject.scene.name, selector.scene, StringComparison.OrdinalIgnoreCase))
            .Where(gameObject =>
                string.IsNullOrWhiteSpace(selector.path) ||
                string.Equals(ObjectPathV2(gameObject), selector.path, StringComparison.Ordinal))
            .Where(gameObject =>
                string.IsNullOrWhiteSpace(selector.name) ||
                string.Equals(gameObject.name, selector.name, StringComparison.OrdinalIgnoreCase))
            .Where(gameObject =>
                string.IsNullOrWhiteSpace(selector.component) ||
                HasComponentV2(gameObject, selector.component))
            .Take(2)
            .ToArray();
        if (matches.Length == 0)
            throw new KeyNotFoundException("No GameObject matched the supplied selector.");
        if (matches.Length > 1)
            throw new AmbiguousMatchException(
                "The selector matched more than one GameObject. Add a scene or hierarchy path.");
        return matches[0];
    }

    private bool HasComponentV2(GameObject gameObject, string componentName)
    {
        return gameObject.GetComponents<Component>().Any(component =>
            component != null &&
            (string.Equals(component.GetType().FullName, componentName, StringComparison.Ordinal) ||
             string.Equals(component.GetType().Name, componentName, StringComparison.Ordinal)));
    }

    private static MonoObjectSelector SelectorForV2(GameObject gameObject) =>
        new MonoObjectSelector
        {
            scene = gameObject.scene.name,
            path = ObjectPathV2(gameObject),
            name = gameObject.name
        };

    private static object ToSearchResult(GameObject gameObject) => new
    {
        name = gameObject.name,
        id = gameObject.GetInstanceID(),
        scene = gameObject.scene.name,
        path = ObjectPathV2(gameObject),
        active = gameObject.activeInHierarchy
    };

    private static string ObjectPathV2(GameObject gameObject)
    {
        var segments = new Stack<string>();
        var current = gameObject.transform;
        while (current != null)
        {
            segments.Push(
                $"{Uri.EscapeDataString(current.gameObject.name)}[{current.GetSiblingIndex()}]");
            current = current.parent;
        }
        return "/" + string.Join("/", segments);
    }

    private static object TypeSummaryV2(Type type) => new
    {
        name = type.Name,
        fullName = TypeName(type),
        assembly = type.Assembly.GetName().Name ?? "unknown",
        isComponent = typeof(Component).IsAssignableFrom(type)
    };

    private static IEnumerable<Type> GetLoadableTypesV2(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(type => type != null);
        }
        catch { return Array.Empty<Type>(); }
    }

    private static string FormatMethodV2(MethodInfo method) =>
        $"{TypeName(method.ReturnType)} {method.Name}(" +
        string.Join(", ", method.GetParameters().Select(parameter =>
            $"{TypeName(parameter.ParameterType)} {parameter.Name}")) + ")";

    private static string TypeName(Type type) => type.FullName ?? type.Name;
    private static string SafeString(object value) =>
        value == null ? "null" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null";

    private static string DetectNetworkFrameworkV2(Type type)
    {
        var name = TypeName(type);
        if (name.IndexOf("FishNet.", StringComparison.Ordinal) >= 0) return "FishNet";
        if (name.IndexOf("Mirror.", StringComparison.Ordinal) >= 0) return "Mirror";
        if (name.IndexOf("Unity.Netcode.", StringComparison.Ordinal) >= 0) return "Unity.Netcode";
        if (name.IndexOf("Photon.", StringComparison.Ordinal) >= 0) return "Photon";
        return null;
    }

    private void EnsureWatchCapacityV2()
    {
        if (productivityWatchers.Count >= ProductivityMaxWatchers)
            throw new InvalidOperationException(
                $"The watcher limit of {ProductivityMaxWatchers} has been reached.");
    }

    private string RegistrationIdV2(string requested, string prefix)
    {
        var id = string.IsNullOrWhiteSpace(requested)
            ? $"{prefix}-{Guid.NewGuid():N}"
            : requested.Trim();
        if (id.Length > 128 ||
            id.Any(character =>
                !(char.IsLetterOrDigit(character) || character == '-' ||
                  character == '_' || character == '.')))
            throw new ArgumentException("Invalid registration ID.");
        if (productivityWatchers.ContainsKey(id) || productivityPatches.ContainsKey(id))
            throw new InvalidOperationException($"Registration ID '{id}' already exists.");
        return id;
    }

    private static string Required(HttpListenerRequest request, string name)
    {
        var value = request.QueryString[name];
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"Missing required query parameter '{name}'.");
        return value;
    }

    private static int RequiredInt(HttpListenerRequest request, string name)
    {
        if (!int.TryParse(request.QueryString[name], out var result))
            throw new ArgumentException($"Query parameter '{name}' must be a 32-bit integer.");
        return result;
    }

    private static string Optional(HttpListenerRequest request, string name) =>
        request.QueryString[name] ?? string.Empty;

    private static int OptionalInt(
        HttpListenerRequest request,
        string name,
        int defaultValue,
        int minimum,
        int maximum)
    {
        var value = request.QueryString[name];
        if (value == null)
            return defaultValue;
        if (!int.TryParse(value, out var result) || result < minimum || result > maximum)
            throw new ArgumentException(
                $"Query parameter '{name}' must be an integer from {minimum} to {maximum}.");
        return result;
    }

    private static int? OptionalNullableInt(HttpListenerRequest request, string name)
    {
        var value = request.QueryString[name];
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (!int.TryParse(value, out var result))
            throw new ArgumentException($"Query parameter '{name}' must be a 32-bit integer.");
        return result;
    }

    private static bool OptionalBool(
        HttpListenerRequest request,
        string name,
        bool defaultValue)
    {
        var value = request.QueryString[name];
        if (value == null)
            return defaultValue;
        if (!bool.TryParse(value, out var result))
            throw new ArgumentException($"Query parameter '{name}' must be true or false.");
        return result;
    }

    private static string RequiredTokenV2(JObject value, string name) =>
        value.Value<string>(name)
        ?? throw new ArgumentException($"Batch parameter '{name}' is required.");

    private static int ClampV2(int value, int minimum, int maximum) =>
        value < minimum ? minimum : value > maximum ? maximum : value;

    private static string Json(object value) =>
        JsonConvert.SerializeObject(value, Formatting.None);

    private static string JsonError(string message, string code) =>
        Json(new { error = message, code });

    private sealed class MonoObjectSelector
    {
        public string scene;
        public string path;
        public string component = null;
        public string name;
    }

    private sealed class MonoTypeMember
    {
        public string name;
        public string type;
        public string kind;
        public bool readable;
        public bool writable;
        public bool isStatic;
    }

    private sealed class MonoMemberValue
    {
        public string name;
        public string type;
        public string value;
        public bool writable;
        public string kind;
    }

    private sealed class MonoWatchRegistration
    {
        public string id;
        public string kind;
        public MonoObjectSelector selector;
        public string component;
        public string member;
        public int intervalMs;
        public bool active;
        public int? instanceId;
    }

    private sealed class MonoWatchState
    {
        public MonoWatchRegistration Registration;
        public string LastValue;
        public float NextPollAt;
        public bool TargetLostReported;
    }

    private sealed class MonoPatchInfo
    {
        public string id;
        public string kind;
        public string target;
        public string patchType;
        public bool active;
    }

    private sealed class MonoPatchState
    {
        public MonoPatchInfo Info;
        public MethodBase Original;
        public MethodInfo PatchMethod;
        public string LegacyId;
        public string Fingerprint;
    }
}

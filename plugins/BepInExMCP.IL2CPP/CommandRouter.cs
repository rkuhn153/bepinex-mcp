using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;
using BepInEx.Logging;

namespace BepInExMCP.IL2CPP;

internal sealed class CommandRouter
{
    private const int DefaultValueLimit = 16 * 1024;
    private readonly Il2CppGameBackend backend;
    private readonly PatchService patches;
    private readonly WatcherService watchers;
    private readonly ManualLogSource log;
    private readonly TimeSpan requestTimeout;

    internal CommandRouter(
        Il2CppGameBackend backend,
        PatchService patches,
        WatcherService watchers,
        ManualLogSource log,
        TimeSpan requestTimeout)
    {
        this.backend = backend;
        this.patches = patches;
        this.watchers = watchers;
        this.log = log;
        this.requestTimeout = requestTimeout;
    }

    internal async Task<ApiResponse> HandleAsync(
        HttpListenerRequest request,
        string? requestBody,
        CancellationToken serverCancellationToken)
    {
        var command = GetCommand(request);
        var isGet = string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase);
        var isBatchPost =
            string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase) &&
            command == "batch";
        var isInjectPost =
            string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase) &&
            command == "mod:inject_class";
        if (!isGet && !isBatchPost && !isInjectPost)
        {
            return ApiResponse.Failure(
                405,
                "Protocol 2.0 accepts GET commands, POST /mcp/batch, and POST /mcp/mod:inject_class.",
                "method_not_allowed");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            serverCancellationToken);
        timeout.CancelAfter(requestTimeout);

        try
        {
            object result = command switch
            {
                "batch" when isBatchPost => await OnMainThread(
                    () => ExecuteBatch(ParseBatch(requestBody)),
                    timeout.Token),
                "system/capabilities" => await OnMainThread(
                    backend.GetCapabilities,
                    timeout.Token),
                "system/paths" => await OnMainThread(
                    () => {
                        string gameDirectory = "";
                        try
                        {
                            gameDirectory = System.IO.Directory.GetParent(UnityEngine.Application.dataPath).FullName;
                        }
                        catch (Exception)
                        {
                            gameDirectory = UnityEngine.Application.dataPath;
                        }

                        string assemblyPath = "";
                        try
                        {
                            string gameAssemblyPath = System.IO.Path.Combine(gameDirectory, "GameAssembly.dll");
                            if (System.IO.File.Exists(gameAssemblyPath))
                            {
                                assemblyPath = gameAssemblyPath;
                            }
                        }
                        catch (Exception) {}

                        return new {
                            gameDirectory = gameDirectory,
                            assemblyPath = assemblyPath
                        };
                    },
                    timeout.Token),
                "system/ping" => new
                {
                    status = "ok",
                    protocolVersion = Protocol.Version,
                    timestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                },
                "scene/list_root_gameobjects" => await OnMainThread(
                    backend.ListRootGameObjects,
                    timeout.Token),
                "scene/search_gameobjects" => await OnMainThread(
                    () => backend.SearchGameObjects(
                        GetOptional(request, "name", string.Empty),
                        GetOptional(request, "componentName", string.Empty),
                        GetOptional(request, "tag", string.Empty),
                        GetOptional(request, "scene", string.Empty),
                        GetOptionalBool(request, "includeInactive", true),
                        GetOptionalInt(request, "limit", 100, 1, 1_000)),
                    timeout.Token),
                "scene/hierarchy_snapshot" => await OnMainThread(
                    () => backend.GetHierarchySnapshot(
                        GetOptionalNullableInt(request, "id"),
                        GetOptionalInt(request, "depth", 3, 0, 16),
                        GetOptionalInt(
                            request,
                            "maxNodes",
                            500,
                            1,
                            Protocol.MaxSnapshotNodes)),
                    timeout.Token),
                "scene/resolve_selector" => await OnMainThread(
                    () => backend.ResolveSelector(GetSelector(request)),
                    timeout.Token),
                "gameobject/list_children" => await OnMainThread(
                    () => backend.ListChildren(GetId(request)),
                    timeout.Token),
                "gameobject/inspect_components" => await OnMainThread(
                    () => backend.InspectComponents(GetId(request)),
                    timeout.Token),
                "component/get_details" => await OnMainThread(
                    () => backend.GetComponentDetails(
                        GetId(request),
                        GetRequired(request, "componentName")),
                    timeout.Token),
                "component/get_member" => await OnMainThread(
                    () => backend.GetComponentMember(
                        GetId(request),
                        GetRequired(request, "componentName"),
                        GetRequired(request, "memberName")),
                    timeout.Token),
                "component/set_value" => await OnMainThread(
                    () => backend.SetComponentValue(
                        GetId(request),
                        GetRequired(request, "componentName"),
                        GetRequired(request, "memberName"),
                        GetRequired(request, "value", allowEmpty: true)),
                    timeout.Token),
                "component/call_method" => await OnMainThread(
                    () => backend.CallComponentMethod(
                        GetId(request),
                        GetRequired(request, "componentName"),
                        GetRequired(request, "methodName"),
                        GetOptional(request, "args", "[]", DefaultValueLimit)),
                    timeout.Token),
                "component/list_methods" => await OnMainThread(
                    () => backend.ListComponentMethods(
                        GetId(request),
                        GetRequired(request, "componentName")),
                    timeout.Token),
                "scene:find_objects_with_component" => await OnMainThread(
                    () => backend.FindObjectsWithComponent(
                        GetRequired(request, "componentName")),
                    timeout.Token),
                "type/search" => await OnMainThread(
                    () => backend.SearchTypes(
                        GetOptional(request, "query", string.Empty),
                        GetOptionalInt(request, "offset", 0, 0, int.MaxValue),
                        GetOptionalInt(request, "limit", 100, 1, 500)),
                    timeout.Token),
                "type/describe" => await OnMainThread(
                    () => backend.DescribeType(
                        GetRequired(request, "typeName"),
                        GetOptionalInt(request, "offset", 0, 0, int.MaxValue),
                        GetOptionalInt(request, "limit", 200, 1, 500)),
                    timeout.Token),
                "network/diagnostics" => await OnMainThread(
                    () => backend.GetNetworkDiagnostics(GetId(request)),
                    timeout.Token),
                "watch/member" => await OnMainThread(
                    () => watchers.CreateMemberWatch(
                        GetOptional(request, "registrationId", string.Empty, 128),
                        GetSelector(request),
                        GetRequired(request, "componentName"),
                        GetRequired(request, "memberName"),
                        GetOptionalInt(request, "intervalMs", 500, 100, 60_000)),
                    timeout.Token),
                "watch/scene" => await OnMainThread(
                    () => watchers.CreateSceneWatch(
                        GetOptional(request, "registrationId", string.Empty, 128),
                        GetOptionalInt(request, "intervalMs", 500, 100, 60_000)),
                    timeout.Token),
                "watch/list" => await OnMainThread(watchers.List, timeout.Token),
                "watch/remove" => await OnMainThread(
                    () => watchers.Remove(GetRequired(request, "registrationId", maxLength: 128)),
                    timeout.Token),
                "mod:subscribe_to_method" => await OnMainThread(
                    () => patches.Subscribe(
                        GetId(request),
                        GetRequired(request, "componentName"),
                        GetRequired(request, "methodName"),
                        GetOptional(request, "registrationId", string.Empty, 128)),
                    timeout.Token),
                "mod:patch_method" => await OnMainThread(
                    () => patches.ApplyDynamicPatch(
                        GetRequired(request, "targetClass"),
                        GetRequired(request, "targetMethod"),
                        GetOptional(request, "parameterTypes", string.Empty),
                        GetOptional(request, "patchType", "prefix", 32),
                        GetRequired(request, "patchCode", maxLength: 48 * 1024),
                        GetOptional(request, "registrationId", string.Empty, 128)),
                    timeout.Token),
                "mod:list_patches" => await OnMainThread(
                    patches.ListRegistrations,
                    timeout.Token),
                 "mod:remove_patch" => await OnMainThread(
                    () => patches.Remove(
                        GetRequired(request, "registrationId", maxLength: 128)),
                    timeout.Token),
                "mod:inject_class" => throw new NotSupportedException("Class injection is not supported on IL2CPP runtimes."),
                _ => throw new UnknownCommandException(command)
            };

            return ApiResponse.Ok(result);
        }
        catch (UnknownCommandException exception)
        {
            return ApiResponse.Failure(404, exception.Message, "unknown_command");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return ApiResponse.Failure(
                504,
                "The Unity main thread did not complete the request before its timeout.",
                "request_timeout");
        }
        catch (Exception exception)
        {
            var unwrapped = Unwrap(exception);
            var (statusCode, code) = Classify(unwrapped);

            if (statusCode >= 500 && unwrapped is not NotSupportedException)
            {
                log.LogError($"Command '{command}' failed: {unwrapped}");
            }
            else
            {
                log.LogWarning($"Command '{command}' rejected: {unwrapped.Message}");
            }

            return ApiResponse.Failure(statusCode, unwrapped.Message, code);
        }
    }

    private static Task<T> OnMainThread<T>(Func<T> action, CancellationToken cancellationToken) =>
        MainThreadQueue.InvokeAsync(action, cancellationToken);

    private static string GetCommand(HttpListenerRequest request)
    {
        const string prefix = "/mcp/";
        var path = request.Url?.AbsolutePath
                   ?? throw new ArgumentException("Request URL is missing.");

        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Request path must begin with '{prefix}'.");
        }

        return path[prefix.Length..].Trim('/');
    }

    private static int GetId(HttpListenerRequest request)
    {
        var raw = GetRequired(request, "id", maxLength: 32);
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            throw new ArgumentException("Query parameter 'id' must be a 32-bit integer.");
        }

        return id;
    }

    private static string GetRequired(
        HttpListenerRequest request,
        string name,
        bool allowEmpty = false,
        int maxLength = DefaultValueLimit)
    {
        var value = request.QueryString[name];
        if (value is null || (!allowEmpty && string.IsNullOrWhiteSpace(value)))
        {
            throw new ArgumentException($"Missing required query parameter '{name}'.");
        }

        if (value.Length > maxLength)
        {
            throw new ArgumentException(
                $"Query parameter '{name}' exceeds the {maxLength}-character limit.");
        }

        return value;
    }

    private static string GetOptional(
        HttpListenerRequest request,
        string name,
        string defaultValue,
        int maxLength = DefaultValueLimit)
    {
        var value = request.QueryString[name];
        if (value is null)
        {
            return defaultValue;
        }

        if (value.Length > maxLength)
        {
            throw new ArgumentException(
                $"Query parameter '{name}' exceeds the {maxLength}-character limit.");
        }

        return value;
    }

    private ObjectSelector GetSelector(HttpListenerRequest request)
    {
        var json = GetOptional(request, "selector", string.Empty);
        if (!string.IsNullOrWhiteSpace(json))
        {
            return JsonSerializer.Deserialize<ObjectSelector>(json, Protocol.JsonOptions)
                   ?? throw new ArgumentException("Selector JSON is invalid.");
        }

        var id = GetOptionalNullableInt(request, "id");
        if (id.HasValue)
        {
            return backend.GetSelector(id.Value);
        }

        throw new ArgumentException("Supply either 'selector' JSON or an 'id'.");
    }

    private static int GetOptionalInt(
        HttpListenerRequest request,
        string name,
        int defaultValue,
        int minimum,
        int maximum)
    {
        var value = request.QueryString[name];
        if (value is null)
        {
            return defaultValue;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ||
            result < minimum ||
            result > maximum)
        {
            throw new ArgumentException(
                $"Query parameter '{name}' must be an integer from {minimum} to {maximum}.");
        }

        return result;
    }

    private static int? GetOptionalNullableInt(HttpListenerRequest request, string name)
    {
        var value = request.QueryString[name];
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            throw new ArgumentException($"Query parameter '{name}' must be a 32-bit integer.");
        }

        return result;
    }

    private static bool GetOptionalBool(
        HttpListenerRequest request,
        string name,
        bool defaultValue)
    {
        var value = request.QueryString[name];
        if (value is null)
        {
            return defaultValue;
        }

        if (!bool.TryParse(value, out var result))
        {
            throw new ArgumentException($"Query parameter '{name}' must be true or false.");
        }

        return result;
    }

    private static BatchRequest ParseBatch(string? requestBody)
    {
        if (string.IsNullOrWhiteSpace(requestBody))
        {
            throw new ArgumentException("A JSON batch body is required.");
        }

        var request = JsonSerializer.Deserialize<BatchRequest>(
            requestBody,
            Protocol.JsonOptions)
            ?? throw new ArgumentException("Batch JSON is invalid.");
        if (request.Operations.Count is < 1 or > Protocol.MaxBatchOperations)
        {
            throw new ArgumentException(
                $"A batch must contain 1 to {Protocol.MaxBatchOperations} operations.");
        }

        return request;
    }

    private BatchResponse ExecuteBatch(BatchRequest request)
    {
        var results = new List<BatchItemResult>(request.Operations.Count);

        foreach (var operation in request.Operations)
        {
            try
            {
                object result = operation.Command switch
                {
                    "scene/list_root_gameobjects" => backend.ListRootGameObjects(),
                    "scene/search_gameobjects" => backend.SearchGameObjects(
                        BatchString(operation, "name", false),
                        BatchString(operation, "componentName", false),
                        BatchString(operation, "tag", false),
                        BatchString(operation, "scene", false),
                        BatchBool(operation, "includeInactive", true),
                        BatchInt(operation, "limit", 100, 1, 1_000)),
                    "scene/hierarchy_snapshot" => backend.GetHierarchySnapshot(
                        BatchNullableInt(operation, "id"),
                        BatchInt(operation, "depth", 3, 0, 16),
                        BatchInt(operation, "maxNodes", 500, 1, Protocol.MaxSnapshotNodes)),
                    "scene/resolve_selector" => backend.ResolveSelector(
                        BatchSelector(operation)),
                    "scene:find_objects_with_component" => backend.FindObjectsWithComponent(
                        BatchString(operation, "componentName")),
                    "gameobject/list_children" => backend.ListChildren(
                        BatchInt(operation, "id")),
                    "gameobject/inspect_components" => backend.InspectComponents(
                        BatchInt(operation, "id")),
                    "component/get_details" => backend.GetComponentDetails(
                        BatchInt(operation, "id"),
                        BatchString(operation, "componentName")),
                    "component/get_member" => backend.GetComponentMember(
                        BatchInt(operation, "id"),
                        BatchString(operation, "componentName"),
                        BatchString(operation, "memberName")),
                    "component/set_value" => backend.SetComponentValue(
                        BatchInt(operation, "id"),
                        BatchString(operation, "componentName"),
                        BatchString(operation, "memberName"),
                        BatchString(operation, "value", false)),
                    "component/call_method" => backend.CallComponentMethod(
                        BatchInt(operation, "id"),
                        BatchString(operation, "componentName"),
                        BatchString(operation, "methodName"),
                        BatchString(operation, "args", false, "[]")),
                    "component/list_methods" => backend.ListComponentMethods(
                        BatchInt(operation, "id"),
                        BatchString(operation, "componentName")),
                    "type/search" => backend.SearchTypes(
                        BatchString(operation, "query", false),
                        BatchInt(operation, "offset", 0, 0, 100_000),
                        BatchInt(operation, "limit", 50, 1, 500)),
                    "type/describe" => backend.DescribeType(
                        BatchString(operation, "typeName"),
                        BatchInt(operation, "offset", 0, 0, 100_000),
                        BatchInt(operation, "limit", 100, 1, 1_000)),
                    "network/diagnostics" => backend.GetNetworkDiagnostics(
                        BatchInt(operation, "id")),
                    "watch/list" => watchers.List(),
                    "mod:list_patches" => patches.ListRegistrations(),
                    _ => throw new NotSupportedException(
                        $"Command '{operation.Command}' is not allowed in a batch.")
                };
                results.Add(new BatchItemResult(operation.Id, true, result));
            }
            catch (Exception exception)
            {
                var unwrapped = Unwrap(exception);
                var (_, code) = Classify(unwrapped);
                results.Add(new BatchItemResult(
                    operation.Id,
                    false,
                    Error: new ErrorResponse(unwrapped.Message, code)));
                if (request.StopOnError)
                {
                    break;
                }
            }
        }

        return new BatchResponse(results);
    }

    private static JsonElement BatchParameter(BatchOperation operation, string name)
    {
        if (operation.Parameters is null ||
            !operation.Parameters.TryGetValue(name, out var value))
        {
            throw new ArgumentException(
                $"Batch operation '{operation.Id}' is missing parameter '{name}'.");
        }

        return value;
    }

    private static string BatchString(
        BatchOperation operation,
        string name,
        bool required = true,
        string defaultValue = "")
    {
        if (operation.Parameters is null ||
            !operation.Parameters.TryGetValue(name, out var value))
        {
            if (!required)
            {
                return defaultValue;
            }

            throw new ArgumentException(
                $"Batch operation '{operation.Id}' is missing parameter '{name}'.");
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.GetRawText();
    }

    private static int BatchInt(
        BatchOperation operation,
        string name,
        int? defaultValue = null,
        int minimum = int.MinValue,
        int maximum = int.MaxValue)
    {
        if (operation.Parameters is null ||
            !operation.Parameters.TryGetValue(name, out var value))
        {
            if (defaultValue.HasValue)
            {
                return defaultValue.Value;
            }

            throw new ArgumentException(
                $"Batch operation '{operation.Id}' is missing parameter '{name}'.");
        }

        int result;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out result) ||
            value.ValueKind == JsonValueKind.String &&
            int.TryParse(
                value.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out result))
        {
            if (result >= minimum && result <= maximum)
            {
                return result;
            }
        }

        throw new ArgumentException(
            $"Batch parameter '{name}' must be an integer from {minimum} to {maximum}.");
    }

    private static int? BatchNullableInt(BatchOperation operation, string name)
    {
        if (operation.Parameters is null ||
            !operation.Parameters.TryGetValue(name, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            if (int.TryParse(
                    text,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out number))
            {
                return number;
            }
        }

        throw new ArgumentException($"Batch parameter '{name}' must be an integer or null.");
    }

    private static bool BatchBool(BatchOperation operation, string name, bool defaultValue)
    {
        if (operation.Parameters is null ||
            !operation.Parameters.TryGetValue(name, out var value))
        {
            return defaultValue;
        }

        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }

        if (value.ValueKind == JsonValueKind.String &&
            bool.TryParse(value.GetString(), out var result))
        {
            return result;
        }

        throw new ArgumentException($"Batch parameter '{name}' must be true or false.");
    }

    private static ObjectSelector BatchSelector(BatchOperation operation)
    {
        var value = BatchParameter(operation, "selector");
        return value.Deserialize<ObjectSelector>(Protocol.JsonOptions)
               ?? throw new ArgumentException("Batch selector is invalid.");
    }

    private static Exception Unwrap(Exception exception)
    {
        while (exception is AggregateException { InnerExceptions.Count: 1 } aggregate)
        {
            exception = aggregate.InnerExceptions[0];
        }

        if (exception is TargetInvocationException { InnerException: not null } target)
        {
            return target.InnerException!;
        }

        return exception;
    }

    private static (int StatusCode, string Code) Classify(Exception exception) =>
        exception switch
        {
            ArgumentException or FormatException or JsonException =>
                (400, "invalid_request"),
            KeyNotFoundException or TypeLoadException or MissingMemberException =>
                (404, "not_found"),
            AmbiguousMatchException =>
                (409, "ambiguous_member"),
            NotSupportedException =>
                (501, "unsupported"),
            InvalidOperationException =>
                (422, "operation_failed"),
            _ =>
                (500, "internal_error")
        };

    private sealed class UnknownCommandException : Exception
    {
        internal UnknownCommandException(string command)
            : base($"Unknown command: {command}")
        {
        }
    }
}

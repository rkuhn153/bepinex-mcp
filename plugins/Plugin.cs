using BepInEx;
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Text;
using System.IO;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine.SceneManagement;
using UnityEngine;
using System.Reflection;
using System.Globalization; // --- NEW: For parsing floats correctly ---
using HarmonyLib;
using Microsoft.CodeAnalysis; // --- NEW: For Roslyn compilation ---
using Microsoft.CodeAnalysis.CSharp; // --- NEW: For C# compilation ---
using Microsoft.CodeAnalysis.Emit; // --- NEW: For compilation results ---
using BepInEx.Configuration;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

[BepInPlugin("com.ryan.mcphelper", "MCP Helper Server", "1.0.0")]
public partial class McpServerMod : BaseUnityPlugin
{
    public static McpServerMod Instance { get; private set; }

    private Task serverTask;
    private CancellationTokenSource cts;
    private Harmony harmony;
    private static readonly HttpClient httpClient = new HttpClient(); // For sending webhooks
    private static BepInEx.Logging.ManualLogSource PatcherLogger;
    private static ConcurrentDictionary<string, MethodInfo> patchedMethods = new ConcurrentDictionary<string, MethodInfo>(); // Keep track of what we've patched
    public static ConfigEntry<string> ConfigListenIP;
    public static ConfigEntry<string> ConfigWebhookIP;
    // --- The "Mailbox" ---
    private static ConcurrentQueue<Tuple<Func<string>, TaskCompletionSource<string>>> mainThreadJobs =
        new ConcurrentQueue<Tuple<Func<string>, TaskCompletionSource<string>>>();
    
    // --- NEW: Store dynamic patch data ---
    private static ConcurrentDictionary<string, Dictionary<string, object>> patchData =
        new ConcurrentDictionary<string, Dictionary<string, object>>();

    // --- Data Structs ---
    private struct SimpleGameObject { public string name; public int id; }
    private struct ComponentMemberInfo { public string type; public string value; }

    // --- BepInEx Main Thread ---
    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            // A copy already exists! Log it and destroy this duplicate.
            Logger.LogWarning("Duplicate instance of McpServerMod detected. Destroying this one.");
            Destroy(this.gameObject);
            return; // Stop running the rest of Awake()
        }

        // This is the first and only copy.
        // Set it as the main 'Instance'.
        Instance = this;
        ConfigListenIP = Config.Bind("Network", "ListenIP", "localhost", "Use '*' to allow connections from other computers.");
        ConfigWebhookIP = Config.Bind("Network", "WebhookIP", "127.0.0.1", "The IP address of the main PC running the AI App.");
        // 1. This is the "Don't destroy on scene load" fix
        DontDestroyOnLoad(this.gameObject);
        this.gameObject.hideFlags = HideFlags.HideAndDontSave;

        harmony = new Harmony("com.ryan.mcphelper.patcher");
        InitializeProductivity();
        PatcherLogger = BepInEx.Logging.Logger.CreateLogSource("MCP_Patcher");
        Logger.LogInfo("Harmony instance created.");
        Logger.LogInfo("Starting MCP Server Task...");

        // 2. Create the "kill switch"
        cts = new CancellationTokenSource();

        // 3. Start the server on a background Task, not a Thread
        //    We pass the "kill switch" (Token) to it.
        serverTask = Task.Run(() => StartHttpServer(cts.Token), cts.Token);

        Logger.LogInfo("MCP Server Task is running.");
    }

    // --- BepInEx Main Thread ---
    void Update()
    {
        while (mainThreadJobs.TryDequeue(out var job))
        {
            try
            {
                string result = job.Item1();
                job.Item2.TrySetResult(result);
            }
            catch (Exception e)
            {
                job.Item2.TrySetException(e);
            }
        }
        TickProductivity();
    }
    // --- Worker Task (No longer a Thread) ---
    private async Task StartHttpServer(CancellationToken token)
    {
        HttpListener listener = new HttpListener();
        listener.Prefixes.Add($"http://{ConfigListenIP.Value}:8080/mcp/");

        try
        {
            listener.Start();
            Logger.LogInfo($"Listening for MCP requests on http://{ConfigListenIP.Value}:8080/mcp/");

            while (listener.IsListening)
            {
                // Check if our "kill switch" has been flipped
                if (token.IsCancellationRequested)
                {
                    Logger.LogInfo("Cancellation requested. Shutting down server loop.");
                    listener.Stop();
                    break; // Exit the loop
                }

                try
                {
                    // Asynchronously wait for a request
                    HttpListenerContext context = await listener.GetContextAsync();

                    // Handle it on a *new* task so we don't block the listener
                    _ = Task.Run(() => HandleMcpRequest(context), token);
                }
                catch (HttpListenerException ex) when (ex.ErrorCode == 995 || ex.NativeErrorCode == 995)
                {
                    // Error 995 is "Operation Aborted", which happens on shutdown.
                    // This is expected. We just exit the loop.
                    Logger.LogInfo("Server loop interrupted (expected shutdown).");
                    break;
                }
                catch (ObjectDisposedException)
                {
                    // This can also happen on shutdown.
                    Logger.LogInfo("Server loop interrupted (listener disposed).");
                    break;
                }
            }
        }
        catch (Exception e)
        {
            // This catches errors from listener.Start() or other major failures
            Logger.LogError($"Fatal server error: {e}");
        }
        finally
        {
            // Ensure the listener is stopped no matter what
            if (listener.IsListening)
            {
                listener.Stop();
            }
            listener.Close();
            Logger.LogInfo("MCP Server has stopped.");
        }
    }
    // --- Worker Thread ---
    private async Task HandleMcpRequest(HttpListenerContext context)
    {
        HttpListenerRequest request = context.Request;
        string responseString = "";
        int statusCode = 200;

        try
        {
            string command = request.Url.AbsolutePath.Replace("/mcp/", "");
            Logger.LogInfo($"Received command: {command}");
            string requestBody = null;
            if (request.HasEntityBody)
            {
                if (request.ContentLength64 > ProductivityMaxRequestBodyBytes)
                {
                    statusCode = 413;
                    responseString = JsonError(
                        $"Request body exceeds the {ProductivityMaxRequestBodyBytes}-byte limit.",
                        "request_too_large");
                    goto SendResponse;
                }

                using (var reader = new StreamReader(
                           request.InputStream,
                           request.ContentEncoding ?? Encoding.UTF8))
                {
                    requestBody = await reader.ReadToEndAsync();
                }
            }

            // --- THE FINAL ROUTER (v5) ---
            if (TryCreateProductivityJob(
                    command,
                    request,
                    requestBody,
                    out var productivityJob,
                    out statusCode,
                    out responseString))
            {
                if (productivityJob != null)
                {
                    var productivityTcs = new TaskCompletionSource<string>();
                    mainThreadJobs.Enqueue(Tuple.Create(productivityJob, productivityTcs));
                    responseString = await productivityTcs.Task;
                }
            }
            else switch (command)
            {
                case "system/capabilities":
                    {
                        var tcs = new TaskCompletionSource<string>();
                        mainThreadJobs.Enqueue(Tuple.Create(GetCapabilities(), tcs));
                        responseString = await tcs.Task;
                        break;
                    }
                case "system/paths":
                    {
                        var tcs = new TaskCompletionSource<string>();
                        mainThreadJobs.Enqueue(Tuple.Create(new Func<string>(() => {
                            string gameDirectory = "";
                            try
                            {
                                gameDirectory = Directory.GetParent(Application.dataPath).FullName;
                            }
                            catch (Exception ex)
                            {
                                gameDirectory = Application.dataPath;
                            }
                            
                            string assemblyPath = "";
                            try
                            {
                                var csharpAssembly = AppDomain.CurrentDomain.GetAssemblies()
                                    .FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp" || a.GetName().Name == "Assembly-UnityScript");
                                    
                                if (csharpAssembly != null && !string.IsNullOrEmpty(csharpAssembly.Location))
                                {
                                    assemblyPath = csharpAssembly.Location;
                                }
                                else
                                {
                                    string managedPath = Path.Combine(Application.dataPath, "Managed");
                                    string dllPath = Path.Combine(managedPath, "Assembly-CSharp.dll");
                                    if (File.Exists(dllPath))
                                    {
                                        assemblyPath = dllPath;
                                    }
                                    else
                                    {
                                        dllPath = Path.Combine(managedPath, "Assembly-UnityScript.dll");
                                        if (File.Exists(dllPath))
                                        {
                                            assemblyPath = dllPath;
                                        }
                                    }
                                }
                            }
                            catch (Exception) {}
                            
                            return JsonConvert.SerializeObject(new {
                                gameDirectory = gameDirectory,
                                assemblyPath = assemblyPath
                            });
                        }), tcs));
                        responseString = await tcs.Task;
                        break;
                    }
                case "scene/list_root_gameobjects":
                    {
                        var tcs = new TaskCompletionSource<string>();
                        mainThreadJobs.Enqueue(Tuple.Create(ListRootGameObjects(), tcs));
                        responseString = await tcs.Task;
                        break;
                    }

                case "gameobject/list_children":
                    {
                        if (!int.TryParse(request.QueryString["id"], out int parentId))
                        {
                            statusCode = 400; responseString = "{\"error\": \"Missing or invalid 'id' parameter.\"}"; break;
                        }
                        var tcs = new TaskCompletionSource<string>();
                        mainThreadJobs.Enqueue(Tuple.Create(ListChildren(parentId), tcs));
                        responseString = await tcs.Task;
                        break;
                    }

                case "gameobject/inspect_components":
                    {
                        if (!int.TryParse(request.QueryString["id"], out int gameObjectId))
                        {
                            statusCode = 400; responseString = "{\"error\": \"Missing or invalid 'id' parameter.\"}"; break;
                        }
                        var tcs = new TaskCompletionSource<string>();
                        mainThreadJobs.Enqueue(Tuple.Create(InspectGameObject(gameObjectId), tcs));
                        responseString = await tcs.Task;
                        break;
                    }

                case "component/get_details":
                    {
                        if (!int.TryParse(request.QueryString["id"], out int gameObjectId))
                        {
                            statusCode = 400; responseString = "{\"error\": \"Missing or invalid 'id' parameter.\"}"; break;
                        }
                        string componentName = request.QueryString["componentName"];
                        if (string.IsNullOrEmpty(componentName))
                        {
                            statusCode = 400; responseString = "{\"error\": \"Missing 'componentName' parameter\"}"; break;
                        }
                        var tcs = new TaskCompletionSource<string>();
                        mainThreadJobs.Enqueue(Tuple.Create(InspectComponentDetails(gameObjectId, componentName), tcs));
                        responseString = await tcs.Task;
                        break;
                    }

                // --- NEW COMMAND 5: component/set_value ---
                case "component/set_value":
                    {
                        if (!int.TryParse(request.QueryString["id"], out int gameObjectId))
                        {
                            statusCode = 400; responseString = "{\"error\": \"Missing or invalid 'id' parameter.\"}"; break;
                        }
                        string componentName = request.QueryString["componentName"];
                        string memberName = request.QueryString["memberName"];
                        string value = request.QueryString["value"]; // The new value, always a string

                        if (string.IsNullOrEmpty(componentName) || string.IsNullOrEmpty(memberName) || value == null)
                        {
                            statusCode = 400;
                            responseString = "{\"error\": \"Missing 'componentName', 'memberName', or 'value' parameter\"}";
                            break;
                        }

                        var tcs = new TaskCompletionSource<string>();
                        mainThreadJobs.Enqueue(Tuple.Create(SetComponentValue(gameObjectId, componentName, memberName, value), tcs));
                        responseString = await tcs.Task;
                        break;
                    }
                case "component/call_method":
                    {
                        if (!int.TryParse(request.QueryString["id"], out int gameObjectId))
                        {
                            statusCode = 400; responseString = "{\"error\": \"Missing or invalid 'id' parameter.\"}"; break;
                        }
                        string componentName = request.QueryString["componentName"];
                        string methodName = request.QueryString["methodName"];
                        string argsString = request.QueryString["args"] ?? "[]"; // e.g., "[\"5\", \"true\"]"

                        if (string.IsNullOrEmpty(componentName) || string.IsNullOrEmpty(methodName))
                        {
                            statusCode = 400;
                            responseString = "{\"error\": \"Missing 'componentName' or 'methodName' parameter\"}";
                            break;
                        }

                        var tcs = new TaskCompletionSource<string>();
                        mainThreadJobs.Enqueue(Tuple.Create(CallComponentMethod(gameObjectId, componentName, methodName, argsString), tcs));
                        responseString = await tcs.Task;
                        break;
                    }
                case "component/list_methods":
                    {
                        if (!int.TryParse(request.QueryString["id"], out int gameObjectId))
                        {
                            statusCode = 400; responseString = "{\"error\": \"Missing or invalid 'id' parameter.\"}"; break;
                        }
                        string componentName = request.QueryString["componentName"];
                        if (string.IsNullOrEmpty(componentName))
                        {
                            statusCode = 400; responseString = "{\"error\": \"Missing 'componentName' parameter\"}"; break;
                        }

                        var tcs = new TaskCompletionSource<string>();
                        mainThreadJobs.Enqueue(Tuple.Create(ListComponentMethods(gameObjectId, componentName), tcs));
                        responseString = await tcs.Task;
                        break;
                    }
                case "scene:find_objects_with_component":
                    {
                        string componentName = request.QueryString["componentName"];
                        if (string.IsNullOrEmpty(componentName))
                        {
                            statusCode = 400; responseString = "{\"error\": \"Missing 'componentName' parameter\"}"; break;
                        }

                        var tcs = new TaskCompletionSource<string>();
                        mainThreadJobs.Enqueue(Tuple.Create(FindObjectsWithComponent(componentName), tcs));
                        responseString = await tcs.Task;
                        break;
                    }
                case "mod:subscribe_to_method":
                    {
                        if (!int.TryParse(request.QueryString["id"], out int gameObjectId))
                        {
                            statusCode = 400; responseString = "{\"error\": \"Missing or invalid 'id' parameter.\"}"; break;
                        }
                        string componentName = request.QueryString["componentName"];
                        string methodName = request.QueryString["methodName"];
                        string registrationId = request.QueryString["registrationId"];

                        if (string.IsNullOrEmpty(componentName) || string.IsNullOrEmpty(methodName))
                        {
                            statusCode = 400;
                            responseString = "{\"error\": \"Missing 'componentName' or 'methodName' parameter\"}";
                            break;
                        }

                        var tcs = new TaskCompletionSource<string>();
                        mainThreadJobs.Enqueue(Tuple.Create(SubscribeToMethod(gameObjectId, componentName, methodName, registrationId), tcs));
                        responseString = await tcs.Task;
                        break;
                    }
                case "mod:patch_method":
                    {
                        string targetClass = request.QueryString["targetClass"];
                        string targetMethod = request.QueryString["targetMethod"];
                        string parameterTypes = request.QueryString["parameterTypes"] ?? "";
                        string patchType = request.QueryString["patchType"] ?? "prefix";
                        string patchCode = request.QueryString["patchCode"] ?? "";
                        string registrationId = request.QueryString["registrationId"];

                        if (string.IsNullOrEmpty(targetClass) || string.IsNullOrEmpty(targetMethod))
                        {
                            statusCode = 400;
                            responseString = "{\"error\": \"Missing 'targetClass' or 'targetMethod' parameter\"}";
                            break;
                        }

                        if (string.IsNullOrEmpty(patchCode))
                        {
                            statusCode = 400;
                            responseString = "{\"error\": \"Missing 'patchCode' parameter\"}";
                            break;
                        }

                        var tcs = new TaskCompletionSource<string>();
                        mainThreadJobs.Enqueue(Tuple.Create(PatchMethod(targetClass, targetMethod, parameterTypes, patchType, patchCode, registrationId), tcs));
                        responseString = await tcs.Task;

                        break;
                    }
                case "mod:inject_class":
                    {
                        if (!string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                        {
                            statusCode = 405;
                            responseString = "{\"error\": \"mod:inject_class requires HTTP POST.\"}";
                            break;
                        }

                        string classCode = "";
                        string attachToGameObjectIdStr = "";
                        try
                        {
                            var json = Newtonsoft.Json.Linq.JObject.Parse(requestBody);
                            classCode = json.Value<string>("classCode") ?? "";
                            attachToGameObjectIdStr = json.Value<string>("attachToGameObjectId") ?? "";
                        }
                        catch (Exception ex)
                        {
                            statusCode = 400;
                            responseString = $"{{\"error\": \"Invalid JSON body: {ex.Message}\"}}";
                            break;
                        }

                        if (string.IsNullOrEmpty(classCode))
                        {
                            statusCode = 400;
                            responseString = "{\"error\": \"Missing 'classCode' in JSON body.\"}";
                            break;
                        }

                        var tcs = new TaskCompletionSource<string>();
                        mainThreadJobs.Enqueue(Tuple.Create(InjectClass(classCode, attachToGameObjectIdStr), tcs));
                        responseString = await tcs.Task;
                        break;
                    }


                // --- DEFAULT: Unknown command ---
                default:
                    statusCode = 404;
                    responseString = $"{{\"error\": \"Unknown command: {command}\"}}";
                    break;
            }

        }
        catch (Exception e)
        {
            Logger.LogError($"Error processing request: {e}");
            responseString = $"{{\"error\": \"{e.Message}\"}}";
            statusCode = 500;
        }

        // --- Send the response back to the client ---
        SendResponse:
        byte[] buffer = Encoding.UTF8.GetBytes(responseString);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = buffer.Length;
        Stream output = context.Response.OutputStream;
        await output.WriteAsync(buffer, 0, buffer.Length);
        output.Close();
        context.Response.Close();
    }

    void OnDestroy()
    {
        Logger.LogInfo("Stopping MCP Server...");

        // 1. "Flip the kill switch"
        if (cts != null && !cts.IsCancellationRequested)
        {
            cts.Cancel();
            Logger.LogInfo("Cancellation token triggered.");
        }

        // 2. Wait for the server task to *actually* finish (optional, but good practice)
        try
        {
            // Give it 1 second to shut down gracefully
            serverTask?.Wait(1000);
        }
        catch (Exception e)
        {
            Logger.LogWarning($"Exception while waiting for server task to stop: {e.Message}");
        }
        if (Instance == this)
        {
            Instance = null;
        }

        ShutdownProductivity();
        harmony?.UnpatchSelf();

        Logger.LogInfo("MCP Server is fully stopped.");
    }

    // ====================================================================
    // --- MAIN THREAD JOBS & HELPERS ---
    // ====================================================================

    private Func<string> GetCapabilities()
    {
        return () =>
        {
            Assembly bepinexAssembly = typeof(BaseUnityPlugin).Assembly;
            string bepinexVersion =
                bepinexAssembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion
                ?? bepinexAssembly.GetName().Version?.ToString()
                ?? "unknown";
            string architecture =
                RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

            return Json(new
            {
                protocolVersion = ProductivityProtocolVersion,
                runtime = "mono",
                unityVersion = Application.unityVersion,
                bepInExVersion = bepinexVersion,
                architecture,
                tools = new[]
                {
                    "system/capabilities", "system/ping", "system/paths",
                    "scene/list_root_gameobjects", "scene/search_gameobjects",
                    "scene/hierarchy_snapshot", "scene/resolve_selector",
                    "gameobject/list_children", "gameobject/inspect_components",
                    "component/get_details", "component/get_member",
                    "component/set_value", "component/call_method",
                    "component/list_methods", "scene:find_objects_with_component",
                    "type/search", "type/describe", "network/diagnostics",
                    "watch/member", "watch/scene", "watch/list", "watch/remove",
                    "mod:subscribe_to_method", "mod:patch_method", "mod:inject_class",
                    "mod:list_patches", "mod:remove_patch", "batch", "events/webhook"
                },
                patchTypes = new[] { "prefix", "postfix", "transpiler", "finalizer" },
                limitations = new[] { "Mono reflection metadata varies by game build" },
                features = new[]
                {
                    "stableSelectors", "hierarchySnapshots", "targetedMemberReads",
                    "typeDiscovery", "batchPost", "memberWatchers", "sceneWatchers",
                    "patchLifecycle", "networkDiagnostics"
                },
                limits = new
                {
                    maxBatchOperations = ProductivityMaxBatchOperations,
                    maxRequestBodyBytes = ProductivityMaxRequestBodyBytes,
                    maxWatchers = ProductivityMaxWatchers,
                    maxPatchRegistrations = ProductivityMaxPatchRegistrations,
                    maxSnapshotNodes = ProductivityMaxSnapshotNodes
                }
            });
        };
    }

    // --- CORE HELPER: Find any GameObject by its InstanceID ---
    private GameObject FindObjectById(int instanceId)
    {
        return Resources.FindObjectsOfTypeAll<GameObject>()
                        .FirstOrDefault(obj => obj.GetInstanceID() == instanceId);
    }
    // --- NEW CORE HELPER: Find a Type in any loaded assembly ---
    private Type FindType(string fullTypeName)
    {
        // First try the easy way
        Type type = Type.GetType(fullTypeName);
        if (type != null)
        {
            return type;
        }

        // If that fails, search all loaded assemblies
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            type = assembly.GetType(fullTypeName);
            if (type != null)
            {
                return type;
            }
        }

        // For types like "StartScreen" that aren't assembly-qualified
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            type = assembly.GetTypes().FirstOrDefault(t => t.Name == fullTypeName);
            if (type != null)
            {
                return type;
            }
        }

        return null; // Not found
    }
    // --- NEW HELPER: The "Smart Converter" ---
    private object ConvertValue(string value, Type targetType)
    {
        string typeName = targetType.Name;
        Logger.LogInfo($"Attempting to convert string \"{value}\" to type {typeName}");

        // Using InvariantCulture to handle floats like "3.14" instead of "3,14"
        var culture = CultureInfo.InvariantCulture;

        switch (typeName)
        {
            case "String":
                return value;
            case "Int32":
                return int.Parse(value, culture);
            case "Single": // This is Unity's 'float'
                return float.Parse(value, culture);
            case "Boolean":
                return bool.Parse(value);
            case "Vector3":
                try
                {
                    // 1. Remove ( and )
                    string trimmedValue = value.Trim('(', ')');

                    // 2. --- THIS IS THE FIX ---
                    //    Check for the AI's new semicolon format first.
                    //    If it's not there, fall back to the old comma format.
                    string[] parts;
                    if (trimmedValue.Contains(";"))
                    {
                        // New, safe format for method calls: "10000;500;-10000"
                        parts = trimmedValue.Split(';');
                    }
                    else
                    {
                        // Old format for set_value: "10000, 500, -10000"
                        parts = trimmedValue.Split(',');
                    }
                    // --- END FIX ---

                    if (parts.Length != 3)
                        throw new ArgumentException($"Invalid Vector3 format. Expected 3 parts, got {parts.Length}.");

                    // 3. Parse each part as a float
                    float x = float.Parse(parts[0], culture);
                    float y = float.Parse(parts[1], culture);
                    float z = float.Parse(parts[2], culture);

                    // 4. Create the new Vector3
                    return new Vector3(x, y, z);
                }
                catch (Exception e)
                {
                    throw new ArgumentException($"Failed to parse Vector3 string \"{value}\". Error: {e.Message}");
                }
            // --- TODO: Add more types here as needed (Vector3, Color, etc.) ---
            default:
                throw new ArgumentException($"Don't know how to convert string to type {typeName}");
        }
    }
    // --- NEW HELPER: Simple JSON String Array Parser ---
    private List<string> ParseStringArray(string jsonArray)
    {
        var list = new List<string>();
        string trimmed = jsonArray.Trim('[', ']'); // "arg1", "arg2"
        if (string.IsNullOrEmpty(trimmed))
            return list;

        // This is a simple parser. It splits by comma
        // and trims quotes and whitespace.
        // It will fail if a string arg contains a comma.
        string[] items = trimmed.Split(',');
        foreach (string item in items)
        {
            list.Add(item.Trim().Trim('\"')); // "arg1" -> arg1
        }
        return list;
    }

    // --- JSON Helpers ---
    private string BuildJsonArray(IEnumerable<string> items)
    {
        var escapedItems = items.Select(s => s.Replace("\"", "\\\""));
        return "[\"" + string.Join("\",\"", escapedItems) + "\"]";
    }
    private string BuildJsonObjectArray(IEnumerable<SimpleGameObject> items)
    {
        var entries = items.Select(item =>
            $"{{\"name\":\"{item.name.Replace("\"", "\\\"")}\",\"id\":{item.id}}}"
        );
        return "[" + string.Join(",", entries) + "]";
    }
    private string BuildJsonObject(Dictionary<string, ComponentMemberInfo> items)
    {
        var entries = items.Select(pair =>
        {
            string key = $"\"{pair.Key.Replace("\"", "\\\"")}\"";
            string val = $"{{\"type\":\"{pair.Value.type}\",\"value\":\"{pair.Value.value?.Replace("\"", "\\\"") ?? "null"}\"}}";
            return $"{key}:{val}";
        });
        return "{" + string.Join(",", entries) + "}";
    }

    // --- MAIN THREAD JOB 1 (Read) ---
    private Func<string> ListRootGameObjects()
    {
        return () =>
        {
            var scene = SceneManager.GetActiveScene();
            var objectList = scene.GetRootGameObjects()
                .Select(obj => new SimpleGameObject { name = obj.name, id = obj.GetInstanceID() })
                .ToList();
            return BuildJsonObjectArray(objectList);
        };
    }

    // --- MAIN THREAD JOB 2 (Read) ---
    private Func<string> ListChildren(int parentId)
    {
        return () =>
        {
            GameObject parent = FindObjectById(parentId);
            if (parent == null) throw new Exception($"GameObject with ID {parentId} not found.");
            var childrenList = new List<SimpleGameObject>();
            foreach (Transform childTransform in parent.transform)
            {
                childrenList.Add(new SimpleGameObject
                {
                    name = childTransform.gameObject.name,
                    id = childTransform.gameObject.GetInstanceID()
                });
            }
            return BuildJsonObjectArray(childrenList);
        };
    }

    // --- MAIN THREAD JOB 3 (Read) ---
    private Func<string> InspectGameObject(int gameObjectId)
    {
        return () =>
        {
            GameObject obj = FindObjectById(gameObjectId);
            if (obj == null) throw new Exception($"GameObject with ID {gameObjectId} not found.");
            var componentNames = obj.GetComponents<Component>()
                .Select(c => c.GetType().ToString())
                .ToList();
            return BuildJsonArray(componentNames);
        };
    }

    // --- MAIN THREAD JOB 4 (Read) ---
    private Func<string> InspectComponentDetails(int gameObjectId, string componentName)
    {
        return () =>
        {
            GameObject obj = FindObjectById(gameObjectId);
            if (obj == null) throw new Exception($"GameObject with ID {gameObjectId} not found.");
            Type componentType = FindType(componentName);
            if (componentType == null) throw new Exception($"Type '{componentName}' not found in any loaded assembly.");
            Component component = obj.GetComponent(componentType);
            if (component == null) throw new Exception($"Component '{componentName}' not found on GameObject with ID {gameObjectId}.");

            var details = new Dictionary<string, ComponentMemberInfo>();
            //var componentType = component.GetType();
            var bindingFlags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic;

            foreach (var field in componentType.GetFields(bindingFlags))
            {
                try
                {
                    object value = field.GetValue(component);
                    details[field.Name] = new ComponentMemberInfo { type = field.FieldType.ToString(), value = value?.ToString() ?? "null" };
                }
                catch { /* Ignore read errors */ }
            }
            foreach (var prop in componentType.GetProperties(bindingFlags))
            {
                if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                try
                {
                    object value = prop.GetValue(component, null);
                    details[prop.Name] = new ComponentMemberInfo { type = prop.PropertyType.ToString(), value = value?.ToString() ?? "null" };
                }
                catch { /* Ignore read errors */ }
            }
            return BuildJsonObject(details);
        };
    }

    // --- NEW MAIN THREAD JOB 5 (Write) ---
    private Func<string> SetComponentValue(int gameObjectId, string componentName, string memberName, string value)
    {
        return () =>
        {
            Logger.LogInfo($"Running job: SetComponentValue(id={gameObjectId}, comp={componentName}, member={memberName}, value={value})");
            GameObject obj = FindObjectById(gameObjectId);
            if (obj == null) throw new Exception($"GameObject with ID {gameObjectId} not found.");
            Type componentType = FindType(componentName);
            if (componentType == null) throw new Exception($"Type '{componentName}' not found in any loaded assembly.");
            Component component = obj.GetComponent(componentType);
            if (component == null) throw new Exception($"Component '{componentName}' not found on GameObject with ID {gameObjectId}.");

            // var componentType = component.GetType();
            var bindingFlags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase | BindingFlags.NonPublic;

            // --- Try to find a FIELD first ---
            FieldInfo field = componentType.GetField(memberName, bindingFlags);
            if (field != null)
            {
                object convertedValue = ConvertValue(value, field.FieldType);
                field.SetValue(component, convertedValue);
                Logger.LogInfo($"Successfully set FIELD '{memberName}' to '{value}'");
                return $"{{\"status\":\"ok\", \"message\":\"Set FIELD '{memberName}' to '{value}'\"}}";
            }

            // --- If no field, try to find a PROPERTY ---
            PropertyInfo prop = componentType.GetProperty(memberName, bindingFlags);
            if (prop != null)
            {
                if (!prop.CanWrite)
                    throw new Exception($"Property '{memberName}' is read-only.");

                object convertedValue = ConvertValue(value, prop.PropertyType);
                prop.SetValue(component, convertedValue, null);
                Logger.LogInfo($"Successfully set PROPERTY '{memberName}' to '{value}'");
                return $"{{\"status\":\"ok\", \"message\":\"Set PROPERTY '{memberName}' to '{value}'\"}}";
            }

            // --- If nothing is found ---
            throw new Exception($"Member '{memberName}' not found on component '{componentName}'");
        };
    }

    private Func<string> CallComponentMethod(int gameObjectId, string componentName, string methodName, string argsString)
    {
        return () =>
        {
            Logger.LogInfo($"Running job: CallComponentMethod(id={gameObjectId}, comp={componentName}, method={methodName}, args={argsString})");
            GameObject obj = FindObjectById(gameObjectId);
            if (obj == null) throw new Exception($"GameObject with ID {gameObjectId} not found.");
            Type componentType = FindType(componentName);
            if (componentType == null) throw new Exception($"Type '{componentName}' not found in any loaded assembly.");
            Component component = obj.GetComponent(componentType);
            if (component == null) throw new Exception($"Component '{componentName}' not found on GameObject with ID {gameObjectId}.");

            // var componentType = component.GetType();
            var bindingFlags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase | BindingFlags.NonPublic;

            // 1. Parse the string arguments
            var stringArgs = ParseStringArray(argsString);

            // 2. Find a method that matches the name AND argument count
            var method = componentType.GetMethods(bindingFlags)
                .FirstOrDefault(m =>
                    m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase) &&
                    m.GetParameters().Length == stringArgs.Count
                );

            if (method == null)
            {
                throw new Exception($"No method found on '{componentName}' with name '{methodName}' and {stringArgs.Count} arguments.");
            }

            // 3. Convert string args to the real types
            var parameters = method.GetParameters(); // Get the required parameter types
            var convertedArgs = new object[stringArgs.Count];
            for (int i = 0; i < stringArgs.Count; i++)
            {
                var requiredType = parameters[i].ParameterType;
                var stringValue = stringArgs[i];
                convertedArgs[i] = ConvertValue(stringValue, requiredType);
            }

            // 4. Call the method
            Logger.LogInfo($"Invoking method '{method.Name}'...");
            object returnValue = method.Invoke(component, convertedArgs);

            string returnValueString = returnValue?.ToString() ?? "null";
            Logger.LogInfo($"Method returned: {returnValueString}");

            return $"{{\"status\":\"ok\", \"message\":\"Called '{methodName}' with {stringArgs.Count} args. ReturnValue: {returnValueString}\"}}";
        };
    }
    // --- NEW MAIN THREAD JOB 7 (Read) ---
    private Func<string> ListComponentMethods(int gameObjectId, string componentName)
    {
        return () =>
        {
            Logger.LogInfo($"Running job: ListComponentMethods(id={gameObjectId}, comp={componentName})");
            GameObject obj = FindObjectById(gameObjectId);
            if (obj == null) throw new Exception($"GameObject with ID {gameObjectId} not found.");

            Component component = obj.GetComponents<Component>()
                .FirstOrDefault(c => c.GetType().ToString() == componentName);

            if (component == null) throw new Exception($"Component '{componentName}' not found on GameObject with ID {gameObjectId}.");

            var componentType = component.GetType();
            var bindingFlags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic;

            var methodList = new List<string>();

            // Get all public, instance-level methods
            foreach (var method in componentType.GetMethods(bindingFlags))
            {
                // We'll skip the "special" methods that get/set properties
                if (method.IsSpecialName)
                    continue;

                // Build a nice signature string, e.g., "SetPosition(Vector3 position)"
                var parameters = method.GetParameters()
                    .Select(p => $"{p.ParameterType.Name} {p.Name}");

                string signature = $"{method.ReturnType.Name} {method.Name}({string.Join(", ", parameters)})";
                methodList.Add(signature);
            }

            // We'll return this as a simple JSON array of strings
            return BuildJsonArray(methodList.OrderBy(s => s));
        };
    }
    // --- NEW MAIN THREAD JOB 8 (Read) ---
    private Func<string> FindObjectsWithComponent(string componentName)
    {
        return () =>
        {
            Logger.LogInfo($"Running job: FindObjectsWithComponent(comp={componentName})");

            // 1. We use your 'FindType' helper to get the Type from the string name
            Type componentType = FindType(componentName);
            if (componentType == null)
            {
                throw new Exception($"Type '{componentName}' not found in any loaded assembly.");
            }

            // 2. This gets the generic 'Object[]'
            var objects = Resources.FindObjectsOfTypeAll(componentType);

            // 3. --- THIS IS THE FIX ---
            //    We use .Cast<Component>() to convert the 'Object[]'
            //    into an 'IEnumerable<Component>'.
            //    (This requires 'using System.Linq;' at the top of your file)
            var components = objects.Cast<Component>();
            // --- END FIX ---

            Logger.LogInfo($"Found {components.Count()} objects with component '{componentName}'");

            // 4. We'll build a list of the GameObjects that have this component
            var objectList = new List<SimpleGameObject>();

            // 5. Now this loop works, because 'component' is a Component!
            foreach (var component in components)
            {
                // We only want components that are in the scene, not prefabs
                if (component.gameObject.scene.name != null)
                {
                    objectList.Add(new SimpleGameObject
                    {
                        name = component.gameObject.name,
                        id = component.gameObject.GetInstanceID()
                    });
                }
            }

            // 6. Return the list as a JSON array
            return BuildJsonObjectArray(objectList.Distinct());
        };
    }
    // --- NEW MAIN THREAD JOB 9 (The Patch Factory) ---
    private Func<string> SubscribeToMethod(int gameObjectId, string componentName, string methodName, string registrationId = null)
    {
        return () =>
        {
            Logger.LogInfo($"Running job: SubscribeToMethod(id={gameObjectId}, comp={componentName}, method={methodName})");
            GameObject obj = FindObjectById(gameObjectId);
            if (obj == null) throw new Exception($"GameObject with ID {gameObjectId} not found.");

            Component component = obj.GetComponents<Component>()
                .FirstOrDefault(c => c.GetType().ToString() == componentName);
            // --- END FIX ---
            if (component == null) throw new Exception($"Component '{componentName}' not found on GameObject.");

            var componentType = component.GetType();
            var bindingFlags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic;

            // 1. Find the target method. We're not picky about arguments yet.
            var targetMethod = componentType.GetMethod(methodName, bindingFlags);
            if (targetMethod == null)
            {
                throw new Exception($"Method '{methodName}' not found on component '{componentName}'.");
            }

            // 2. Check if we've *already* patched this exact method
            string patchId = $"{componentName}::{methodName}";
            if (patchedMethods.ContainsKey(patchId))
            {
                Logger.LogWarning($"Method '{patchId}' is already patched. Subscription re-confirmed.");
                var existing = productivityPatches.Values
                    .FirstOrDefault(state => state.LegacyId == patchId);
                return Json(new
                {
                    status = "ok",
                    message = $"Already subscribed to '{patchId}'",
                    id = existing?.Info.id
                });
            }

            // 3. Get our generic "Postfix" patch
            var postfix = typeof(McpServerMod).GetMethod(nameof(GenericPostfix), BindingFlags.Static | BindingFlags.NonPublic);

            // 4. APPLY THE PATCH!
            try
            {
                harmony.Patch(targetMethod, postfix: new HarmonyMethod(postfix));
                patchedMethods[patchId] = targetMethod; // Remember this patch
                var registration = RegisterPatchV2(
                    registrationId,
                    "subscription",
                    patchId,
                    "postfix",
                    targetMethod,
                    postfix,
                    patchId,
                    $"subscription:{patchId}");
                Logger.LogInfo($"Registered subscription lifecycle ID '{registration.id}'.");
            }
            catch (Exception e)
            {
                Logger.LogError($"Harmony Patch failed for '{patchId}': {e}");
                throw new Exception($"Harmony Patch failed: {e.Message}");
            }

            Logger.LogInfo($"SUCCESS: Harmony patch applied to '{patchId}'.");
            var registered = productivityPatches.Values
                .First(state => state.LegacyId == patchId)
                .Info;
            return Json(new
            {
                status = "ok",
                message = $"Successfully subscribed to method '{patchId}'",
                id = registered.id
            });
        };
    }

    // --- NEW: The Generic Postfix Patch ---
    // This is the "hook" that will run after *any* method we subscribe to.
    // It runs on the main game thread.
    private static void GenericPostfix(MethodInfo __originalMethod, object __instance, object[] __args)
    {
        try
        {
            string patchId = $"{__instance.GetType().Name}::{__originalMethod.Name}";
            PatcherLogger.LogInfo($"[HarmonyPatch] Event triggered: {patchId}");

            // 1. Convert arguments to simple strings
            var argsList = new List<string>();
            foreach (var arg in __args)
            {
                argsList.Add(arg?.ToString() ?? "null");
            }

            var registration = Instance?.productivityPatches.Values
                .FirstOrDefault(state => state.Original == __originalMethod);
            string payload = Json(new
            {
                kind = "method.called",
                registrationId = registration?.Info.id ?? patchId,
                timestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                instanceId = (__instance as Component)?.gameObject.GetInstanceID(),
                method = $"{__originalMethod.DeclaringType?.FullName}::{__originalMethod.Name}",
                args = argsList
            });

            // 3. Send the webhook on a background thread so we don't lag the game
            Task.Run(() => SendWebhook(payload));
        }
        catch (Exception e)
        {
            // We must *never* let a patch throw an exception,
            // or it will crash the game.
            PatcherLogger.LogError($"[HarmonyPatch] Error in Postfix: {e}");
        }
    }

    // --- NEW: The Webhook Sender ---
    // This runs on a background thread.
    // --- UPDATED (v2) - More Logging ---
    private static async Task SendWebhook(string payload)
    {
        try
        {
            string webhookUrl = $"http://{ConfigWebhookIP.Value}:8081/event";
            var content = new StringContent(payload, Encoding.UTF8, "application/json");

            PatcherLogger.LogInfo($"[Webhook] Sending: {payload} to {webhookUrl}...");

            // Set a short timeout (e.g., 2 seconds)
            httpClient.Timeout = TimeSpan.FromSeconds(2);
            var response = await httpClient.PostAsync(webhookUrl, content);

            // Check if the Python server received it
            if (response.IsSuccessStatusCode)
            {
                PatcherLogger.LogInfo("[Webhook] Success! Python app received the event.");
            }
            else
            {
                PatcherLogger.LogWarning($"[Webhook] FAILED. Python app responded with: {response.StatusCode}");
            }
        }
        catch (Exception e)
        {
            // This will now catch firewall blocks and connection errors
            PatcherLogger.LogError($"[Webhook] CRITICAL FAIL. Failed to send event: {e.Message}");
        }
    }

    // --- NEW MAIN THREAD JOB 10: Patch any method in the game ---
    private Func<string> PatchMethod(string targetClass, string targetMethod, string parameterTypes, string patchType, string patchCode, string registrationId = null)
    {
        return () =>
        {
            Logger.LogInfo($"[DynamicPatch] Request: {targetClass}::{targetMethod} (Type: {patchType})");

            // 1. Find Target Type
            Type type = FindType(targetClass);
            if (type == null) throw new Exception($"Type '{targetClass}' not found.");

            // 2. Parse Parameter Types (if provided)
            Type[] paramTypes = null;
            if (!string.IsNullOrEmpty(parameterTypes))
            {
                var paramTypeNames = parameterTypes.Split(new[] {','}, StringSplitOptions.RemoveEmptyEntries);
                var paramTypeList = new List<Type>();
                foreach (var paramName in paramTypeNames)
                {
                    Type paramType = FindType(paramName.Trim());
                    if (paramType == null) throw new Exception($"Parameter type '{paramName}' not found.");
                    paramTypeList.Add(paramType);
                }
                paramTypes = paramTypeList.ToArray();
            }

            // 3. Find Target Method
            var bindingFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            MethodInfo targetMethodInfo;
            if (paramTypes != null && paramTypes.Length > 0)
                targetMethodInfo = type.GetMethod(targetMethod, bindingFlags, null, paramTypes, null);
            else
                targetMethodInfo = AccessTools.Method(type, targetMethod);

            if (targetMethodInfo == null)
                throw new Exception($"Method '{targetMethod}' not found in type '{targetClass}'.");

            string fingerprint =
                $"{targetMethodInfo.DeclaringType?.FullName}::{targetMethodInfo.Name}\n" +
                $"{patchType.ToLowerInvariant()}\n{patchCode}";
            var duplicate = productivityPatches.Values.FirstOrDefault(
                state => string.Equals(
                    state.Fingerprint,
                    fingerprint,
                    StringComparison.Ordinal));
            if (duplicate != null)
            {
                return Json(new
                {
                    status = "ok",
                    message = $"An identical {patchType} patch is already applied.",
                    id = duplicate.Info.id
                });
            }

            // 4. Compile the patch code using Roslyn
            Logger.LogInfo($"[DynamicPatch] Compiling patch code...");

            var syntaxTree = CSharpSyntaxTree.ParseText(patchCode);

            // Get references to all loaded assemblies
            var references = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(a => MetadataReference.CreateFromFile(a.Location))
                .Cast<MetadataReference>()
                .ToList();

            // --- NEW: Auto-detect and add Unity DLLs from the game's Managed folder ---
            try
            {
                // Find the game's Managed folder
                // Unity games typically have: GameName.exe -> GameName_Data/Managed/
                string gameDataPath = Application.dataPath; // This points to "GameName_Data" folder
                string managedPath = Path.Combine(gameDataPath, "Managed");

                if (Directory.Exists(managedPath))
                {
                    Logger.LogInfo($"[DynamicPatch] Found Managed folder at: {managedPath}");

                    // Get all DLL files in the Managed folder
                    var managedDlls = Directory.GetFiles(managedPath, "*.dll");
                    Logger.LogInfo($"[DynamicPatch] Found {managedDlls.Length} DLLs in Managed folder");

                    // Add references for important Unity assemblies and game code
                    var importantDlls = new[]
                    {
                        "UnityEngine.CoreModule.dll",
                        "UnityEngine.dll",
                        "Assembly-CSharp.dll",
                        "UnityEngine.UI.dll",
                        "UnityEngine.PhysicsModule.dll",
                        "UnityEngine.Physics2DModule.dll",
                        "UnityEngine.InputLegacyModule.dll"
                    };

                    foreach (var dllName in importantDlls)
                    {
                        string dllPath = Path.Combine(managedPath, dllName);
                        if (File.Exists(dllPath))
                        {
                            // Check if we already have this reference (to avoid duplicates)
                            if (!references.Any(r => r.Display.Contains(dllName)))
                            {
                                references.Add(MetadataReference.CreateFromFile(dllPath));
                                Logger.LogInfo($"[DynamicPatch] Added reference: {dllName}");
                            }
                        }
                    }
                }
                else
                {
                    Logger.LogWarning($"[DynamicPatch] Managed folder not found at: {managedPath}");
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[DynamicPatch] Failed to auto-detect Unity DLLs: {ex.Message}");
            }
            // --- END AUTO-DETECTION ---

            var compilation = CSharpCompilation.Create(
                "DynamicPatch_" + Guid.NewGuid().ToString("N"),
                new[] { syntaxTree },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            using (var ms = new MemoryStream())
            {
                EmitResult result = compilation.Emit(ms);

                if (!result.Success)
                {
                    var failures = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
                    string errors = string.Join("\n", failures.Select(d => $"{d.Id}: {d.GetMessage()}"));
                    Logger.LogError($"[DynamicPatch] COMPILE FAILED:\n{errors}");
                    throw new Exception($"C# Compile Error:\n{errors}");
                }

                ms.Seek(0, SeekOrigin.Begin);
                var assembly = Assembly.Load(ms.ToArray());

                // 5. Find the patch method in the compiled assembly
                var patcherType = assembly.GetTypes().FirstOrDefault(t => t.Name == "DynamicPatcher");
                if (patcherType == null)
                    throw new Exception("Patch code compiled, but the required 'public class DynamicPatcher' was not found.");

                string patchMethodName = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(patchType.ToLower());
                var patchMethod = patcherType.GetMethod(patchMethodName, BindingFlags.Public | BindingFlags.Static);

                if (patchMethod == null)
                    throw new Exception($"Compiled code, but 'DynamicPatcher.{patchMethodName}()' static method was not found.");

                // 6. Apply the patch using Harmony
                Logger.LogInfo($"[DynamicPatch] Applying {patchType} patch to {targetClass}::{targetMethod}...");
                var harmonyMethod = new HarmonyMethod(patchMethod);

                if (patchType.ToLower() == "prefix")
                    harmony.Patch(targetMethodInfo, prefix: harmonyMethod);
                else if (patchType.ToLower() == "postfix")
                    harmony.Patch(targetMethodInfo, postfix: harmonyMethod);
                else if (patchType.ToLower() == "transpiler")
                    harmony.Patch(targetMethodInfo, transpiler: harmonyMethod);
                else if (patchType.ToLower() == "finalizer")
                    harmony.Patch(targetMethodInfo, finalizer: harmonyMethod);
                else
                    throw new Exception($"Invalid patchType '{patchType}'. Must be prefix, postfix, transpiler, or finalizer.");

                var registration = RegisterPatchV2(
                    registrationId,
                    "dynamic",
                    $"{targetClass}::{targetMethod}",
                    patchType.ToLowerInvariant(),
                    targetMethodInfo,
                    patchMethod,
                    fingerprint: fingerprint);
                Logger.LogInfo($"[DynamicPatch] SUCCESS: Applied {patchType} patch to {targetClass}::{targetMethod}");
                return Json(new
                {
                    status = "ok",
                    message = $"Successfully applied {patchType} patch to {targetClass}::{targetMethod}",
                    id = registration.id
                });
            }
        };
    }

    private Func<string> InjectClass(string classCode, string attachToGameObjectIdStr)
    {
        return () =>
        {
            Logger.LogInfo($"[ClassInjection] Request received. Attach to GameObject ID: '{attachToGameObjectIdStr}'");

            // 1. Compile the class code using Roslyn
            var syntaxTree = CSharpSyntaxTree.ParseText(classCode);

            // Get references to all loaded assemblies
            var references = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(a => MetadataReference.CreateFromFile(a.Location))
                .Cast<MetadataReference>()
                .ToList();

            // Auto-detect and add Unity DLLs from the game's Managed folder
            try
            {
                string gameDataPath = Application.dataPath;
                string managedPath = Path.Combine(gameDataPath, "Managed");

                if (Directory.Exists(managedPath))
                {
                    var managedDlls = Directory.GetFiles(managedPath, "*.dll");
                    var importantDlls = new[]
                    {
                        "UnityEngine.CoreModule.dll",
                        "UnityEngine.dll",
                        "Assembly-CSharp.dll",
                        "UnityEngine.UI.dll",
                        "UnityEngine.PhysicsModule.dll",
                        "UnityEngine.Physics2DModule.dll",
                        "UnityEngine.InputLegacyModule.dll"
                    };

                    foreach (var dllName in importantDlls)
                    {
                        string dllPath = Path.Combine(managedPath, dllName);
                        if (File.Exists(dllPath))
                        {
                            if (!references.Any(r => r.Display.Contains(dllName)))
                            {
                                references.Add(MetadataReference.CreateFromFile(dllPath));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[ClassInjection] Failed to auto-detect Unity DLLs: {ex.Message}");
            }

            var compilation = CSharpCompilation.Create(
                "DynamicClass_" + Guid.NewGuid().ToString("N"),
                new[] { syntaxTree },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            using (var ms = new MemoryStream())
            {
                EmitResult result = compilation.Emit(ms);

                if (!result.Success)
                {
                    var failures = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
                    string errors = string.Join("\n", failures.Select(d => $"{d.Id}: {d.GetMessage()}"));
                    Logger.LogError($"[ClassInjection] COMPILE FAILED:\n{errors}");
                    throw new Exception($"C# Compile Error:\n{errors}");
                }

                ms.Seek(0, SeekOrigin.Begin);
                var assembly = Assembly.Load(ms.ToArray());

                // Find all Types defined in the compiled assembly
                var types = assembly.GetTypes();
                var loadedTypeNames = types.Select(t => t.FullName).ToArray();
                Logger.LogInfo($"[ClassInjection] Successfully loaded assembly. Types: {string.Join(", ", loadedTypeNames)}");

                string attachmentMessage = "";
                if (!string.IsNullOrEmpty(attachToGameObjectIdStr))
                {
                    if (int.TryParse(attachToGameObjectIdStr, out int gameObjectId))
                    {
                        GameObject go = FindObjectById(gameObjectId);
                        if (go == null)
                        {
                            throw new Exception($"GameObject with ID {gameObjectId} not found.");
                        }

                        // Find the MonoBehaviour type to attach
                        Type componentType = types.FirstOrDefault(t => typeof(MonoBehaviour).IsAssignableFrom(t));
                        if (componentType == null)
                        {
                            throw new Exception($"Compiled assembly contains types ({string.Join(", ", loadedTypeNames)}) but none inherit from MonoBehaviour.");
                        }

                        // Attach the component!
                        Component component = go.AddComponent(componentType);
                        attachmentMessage = $", attached component '{componentType.FullName}' to GameObject '{go.name}' ({gameObjectId})";
                    }
                    else
                    {
                        throw new Exception($"Invalid GameObject ID '{attachToGameObjectIdStr}'.");
                    }
                }

                return Json(new
                {
                    status = "ok",
                    message = $"Successfully compiled and loaded assembly with types: {string.Join(", ", loadedTypeNames)}{attachmentMessage}",
                    types = loadedTypeNames
                });
            }
        };
    }
}
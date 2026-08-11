using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BepInExMCP.IL2CPP;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BasePlugin
{
    internal const string PluginGuid = "com.ryan.mcphelper.il2cpp";
    internal const string PluginName = "MCP Helper Server (IL2CPP)";
    internal const string PluginVersion = "1.0.0";

    private MainThreadDispatcher? dispatcher;
    private GameObject? dispatcherObject;
    private GameObject? selfTestObject;
    private Il2CppTypeResolver? typeResolver;
    private WebhookClient? webhookClient;
    private RegistrationIdStore? registrationIds;
    private PatchService? patchService;
    private WatcherService? watcherService;
    private HttpBridgeServer? httpServer;

    public override void Load()
    {
        var listenAddress = Config.Bind(
            "Network",
            "ListenIP",
            "localhost",
            "HTTP listen address. Use '*' only when remote access is intentionally required.");
        var listenPort = Config.Bind(
            "Network",
            "ListenPort",
            8080,
            "HTTP listen port for MCP bridge commands.");
        var webhookAddress = Config.Bind(
            "Network",
            "WebhookIP",
            "127.0.0.1",
            "Host running the Python MCP translator webhook.");
        var webhookPort = Config.Bind(
            "Network",
            "WebhookPort",
            8081,
            "Python MCP translator webhook port.");
        var timeoutSeconds = Config.Bind(
            "Network",
            "RequestTimeoutSeconds",
            15,
            new ConfigDescription(
                "Maximum time an HTTP request may wait for Unity's main thread.",
                new AcceptableValueRange<int>(1, 120)));
        var enableSelfTest = Config.Bind(
            "Diagnostics",
            "EnableSelfTestObject",
            true,
            "Create a hidden, persistent GameObject used for non-destructive bridge smoke tests.");

        try
        {
            ClassInjector.RegisterTypeInIl2Cpp<MainThreadDispatcher>();
            dispatcherObject = new GameObject("__BepInExMCP_Dispatcher")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            Object.DontDestroyOnLoad(dispatcherObject);
            dispatcher = dispatcherObject.AddComponent<MainThreadDispatcher>();
            MainThreadQueue.Start();

            if (enableSelfTest.Value)
            {
                ClassInjector.RegisterTypeInIl2Cpp<BridgeSelfTestComponent>();
                selfTestObject = CreateSelfTestObject();
            }

            typeResolver = new Il2CppTypeResolver(Log);
            var backend = new Il2CppGameBackend(
                typeResolver,
                () => selfTestObject is null ? null : selfTestObject.GetInstanceID());
            webhookClient = new WebhookClient(
                Log,
                webhookAddress.Value,
                webhookPort.Value);
            registrationIds = new RegistrationIdStore();
            watcherService = new WatcherService(backend, webhookClient, registrationIds);
            MainThreadQueue.SetFrameTick(watcherService.Tick);
            patchService = new PatchService(
                backend,
                webhookClient,
                Log,
                PluginGuid + ".harmony",
                registrationIds);
            var router = new CommandRouter(
                backend,
                patchService,
                watcherService,
                Log,
                TimeSpan.FromSeconds(timeoutSeconds.Value));
            httpServer = new HttpBridgeServer(
                router,
                Log,
                listenAddress.Value,
                listenPort.Value);
            httpServer.Start();

            Log.LogInfo(
                $"{PluginName} {PluginVersion} loaded. " +
                $"Unity {Application.unityVersion}; BepInEx IL2CPP " +
                $"{typeof(BasePlugin).Assembly.GetName().Version}.");
        }
        catch
        {
            Cleanup();
            throw;
        }
    }

    public override bool Unload()
    {
        Cleanup();
        Log.LogInfo($"{PluginName} unloaded.");
        return true;
    }

    private static GameObject CreateSelfTestObject()
    {
        var root = new GameObject("__BepInExMCP_IL2CPP_SelfTest");
        root.hideFlags = HideFlags.HideAndDontSave;
        Object.DontDestroyOnLoad(root);

        var child = new GameObject("Child");
        child.hideFlags = HideFlags.HideAndDontSave;
        child.transform.SetParent(root.transform, false);
        root.AddComponent<BridgeSelfTestComponent>();
        return root;
    }

    private void Cleanup()
    {
        if (httpServer is not null)
        {
            try
            {
                httpServer.StopAsync().GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                Log.LogWarning($"HTTP bridge shutdown failed: {exception.Message}");
            }

            httpServer = null;
        }

        patchService?.Dispose();
        patchService = null;
        watcherService?.Dispose();
        watcherService = null;
        registrationIds?.Clear();
        registrationIds = null;
        webhookClient?.Dispose();
        webhookClient = null;
        typeResolver?.Dispose();
        typeResolver = null;

        if (selfTestObject is not null)
        {
            Object.Destroy(selfTestObject);
            selfTestObject = null;
        }

        if (dispatcherObject is not null)
        {
            Object.Destroy(dispatcherObject);
            dispatcherObject = null;
        }

        dispatcher = null;
        MainThreadQueue.Stop(new ObjectDisposedException(PluginName));
    }
}

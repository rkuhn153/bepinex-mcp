using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using UnityEngine;

namespace BepInExMCP.IL2CPP;

internal sealed class PatchService : IDisposable
{
    private const int MaxPatchCodeLength = 48 * 1024;
    private static PatchService? current;

    private readonly Harmony harmony;
    private readonly Il2CppGameBackend backend;
    private readonly WebhookClient webhookClient;
    private readonly ManualLogSource log;
    private readonly ConcurrentDictionary<MethodBase, string> subscriptions = new();
    private readonly ConcurrentDictionary<string, PatchState> registrations =
        new(StringComparer.Ordinal);
    private readonly RegistrationIdStore registrationIds;

    internal PatchService(
        Il2CppGameBackend backend,
        WebhookClient webhookClient,
        ManualLogSource log,
        string harmonyId,
        RegistrationIdStore registrationIds)
    {
        if (current is not null)
        {
            throw new InvalidOperationException("Only one IL2CPP patch service may be active.");
        }

        this.backend = backend;
        this.webhookClient = webhookClient;
        this.log = log;
        this.registrationIds = registrationIds;
        harmony = new Harmony(harmonyId);
        current = this;
    }

    public void Dispose()
    {
        foreach (var id in registrations.Keys.ToArray())
        {
            registrationIds.Release(id);
        }

        harmony.UnpatchSelf();
        subscriptions.Clear();
        registrations.Clear();

        if (ReferenceEquals(current, this))
        {
            current = null;
        }
    }

    internal StatusResponse Subscribe(
        int gameObjectId,
        string componentName,
        string methodName,
        string? requestedId = null)
    {
        var component = backend.ResolveComponent(gameObjectId, componentName);
        var target = backend.ResolveMethod(component.Type, methodName, string.Empty);

        if (subscriptions.TryGetValue(target, out var existingId))
        {
            return new StatusResponse(
                "ok",
                $"Already subscribed to '{BuildPatchId(target)}'.",
                existingId);
        }

        var registrationId = AllocateRegistrationId(requestedId, "subscription");
        try
        {
            var callback = typeof(PatchService).GetMethod(
                nameof(SubscriptionPostfix),
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new MissingMethodException(nameof(SubscriptionPostfix));

            harmony.Patch(target, postfix: new HarmonyMethod(callback));
            subscriptions[target] = registrationId;
            var targetId = BuildPatchId(target);
            registrations[registrationId] = new PatchState(
                new PatchRegistration(
                    registrationId,
                    "subscription",
                    targetId,
                    "postfix",
                    true),
                target,
                callback,
                $"subscription:{targetId}");
            return new StatusResponse("ok", $"Subscribed to '{targetId}'.", registrationId);
        }
        catch
        {
            registrationIds.Release(registrationId);
            throw;
        }
    }

    internal StatusResponse ApplyDynamicPatch(
        string targetClass,
        string targetMethod,
        string parameterTypes,
        string patchType,
        string patchCode,
        string? requestedId = null)
    {
        if (patchCode.Length > MaxPatchCodeLength)
        {
            throw new ArgumentException(
                $"Patch source exceeds the {MaxPatchCodeLength}-character limit.",
                nameof(patchCode));
        }

        patchType = patchType.Trim().ToLowerInvariant();
        if (patchType is not ("prefix" or "postfix"))
        {
            throw new NotSupportedException(
                $"IL2CPP dynamic patch type '{patchType}' is unsupported. " +
                "Only prefix and postfix are supported.");
        }

        var targetType = backend.ResolveType(targetClass);
        var original = backend.ResolveMethod(targetType, targetMethod, parameterTypes);
        var targetId = BuildPatchId(original);
        var fingerprint = BuildFingerprint(targetId, patchType, patchCode);
        var duplicate = registrations.Values.FirstOrDefault(
            state => string.Equals(state.Fingerprint, fingerprint, StringComparison.Ordinal));
        if (duplicate is not null)
        {
            return new StatusResponse(
                "ok",
                $"An identical {patchType} patch is already applied to '{targetId}'.",
                duplicate.Registration.Id);
        }

        var registrationId = AllocateRegistrationId(requestedId, "patch");
        try
        {
            var patchMethod = CompilePatch(patchCode, patchType);
            var harmonyMethod = new HarmonyMethod(patchMethod);

            if (patchType == "prefix")
            {
                harmony.Patch(original, prefix: harmonyMethod);
            }
            else
            {
                harmony.Patch(original, postfix: harmonyMethod);
            }

            registrations[registrationId] = new PatchState(
                new PatchRegistration(
                    registrationId,
                    "dynamic",
                    targetId,
                    patchType,
                    true),
                original,
                patchMethod,
                fingerprint);
            return new StatusResponse(
                "ok",
                $"Applied IL2CPP {patchType} patch to '{targetId}'.",
                registrationId);
        }
        catch
        {
            registrationIds.Release(registrationId);
            throw;
        }
    }

    internal IReadOnlyList<PatchRegistration> ListRegistrations() =>
        registrations.Values
            .Select(state => state.Registration)
            .OrderBy(registration => registration.Id, StringComparer.Ordinal)
            .ToArray();

    internal StatusResponse Remove(string registrationId)
    {
        if (!registrations.TryRemove(registrationId, out var state))
        {
            throw new KeyNotFoundException(
                $"Patch or subscription '{registrationId}' was not found.");
        }

        harmony.Unpatch(state.Original, state.PatchMethod);
        if (state.Registration.Kind == "subscription")
        {
            subscriptions.TryRemove(state.Original, out _);
        }

        registrationIds.Release(registrationId);
        return new StatusResponse(
            "ok",
            $"Removed {state.Registration.Kind} '{registrationId}'.",
            registrationId);
    }

    private MethodInfo CompilePatch(string patchCode, string patchType)
    {
        if (string.IsNullOrWhiteSpace(patchCode))
        {
            throw new ArgumentException("Patch source is required.", nameof(patchCode));
        }

        var syntaxTree = CSharpSyntaxTree.ParseText(
            patchCode,
            new CSharpParseOptions(LanguageVersion.Latest));
        var references = BuildMetadataReferences();
        var compilation = CSharpCompilation.Create(
            $"BepInExMCP_DynamicPatch_{Guid.NewGuid():N}",
            new[] { syntaxTree },
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                allowUnsafe: true));

        using var assemblyStream = new MemoryStream();
        var result = compilation.Emit(assemblyStream);
        if (!result.Success)
        {
            var errors = result.Diagnostics
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .Take(25)
                .Select(diagnostic => $"{diagnostic.Id}: {diagnostic.GetMessage()}")
                .ToArray();
            throw new InvalidOperationException(
                "Dynamic patch compilation failed: " + string.Join(" | ", errors));
        }

        var assembly = Assembly.Load(assemblyStream.ToArray());
        var patcherType = assembly
            .GetTypes()
            .FirstOrDefault(type => type.Name == "DynamicPatcher")
            ?? throw new InvalidOperationException(
                "Patch source must declare a public class named 'DynamicPatcher'.");
        var methodName = patchType == "prefix" ? "Prefix" : "Postfix";
        var patchMethod = patcherType.GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.Static);

        if (patchMethod is null)
        {
            throw new InvalidOperationException(
                $"DynamicPatcher must declare a public static {methodName} method.");
        }

        return patchMethod;
    }

    private IReadOnlyList<MetadataReference> BuildMetadataReferences()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trustedAssemblies)
        {
            foreach (var path in trustedAssemblies.Split(Path.PathSeparator))
            {
                AddAssemblyPath(paths, path);
            }
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!assembly.IsDynamic)
            {
                AddAssemblyPath(paths, assembly.Location);
            }
        }

        AddDirectory(paths, Path.Combine(Paths.BepInExRootPath, "core"));
        AddDirectory(paths, Path.Combine(Paths.BepInExRootPath, "interop"));

        var references = new List<MetadataReference>();
        foreach (var path in paths)
        {
            try
            {
                references.Add(MetadataReference.CreateFromFile(path));
            }
            catch (Exception exception)
            {
                log.LogDebug($"Skipping compiler reference '{path}': {exception.Message}");
            }
        }

        return references;
    }

    private static void AddDirectory(ISet<string> paths, string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.dll"))
        {
            AddAssemblyPath(paths, file);
        }
    }

    private static void AddAssemblyPath(ISet<string> paths, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            // TPA and BepInEx folders also contain native DLLs. Roslyn accepts a
            // MetadataReference for those paths initially, then fails the entire
            // compilation with CS0009 when it reads them.
            _ = AssemblyName.GetAssemblyName(path);
            paths.Add(Path.GetFullPath(path));
        }
        catch (BadImageFormatException)
        {
            // Native DLL, not a managed compiler reference.
        }
        catch (FileLoadException)
        {
            // Invalid or unsupported managed metadata.
        }
    }

    private static void SubscriptionPostfix(
        MethodBase __originalMethod,
        object? __instance,
        object?[]? __args)
    {
        var service = current;
        if (service is null)
        {
            return;
        }

        try
        {
            var registrationId =
                service.subscriptions.TryGetValue(__originalMethod, out var storedId)
                    ? storedId
                    : BuildPatchId(__originalMethod);
            var arguments = (__args ?? Array.Empty<object?>())
                .Select(ValueConverter.SafeToString)
                .ToArray();
            int? instanceId = __instance is Component component
                ? component.gameObject.GetInstanceID()
                : null;

            _ = service.webhookClient.SendAsync(
                new BridgeEvent(
                    "method.called",
                    registrationId,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    InstanceId: instanceId,
                    Method: BuildPatchId(__originalMethod),
                    Args: arguments));
        }
        catch (Exception exception)
        {
            service.log.LogError($"Harmony event callback failed: {exception}");
        }
    }

    private static string BuildPatchId(MethodBase method)
    {
        var declaringType = method.DeclaringType?.FullName ?? "<unknown>";
        var parameters = string.Join(
            ",",
            method.GetParameters().Select(parameter =>
                parameter.ParameterType.FullName ?? parameter.ParameterType.Name));
        return $"{declaringType}::{method.Name}({parameters})";
    }

    private string AllocateRegistrationId(string? requestedId, string prefix)
    {
        if (registrations.Count >= Protocol.MaxPatchRegistrations)
        {
            throw new InvalidOperationException(
                $"The patch registration limit of {Protocol.MaxPatchRegistrations} has been reached.");
        }

        return registrationIds.Allocate(requestedId, prefix);
    }

    private static string BuildFingerprint(string target, string patchType, string patchCode)
    {
        var data = Encoding.UTF8.GetBytes($"{target}\n{patchType}\n{patchCode}");
        return Convert.ToHexString(SHA256.HashData(data));
    }

    private sealed record PatchState(
        PatchRegistration Registration,
        MethodBase Original,
        MethodInfo PatchMethod,
        string Fingerprint);
}

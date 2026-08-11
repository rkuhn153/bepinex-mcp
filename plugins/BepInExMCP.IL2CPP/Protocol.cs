using System.Text.Json;
using System.Text.Json.Serialization;

namespace BepInExMCP.IL2CPP;

internal static class Protocol
{
    internal const string Version = "2.0";
    internal const int MaxBatchOperations = 100;
    internal const int MaxRequestBodyBytes = 256 * 1024;
    internal const int MaxWatchers = 128;
    internal const int MaxPatchRegistrations = 128;
    internal const int MaxSnapshotNodes = 1_000;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    internal static string Serialize(object value) =>
        JsonSerializer.Serialize(value, value.GetType(), JsonOptions);

    internal static string Error(string message, string? code = null) =>
        JsonSerializer.Serialize(new ErrorResponse(message, code), JsonOptions);
}

internal sealed record ErrorResponse(string Error, string? Code = null);

internal sealed record StatusResponse(string Status, string Message, string? Id = null);

internal sealed record SimpleGameObject(string Name, int Id);

internal sealed record ComponentMemberInfo(string Type, string Value);

internal sealed record MemberValueResponse(
    string Name,
    string Type,
    string Value,
    bool Writable,
    string Kind);

internal sealed record ObjectSelector(
    string? Scene = null,
    string? Path = null,
    string? Component = null,
    string? Name = null);

internal sealed record GameObjectSearchResult(
    string Name,
    int Id,
    string Scene,
    string Path,
    bool Active);

internal sealed record SearchResponse(
    IReadOnlyList<GameObjectSearchResult> Items,
    bool Truncated);

internal sealed record HierarchyNode(
    string Name,
    int Id,
    string Scene,
    string Path,
    bool Active,
    IReadOnlyList<HierarchyNode> Children);

internal sealed record TypeSummary(
    string Name,
    string FullName,
    string Assembly,
    bool IsComponent);

internal sealed record MemberDescription(
    string Name,
    string Type,
    string Kind,
    bool Readable,
    bool Writable,
    bool IsStatic);

internal sealed record MethodDescription(
    string Name,
    string Signature,
    string ReturnType,
    IReadOnlyList<string> ParameterTypes,
    bool IsStatic);

internal sealed record TypeDescription(
    TypeSummary Type,
    IReadOnlyList<MemberDescription> Members,
    IReadOnlyList<MethodDescription> Methods);

internal sealed record BatchRequest(
    IReadOnlyList<BatchOperation> Operations,
    bool StopOnError = false);

internal sealed record BatchOperation(
    string Id,
    string Command,
    Dictionary<string, JsonElement>? Parameters = null);

internal sealed record BatchItemResult(
    string Id,
    bool Ok,
    object? Result = null,
    ErrorResponse? Error = null);

internal sealed record BatchResponse(IReadOnlyList<BatchItemResult> Results);

internal sealed record WatchRegistration(
    string Id,
    string Kind,
    ObjectSelector? Selector,
    string? Component,
    string? Member,
    int IntervalMs,
    bool Active,
    int? InstanceId = null,
    long? LastPolledUnixMs = null,
    string? LastError = null);

internal sealed record PatchRegistration(
    string Id,
    string Kind,
    string Target,
    string PatchType,
    bool Active);

internal sealed record NetworkObjectDiagnostic(
    string Component,
    string? Framework,
    IReadOnlyDictionary<string, string> Values);

internal sealed record NetworkDiagnosticsResponse(
    int InstanceId,
    IReadOnlyList<NetworkObjectDiagnostic> Components);

internal sealed record BridgeEvent(
    string Kind,
    string RegistrationId,
    long TimestampUnixMs,
    ObjectSelector? Selector = null,
    int? InstanceId = null,
    string? Component = null,
    string? Member = null,
    string? Method = null,
    string? OldValue = null,
    string? NewValue = null,
    IReadOnlyList<string>? Args = null);

internal sealed record CapabilitiesResponse(
    string ProtocolVersion,
    string Runtime,
    string UnityVersion,
    string BepInExVersion,
    string Architecture,
    IReadOnlyList<string> Tools,
    IReadOnlyList<string> PatchTypes,
    IReadOnlyList<string> Limitations,
    int? SelfTestObjectId = null,
    IReadOnlyList<string>? Features = null,
    IReadOnlyDictionary<string, int>? Limits = null);

internal sealed record GameEvent(
    string Event,
    IReadOnlyList<string> Args,
    int? InstanceId = null);

internal readonly record struct ApiResponse(int StatusCode, string Body)
{
    internal const string ContentType = "application/json; charset=utf-8";

    internal static ApiResponse Ok(object value) => new(200, Protocol.Serialize(value));

    internal static ApiResponse Failure(int statusCode, string message, string? code = null) =>
        new(statusCode, Protocol.Error(message, code));
}

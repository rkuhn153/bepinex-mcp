using UnityEngine;
using UnityEngine.SceneManagement;

namespace BepInExMCP.IL2CPP;

internal sealed class WatcherService : IDisposable
{
    private readonly Dictionary<string, WatchState> watches =
        new(StringComparer.Ordinal);
    private readonly Il2CppGameBackend backend;
    private readonly WebhookClient webhook;
    private readonly RegistrationIdStore registrationIds;

    internal WatcherService(
        Il2CppGameBackend backend,
        WebhookClient webhook,
        RegistrationIdStore registrationIds)
    {
        this.backend = backend;
        this.webhook = webhook;
        this.registrationIds = registrationIds;
    }

    public void Dispose()
    {
        foreach (var id in watches.Keys.ToArray())
        {
            registrationIds.Release(id);
        }

        watches.Clear();
    }

    internal WatchRegistration CreateMemberWatch(
        string? requestedId,
        ObjectSelector selector,
        string component,
        string member,
        int intervalMs)
    {
        if (watches.Count >= Protocol.MaxWatchers)
        {
            throw new InvalidOperationException(
                $"The watcher limit of {Protocol.MaxWatchers} has been reached.");
        }

        if (string.IsNullOrWhiteSpace(component) || string.IsNullOrWhiteSpace(member))
        {
            throw new ArgumentException("A component and member are required.");
        }

        var id = registrationIds.Allocate(requestedId, "watch");
        try
        {
            intervalMs = Math.Clamp(intervalMs, 100, 60_000);
            var resolved = backend.ResolveSelector(selector);
            var initialValue = backend
                .GetComponentMember(resolved.Id, component, member)
                .Value;
            var registration = new WatchRegistration(
                id,
                "member",
                selector,
                component,
                member,
                intervalMs,
                true,
                resolved.Id);
            watches.Add(
                id,
                new WatchState(registration, initialValue, Time.realtimeSinceStartup));
            return registration;
        }
        catch
        {
            registrationIds.Release(id);
            throw;
        }
    }

    internal WatchRegistration CreateSceneWatch(string? requestedId, int intervalMs)
    {
        if (watches.Count >= Protocol.MaxWatchers)
        {
            throw new InvalidOperationException(
                $"The watcher limit of {Protocol.MaxWatchers} has been reached.");
        }

        var id = registrationIds.Allocate(requestedId, "scene");
        try
        {
            intervalMs = Math.Clamp(intervalMs, 100, 60_000);
            var scene = SceneManager.GetActiveScene();
            var registration = new WatchRegistration(
                id,
                "scene",
                null,
                null,
                null,
                intervalMs,
                true);
            watches.Add(
                id,
                new WatchState(
                    registration,
                    $"{scene.handle}:{scene.name}",
                    Time.realtimeSinceStartup));
            return registration;
        }
        catch
        {
            registrationIds.Release(id);
            throw;
        }
    }

    internal IReadOnlyList<WatchRegistration> List() =>
        watches.Values
            .Select(state => state.Registration)
            .OrderBy(registration => registration.Id, StringComparer.Ordinal)
            .ToArray();

    internal StatusResponse Remove(string id)
    {
        if (!watches.Remove(id))
        {
            throw new KeyNotFoundException($"Watcher '{id}' was not found.");
        }

        registrationIds.Release(id);
        return new StatusResponse("ok", $"Removed watcher '{id}'.", id);
    }

    internal void Tick()
    {
        var now = Time.realtimeSinceStartup;

        foreach (var state in watches.Values.ToArray())
        {
            if (now < state.NextPollAt)
            {
                continue;
            }

            state.NextPollAt = now + state.Registration.IntervalMs / 1_000f;
            state.Registration = state.Registration with
            {
                LastPolledUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            if (state.Registration.Kind == "scene")
            {
                PollScene(state);
            }
            else
            {
                PollMember(state);
            }
        }
    }

    private void PollScene(WatchState state)
    {
        var scene = SceneManager.GetActiveScene();
        var value = $"{scene.handle}:{scene.name}";
        if (string.Equals(value, state.LastValue, StringComparison.Ordinal))
        {
            return;
        }

        var previous = state.LastValue;
        state.LastValue = value;
        _ = webhook.SendAsync(new BridgeEvent(
            "scene.changed",
            state.Registration.Id,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            OldValue: previous,
            NewValue: value));
    }

    private void PollMember(WatchState state)
    {
        try
        {
            var selector = state.Registration.Selector
                           ?? throw new InvalidOperationException("Member watcher has no selector.");
            var resolved = backend.ResolveSelector(selector);
            var value = backend.GetComponentMember(
                resolved.Id,
                state.Registration.Component!,
                state.Registration.Member!).Value;
            state.TargetLostReported = false;
            state.Registration = state.Registration with
            {
                InstanceId = resolved.Id,
                LastError = null
            };

            if (string.Equals(value, state.LastValue, StringComparison.Ordinal))
            {
                return;
            }

            var previous = state.LastValue;
            state.LastValue = value;
            _ = webhook.SendAsync(new BridgeEvent(
                "watch.changed",
                state.Registration.Id,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                selector,
                resolved.Id,
                state.Registration.Component,
                state.Registration.Member,
                OldValue: previous,
                NewValue: value));
        }
        catch (Exception exception)
        {
            if (state.TargetLostReported)
            {
                return;
            }

            state.TargetLostReported = true;
            state.Registration = state.Registration with { LastError = exception.Message };
            _ = webhook.SendAsync(new BridgeEvent(
                "watch.target_lost",
                state.Registration.Id,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                state.Registration.Selector,
                state.Registration.InstanceId,
                state.Registration.Component,
                state.Registration.Member,
                NewValue: exception.Message));
        }
    }

    private sealed class WatchState
    {
        internal WatchState(WatchRegistration registration, string lastValue, float nextPollAt)
        {
            Registration = registration;
            LastValue = lastValue;
            NextPollAt = nextPollAt;
        }

        internal WatchRegistration Registration { get; set; }
        internal string LastValue { get; set; }
        internal float NextPollAt { get; set; }
        internal bool TargetLostReported { get; set; }
    }
}

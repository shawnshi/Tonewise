namespace Tonewise.Audio;

public sealed record EndpointReconcileResult(
    bool CollectionMutated,
    bool LostPreviousSelection,
    AudioOutputEndpoint? TargetSelection);

/// <summary>
/// Reconciles an <c>ObservableCollection</c>-backed endpoint list in-place so unrelated
/// endpoint changes (such as a DisplayPort/HDMI monitor audio device disconnecting on display sleep)
/// never trigger <c>Reset</c> notifications or spurious WinUI ComboBox deselections.
/// </summary>
public static class AudioEndpointReconciler
{
    public static EndpointReconcileResult ReconcileInPlace(
        IList<AudioOutputEndpoint> current,
        IReadOnlyList<AudioOutputEndpoint> latest,
        string? previousSelectedId)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(latest);

        if (current.Count == latest.Count)
        {
            bool identical = true;
            for (int i = 0; i < current.Count; i++)
            {
                if (!Equals(current[i], latest[i]))
                {
                    identical = false;
                    break;
                }
            }

            if (identical)
            {
                return ResolveSelection(current, previousSelectedId, collectionMutated: false);
            }
        }

        bool mutated = false;
        var latestById = new Dictionary<string, AudioOutputEndpoint>(StringComparer.Ordinal);
        foreach (var ep in latest)
        {
            latestById.TryAdd(ep.Id, ep);
        }

        // Remove only endpoints that actually disappeared
        for (int i = current.Count - 1; i >= 0; i--)
        {
            if (!latestById.ContainsKey(current[i].Id))
            {
                current.RemoveAt(i);
                mutated = true;
            }
        }

        // Track remaining IDs and update properties in-place only when changed
        var presentIds = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < current.Count; i++)
        {
            var existing = current[i];
            presentIds.Add(existing.Id);
            if (latestById.TryGetValue(existing.Id, out var updated) && !Equals(existing, updated))
            {
                current[i] = updated;
                mutated = true;
            }
        }

        // Append newly arrived endpoints
        foreach (var ep in latest)
        {
            if (presentIds.Add(ep.Id))
            {
                current.Add(ep);
                mutated = true;
            }
        }

        return ResolveSelection(current, previousSelectedId, mutated);
    }

    private static EndpointReconcileResult ResolveSelection(
        IList<AudioOutputEndpoint> current,
        string? previousSelectedId,
        bool collectionMutated)
    {
        AudioOutputEndpoint? matched = null;
        AudioOutputEndpoint? fallbackDefault = null;

        for (int i = 0; i < current.Count; i++)
        {
            var ep = current[i];
            if (previousSelectedId is not null
                && matched is null
                && string.Equals(ep.Id, previousSelectedId, StringComparison.Ordinal))
            {
                matched = ep;
            }
            if (ep.IsDefault && fallbackDefault is null)
            {
                fallbackDefault = ep;
            }
        }

        bool lostPrevious = previousSelectedId is not null && matched is null;
        var target = matched ?? fallbackDefault ?? (current.Count > 0 ? current[0] : null);
        return new EndpointReconcileResult(collectionMutated, lostPrevious, target);
    }
}

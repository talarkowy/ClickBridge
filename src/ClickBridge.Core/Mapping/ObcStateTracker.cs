using ClickBridge.Core.Obc;
using ClickBridge.Core.Zwift;

namespace ClickBridge.Core.Mapping;

/// <summary>
/// Merges the button state of every connected controller into one set of pressed OBC buttons and reports
/// only the changes, as the protocol requires. Two controllers mapping to the same OBC button keep it
/// pressed until both release it.
/// </summary>
public sealed class ObcStateTracker(ButtonMapping mapping)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, ZwiftButtons> _sources = [];
    private HashSet<byte> _active = [];

    public IReadOnlyList<ObcButtonState> Update(string source, ZwiftButtons held)
    {
        lock (_lock)
        {
            _sources[source] = held;
            return Recompute();
        }
    }

    /// <summary>Forgets a controller (e.g. disconnected) and releases whatever it was holding.</summary>
    public IReadOnlyList<ObcButtonState> Remove(string source)
    {
        lock (_lock)
        {
            return _sources.Remove(source) ? Recompute() : [];
        }
    }

    private List<ObcButtonState> Recompute()
    {
        var now = _sources.Values.SelectMany(mapping.Map).ToHashSet();
        var changes = now.Except(_active).Select(ObcButtonState.Press)
            .Concat(_active.Except(now).Select(ObcButtonState.Release))
            .OrderBy(s => s.Id)
            .ToList();
        _active = now;
        return changes;
    }
}

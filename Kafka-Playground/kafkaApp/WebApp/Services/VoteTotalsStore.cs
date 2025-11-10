using System.Collections.Concurrent;
using System.Linq;

namespace WebApp.Services;

// -------------------------------------------------------------------------------------------------
// VoteTotalsStore
//
// Responsibilities:
// 1. Maintain an in-memory thread-safe map of global vote counts per option.
// 2. Provide change notifications (event) for UI components displaying totals.
// 3. Support incremental single-option updates and bulk synchronization operations.
//
// Notes:
// - ConcurrentDictionary is used for simplicity; operations are low contention.
// - Snapshot method produces a stable, ordered dictionary for UI rendering.
// - No abbreviations used; variable names emphasize intent.
// -------------------------------------------------------------------------------------------------
public sealed class VoteTotalsStore
{
    private readonly ConcurrentDictionary<string, int> _totalsByOption = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Raised whenever totals change (addition, update, or removal).
    /// </summary>
    public event Action? TotalsChanged;

    /// <summary>
    /// Returns an ordered snapshot of current totals.
    /// </summary>
    public IReadOnlyDictionary<string, int> GetSnapshot() => _totalsByOption
        .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Sets a single option total if changed.
    /// </summary>
    public void SetTotal(string option, int count)
    {
        if (string.IsNullOrWhiteSpace(option))
        {
            return;
        }

        if (_totalsByOption.TryGetValue(option, out var existingCount) && existingCount == count)
        {
            return; // no change
        }

        _totalsByOption[option] = count;
        RaiseChanged();
    }

    /// <summary>
    /// Synchronizes the store with a sequence of totals, adding/updating/removing as necessary.
    /// </summary>
    public void UpdateTotals(IEnumerable<KeyValuePair<string, int>> totals)
    {
        var anyChange = false;
        var incomingSnapshot = totals?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
                                ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Apply additions/updates.
        foreach (var incoming in incomingSnapshot)
        {
            if (!_totalsByOption.TryGetValue(incoming.Key, out var existingCount) || existingCount != incoming.Value)
            {
                _totalsByOption[incoming.Key] = incoming.Value;
                anyChange = true;
            }
        }

        // Remove missing keys.
        foreach (var existingOption in _totalsByOption.Keys.ToArray())
        {
            if (!incomingSnapshot.ContainsKey(existingOption) && _totalsByOption.TryRemove(existingOption, out _))
            {
                anyChange = true;
            }
        }

        if (anyChange)
        {
            RaiseChanged();
        }
    }

    private void RaiseChanged() => TotalsChanged?.Invoke();
}

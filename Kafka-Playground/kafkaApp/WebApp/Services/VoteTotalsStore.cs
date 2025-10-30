using System.Collections.Concurrent;
using System.Linq;

namespace WebApp.Services;

public sealed class VoteTotalsStore
{
    private readonly ConcurrentDictionary<string, int> _totals = new(StringComparer.OrdinalIgnoreCase);

    public event Action? TotalsChanged;

    public IReadOnlyDictionary<string, int> GetSnapshot() => _totals
        .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

    public void SetTotal(string option, int count)
    {
        if (string.IsNullOrWhiteSpace(option))
        {
            return;
        }

        if (_totals.TryGetValue(option, out var existing) && existing == count)
        {
            return;
        }

        _totals[option] = count;
        RaiseChanged();
    }

    public void UpdateTotals(IEnumerable<KeyValuePair<string, int>> totals)
    {
        var changed = false;
        var snapshot = totals?.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase)
                       ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var kv in snapshot)
        {
            if (!_totals.TryGetValue(kv.Key, out var existing) || existing != kv.Value)
            {
                _totals[kv.Key] = kv.Value;
                changed = true;
            }
        }

        foreach (var key in _totals.Keys.ToArray())
        {
            if (!snapshot.ContainsKey(key) && _totals.TryRemove(key, out _))
            {
                changed = true;
            }
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    private void RaiseChanged() => TotalsChanged?.Invoke();
}

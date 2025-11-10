using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using WebApp.Models;

namespace WebApp.Services;

public sealed class CityVoteStore
{
    private readonly ConcurrentDictionary<string, CityVoteEntry> _cities = new(StringComparer.OrdinalIgnoreCase);

    public event Action? CityVotesChanged;

        /// <summary>
        /// Sets the city vote based on the provided VoteTotal.
        /// </summary>
        /// <param name="total">The VoteTotal containing the vote information.</param>
    public void SetCityVote(VoteTotal total)
    {
        if (total is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(total.Option) || string.IsNullOrWhiteSpace(total.City))
        {
            return;
        }

        var zipCode = total.ZipCode ?? -1;
        var city = total.City.Trim();
        var key = BuildKey(city, zipCode);

        var option = total.Option.Trim();
        var entry = _cities.GetOrAdd(key, _ => new CityVoteEntry(city, zipCode));
        var updatedAt = total.UpdatedAt == default ? DateTimeOffset.UtcNow : total.UpdatedAt;

        if (entry.Update(city, zipCode, option, total.Count, updatedAt))
        {
            RaiseChanged();
        }
    }

    public IReadOnlyList<CityVoteSnapshot> GetSnapshot()
    {
            /// <summary>
            /// Gets a snapshot of the current city votes.
            /// </summary>
            /// <returns>A list of CityVoteSnapshot representing the current votes.</returns>
        return _cities.Values
            .Select(entry => entry.ToSnapshot())
            .OrderByDescending(snapshot => snapshot.TotalVotes)
            .ThenBy(snapshot => snapshot.ZipCode)
            .ThenBy(snapshot => snapshot.City, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string BuildKey(string city, int zipCode) => $"{zipCode}:{city}";

    private void RaiseChanged() => CityVotesChanged?.Invoke();

    private sealed class CityVoteEntry
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, int> _totals = new(StringComparer.OrdinalIgnoreCase);
        private string _city;
        private int _zipCode;
        private DateTimeOffset _updatedAt;

            /// <summary>
            /// Initializes a new instance of the CityVoteEntry class.
            /// </summary>
            /// <param name="city">The city name.</param>
            /// <param name="zipCode">The zip code of the city.</param>
        public CityVoteEntry(string city, int zipCode)
        {
            _city = city;
            _zipCode = zipCode;
            _updatedAt = DateTimeOffset.UtcNow;
        }

        public bool Update(string city, int zipCode, string option, int count, DateTimeOffset updatedAt)
        {
            lock (_gate)
            {
                _city = city;
                _zipCode = zipCode;
                _updatedAt = updatedAt;

                if (_totals.TryGetValue(option, out var existing) && existing == count)
                {
                    return false;
                }

                _totals[option] = count;
                return true;
            }
        }

            /// <summary>
            /// Converts the current entry to a CityVoteSnapshot.
            /// </summary>
            /// <returns>A CityVoteSnapshot representing the current entry.</returns>
        public CityVoteSnapshot ToSnapshot()
        {
            lock (_gate)
            {
                return CityVoteSnapshot.Create(_city, _zipCode, _totals, _updatedAt);
            }
        }
    }
}

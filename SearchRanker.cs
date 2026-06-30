using System;
using System.Collections.Generic;
using System.Linq;
using FuzzySharp;

namespace Filey
{
    /// <summary>
    /// Shared ranking used by both the in-memory index search and the live "local-first"
    /// search of the active directory. Cheap subsequence pre-filter, then FuzzySharp scoring
    /// with a prefix/substring bonus, so the same relevance order applies everywhere.
    /// </summary>
    internal static class SearchRanker
    {
        /// <summary>Ranks <paramref name="entries"/> against <paramref name="query"/>, best first.</summary>
        public static List<IndexEntry> Rank(string query, IEnumerable<IndexEntry> entries, int max)
        {
            var results = new List<IndexEntry>();
            if (string.IsNullOrWhiteSpace(query) || entries == null) return results;
            string q = query.Trim().ToLowerInvariant();

            var scored = new List<KeyValuePair<int, IndexEntry>>();
            foreach (var e in entries)
            {
                if (e == null || string.IsNullOrEmpty(e.Name)) continue;
                string nl = e.Name.ToLowerInvariant();

                int bonus;
                if (!QuickMatch(nl, q, out bonus)) continue;

                int score = Fuzz.WeightedRatio(q, nl) + bonus;
                scored.Add(new KeyValuePair<int, IndexEntry>(score, e));
            }

            return scored
                .OrderByDescending(kv => kv.Key)
                .ThenBy(kv => kv.Value.Name, StringComparer.OrdinalIgnoreCase)
                .Take(max)
                .Select(kv => kv.Value)
                .ToList();
        }

        /// <summary>
        /// Cheap gate before fuzzy scoring: requires the query to appear as an in-order
        /// subsequence of the name. Awards a bonus for exact/prefix/substring matches so
        /// they outrank loose fuzzy hits.
        /// </summary>
        public static bool QuickMatch(string nameLower, string queryLower, out int bonus)
        {
            bonus = 0;
            if (nameLower == queryLower) { bonus = 200; return true; }
            if (nameLower.StartsWith(queryLower, StringComparison.Ordinal)) { bonus = 120; return true; }

            if (nameLower.IndexOf(queryLower, StringComparison.Ordinal) >= 0) { bonus = 60; return true; }

            // Subsequence test (chars in order, gaps allowed).
            int qi = 0;
            for (int i = 0; i < nameLower.Length && qi < queryLower.Length; i++)
                if (nameLower[i] == queryLower[qi]) qi++;
            return qi == queryLower.Length;
        }
    }
}

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
        private const int MinScoreCutoff = 70;

        /// <summary>Ranks <paramref name="entries"/> against <paramref name="query"/>, best first.</summary>
        public static List<IndexEntry> Rank(string query, IEnumerable<IndexEntry> entries, int max, string activeDirectory = null)
        {
            var results = new List<IndexEntry>();
            if (string.IsNullOrWhiteSpace(query) || entries == null) return results;
            string q = query.Trim().ToLowerInvariant();

            // Split the query into terms by spaces and common directory separators
            var terms = q.Split(new[] { ' ', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (terms.Length == 0) return results;

            // Pre-fetch user profile path for stripping in path cleaning
            string userProfile = "";
            try { userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).ToLowerInvariant(); }
            catch { }

            // Run ranking in parallel using PLINQ for performance
            var scored = entries
                .AsParallel()
                .Where(e => e != null)
                .Select(e =>
                {
                    string nl = e.NameLower;
                    if (string.IsNullOrEmpty(nl)) return new KeyValuePair<int, IndexEntry>(-1, null);

                    string parentLower = GetCleanParentPath(e.ParentPath, userProfile);

                    int totalScore = 0;
                    bool allTermsMatched = true;

                    // A query-wide exact/prefix/substring bonus on the filename
                    int queryBonus = 0;
                    if (nl == q) queryBonus = 200;
                    else if (nl.StartsWith(q, StringComparison.Ordinal)) queryBonus = 120;
                    else if (nl.IndexOf(q, StringComparison.Ordinal) >= 0) queryBonus = 60;
                    else if (e.FullPath != null && e.FullPath.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) queryBonus = 30; // whole path match bonus

                    foreach (var term in terms)
                    {
                        // Check if this term matches either the name or the parent path
                        int termNameScore = 0;
                        int termPathScore = 0;

                        bool nameMatch = PassPreFilter(nl, term);
                        bool pathMatch = !string.IsNullOrEmpty(parentLower) && PassPreFilter(parentLower, term);

                        if (!nameMatch && !pathMatch)
                        {
                            allTermsMatched = false;
                            break;
                        }

                        if (nameMatch)
                        {
                            // WeightedRatio is the best FuzzySharp implementation for composite matching
                            termNameScore = Fuzz.WeightedRatio(term, nl);
                            
                            // Extra bonus if it's an exact/prefix match of the term
                            if (nl == term) termNameScore += 50;
                            else if (nl.StartsWith(term, StringComparison.Ordinal)) termNameScore += 30;
                            else if (nl.IndexOf(term, StringComparison.Ordinal) >= 0) termNameScore += 15;
                        }

                        if (pathMatch)
                        {
                            termPathScore = Fuzz.WeightedRatio(term, parentLower);
                        }

                        // Combine term scores: filename match is prioritized
                        totalScore += Math.Max(termNameScore, (int)(termPathScore * 0.6));
                    }

                    if (!allTermsMatched) return new KeyValuePair<int, IndexEntry>(-1, null);

                    // Final score is average term score plus the overall query bonus
                    int finalScore = (totalScore / terms.Length) + queryBonus;

                    return new KeyValuePair<int, IndexEntry>(finalScore, e);
                })
                .Where(kv => kv.Key >= MinScoreCutoff && kv.Value != null)
                .ToList();

            return scored
                .OrderByDescending(kv => !string.IsNullOrEmpty(activeDirectory) && 
                                         kv.Value.FullPath != null && 
                                         kv.Value.FullPath.StartsWith(activeDirectory, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(kv => kv.Key)
                .ThenBy(kv => kv.Value.Name, StringComparer.OrdinalIgnoreCase)
                .Take(max)
                .Select(kv => kv.Value)
                .ToList();
        }

        private static string GetCleanParentPath(string parentPath, string userProfile)
        {
            if (string.IsNullOrEmpty(parentPath)) return "";

            string path = parentPath.ToLowerInvariant();
            
            if (!string.IsNullOrEmpty(userProfile) && path.StartsWith(userProfile))
            {
                // Strip user profile prefix
                return path.Substring(userProfile.Length).TrimStart('\\', '/');
            }

            // Strip drive letter prefix like "c:\"
            if (path.Length >= 3 && path[1] == ':' && path[2] == '\\')
            {
                path = path.Substring(3);
            }
            
            // Also strip "users\" if it is at the start
            if (path.StartsWith("users\\"))
            {
                path = path.Substring(6);
            }
            else if (path.StartsWith("users/"))
            {
                path = path.Substring(6);
            }

            return path.TrimStart('\\', '/');
        }

        private static bool IsInOrImmediateChild(string fullPath, string activeDirectory)
        {
            if (string.IsNullOrEmpty(fullPath) || string.IsNullOrEmpty(activeDirectory)) return false;

            // Extract the directory path of the file
            string path = System.IO.Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(path)) return false;

            string normPath = path.TrimEnd('\\', '/').ToLowerInvariant();
            string normActive = activeDirectory.TrimEnd('\\', '/').ToLowerInvariant();

            // Case 1: directly in the active directory
            if (normPath == normActive) return true;

            // Case 2: in an immediate child folder of active directory
            string parentOfPath = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parentOfPath))
            {
                string normParent = parentOfPath.TrimEnd('\\', '/').ToLowerInvariant();
                if (normParent == normActive) return true;
            }

            return false;
        }

        private static bool PassPreFilter(string target, string term)
        {
            if (term.Length <= 3)
            {
                // For very short terms, require substring match (extremely fast)
                return target.IndexOf(term, StringComparison.Ordinal) >= 0;
            }

            // A term matches if its characters are mostly present in the target string
            // AND the term's first character or second character is present in the target string.
            // This is extremely fast and avoids matching completely unrelated words.
            char firstChar = term[0];
            char secondChar = term[1];
            if (target.IndexOf(firstChar) < 0 && target.IndexOf(secondChar) < 0)
            {
                return false;
            }

            int maxErrors = term.Length <= 6 ? 1 : 2;
            return FastOverlap(target, term, maxErrors);
        }

        private static bool FastOverlap(string target, string term, int maxErrors)
        {
            if (term.Length <= maxErrors) return true;

            // Check if target is mostly ASCII
            bool isAscii = true;
            for (int i = 0; i < target.Length; i++)
            {
                if (target[i] >= 256) { isAscii = false; break; }
            }
            for (int i = 0; i < term.Length; i++)
            {
                if (term[i] >= 256) { isAscii = false; break; }
            }

            if (isAscii)
            {
                int[] counts = new int[256];
                for (int i = 0; i < target.Length; i++)
                {
                    counts[target[i]]++;
                }

                int matchCount = 0;
                for (int i = 0; i < term.Length; i++)
                {
                    char c = term[i];
                    if (counts[c] > 0)
                    {
                        counts[c]--;
                        matchCount++;
                    }
                }
                return matchCount >= (term.Length - maxErrors);
            }
            else
            {
                var counts = new Dictionary<char, int>();
                for (int i = 0; i < target.Length; i++)
                {
                    char c = target[i];
                    if (counts.TryGetValue(c, out int count))
                        counts[c] = count + 1;
                    else
                        counts[c] = 1;
                }

                int matchCount = 0;
                for (int i = 0; i < term.Length; i++)
                {
                    char c = term[i];
                    if (counts.TryGetValue(c, out int count) && count > 0)
                    {
                        counts[c] = count - 1;
                        matchCount++;
                    }
                }
                return matchCount >= (term.Length - maxErrors);
            }
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

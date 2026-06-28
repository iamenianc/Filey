using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json;

namespace Filey
{
    /// <summary>
    /// Back/forward navigation stacks for a single pane. Owns the push/pop rules and the
    /// single cap on retained entries, so no caller has to re-implement the trim. The
    /// forward stack is session-only; only the back stack is persisted (see
    /// <see cref="NavigationHistoryStore"/>).
    /// </summary>
    public class NavigationHistory
    {
        /// <summary>Maximum back-stack entries retained (and persisted) per pane.</summary>
        public const int MaxEntries = 50;

        private readonly List<string> _back = new List<string>();
        private readonly List<string> _forward = new List<string>();

        public bool CanGoBack => _back.Count > 0;
        public bool CanGoForward => _forward.Count > 0;

        /// <summary>
        /// Records a navigation away from <paramref name="fromPath"/>. Pushing a new
        /// destination invalidates the forward stack, matching browser semantics.
        /// </summary>
        public void Push(string fromPath)
        {
            if (string.IsNullOrEmpty(fromPath)) return;
            _back.Add(fromPath);
            Trim(_back);
            _forward.Clear();
        }

        /// <summary>
        /// Pops the most recent back entry, recording <paramref name="currentPath"/> onto
        /// the forward stack. Returns null when there is nothing to go back to.
        /// </summary>
        public string Back(string currentPath)
        {
            if (!CanGoBack) return null;
            string prev = _back[_back.Count - 1];
            _back.RemoveAt(_back.Count - 1);
            if (!string.IsNullOrEmpty(currentPath))
                _forward.Add(currentPath);
            return prev;
        }

        /// <summary>
        /// Pops the most recent forward entry, recording <paramref name="currentPath"/> onto
        /// the back stack. Returns null when there is nothing to go forward to.
        /// </summary>
        public string Forward(string currentPath)
        {
            if (!CanGoForward) return null;
            string next = _forward[_forward.Count - 1];
            _forward.RemoveAt(_forward.Count - 1);
            if (!string.IsNullOrEmpty(currentPath))
            {
                _back.Add(currentPath);
                Trim(_back);
            }
            return next;
        }

        /// <summary>Snapshot of the back stack (oldest first) for persistence.</summary>
        public List<string> GetBackStackSnapshot() => new List<string>(_back);

        /// <summary>
        /// Replaces the back stack with persisted entries (oldest first). The forward
        /// stack is intentionally cleared and not carried across sessions.
        /// </summary>
        public void RestoreBackStack(IEnumerable<string> paths)
        {
            _back.Clear();
            _forward.Clear();
            if (paths != null)
            {
                foreach (var p in paths)
                {
                    if (!string.IsNullOrEmpty(p))
                        _back.Add(p);
                }
                Trim(_back);
            }
        }

        private static void Trim(List<string> entries)
        {
            if (entries.Count > MaxEntries)
                entries.RemoveRange(0, entries.Count - MaxEntries);
        }
    }

    /// <summary>Persisted back stacks for both panes, in history.json.</summary>
    public class NavigationHistoryRecord
    {
        public List<string> Left { get; set; } = new List<string>();
        public List<string> Right { get; set; } = new List<string>();
    }

    /// <summary>Loads and saves both panes' back stacks to history.json via AppStorage.</summary>
    internal static class NavigationHistoryStore
    {
        private const string FileName = "history.json";

        public static NavigationHistoryRecord Load()
        {
            string json = AppStorage.ReadAllTextOrNull(AppStorage.PathFor(FileName));
            if (string.IsNullOrEmpty(json)) return new NavigationHistoryRecord();

            try
            {
                return JsonConvert.DeserializeObject<NavigationHistoryRecord>(json) ?? new NavigationHistoryRecord();
            }
            catch (JsonException ex)
            {
                Debug.WriteLine($"history.json parse failed: {ex.Message}");
                return new NavigationHistoryRecord();
            }
        }

        public static void Save(NavigationHistoryRecord record)
        {
            string json = JsonConvert.SerializeObject(record, Formatting.Indented);
            AppStorage.WriteAllTextAtomic(AppStorage.PathFor(FileName), json);
        }
    }
}

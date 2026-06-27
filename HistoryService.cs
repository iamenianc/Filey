using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json;

namespace Filey
{
    /// <summary>
    /// Persisted navigation history (back stacks) for both sides, in history.json.
    /// </summary>
    public class NavigationHistory
    {
        public List<string> Left { get; set; } = new List<string>();
        public List<string> Right { get; set; } = new List<string>();
    }

    internal static class HistoryService
    {
        private const string FileName = "history.json";

        /// <summary>Maximum back-stack entries persisted per side.</summary>
        public const int MaxEntriesPerSide = 50;

        public static NavigationHistory Load()
        {
            string json = AppStorage.ReadAllTextOrNull(AppStorage.PathFor(FileName));
            if (string.IsNullOrEmpty(json)) return new NavigationHistory();

            try
            {
                return JsonConvert.DeserializeObject<NavigationHistory>(json) ?? new NavigationHistory();
            }
            catch (JsonException ex)
            {
                Debug.WriteLine($"history.json parse failed: {ex.Message}");
                return new NavigationHistory();
            }
        }

        public static void Save(NavigationHistory history)
        {
            Trim(history.Left);
            Trim(history.Right);
            string json = JsonConvert.SerializeObject(history, Formatting.Indented);
            AppStorage.WriteAllTextAtomic(AppStorage.PathFor(FileName), json);
        }

        private static void Trim(List<string> entries)
        {
            if (entries.Count > MaxEntriesPerSide)
                entries.RemoveRange(0, entries.Count - MaxEntriesPerSide);
        }
    }
}

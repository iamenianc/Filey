using System.Diagnostics;
using Newtonsoft.Json;

namespace Filey
{
    /// <summary>
    /// Loads and saves <see cref="AppSettings"/> to settings.json. A failed or absent
    /// file yields default settings so the app always starts.
    /// </summary>
    internal static class SettingsService
    {
        private const string FileName = "settings.json";

        public static AppSettings Load()
        {
            string json = AppStorage.ReadAllTextOrNull(AppStorage.PathFor(FileName));
            if (string.IsNullOrEmpty(json)) return new AppSettings();

            try
            {
                return JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
            }
            catch (JsonException ex)
            {
                Debug.WriteLine($"settings.json parse failed: {ex.Message}");
                return new AppSettings();
            }
        }

        public static void Save(AppSettings settings)
        {
            string json = JsonConvert.SerializeObject(settings, Formatting.Indented);
            AppStorage.WriteAllTextAtomic(AppStorage.PathFor(FileName), json);
        }
    }
}

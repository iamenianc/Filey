using System.Collections.Generic;

namespace Filey
{
    /// <summary>
    /// User-facing application settings persisted to %APPDATA%\Filey\settings.json.
    /// </summary>
    public class AppSettings
    {
        public string Theme { get; set; } = "dark";

        /// <summary>Per-pane Home directories. Each pane opens here on startup and when
        /// the Home button is clicked. Null/missing falls back to the user profile.</summary>
        public string LeftHomePath { get; set; }
        public string RightHomePath { get; set; }

        /// <summary>Whether the right pane is shown. Hiding it lets the left pane fill the width.</summary>
        public bool RightPaneVisible { get; set; } = true;

        /// <summary>Whether hidden/system folders and files are shown in the panes. Off by default.</summary>
        public bool ShowHidden { get; set; } = false;

        /// <summary>Whether list views use reduced row spacing (compact density).</summary>
        public bool CompactMode { get; set; } = false;

        /// <summary>
        /// Pixel widths for the six resizable content columns, in order:
        /// Left[Favourites, ParentFolders, Contents], Right[Favourites, ParentFolders, Contents].
        /// </summary>
        public List<double> SplitterPositions { get; set; } = new List<double>();
    }
}

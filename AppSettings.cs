using System.Collections.Generic;

namespace Filey
{
    /// <summary>
    /// User-facing application settings persisted to %APPDATA%\Filey\settings.json.
    /// </summary>
    public class AppSettings
    {
        public string Theme { get; set; } = "dark";

        public string LeftRootPath { get; set; }
        public string RightRootPath { get; set; }

        /// <summary>Whether the right pane is shown. Hiding it lets the left pane fill the width.</summary>
        public bool RightPaneVisible { get; set; } = true;

        /// <summary>
        /// Pixel widths for the six resizable content columns, in order:
        /// Left[Favourites, ParentFolders, Contents], Right[Favourites, ParentFolders, Contents].
        /// </summary>
        public List<double> SplitterPositions { get; set; } = new List<double>();
    }
}

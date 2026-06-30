using System;

namespace Filey
{
    /// <summary>
    /// One indexed file or folder. Deliberately minimal — name/path/parent plus the bit
    /// of metadata search results show (size, modified time) — so a few tens of thousands
    /// of entries stay cheap in memory. No file contents are stored.
    /// </summary>
    public class IndexEntry
    {
        public string Name { get; set; }
        public string FullPath { get; set; }
        public string ParentPath { get; set; }
        public bool IsDirectory { get; set; }
        public long Size { get; set; }
        public DateTime DateModifiedUtc { get; set; }

        /// <summary>Projects an index hit into the UI's row model for the results list.</summary>
        public FolderItem ToFolderItem()
        {
            DateTime? modified = DateModifiedUtc == DateTime.MinValue
                ? (DateTime?)null
                : DateModifiedUtc.ToLocalTime();

            return new FolderItem
            {
                Name = Name,
                FullPath = FullPath,
                IsDirectory = IsDirectory,
                DateModified = modified,
                Size = IsDirectory ? (long?)null : Size,
                Extension = IsDirectory ? string.Empty : System.IO.Path.GetExtension(Name),
                Type = IsDirectory ? "Folder" : "File"
            };
        }
    }
}

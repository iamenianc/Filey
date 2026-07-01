using System;
using Newtonsoft.Json;

namespace Filey
{
    /// <summary>
    /// One indexed file or folder. Deliberately minimal — name/path/parent plus the bit
    /// of metadata search results show (size, modified time) — so a few tens of thousands
    /// of entries stay cheap in memory. No file contents are stored.
    /// </summary>
    public class IndexEntry
    {
        private string _name;

        public string Name
        {
            get => _name;
            set
            {
                _name = value;
                NameLower = value?.ToLowerInvariant();
            }
        }

        /// <summary>
        /// Lowercased <see cref="Name"/>, cached so search doesn't re-lowercase every entry on
        /// each keystroke. Derived from <see cref="Name"/> (recomputed on set / deserialize),
        /// so it is not persisted.
        /// </summary>
        [JsonIgnore]
        public string NameLower { get; private set; }

        public int ParentId { get; set; }

        [JsonIgnore]
        public string ParentPath => DirectoryRegistry.Instance.GetPath(ParentId);

        [JsonIgnore]
        public string FullPath => System.IO.Path.Combine(ParentPath ?? "", Name);

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

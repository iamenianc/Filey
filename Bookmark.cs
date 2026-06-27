using System;
using System.Collections.ObjectModel;

namespace Filey
{
    public class Bookmark : ViewModelBase
    {
        private string _name;
        private string _folderGroup;
        private bool _isEditing;

        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string Path { get; set; }

        public string Name
        {
            get => _name;
            set => SetField(ref _name, value);
        }

        /// <summary>Optional group name. Bookmarks with the same group are shown together.</summary>
        public string FolderGroup
        {
            get => _folderGroup;
            set => SetField(ref _folderGroup, value);
        }

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public bool IsEditing
        {
            get => _isEditing;
            set => SetField(ref _isEditing, value);
        }

        public Bookmark Clone()
        {
            return new Bookmark
            {
                Id = Guid.NewGuid().ToString(),
                Path = Path,
                Name = Name,
                FolderGroup = FolderGroup,
                CreatedAt = DateTime.Now
            };
        }
    }
}

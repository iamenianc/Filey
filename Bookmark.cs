using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Filey
{
    public class Bookmark : ViewModelBase
    {
        private string _name;
        private bool _isEditing;

        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string Path { get; set; }

        public string Name
        {
            get => _name;
            set => SetField(ref _name, value);
        }

        public ObservableCollection<string> Tags { get; set; } = new ObservableCollection<string>();

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        // Transient UI state (not persisted) — drives inline rename, same pattern as FolderItem.
        public bool IsEditing
        {
            get => _isEditing;
            set => SetField(ref _isEditing, value);
        }

        public bool HasTags => Tags != null && Tags.Count > 0;

        public Bookmark Clone()
        {
            return new Bookmark
            {
                Id = Guid.NewGuid().ToString(),
                Path = Path,
                Name = Name,
                Tags = new ObservableCollection<string>(Tags ?? new ObservableCollection<string>()),
                CreatedAt = DateTime.Now
            };
        }
    }
}

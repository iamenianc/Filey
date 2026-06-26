using System;
using System.Windows.Media;

namespace Filey
{
    public class FolderItem : ViewModelBase
    {
        private bool _isEditing;
        
        public string Name { get; set; }
        public string FullPath { get; set; }
        public bool IsDirectory { get; set; }
        public DateTime? DateModified { get; set; }
        public long? Size { get; set; }
        public string Extension { get; set; }
        public string Type { get; set; }
        public ImageSource Icon { get; set; }

        public bool IsEditing
        {
            get => _isEditing;
            set => SetField(ref _isEditing, value);
        }

        public string SizeFormatted
        {
            get
            {
                if (IsDirectory || !Size.HasValue)
                    return string.Empty;

                long bytes = Size.Value;
                if (bytes >= 1073741824)
                    return $"{(bytes / 1073741824.0):N1} GB";
                if (bytes >= 1048576)
                    return $"{(bytes / 1048576.0):N1} MB";
                if (bytes >= 1024)
                    return $"{(bytes / 1024.0):N1} KB";
                return $"{bytes} B";
            }
        }

        public string DateModifiedFormatted => DateModified?.ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty;
    }
}

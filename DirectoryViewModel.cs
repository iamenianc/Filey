using System;
using System.Collections.ObjectModel;
using System.IO;

namespace Filey
{
    public class DirectoryViewModel : ViewModelBase
    {
        private string _currentPath;
        private ObservableCollection<FolderItem> _folders;
        private ObservableCollection<FolderItem> _contents;

        public DirectoryViewModel()
        {
            _folders = new ObservableCollection<FolderItem>();
            _contents = new ObservableCollection<FolderItem>();
        }

        public string CurrentPath
        {
            get => _currentPath;
            set => SetField(ref _currentPath, value);
        }

        public ObservableCollection<FolderItem> Folders
        {
            get => _folders;
            set => SetField(ref _folders, value);
        }

        public ObservableCollection<FolderItem> Contents
        {
            get => _contents;
            set => SetField(ref _contents, value);
        }

        public void LoadDirectory(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                    return;

                Folders.Clear();
                Contents.Clear();

                var dirInfo = new DirectoryInfo(path);

                // 1. Load Folders (for folders list)
                var folderList = new System.Collections.Generic.List<FolderItem>();
                try
                {
                    foreach (var dir in dirInfo.GetDirectories())
                    {
                        try
                        {
                            var item = CreateFolderItem(dir);
                            folderList.Add(item);
                        }
                        catch (UnauthorizedAccessException) { }
                        catch (FileNotFoundException) { }
                        catch (DirectoryNotFoundException) { }
                        catch (Exception) { }
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (Exception) { }

                // Sort folders alphabetically
                folderList.Sort((x, y) => string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase));
                foreach (var item in folderList)
                {
                    Folders.Add(item);
                }

                // 2. Load Contents (for contents list, including both folders and files)
                var contentList = new System.Collections.Generic.List<FolderItem>();

                // Add folders to contents
                foreach (var folder in Folders)
                {
                    contentList.Add(folder);
                }

                // Add files to contents
                try
                {
                    foreach (var file in dirInfo.GetFiles())
                    {
                        try
                        {
                            var item = CreateFolderItem(file);
                            contentList.Add(item);
                        }
                        catch (UnauthorizedAccessException) { }
                        catch (FileNotFoundException) { }
                        catch (Exception) { }
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (Exception) { }

                foreach (var item in contentList)
                {
                    Contents.Add(item);
                }

                CurrentPath = path;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading directory: {ex.Message}");
            }
        }

        private FolderItem CreateFolderItem(FileSystemInfo info)
        {
            var item = new FolderItem
            {
                Name = info.Name,
                FullPath = info.FullName,
                DateModified = info.LastWriteTime
            };

            if (info is DirectoryInfo)
            {
                item.IsDirectory = true;
                item.Size = null;
                item.Extension = string.Empty;
                item.Type = "File folder";
                item.Icon = IconHelper.GetFolderIcon();
            }
            else if (info is FileInfo fileInfo)
            {
                item.IsDirectory = false;
                item.Size = fileInfo.Length;
                item.Extension = fileInfo.Extension;
                item.Type = GetFileTypeDescription(fileInfo.Extension);
                item.Icon = IconHelper.GetFileIcon(fileInfo.FullName);
            }

            return item;
        }

        private string GetFileTypeDescription(string extension)
        {
            if (string.IsNullOrEmpty(extension))
                return "File";

            string ext = extension.TrimStart('.').ToUpper();
            if (string.IsNullOrEmpty(ext)) return "File";
            return $"{ext} File";
        }
    }
}

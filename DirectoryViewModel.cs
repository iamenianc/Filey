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
        private ObservableCollection<FolderItem> _parentFolders;
        private FolderItem _selectedParentFolder;
        private FolderItem _selectedItem;

        private readonly System.Collections.Generic.List<string> _backStack = new System.Collections.Generic.List<string>();
        private readonly System.Collections.Generic.List<string> _forwardStack = new System.Collections.Generic.List<string>();

        /// <summary>
        /// When false (default) entries flagged Hidden or System are filtered out of all
        /// panes. Shared across both sides; persisted in settings.json.
        /// </summary>
        public static bool ShowHidden { get; set; } = false;

        private static bool IsHidden(FileSystemInfo info)
        {
            var attr = info.Attributes;
            return (attr & (FileAttributes.Hidden | FileAttributes.System)) != 0;
        }

        public DirectoryViewModel()
        {
            _folders = new ObservableCollection<FolderItem>();
            _contents = new ObservableCollection<FolderItem>();
            _parentFolders = new ObservableCollection<FolderItem>();
        }

        public string CurrentPath
        {
            get => _selectedItem != null ? _selectedItem.FullPath : _currentPath;
            set
            {
                if (SetField(ref _currentPath, value))
                {
                    OnPropertyChanged(nameof(ParentFolderName));
                }
            }
        }

        /// <summary>The loaded directory, ignoring any selected file (unlike CurrentPath).</summary>
        public string CurrentDirectory => _currentPath;

        public bool CanGoBack => _backStack.Count > 0;
        public bool CanGoForward => _forwardStack.Count > 0;

        public bool CanGoToParent
        {
            get
            {
                if (string.IsNullOrEmpty(CurrentPath)) return false;
                try
                {
                    var dirInfo = new DirectoryInfo(CurrentPath);
                    return dirInfo.Parent != null;
                }
                catch
                {
                    return false;
                }
            }
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

        public ObservableCollection<FolderItem> ParentFolders
        {
            get => _parentFolders;
            set => SetField(ref _parentFolders, value);
        }

        public FolderItem SelectedParentFolder
        {
            get => _selectedParentFolder;
            set
            {
                if (SetField(ref _selectedParentFolder, value))
                {
                    if (value != null && value.IsDirectory && value.FullPath != CurrentPath)
                    {
                        LoadDirectory(value.FullPath);
                    }
                }
            }
        }

        public FolderItem SelectedItem
        {
            get => _selectedItem;
            set
            {
                if (SetField(ref _selectedItem, value))
                {
                    OnPropertyChanged(nameof(CurrentPath));
                    OnPropertyChanged(nameof(ParentFolderName));
                }
            }
        }

        public string ParentFolderName
        {
            get
            {
                if (string.IsNullOrEmpty(CurrentPath)) return string.Empty;
                try
                {
                    var dirInfo = new DirectoryInfo(CurrentPath);
                    return dirInfo.Parent != null ? dirInfo.Parent.Name : string.Empty;
                }
                catch
                {
                    return string.Empty;
                }
            }
        }

        /// <summary>Snapshot of the back stack (oldest first) for persistence.</summary>
        public System.Collections.Generic.List<string> GetBackStackSnapshot()
        {
            return new System.Collections.Generic.List<string>(_backStack);
        }

        /// <summary>
        /// Replaces the back stack with persisted history (oldest first). The forward
        /// stack is intentionally left empty across sessions.
        /// </summary>
        public void RestoreBackStack(System.Collections.Generic.IEnumerable<string> paths)
        {
            _backStack.Clear();
            _forwardStack.Clear();
            if (paths != null)
            {
                foreach (var p in paths)
                {
                    if (!string.IsNullOrEmpty(p))
                        _backStack.Add(p);
                }
                if (_backStack.Count > 50)
                    _backStack.RemoveRange(0, _backStack.Count - 50);
            }
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(CanGoForward));
        }

        public void GoToParent()
        {
            if (string.IsNullOrEmpty(CurrentPath)) return;
            try
            {
                var dirInfo = new DirectoryInfo(CurrentPath);
                if (dirInfo.Parent != null)
                {
                    LoadDirectory(dirInfo.Parent.FullName);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error going to parent: {ex.Message}");
            }
        }

        public void GoBack()
        {
            if (!CanGoBack) return;
            string prevPath = _backStack[_backStack.Count - 1];
            _backStack.RemoveAt(_backStack.Count - 1);
            if (!string.IsNullOrEmpty(CurrentPath))
            {
                _forwardStack.Add(CurrentPath);
            }
            LoadDirectory(prevPath, pushToHistory: false);
        }

        public void GoForward()
        {
            if (!CanGoForward) return;
            string nextPath = _forwardStack[_forwardStack.Count - 1];
            _forwardStack.RemoveAt(_forwardStack.Count - 1);
            if (!string.IsNullOrEmpty(CurrentPath))
            {
                _backStack.Add(CurrentPath);
                if (_backStack.Count > 50)
                {
                    _backStack.RemoveAt(0);
                }
            }
            LoadDirectory(nextPath, pushToHistory: false);
        }

        public void LoadDirectory(string path)
        {
            LoadDirectory(path, pushToHistory: true);
        }

        public void LoadDirectory(string path, bool pushToHistory)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                    return;

                string oldPath = _currentPath;

                Folders.Clear();
                Contents.Clear();
                ParentFolders.Clear();

                SelectedItem = null;

                var dirInfo = new DirectoryInfo(path);

                // 0. Load ParentFolders (folders in parent directory)
                if (dirInfo.Parent != null)
                {
                    var parentFolderList = new System.Collections.Generic.List<FolderItem>();
                    try
                    {
                        foreach (var dir in dirInfo.Parent.GetDirectories())
                        {
                            try
                            {
                                if (!ShowHidden && IsHidden(dir)) continue;
                                var item = CreateFolderItem(dir);
                                parentFolderList.Add(item);
                            }
                            catch (UnauthorizedAccessException) { }
                            catch (FileNotFoundException) { }
                            catch (DirectoryNotFoundException) { }
                            catch (Exception) { }
                        }
                    }
                    catch (UnauthorizedAccessException) { }
                    catch (Exception) { }

                    parentFolderList.Sort((x, y) => string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase));
                    foreach (var item in parentFolderList)
                    {
                        ParentFolders.Add(item);
                    }
                }

                // 1. Load Folders (for folders list)
                var folderList = new System.Collections.Generic.List<FolderItem>();
                try
                {
                    foreach (var dir in dirInfo.GetDirectories())
                    {
                        try
                        {
                            if (!ShowHidden && IsHidden(dir)) continue;
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

                // 2. Load Contents (for contents list, including only files now)
                var contentList = new System.Collections.Generic.List<FolderItem>();

                // Add files to contents (sorted alphabetically ascending)
                var fileList = new System.Collections.Generic.List<FolderItem>();
                try
                {
                    foreach (var file in dirInfo.GetFiles())
                    {
                        try
                        {
                            if (!ShowHidden && IsHidden(file)) continue;
                            var item = CreateFolderItem(file);
                            fileList.Add(item);
                        }
                        catch (UnauthorizedAccessException) { }
                        catch (FileNotFoundException) { }
                        catch (Exception) { }
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (Exception) { }

                fileList.Sort((x, y) => string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase));
                foreach (var file in fileList)
                {
                    contentList.Add(file);
                }

                foreach (var item in contentList)
                {
                    Contents.Add(item);
                }

                CurrentPath = dirInfo.FullName;

                FolderItem matchingParentItem = null;
                foreach (var item in ParentFolders)
                {
                    if (string.Equals(item.FullPath, CurrentPath, StringComparison.OrdinalIgnoreCase))
                    {
                        matchingParentItem = item;
                        break;
                    }
                }
                SelectedParentFolder = matchingParentItem;

                if (pushToHistory && !string.IsNullOrEmpty(oldPath) && oldPath != CurrentPath)
                {
                    _backStack.Add(oldPath);
                    if (_backStack.Count > 50)
                    {
                        _backStack.RemoveAt(0);
                    }
                    _forwardStack.Clear();
                }

                OnPropertyChanged(nameof(CanGoBack));
                OnPropertyChanged(nameof(CanGoForward));
                OnPropertyChanged(nameof(CanGoToParent));
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

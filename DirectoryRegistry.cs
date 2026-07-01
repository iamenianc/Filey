using System;
using System.Collections.Generic;

namespace Filey
{
    internal sealed class DirectoryNode
    {
        private static readonly string UserProfileLower = GetUserProfileLower();
        private static string GetUserProfileLower()
        {
            try { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).ToLowerInvariant(); }
            catch { return ""; }
        }

        public int Id { get; }
        public string Path { get; }
        public string PathLower { get; }
        public char Drive { get; }
        public string CleanPathLower { get; }
        public string[] Segments { get; }

        public DirectoryNode(int id, string path)
        {
            Id = id;
            Path = path;
            PathLower = path?.ToLowerInvariant();
            Drive = path != null && path.Length >= 2 && path[1] == ':' ? char.ToUpperInvariant(path[0]) : '\0';
            Segments = path != null ? path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries) : new string[0];

            if (string.IsNullOrEmpty(PathLower))
            {
                CleanPathLower = "";
            }
            else
            {
                string cp = PathLower;
                if (!string.IsNullOrEmpty(UserProfileLower) && cp.StartsWith(UserProfileLower))
                {
                    cp = cp.Substring(UserProfileLower.Length).TrimStart('\\', '/');
                }
                else if (cp.Length >= 3 && cp[1] == ':' && cp[2] == '\\')
                {
                    cp = cp.Substring(3);
                }

                if (cp.StartsWith("users\\") || cp.StartsWith("users/"))
                {
                    cp = cp.Substring(6);
                }
                CleanPathLower = cp.TrimStart('\\', '/');
            }
        }
    }

    internal sealed class DirectoryRegistry
    {
        private static readonly DirectoryRegistry _instance = new DirectoryRegistry();
        public static DirectoryRegistry Instance => _instance;

        private readonly object _gate = new object();
        private readonly List<DirectoryNode> _idToNode = new List<DirectoryNode>();
        private readonly Dictionary<string, int> _pathToId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private DirectoryRegistry() { }

        public int GetOrAdd(string path)
        {
            if (string.IsNullOrEmpty(path)) return -1;
            string normalized = NormalizeAndClean(path);
            lock (_gate)
            {
                if (_pathToId.TryGetValue(normalized, out int id))
                    return id;

                id = _idToNode.Count;
                var node = new DirectoryNode(id, normalized);
                _idToNode.Add(node);
                _pathToId[normalized] = id;
                return id;
            }
        }

        public static string NormalizeAndClean(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";

            char[] buffer = System.Buffers.ArrayPool<char>.Shared.Rent(path.Length);
            try
            {
                int len = 0;
                bool lastWasSeparator = false;
                for (int i = 0; i < path.Length; i++)
                {
                    char c = path[i];
                    if (c == '/' || c == '\\')
                    {
                        if (!lastWasSeparator)
                        {
                            buffer[len++] = '\\';
                            lastWasSeparator = true;
                        }
                    }
                    else
                    {
                        buffer[len++] = c;
                        lastWasSeparator = false;
                    }
                }

                if (len > 3 && buffer[len - 1] == '\\')
                {
                    len--;
                }

                return new string(buffer, 0, len);
            }
            finally
            {
                System.Buffers.ArrayPool<char>.Shared.Return(buffer);
            }
        }

        public DirectoryNode GetNode(int id)
        {
            if (id < 0) return null;
            lock (_gate)
            {
                if (id < _idToNode.Count)
                    return _idToNode[id];
                return null;
            }
        }

        public string GetPath(int id)
        {
            return GetNode(id)?.Path;
        }

        public void Clear()
        {
            lock (_gate)
            {
                _idToNode.Clear();
                _pathToId.Clear();
            }
        }

        public List<string> GetPathsSnapshot()
        {
            lock (_gate)
            {
                var list = new List<string>(_idToNode.Count);
                foreach (var node in _idToNode)
                {
                    list.Add(node?.Path);
                }
                return list;
            }
        }

        public void LoadPaths(IEnumerable<string> paths)
        {
            if (paths == null) return;
            lock (_gate)
            {
                Clear();
                foreach (var path in paths)
                {
                    if (string.IsNullOrEmpty(path)) continue;
                    int id = _idToNode.Count;
                    var node = new DirectoryNode(id, path);
                    _idToNode.Add(node);
                    _pathToId[path] = id;
                }
            }
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Filey
{
    public class SQLiteIndexService : IDisposable
    {
        private static readonly Lazy<SQLiteIndexService> _instance = 
            new Lazy<SQLiteIndexService>(() => new SQLiteIndexService());

        public static SQLiteIndexService Instance => _instance.Value;

        private readonly string _dbPath;
        private readonly string _connectionString;
        private readonly BlockingCollection<DbWriteCommand> _writeQueue;
        private readonly Thread _writerThread;
        private readonly CancellationTokenSource _cts;
        private bool _isDisposed;

        private const int BatchSize = 10000;
        private const int BatchTimeoutMs = 500;

        private SQLiteIndexService()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string fileyFolder = Path.Combine(appData, "Filey");
            Directory.CreateDirectory(fileyFolder);
            
            _dbPath = Path.Combine(fileyFolder, "file_index.db");
            _connectionString = $"Data Source={_dbPath};Version=3;Journal Mode=WAL;Synchronous=Normal;Cache Size=-2000;Temp Store=Memory;";

            InitializeDatabase();

            _writeQueue = new BlockingCollection<DbWriteCommand>(new ConcurrentQueue<DbWriteCommand>());
            _cts = new CancellationTokenSource();
            
            _writerThread = new Thread(ProcessWriteQueue)
            {
                IsBackground = true,
                Name = "FileySQLiteWriter",
                Priority = ThreadPriority.BelowNormal
            };
            _writerThread.Start();
        }

        private void InitializeDatabase()
        {
            if (!File.Exists(_dbPath))
            {
                SQLiteConnection.CreateFile(_dbPath);
            }

            using (var conn = new SQLiteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
                        CREATE TABLE IF NOT EXISTS IndexEntries (
                            Path TEXT PRIMARY KEY,
                            Name TEXT NOT NULL,
                            ParentPath TEXT NOT NULL,
                            Size INTEGER NOT NULL,
                            LastWriteTime INTEGER NOT NULL,
                            IsDirectory INTEGER NOT NULL
                        );
                        CREATE INDEX IF NOT EXISTS idx_entries_name ON IndexEntries(Name);
                        CREATE INDEX IF NOT EXISTS idx_entries_parent ON IndexEntries(ParentPath);";
                    cmd.ExecuteNonQuery();
                }
            }
        }

        #region Public Write APIs

        public void UpsertEntry(IndexEntry entry)
        {
            EnsureNotDisposed();
            _writeQueue.Add(new DbWriteCommand { Type = WriteType.Upsert, Entry = entry });
        }

        public void UpsertEntries(IEnumerable<IndexEntry> entries)
        {
            EnsureNotDisposed();
            foreach (var entry in entries)
            {
                _writeQueue.Add(new DbWriteCommand { Type = WriteType.Upsert, Entry = entry });
            }
        }

        public void RemoveEntry(string path)
        {
            EnsureNotDisposed();
            _writeQueue.Add(new DbWriteCommand { Type = WriteType.Delete, Path = path });
        }

        public void ClearIndex()
        {
            EnsureNotDisposed();
            _writeQueue.Add(new DbWriteCommand { Type = WriteType.Clear });
        }

        public void DeleteSubtree(string rootPath)
        {
            EnsureNotDisposed();
            _writeQueue.Add(new DbWriteCommand { Type = WriteType.DeleteSubtree, Path = rootPath });
        }

        public void ReplaceDirectoryLevel(string dirPath, IEnumerable<IndexEntry> entries)
        {
            EnsureNotDisposed();
            _writeQueue.Add(new DbWriteCommand { Type = WriteType.ReplaceDirectoryLevel, Path = dirPath, Entries = entries?.ToList() });
        }

        #endregion

        #region Async Writer Processing Loop

        private void ProcessWriteQueue()
        {
            var batch = new List<DbWriteCommand>();
            
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    if (_writeQueue.TryTake(out var item, Timeout.Infinite, _cts.Token))
                    {
                        batch.Add(item);

                        var startTime = DateTime.UtcNow;
                        while (batch.Count < BatchSize && (DateTime.UtcNow - startTime).TotalMilliseconds < BatchTimeoutMs)
                        {
                            if (_writeQueue.TryTake(out var extraItem, 10, _cts.Token))
                            {
                                batch.Add(extraItem);
                            }
                            else
                            {
                                break;
                            }
                        }

                        ExecuteBatch(batch);
                        batch.Clear();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error writing to SQLite index: {ex.Message}");
                    Thread.Sleep(1000); 
                }
            }

            if (_writeQueue.Count > 0)
            {
                ExecuteBatch(_writeQueue.ToList());
            }
        }

        private void ExecuteBatch(List<DbWriteCommand> commands)
        {
            if (commands == null || commands.Count == 0) return;

            using (var conn = new SQLiteConnection(_connectionString))
            {
                conn.Open();
                using (var transaction = conn.BeginTransaction())
                {
                    using (var upsertCmd = conn.CreateCommand())
                    {
                        upsertCmd.CommandText = @"
                            INSERT INTO IndexEntries (Path, Name, ParentPath, Size, LastWriteTime, IsDirectory)
                            VALUES (@Path, @Name, @ParentPath, @Size, @LastWriteTime, @IsDirectory)
                            ON CONFLICT(Path) DO UPDATE SET
                                Name = excluded.Name,
                                ParentPath = excluded.ParentPath,
                                Size = excluded.Size,
                                LastWriteTime = excluded.LastWriteTime,
                                IsDirectory = excluded.IsDirectory;";

                        var pPath = upsertCmd.Parameters.Add("@Path", System.Data.DbType.String);
                        var pName = upsertCmd.Parameters.Add("@Name", System.Data.DbType.String);
                        var pParent = upsertCmd.Parameters.Add("@ParentPath", System.Data.DbType.String);
                        var pSize = upsertCmd.Parameters.Add("@Size", System.Data.DbType.Int64);
                        var pTime = upsertCmd.Parameters.Add("@LastWriteTime", System.Data.DbType.Int64);
                        var pIsDir = upsertCmd.Parameters.Add("@IsDirectory", System.Data.DbType.Int32);

                        using (var deleteCmd = conn.CreateCommand())
                        {
                            deleteCmd.CommandText = "DELETE FROM IndexEntries WHERE Path = @Path OR Path LIKE @PathPrefix;";
                            var pDelPath = deleteCmd.Parameters.Add("@Path", System.Data.DbType.String);
                            var pDelPathPrefix = deleteCmd.Parameters.Add("@PathPrefix", System.Data.DbType.String);

                            using (var clearCmd = conn.CreateCommand())
                            {
                                clearCmd.CommandText = "DELETE FROM IndexEntries;";

                                using (var deleteDirCmd = conn.CreateCommand())
                                {
                                    deleteDirCmd.CommandText = "DELETE FROM IndexEntries WHERE ParentPath = @ParentPath;";
                                    var pDelParent = deleteDirCmd.Parameters.Add("@ParentPath", System.Data.DbType.String);

                                    foreach (var cmd in commands)
                                    {
                                        switch (cmd.Type)
                                        {
                                            case WriteType.Upsert:
                                                pPath.Value = cmd.Entry.Path;
                                                pName.Value = cmd.Entry.Name;
                                                pParent.Value = cmd.Entry.ParentPath;
                                                pSize.Value = cmd.Entry.Size;
                                                pTime.Value = cmd.Entry.LastWriteTime;
                                                pIsDir.Value = cmd.Entry.IsDirectory ? 1 : 0;
                                                upsertCmd.ExecuteNonQuery();
                                                break;

                                            case WriteType.Delete:
                                            case WriteType.DeleteSubtree:
                                                pDelPath.Value = cmd.Path;
                                                pDelPathPrefix.Value = cmd.Path.EndsWith("\\") ? cmd.Path + "%" : cmd.Path + "\\%";
                                                deleteCmd.ExecuteNonQuery();
                                                break;

                                            case WriteType.Clear:
                                                clearCmd.ExecuteNonQuery();
                                                break;

                                            case WriteType.ReplaceDirectoryLevel:
                                                pDelParent.Value = cmd.Path;
                                                deleteDirCmd.ExecuteNonQuery();
                                                if (cmd.Entries != null)
                                                {
                                                    foreach (var entry in cmd.Entries)
                                                    {
                                                        pPath.Value = entry.Path;
                                                        pName.Value = entry.Name;
                                                        pParent.Value = entry.ParentPath;
                                                        pSize.Value = entry.Size;
                                                        pTime.Value = entry.LastWriteTime;
                                                        pIsDir.Value = entry.IsDirectory ? 1 : 0;
                                                        upsertCmd.ExecuteNonQuery();
                                                    }
                                                }
                                                break;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    transaction.Commit();
                }
            }
        }

        #endregion

        #region High-Performance Search APIs

        public async Task<List<IndexEntry>> GetSearchCandidatesAsync(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return new List<IndexEntry>();

            return await Task.Run(() =>
            {
                var candidates = new List<IndexEntry>();
                var terms = query.Split(new[] { ' ', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                if (terms.Length == 0) return candidates;

                using (var conn = new SQLiteConnection(_connectionString))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        var whereClauses = new List<string>();
                        for (int i = 0; i < terms.Length; i++)
                        {
                            whereClauses.Add($"(Name LIKE @Term{i} OR ParentPath LIKE @Term{i})");
                        }

                        cmd.CommandText = $@"
                            SELECT Path, Name, ParentPath, Size, LastWriteTime, IsDirectory 
                            FROM IndexEntries 
                            WHERE {string.Join(" AND ", whereClauses)}
                            LIMIT 3000;";

                        for (int i = 0; i < terms.Length; i++)
                        {
                            cmd.Parameters.AddWithValue($"@Term{i}", $"%{terms[i]}%");
                        }

                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                candidates.Add(new IndexEntry
                                {
                                    Path = reader.GetString(0),
                                    Name = reader.GetString(1),
                                    ParentPath = reader.GetString(2),
                                    Size = reader.GetInt64(3),
                                    LastWriteTime = reader.GetInt64(4),
                                    IsDirectory = reader.GetInt32(5) == 1
                                });
                            }
                        }
                    }
                }
                return candidates;
            });
        }

        public List<IndexEntry> GetChildren(string parentPath)
        {
            var results = new List<IndexEntry>();
            using (var conn = new SQLiteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT Path, Name, ParentPath, Size, LastWriteTime, IsDirectory FROM IndexEntries WHERE ParentPath = @ParentPath;";
                    cmd.Parameters.AddWithValue("@ParentPath", parentPath);

                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            results.Add(new IndexEntry
                            {
                                Path = reader.GetString(0),
                                Name = reader.GetString(1),
                                ParentPath = reader.GetString(2),
                                Size = reader.GetInt64(3),
                                LastWriteTime = reader.GetInt64(4),
                                IsDirectory = reader.GetInt32(5) == 1
                            });
                        }
                    }
                }
            }
            return results;
        }

        public long GetTotalEntryCount()
        {
            using (var conn = new SQLiteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM IndexEntries;";
                    return (long)cmd.ExecuteScalar();
                }
            }
        }

        #endregion

        #region Helper Structs & IDisposable

        private enum WriteType { Upsert, Delete, Clear, DeleteSubtree, ReplaceDirectoryLevel }

        private struct DbWriteCommand
        {
            public WriteType Type { get; set; }
            public IndexEntry Entry { get; set; }
            public string Path { get; set; }
            public List<IndexEntry> Entries { get; set; }
        }

        private void EnsureNotDisposed()
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(SQLiteIndexService));
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            _cts.Cancel();
            try
            {
                _writerThread.Join(2000);
            }
            catch { }

            _writeQueue.Dispose();
            _cts.Dispose();
        }

        #endregion
    }
}
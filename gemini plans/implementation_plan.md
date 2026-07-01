# High-Performance Directory Indexing Optimizations

This plan outlines optimizations to the Filey indexing architecture. It reduces memory usage, prevents GC thrashing, and decouples disk-bound directory reading from CPU-bound index parsing.

## User Review Required

> [!IMPORTANT]
> **Win32 Directory Enumeration Info Level**:
> The `NativeDirectoryEnumerator.cs` file is **already** using `FindFirstFileEx` (the Unicode-mapped `FindFirstFileExW`) with `FINDEX_INFO_LEVELS.FindExInfoBasic` and `FIND_FIRST_EX_LARGE_FETCH`. No changes are required for this specific Win32 API portion.
>
> **File Index Serialization Format**:
> We will change `index.json` format to serialize the directory paths array along with the index entries. This will break backward compatibility with any existing `index.json`, but will transparently recreate it on the next crawl.

## Open Questions

None. The requirements for threading, memory reduction, and buffer pooling are clear and standard high-performance patterns.

---

## Proposed Changes

### Core Project Dependencies

We will add a NuGet reference to support buffer recycling.

#### [MODIFY] [Filey.csproj](file:///c:/Users/ianch/sourecode/repos/Filey/Filey.csproj)
- Add `<PackageReference Include="System.Buffers" Version="4.5.1" />` to enable `System.Buffers.ArrayPool<T>` support under .NET Framework 4.8.

---

### Index Path Memory Minimization

Instead of storing full path strings in every `IndexEntry` (which duplicates long parent path prefixes millions of times), we will introduce a `DirectoryRegistry` to assign integer IDs to directories, and store only the ID in the file entry.

#### [NEW] [DirectoryRegistry.cs](file:///c:/Users/ianch/sourecode/repos/Filey/DirectoryRegistry.cs)
- Implement a thread-safe registry mapping directories to integer IDs.
- Store `DirectoryNode` containing:
  - `string Path` (original folder path, stored exactly once in memory)
  - `string PathLower` (pre-lowercased path to avoid runtime allocations during search)
  - `char Drive` (cached drive letter for active drive filtering)
- Provide fast ID lookup: `int GetOrAdd(string path)` and `DirectoryNode GetNode(int id)`.

#### [MODIFY] [IndexEntry.cs](file:///c:/Users/ianch/sourecode/repos/Filey/IndexEntry.cs)
- Remove `FullPath` and `ParentPath` string fields/properties.
- Add `int ParentId { get; set; }` to store the reference to the parent directory node.
- Re-expose `FullPath` and `ParentPath` as dynamic, non-serialized `[JsonIgnore]` properties computed via `DirectoryRegistry`:
  - `ParentPath => DirectoryRegistry.Instance.GetPath(ParentId)`
  - `FullPath => Path.Combine(ParentPath ?? "", Name)`
- Update `ToFolderItem()` to construct the path on the fly (only done for final UI projection).

#### [MODIFY] [FileIndex.cs](file:///c:/Users/ianch/sourecode/repos/Filey/FileIndex.cs)
- Modify persistence: serialize/deserialize a wrapper payload containing the registry's list of paths and the list of entries.
- Adjust `AddOrUpdate`, `Remove`, `ReplaceSubtree`, and `ReplaceDirectoryLevel` to handle the key entries efficiently.

#### [MODIFY] [IndexWatcher.cs](file:///c:/Users/ianch/sourecode/repos/Filey/IndexWatcher.cs)
- Update file indexing to resolve parent directory IDs via `DirectoryRegistry.Instance.GetOrAdd(fi.DirectoryName)` and set `ParentId` instead of `FullPath` / `ParentPath` string properties.

---

### Channel-Based Thread Architecture & Buffer Pooling

We will decouple directory reading (Thread A) from tokenization and entry population (Threads B-Z) using a producer-consumer setup, and reuse character buffers to avoid GC Gen 2 promotion.

#### [MODIFY] [FileSystemCrawler.cs](file:///c:/Users/ianch/sourecode/repos/FileSystemCrawler.cs)
- Replace single-threaded DFS crawl with a concurrent producer-consumer model:
  - **Thread A (The Reader)**: Walks directories sequentially using a local stack and `NativeDirectoryEnumerator.EnumerateEntries`. Pushes raw `NativeFileEntry` objects along with their parent path into a bounded `BlockingCollection<FileScanResult>` (size cap 10,000 to prevent runaway memory if consumers lag).
  - **Threads B-Z (The Consumers)**: Spawned on thread pool tasks. Pull scan results, resolve parent IDs in the thread-safe registry, parse metadata, and populate `IndexEntry` structures.
  - Collect results in a `ConcurrentBag<IndexEntry>` and merge them into the index.
- Use `System.Buffers.ArrayPool<char>.Shared` to rent working buffers during folder parsing and token extraction instead of allocating temporary substrings.

#### [MODIFY] [SearchRanker.cs](file:///c:/Users/ianch/sourecode/repos/Filey/SearchRanker.cs)
- Optimize matching loop: instead of extracting `e.ParentPath` and lowercasing it dynamically per search term check (which allocates memory inside the PLINQ query), query the cached lowercase fields on `DirectoryRegistry.Instance.GetNode(e.ParentId)`.

---

## Verification Plan

### Automated Tests
- Test build using `msbuild.exe`:
  ```powershell
  msbuild.exe Filey.csproj /t:Build /p:Configuration=Debug
  ```

### Manual Verification
- Ask the user to run the app, trigger a full re-crawl of seed folders, and verify search speed and CPU/Memory usage under Task Manager to confirm minimized memory bloat and absence of GC pauses.

namespace Filey
{
    /// <summary>
    /// Carries an unselected search query (and the directory it was scoped to) from the address
    /// bar to the host, which loads the full ranked result set into the search-results pane.
    /// </summary>
    public sealed class SearchAllRequest
    {
        public SearchAllRequest(string query, string activeDirectory)
        {
            Query = query;
            ActiveDirectory = activeDirectory;
        }

        public string Query { get; }
        public string ActiveDirectory { get; }
    }
}

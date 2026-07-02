using System;

namespace Filey.Previews
{
    public class PreviewStatusEventArgs : EventArgs
    {
        public string Encoding { get; }
        public string Size { get; }

        public PreviewStatusEventArgs(string encoding, string size)
        {
            Encoding = encoding;
            Size = size;
        }
    }

    public interface IPreviewControl
    {
        event EventHandler<PreviewStatusEventArgs> StatusUpdated;
        event EventHandler<string> DirectoryClicked;
        void Preview(string filePath, System.Threading.CancellationToken token);
        void Unload();
    }
}

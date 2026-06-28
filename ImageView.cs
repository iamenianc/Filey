using System;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;

namespace Filey
{
    /// <summary>
    /// Pure image-viewer logic shared by the inline <see cref="PreviewPane"/> and the
    /// pop-out <see cref="PreviewWindow"/>: EXIF orientation reading, fit-to-window scale,
    /// mouse-centred zoom offsets, and the small text/size formatters. None of this touches
    /// a WPF control — callers feed in numbers and wire the results to their own
    /// ScaleTransform and ScrollViewer, which keeps the maths testable in isolation.
    /// </summary>
    public static class ImageView
    {
        /// <summary>Original pixel dimensions and display orientation read from image metadata.</summary>
        public struct ImageInfo
        {
            public int Width;
            public int Height;
            public Rotation Rotation;
        }

        /// <summary>
        /// Reads original dimensions and EXIF orientation from the file's metadata headers
        /// without decoding pixel data. Dimensions are returned already swapped for 90°/270°
        /// orientations so callers see the display size directly. On any failure returns
        /// zero dimensions and <see cref="Rotation.Rotate0"/>.
        /// </summary>
        public static ImageInfo ReadOrientation(string filePath)
        {
            var info = new ImageInfo { Width = 0, Height = 0, Rotation = Rotation.Rotate0 };
            try
            {
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    // DelayCreation avoids decoding pixel data, reading metadata only.
                    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    if (decoder.Frames.Count > 0)
                    {
                        var frame = decoder.Frames[0];
                        info.Width = frame.PixelWidth;
                        info.Height = frame.PixelHeight;

                        // Query EXIF orientation with multiple fallback paths to be robust.
                        var metadata = frame.Metadata as BitmapMetadata;
                        if (metadata != null)
                        {
                            object val = null;
                            if (metadata.ContainsQuery("/System/Photo/Orientation"))
                                val = metadata.GetQuery("/System/Photo/Orientation");
                            else if (metadata.ContainsQuery("System.Photo.Orientation"))
                                val = metadata.GetQuery("System.Photo.Orientation");
                            else if (metadata.ContainsQuery("/app1/ifd/{ushort=274}"))
                                val = metadata.GetQuery("/app1/ifd/{ushort=274}");

                            if (val != null)
                            {
                                ushort orientation = Convert.ToUInt16(val);
                                switch (orientation)
                                {
                                    case 3: // Rotate 180
                                        info.Rotation = Rotation.Rotate180;
                                        break;
                                    case 6: // Rotate 90 CW
                                        info.Rotation = Rotation.Rotate90;
                                        Swap(ref info.Width, ref info.Height);
                                        break;
                                    case 8: // Rotate 270 CW
                                        info.Rotation = Rotation.Rotate270;
                                        Swap(ref info.Width, ref info.Height);
                                        break;
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // Fallback leaves zero dimensions / Rotate0.
            }
            return info;
        }

        /// <summary>Maps an EXIF <see cref="Rotation"/> to its display angle in degrees.</summary>
        public static double AngleFor(Rotation rotation)
        {
            switch (rotation)
            {
                case Rotation.Rotate90: return 90;
                case Rotation.Rotate180: return 180;
                case Rotation.Rotate270: return 270;
                default: return 0;
            }
        }

        /// <summary>Advances a rotation angle by one 90° clockwise step.</summary>
        public static double RotateStep(double angle) => (angle + 90) % 360;

        /// <summary>
        /// Computes the uniform scale that fits an image of the given pixel size into the
        /// viewport, accounting for a 90°/270° rotation that swaps the effective dimensions.
        /// A fixed margin is subtracted from the viewport. Returns 1.0 for degenerate input.
        /// </summary>
        public static double FitScale(double viewportWidth, double viewportHeight,
            double imageWidth, double imageHeight, double rotationAngle, double margin = 16)
        {
            if (imageWidth <= 0 || imageHeight <= 0) return 1.0;

            viewportWidth = Math.Max(100, viewportWidth - margin);
            viewportHeight = Math.Max(100, viewportHeight - margin);

            bool rotated = (rotationAngle == 90 || rotationAngle == 270);
            double activeWidth = rotated ? imageHeight : imageWidth;
            double activeHeight = rotated ? imageWidth : imageHeight;

            double scaleX = viewportWidth / activeWidth;
            double scaleY = viewportHeight / activeHeight;
            return Math.Min(scaleX, scaleY);
        }

        /// <summary>
        /// Given a current scroll offset along one axis, the mouse position in the viewport,
        /// and the ratio by which content just scaled, returns the new offset that keeps the
        /// point under the cursor fixed.
        /// </summary>
        public static double ZoomAroundPoint(double currentOffset, double mousePos, double scaleRatio)
        {
            return (currentOffset + mousePos) * scaleRatio - mousePos;
        }

        public static string GetEncodingName(Encoding encoding)
        {
            if (encoding == null) return "Unknown";
            if (encoding.Equals(Encoding.UTF8)) return "UTF-8";
            if (encoding.Equals(Encoding.Unicode)) return "UTF-16 LE";
            if (encoding.Equals(Encoding.BigEndianUnicode)) return "UTF-16 BE";
            if (encoding.Equals(Encoding.ASCII)) return "ASCII";
            return encoding.WebName.ToUpper();
        }

        public static string GetFormattedFileSize(string filePath)
        {
            try
            {
                return FormatBytes(new FileInfo(filePath).Length);
            }
            catch
            {
                return string.Empty;
            }
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes >= 1073741824)
                return $"{(bytes / 1073741824.0):N1} GB";
            if (bytes >= 1048576)
                return $"{(bytes / 1048576.0):N1} MB";
            if (bytes >= 1024)
                return $"{(bytes / 1024.0):N1} KB";
            return $"{bytes} B";
        }

        private static void Swap(ref int a, ref int b)
        {
            int t = a; a = b; b = t;
        }
    }
}

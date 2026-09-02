using System.Globalization;
using System.IO;
using System.Text;

namespace ScrollCapture.Utils;

/// <summary>
/// Minimal dependency-free PDF writer: one JPEG XObject, N A4 pages (image fitted to
/// page width, vertically tiled). Enough for long-screenshot archival.
/// </summary>
public static class PdfBuilder
{
    private const float PageWidth = 595.28f;   // A4
    private const float PageHeight = 841.89f;  // A4

    private static string F(float v) => v.ToString("F2", CultureInfo.InvariantCulture);
    private static byte[] Ascii(char c) => new[] { (byte)c };
    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    public static void Build(string outputPath, byte[] jpegData, int imageWidthPx, int imageHeightPx)
    {
        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(fs);

        float fitW = PageWidth - 24;               // side margins
        float scale = Math.Min(1.0f, fitW / Math.Max(1, imageWidthPx));
        float drawWidth = imageWidthPx * scale;
        float drawHeight = imageHeightPx * scale;
        int pages = Math.Max(1, (int)Math.Ceiling(drawHeight / PageHeight));

        // object layout: 1 = catalog, 2 = pages, 3 = image XObject, 4..3+pages = content streams
        var objects = new List<byte[]>();
        objects.Add(Ascii("<< /Type /Catalog /Pages 2 0 R >>"));
        {
            string kids = string.Join(" ", Enumerable.Range(0, pages).Select(i => $"{4 + i} 0 R"));
            objects.Add(Ascii($"<< /Type /Pages /Kids [{kids}] /Count {pages} >>"));
        }
        {
            var imageObj = new List<byte>();
            imageObj.AddRange(Ascii("<< /Type /XObject /Subtype /Image /Width "));
            imageObj.AddRange(Ascii(imageWidthPx.ToString(CultureInfo.InvariantCulture)));
            imageObj.AddRange(Ascii(" /Height "));
            imageObj.AddRange(Ascii(imageHeightPx.ToString(CultureInfo.InvariantCulture)));
            imageObj.AddRange(Ascii(" /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length "));
            imageObj.AddRange(Ascii(jpegData.Length.ToString(CultureInfo.InvariantCulture)));
            imageObj.AddRange(Ascii(" >>\nstream\n"));
            imageObj.AddRange(jpegData);
            imageObj.AddRange(Ascii("\nendstream"));
            objects.Add(imageObj.ToArray());
        }

        for (int p = 0; p < pages; p++)
        {
            float hLeft = Math.Max(0, drawHeight - p * PageHeight);
            float sy = Math.Min(1.0f, hLeft / PageHeight);
            if (sy <= 0) { sy = 1.0f; }
            string content = string.Join("\n",
                "q",
                $"1 0 0 {F(sy)} 0 0 cm",
                $"{F(drawWidth)} 0 0 {F(PageHeight)} 0 0 cm",
                "/Im0 Do",
                "Q");
            byte[] body = Ascii(content);
            var stream = new List<byte>();
            stream.AddRange(Ascii("<< /Length " + body.Length + " >>\nstream\n"));
            stream.AddRange(body);
            stream.AddRange(Ascii("\nendstream"));
            objects.Add(stream.ToArray());
        }

        writer.Write(Ascii("%PDF-1.4\n%\xE2\xE3\xCF\xD3\n"));
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add((int)writer.BaseStream.Position);
            writer.Write(Ascii($"{i + 1} 0 obj\n"));
            writer.Write(objects[i]);
            writer.Write(Ascii("\nendobj\n"));
        }
        int xrefOffset = (int)writer.BaseStream.Position;
        writer.Write(Ascii("xref\n"));
        writer.Write(Ascii($"0 {objects.Count + 1}\n"));
        writer.Write(Ascii("0000000000 65535 f \n"));
        foreach (int off in offsets)
        {
            writer.Write(Ascii($"{off:D10} 00000 n \n"));
        }
        writer.Write(Ascii($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF"));
    }
}

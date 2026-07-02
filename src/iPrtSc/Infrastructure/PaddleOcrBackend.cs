using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models.Local;

namespace iPrtSc;

/// <summary>
/// PaddleOCR (PP-OCRv5) backend. The East Slavic recognition model reads Ukrainian,
/// Russian and Belarusian alongside Latin letters and digits in a single pass, so mixed
/// text — e.g. Ukrainian UI with English words — just works. The detector finds text on
/// arbitrary (textured, coloured) backgrounds, so no binarization pre-pass is needed.
/// </summary>
internal static class PaddleOcrBackend
{
    // Human-readable summary of what the bundled model reads, for the Settings pane.
    public static IReadOnlyList<string> DisplayNames { get; } =
        new[] { "English", "Ukrainian", "Russian", "Belarusian" };

    // Engine construction loads the native runtime + models (~seconds), so build it once
    // and reuse. PaddleOcrAll is not thread-safe; recognition is serialized by the lock.
    private static readonly object _gate = new();
    private static PaddleOcrAll? _engine;

    // Regions scoring below this are recognition garbage (texture noise, half-cropped
    // glyphs); real text on screenshots scores well above it.
    private const float MinScore = 0.5f;

    public static Task<IReadOnlyList<OcrService.Word>> RecognizeAsync(BitmapSource image)
    {
        // Encode on the (UI) caller thread — the frozen BitmapSource is safe to read
        // here — then decode and run inference on the pool.
        byte[] png = EncodePng(image);
        return Task.Run<IReadOnlyList<OcrService.Word>>(() => Recognize(png));
    }

    private static IReadOnlyList<OcrService.Word> Recognize(byte[] png)
    {
        try
        {
            PaddleOcrResult result;
            lock (_gate)
            {
                if (_engine == null)
                {
                    // Rotate detection is off: screenshot text is axis-aligned, and the
                    // rotated-crop path in PaddleSharp 3.3.1 flips some crops upside down,
                    // garbling recognition entirely (verified: quality went 0.5 → 0.95+).
                    _engine = new PaddleOcrAll(LocalFullModels.EastSlavicV5, PaddleDevice.Mkldnn())
                    {
                        AllowRotateDetection = false,
                        Enable180Classification = false,
                    };
                    // Wider box expansion keeps edge glyphs from being clipped ("Світла"
                    // losing its "С"); tight UI lines verified not to merge at 2.2.
                    _engine.Detector.UnclipRatio = 2.2f;
                    // Selections can be large; keep small text legible to the detector.
                    _engine.Detector.MaxSize = 1920;
                }
                using var src = Cv2.ImDecode(png, ImreadModes.Color);
                result = _engine.Run(src);
            }
            return Order(result.Regions);
        }
        catch (Exception ex)
        {
            Logger.Log("PaddleOcrBackend.Recognize", ex);
            return Array.Empty<OcrService.Word>();
        }
    }

    /// <summary>A word estimated from a detected region, before line ordering.</summary>
    private readonly record struct Raw(string Text, double X, double Y, double W, double H);

    /// <summary>
    /// Paddle reports whole text regions (visual lines), not words. Split each region's
    /// text on spaces and apportion the region's box to the words by character count —
    /// close enough for word highlighting and rubber-band selection.
    /// </summary>
    private static List<Raw> SplitWords(PaddleOcrResultRegion[] regions)
    {
        var words = new List<Raw>();
        foreach (var region in regions)
        {
            if (region.Score < MinScore) continue;
            string text = region.Text?.Trim() ?? "";
            if (text.Length == 0 || !text.Any(char.IsLetterOrDigit)) continue;

            var box = region.Rect.BoundingRect();
            double perChar = (double)box.Width / Math.Max(1, text.Length);

            int pos = 0;
            foreach (var part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int start = text.IndexOf(part, pos, StringComparison.Ordinal);
                if (start < 0) start = pos;
                pos = start + part.Length;
                words.Add(new Raw(part,
                    box.X + start * perChar, box.Y,
                    part.Length * perChar, box.Height));
            }
        }
        return words;
    }

    /// <summary>Groups words into lines by vertical position, ordered top-to-bottom then left-to-right.</summary>
    private static IReadOnlyList<OcrService.Word> Order(PaddleOcrResultRegion[] regions)
    {
        var words = SplitWords(regions);
        if (words.Count == 0) return Array.Empty<OcrService.Word>();

        var heights = words.Select(w => w.H).OrderBy(x => x).ToList();
        double medH = heights[heights.Count / 2];
        double lineGap = Math.Max(4, medH * 0.6);

        var result = new List<OcrService.Word>(words.Count);
        var line = new List<Raw>();
        double lineRef = double.NaN;
        int lineIdx = -1;

        void Flush()
        {
            if (line.Count == 0) return;
            lineIdx++;
            foreach (var w in line.OrderBy(w => w.X))
                result.Add(new OcrService.Word(w.Text, lineIdx, w.X, w.Y, w.W, w.H));
            line.Clear();
        }

        foreach (var w in words.OrderBy(w => w.Y + w.H / 2))
        {
            double cy = w.Y + w.H / 2;
            if (!double.IsNaN(lineRef) && cy - lineRef > lineGap) Flush();
            if (line.Count == 0) lineRef = cy;
            line.Add(w);
        }
        Flush();
        return result;
    }

    private static byte[] EncodePng(BitmapSource image)
    {
        using var ms = new MemoryStream();
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(image));
        enc.Save(ms);
        return ms.ToArray();
    }
}

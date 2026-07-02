using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using RapidOcrNet;
using SkiaSharp;

namespace iPrtSc;

/// <summary>
/// PP-OCRv5 backend running on onnxruntime via RapidOcrNet — no Paddle or OpenCV native
/// libraries, the whole OCR stack (runtime + models) adds ~35 MB. The recognition model
/// reads Ukrainian and Cyrillic alongside Latin letters and digits in a single pass, so
/// mixed text — e.g. Ukrainian UI with English words — just works. The detector finds
/// text on arbitrary (textured, coloured) backgrounds.
/// </summary>
internal static class RapidOcrBackend
{
    // Engine construction loads the onnx models (~a second), so build it once and reuse.
    // Recognition is serialized by the lock — RapidOcr sessions aren't documented as
    // thread-safe, and captures are one-at-a-time anyway.
    private static readonly object _gate = new();
    private static RapidOcr? _engine;

    // Angle handling is off: screenshot text is axis-aligned, and skipping the
    // classifier both avoids misrotations and saves time. Word boxes feed the
    // interactive selection.
    private static readonly RapidOcrOptions Options = RapidOcrOptions.Default with
    {
        DoAngle = false,
        MostAngle = false,
        ReturnWordBox = true,
    };

    // Words scoring below this are recognition garbage (texture noise, half-cropped
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
            OcrResult result;
            lock (_gate)
            {
                _engine ??= CreateEngine();
                using var bitmap = SKBitmap.Decode(png);
                result = _engine.Detect(bitmap, Options);
            }
            return Order(result);
        }
        catch (Exception ex)
        {
            Logger.Log("RapidOcrBackend.Recognize", ex);
            return Array.Empty<OcrService.Word>();
        }
    }

    private static RapidOcr CreateEngine()
    {
        string packaged = Path.Combine(AppContext.BaseDirectory, RapidOcr.ModelsFolderName, RapidOcr.ModelsVersion);
        string eslav = Path.Combine(AppContext.BaseDirectory, "models", "eslav");

        var engine = new RapidOcr();
        engine.InitModels(
            Path.Combine(packaged, RapidOcr.DefaultDetModelPath),
            Path.Combine(packaged, RapidOcr.DefaultClsModelPath),
            Path.Combine(eslav, "eslav_PP-OCRv5_rec_mobile_infer.onnx"),
            Path.Combine(eslav, "ppocrv5_eslav_dict.txt"));
        return engine;
    }

    /// <summary>A recognized word with its axis-aligned box, before line ordering.</summary>
    private readonly record struct Raw(string Text, double X, double Y, double W, double H);

    private static List<Raw> CollectWords(OcrResult result)
    {
        var words = new List<Raw>();
        foreach (var block in result.TextBlocks)
        {
            if (block.WordResults is not { Length: > 0 }) continue;
            foreach (var w in block.WordResults)
            {
                if (w.Score < MinScore) continue;
                string text = w.Text?.Trim() ?? "";
                if (text.Length == 0 || !text.Any(char.IsLetterOrDigit)) continue;
                words.Add(Bounds(text, w.BoxPoints));
            }
        }
        return words;
    }

    private static Raw Bounds(string text, SKPointI[] pts)
    {
        int minX = pts[0].X, minY = pts[0].Y, maxX = pts[0].X, maxY = pts[0].Y;
        foreach (var p in pts)
        {
            minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
        }
        return new Raw(text, minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>
    /// Groups words into lines by vertical position, ordered top-to-bottom then
    /// left-to-right. The detector splits one visual row into several blocks when the
    /// gaps are wide, so grouping runs over all words geometrically rather than
    /// trusting block order.
    /// </summary>
    private static IReadOnlyList<OcrService.Word> Order(OcrResult result)
    {
        var words = CollectWords(result);
        if (words.Count == 0) return Array.Empty<OcrService.Word>();

        var heights = words.Select(w => w.H).OrderBy(x => x).ToList();
        double medH = heights[heights.Count / 2];
        double lineGap = Math.Max(4, medH * 0.6);

        var resultWords = new List<OcrService.Word>(words.Count);
        var line = new List<Raw>();
        double lineRef = double.NaN;
        int lineIdx = -1;

        void Flush()
        {
            if (line.Count == 0) return;
            lineIdx++;
            foreach (var w in line.OrderBy(w => w.X))
                resultWords.Add(new OcrService.Word(w.Text, lineIdx, w.X, w.Y, w.W, w.H));
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
        return resultWords;
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

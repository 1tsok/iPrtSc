using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace iPrtSc;

/// <summary>
/// Text recognition facade over the bundled PaddleOCR (PP-OCRv5) engine.
/// <para>
/// Windows' public <c>Windows.Media.Ocr</c> engine was rejected: it recognizes only its
/// installed recognizer languages, and on a Cyrillic word with just an English recognizer
/// it does not abstain — it confidently returns Latin look-alikes ("Привіт" → "IIPVlBiT").
/// Tesseract was rejected for noise on textured backgrounds and misses on clean Latin
/// text. PaddleOCR's detector + East Slavic recognizer handles both in one pass.
/// </para>
/// </summary>
public static class OcrService
{
    /// <summary>A recognized word and its bounding box in the input image's pixel space.</summary>
    public readonly record struct Word(string Text, int Line, double X, double Y, double Width, double Height);

    /// <summary>True when a recognizer is available. Models ship with the app, so always.</summary>
    public static bool IsAvailable => true;

    /// <summary>Human-readable names of the languages that will be recognized.</summary>
    public static IReadOnlyList<string> Languages() => PaddleOcrBackend.DisplayNames;

    /// <summary>Recognizes every word in the image with its position, in reading order.</summary>
    public static Task<IReadOnlyList<Word>> RecognizeWordsAsync(BitmapSource image) =>
        PaddleOcrBackend.RecognizeAsync(image);
}

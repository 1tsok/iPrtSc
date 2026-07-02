using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace iPrtSc;

/// <summary>
/// Text recognition facade over the bundled PP-OCRv5 (PaddleOCR) models on onnxruntime.
/// <para>
/// Windows' public <c>Windows.Media.Ocr</c> engine was rejected: it recognizes only its
/// installed recognizer languages, and on a Cyrillic word with just an English recognizer
/// it does not abstain — it confidently returns Latin look-alikes ("Привіт" → "IIPVlBiT").
/// Tesseract was rejected for noise on textured backgrounds and misses on clean Latin
/// text. The Paddle inference runtime was rejected for size (~450 MB of native DLLs).
/// PP-OCRv5's detector + East Slavic recognizer handles all of it in one pass.
/// </para>
/// </summary>
public static class OcrService
{
    /// <summary>A recognized word and its bounding box in the input image's pixel space.</summary>
    public readonly record struct Word(string Text, int Line, double X, double Y, double Width, double Height);

    /// <summary>Recognizes every word in the image with its position, in reading order.</summary>
    public static Task<IReadOnlyList<Word>> RecognizeWordsAsync(BitmapSource image) =>
        RapidOcrBackend.RecognizeAsync(image);
}

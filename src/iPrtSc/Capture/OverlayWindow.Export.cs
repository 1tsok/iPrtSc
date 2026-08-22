using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Anim = System.Windows.Media.Animation;

namespace iPrtSc;

/// <summary>Composing the final image and the copy/save actions built on it.</summary>
public partial class OverlayWindow
{
    private System.Windows.Threading.DispatcherTimer? _hintTimer;   // auto-hides the status pill

    // ===== Export =====
    /// <summary>
    /// Builds the final image. The photo layer is copied verbatim from the source
    /// bitmap (CroppedBitmap = a raw pixel copy) so it never passes through WPF's
    /// rendering pipeline — that pipeline dithers on render, which on dark gradients
    /// shows up as scattered bright speckles. Annotations are rendered on their own
    /// transparent layer and alpha-composited over the pristine crop in code.
    /// </summary>
    /// <summary>Selection rectangle mapped into source (physical) pixels, clamped to the bitmap.</summary>
    private Int32Rect SelectionRectPx()
    {
        int x = (int)Math.Round(_sel.X * _scale);
        int y = (int)Math.Round(_sel.Y * _scale);
        int w = (int)Math.Round(_sel.Width * _scale);
        int h = (int)Math.Round(_sel.Height * _scale);
        x = Math.Clamp(x, 0, _src.PixelWidth - 1);
        y = Math.Clamp(y, 0, _src.PixelHeight - 1);
        w = Math.Clamp(w, 1, _src.PixelWidth - x);
        h = Math.Clamp(h, 1, _src.PixelHeight - y);
        return new Int32Rect(x, y, w, h);
    }

    /// <summary>The pristine photo crop, without any annotations — what OCR reads.</summary>
    private BitmapSource CropPhoto()
    {
        var crop = new CroppedBitmap(_src, SelectionRectPx());
        crop.Freeze();
        return crop;
    }

    private BitmapSource ComposeSelection()
    {
        // Finish any edit first: an unfinished box would be captured with its caret and,
        // worse, with its selection highlight painted over the text.
        EndTextEdit();
        Keyboard.ClearFocus();

        var px = SelectionRectPx();
        int x = px.X, y = px.Y, w = px.Width, h = px.Height;
        if (AnnotCanvas.Children.Count == 0)
        {
            var plain = new CroppedBitmap(_src, new Int32Rect(x, y, w, h));
            plain.Freeze();
            return plain;
        }

        // Render only the annotation layer (transparent backdrop, already clipped to
        // the selection), crop it to match, then composite over the photo.
        double dpi = 96.0 * _scale;
        var annotRtb = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Round(Root.ActualWidth * _scale)),
            Math.Max(1, (int)Math.Round(Root.ActualHeight * _scale)),
            dpi, dpi, PixelFormats.Pbgra32);
        annotRtb.Render(AnnotCanvas);

        // Guard against a 1px rounding gap between the source and the rendered layer.
        w = Math.Min(w, annotRtb.PixelWidth - x);
        h = Math.Min(h, annotRtb.PixelHeight - y);
        var rect = new Int32Rect(x, y, w, h);

        var baseCrop = new CroppedBitmap(_src, rect);
        var annotCrop = new CroppedBitmap(annotRtb, rect);

        return CompositeOver(baseCrop, annotCrop);
    }

    /// <summary>
    /// Alpha-composites a premultiplied (Pbgra32) overlay over an opaque photo,
    /// touching only the pixels the overlay actually covers. Output is opaque Bgra32.
    /// </summary>
    private static BitmapSource CompositeOver(BitmapSource photo, BitmapSource overlay)
    {
        int w = photo.PixelWidth, h = photo.PixelHeight;
        int stride = w * 4;

        var baseBgra = new FormatConvertedBitmap(photo, PixelFormats.Bgra32, null, 0);
        var over = overlay.Format == PixelFormats.Pbgra32
            ? overlay
            : new FormatConvertedBitmap(overlay, PixelFormats.Pbgra32, null, 0);

        var bp = new byte[h * stride];
        var op = new byte[h * stride];
        baseBgra.CopyPixels(bp, stride, 0);
        over.CopyPixels(op, stride, 0);

        for (int i = 0; i < bp.Length; i += 4)
        {
            int a = op[i + 3];
            if (a == 0) continue;          // overlay transparent here → keep photo pixel
            int inv = 255 - a;
            // overlay channels are premultiplied, so: out = over + photo * (1 - a)
            bp[i]     = (byte)(op[i]     + bp[i]     * inv / 255);
            bp[i + 1] = (byte)(op[i + 1] + bp[i + 1] * inv / 255);
            bp[i + 2] = (byte)(op[i + 2] + bp[i + 2] * inv / 255);
            bp[i + 3] = 255;
        }

        var outBmp = BitmapSource.Create(w, h, photo.DpiX, photo.DpiY,
            PixelFormats.Bgra32, null, bp, stride);
        outBmp.Freeze();
        return outBmp;
    }

    // ===== Actions =====
    private void OnCopy(object sender, RoutedEventArgs e) => DoCopy();
    private void OnSave(object sender, RoutedEventArgs e) => DoSave();
    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Quick copy: the region goes to the clipboard and History, a "Copied" flash confirms it
    /// over the selection, and the overlay closes when the flash is done.
    /// </summary>
    private void QuickCopy()
    {
        var image = ComposeSelection();
        ClipboardService.CopyImage(image);
        HistoryService.Archive(image, _settings);
        Copied?.Invoke();
        PlayCopiedFlash();
    }

    /// <summary>Fades the "Copied" pill in over the selection, holds it, fades it out, then closes.</summary>
    private void PlayCopiedFlash()
    {
        CopiedFlash.Visibility = Visibility.Visible;
        CopiedFlash.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double x = _sel.Left + (_sel.Width - CopiedFlash.DesiredSize.Width) / 2;
        double y = _sel.Top + (_sel.Height - CopiedFlash.DesiredSize.Height) / 2;
        Canvas.SetLeft(CopiedFlash, Math.Max(8, x));
        Canvas.SetTop(CopiedFlash, Math.Max(8, y));

        var fade = new Anim.DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new Anim.LinearDoubleKeyFrame(1, Anim.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(55))));
        fade.KeyFrames.Add(new Anim.LinearDoubleKeyFrame(1, Anim.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260))));
        fade.KeyFrames.Add(new Anim.LinearDoubleKeyFrame(0, Anim.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(410))));
        fade.Completed += (_, _) => Close();
        CopiedFlash.BeginAnimation(OpacityProperty, fade);
    }

    private void DoCopy()
    {
        if (_sel.Width < 1 || _sel.Height < 1) return;
        var image = ComposeSelection();
        ClipboardService.CopyImage(image);
        HistoryService.Archive(image, _settings);
        Copied?.Invoke();
        Close();
    }

    /// <summary>
    /// Copies recognized text. If the user has highlighted specific words (Grab text tool),
    /// copies just those; otherwise copies everything found in the selection.
    /// </summary>

    /// <summary>
    /// Surfaces a transient status message centered over the selection. Without
    /// <paramref name="autoHide"/> the pill stays until something else clears it.
    /// </summary>
    private void ShowHint(string message, TimeSpan? autoHide = null)
    {
        HintText.Text = message;
        HintPill.Visibility = Visibility.Visible;

        _hintTimer?.Stop();
        if (autoHide is { } delay)
        {
            _hintTimer ??= new System.Windows.Threading.DispatcherTimer();
            _hintTimer.Interval = delay;
            _hintTimer.Tick -= OnHintTimerTick;
            _hintTimer.Tick += OnHintTimerTick;
            _hintTimer.Start();
        }

        HintPill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double x = _sel.Left + (_sel.Width - HintPill.DesiredSize.Width) / 2;
        double y = _sel.Top + (_sel.Height - HintPill.DesiredSize.Height) / 2;
        Canvas.SetLeft(HintPill, Math.Max(8, x));
        Canvas.SetTop(HintPill, Math.Max(8, y));
    }

    private void OnHintTimerTick(object? sender, EventArgs e)
    {
        _hintTimer?.Stop();
        HintPill.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Dims the selection while recognition runs, so the wait reads at a glance and the
    /// status pill has a dark backdrop to stand out against.
    /// </summary>
    private void ShowBusyVeil()
    {
        if (_sel.Width < 1 || _sel.Height < 1) return;
        Place(DimBusy, _sel.X, _sel.Y, _sel.Width, _sel.Height);
    }

    private void HideBusyVeil() => Hide(DimBusy);

    private void DoSave()
    {
        if (_sel.Width < 1 || _sel.Height < 1) return;
        var image = ComposeSelection();

        if (!_settings.AskWhereToSave)
        {
            var path = Path.Combine(SaveService.DefaultFolder(_settings), SaveService.DefaultFileName(_settings));
            SaveService.SaveSource(image, path);
            if (_settings.CopyToClipboardAlways) ClipboardService.CopyImage(image);
            HistoryService.Archive(image, _settings);
            Saved?.Invoke(path);
            Close();
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save screenshot",
            Filter = "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg",
            FileName = SaveService.DefaultFileName(_settings),
            InitialDirectory = SaveService.DefaultFolder(_settings),
            AddExtension = true,
            OverwritePrompt = true,
            FilterIndex = _settings.SaveFormat.Equals("Jpeg", StringComparison.OrdinalIgnoreCase) ? 2 : 1
        };

        Topmost = false;
        Hide();

        bool? ok = dlg.ShowDialog();
        if (ok == true)
        {
            SaveService.SaveSource(image, dlg.FileName);
            if (_settings.CopyToClipboardAlways) ClipboardService.CopyImage(image);
            HistoryService.Archive(image, _settings);
            Saved?.Invoke(dlg.FileName);
            Close();
        }
        else
        {
            Show();
            Topmost = true;
            Activate();
            Focus();
        }
    }
}

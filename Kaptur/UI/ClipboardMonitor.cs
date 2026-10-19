using System.Runtime.InteropServices;

namespace Kaptur;

/// Pano değişikliklerini yoklamadan dinler (AddClipboardFormatListener, yalnızca mesaj penceresi).
sealed class ClipboardMonitor : NativeWindow, IDisposable
{
    const int WM_CLIPBOARDUPDATE = 0x031D;
    static readonly IntPtr HWND_MESSAGE = new(-3);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    public event Action<string>? TextCopied;

    public ClipboardMonitor()
    {
        CreateHandle(new CreateParams { Parent = HWND_MESSAGE });
        AddClipboardFormatListener(Handle);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_CLIPBOARDUPDATE)
        {
            string? text = null;
            try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); }
            catch (ExternalException) { /* pano başka bir uygulamada kilitli */ }
            if (!string.IsNullOrWhiteSpace(text)) TextCopied?.Invoke(text);
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            RemoveClipboardFormatListener(Handle);
            DestroyHandle();
        }
    }
}

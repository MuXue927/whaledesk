using System.IO;
using System.Threading;
using System.Windows;

namespace WhaleDesk.Interaction;

/// <summary>
/// Clipboard writes that are verified instead of assumed, with BOTH useful formats.
///
/// History of this file (worth keeping, it is the whole reason it is this careful):
///  * v0.9  used Clipboard.SetText and always claimed success. WPF's SetText keeps the data
///          owned by our process (no OleFlushClipboard), so other apps could see nothing.
///  * v0.10 put ONLY the path text on the clipboard - reading the user's report ("粘贴到资源
///         管理器是灰的") made clear what they actually wanted: the FILE, so Explorer can
///          copy it. Text alone can never enable Explorer's paste.
///  * v0.10.2 (this file) puts FileDrop + UnicodeText/Text together, flushes it, and reads it
///          back: Explorer pastes the file, editors/DSH paste the path.
/// </summary>
internal static class ClipboardWriter
{
    /// <summary>
    /// Copy files (FileDrop) and a text representation at the same time.
    /// <paramref name="files"/> may be empty (text-only drop).
    /// </summary>
    public static bool TryCopy(IReadOnlyList<string> files, string text, out string error)
    {
        error = string.Empty;
        if (files.Count == 0 && string.IsNullOrEmpty(text))
        {
            error = "没有内容可复制";
            return false;
        }

        var existing = new List<string>();
        foreach (var path in files)
        {
            try
            {
                if (File.Exists(path) || Directory.Exists(path)) existing.Add(path);
            }
            catch { /* keep going: one bad path must not lose the rest */ }
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var data = new DataObject();
                if (existing.Count > 0)
                {
                    // This is what makes Explorer's "Paste" (and Ctrl+V in a folder) work.
                    data.SetData(DataFormats.FileDrop, existing.ToArray());
                }
                if (!string.IsNullOrEmpty(text))
                {
                    data.SetData(DataFormats.UnicodeText, text);
                    data.SetData(DataFormats.Text, text);
                }
                // copy:true => OleFlushClipboard, so the data survives even if we exit or block.
                Clipboard.SetDataObject(data, true);

                if (existing.Count > 0)
                {
                    var back = Clipboard.GetFileDropList();
                    var missing = existing.Where(p => !back.Contains(p)).ToList();
                    if (missing.Count > 0)
                    {
                        error = $"文件列表读回缺 {missing.Count} 项";
                        Thread.Sleep(80);
                        continue;
                    }
                }
                if (!string.IsNullOrEmpty(text))
                {
                    var textBack = Clipboard.GetText();
                    if (!string.Equals(textBack, text, StringComparison.Ordinal))
                    {
                        error = textBack.Length == 0 ? "读回文本为空" : "读回文本不一致";
                        Thread.Sleep(80);
                        continue;
                    }
                }
                error = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            Thread.Sleep(80);
        }
        return false;
    }
}

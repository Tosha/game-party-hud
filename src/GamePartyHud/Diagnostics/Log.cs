using System;
using System.IO;
using System.Text;

namespace GamePartyHud.Diagnostics;

/// <summary>
/// Simple thread-safe file logger. Writes a single rolling log at
/// <c>%AppData%\GamePartyHud\app.log</c>. Each line: <c>[ts] LEVEL message</c>.
/// Exceptions are formatted with full stack traces (and inner exceptions).
/// </summary>
public static class Log
{
    private const long MaxLogBytes = 2 * 1024 * 1024; // 2 MB — rotate after this.

    private static readonly object Gate = new();
    private static readonly string LogDir;
    private static readonly string LogFile;

    static Log()
    {
        LogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GamePartyHud");
        Directory.CreateDirectory(LogDir);
        LogFile = Path.Combine(LogDir, "app.log");

        RotateIfTooLarge();
    }

    public static string LogPath => LogFile;
    public static string LogDirectory => LogDir;

    public static void Info(string message) => Write("INFO ", message, null);
    public static void Warn(string message) => Write("WARN ", message, null);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        var sb = new StringBuilder(256);
        sb.Append('[').Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(']')
          .Append(' ').Append(level).Append(' ').Append(' ').Append(message);
        if (ex is not null) AppendException(sb, ex);
        sb.AppendLine();

        var text = sb.ToString();
        lock (Gate)
        {
            try
            {
                File.AppendAllText(LogFile, text);
            }
            catch
            {
                // Last-resort: if we can't write the log we can't surface this
                // without causing a feedback loop. Swallow.
            }
        }
    }

    private static void AppendException(StringBuilder sb, Exception ex)
    {
        sb.AppendLine();
        var current = ex;
        int depth = 0;
        while (current is not null)
        {
            if (depth > 0) sb.Append("-- Inner: ");
            sb.Append(current.GetType().FullName).Append(": ").AppendLine(current.Message);
            if (!string.IsNullOrEmpty(current.StackTrace)) sb.AppendLine(current.StackTrace);
            current = current.InnerException;
            depth++;
        }
    }

    private static void RotateIfTooLarge()
    {
        try
        {
            var fi = new FileInfo(LogFile);
            if (fi.Exists && fi.Length > MaxLogBytes)
            {
                var prev = LogFile + ".old";
                if (File.Exists(prev)) File.Delete(prev);
                File.Move(LogFile, prev);
            }
        }
        catch
        {
            // Rotation is best-effort; never block startup on it.
        }
    }
}

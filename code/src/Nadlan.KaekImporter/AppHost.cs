using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nadlan.KaekImporter;

/// <summary>Values from importer.json; anything missing keeps the built-in default.</summary>
public sealed record ImporterSettingsFile(string? ApiUrl, string? Token, string? Delay, string? MissDelay, double? MaxViewMetres, string? Browser,
    bool? Offline = null);

/// <summary>
/// What a double-clicked app on a customer machine needs: settings without a command line, a log file (a Mac app
/// has no console), and fatal errors the user actually sees.
/// </summary>
public static class AppHost
{
    public const string SettingsFileName = "importer.json";

    /// <summary>Per-user folder: %LOCALAPPDATA%\Nadlan on Windows, ~/Library/Application Support/Nadlan on macOS.</summary>
    public static string UserFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nadlan");

    /// <summary>
    /// importer.json next to the program (written by the installer package, e.g. the company's Nadlan address),
    /// then the user's own importer.json on top of it. Command-line options win over both.
    /// </summary>
    public static ImporterSettingsFile LoadSettings()
    {
        var merged = new ImporterSettingsFile(null, null, null, null, null, null);
        foreach (var path in new[] { Path.Combine(AppContext.BaseDirectory, SettingsFileName), Path.Combine(UserFolder, SettingsFileName) })
        {
            if (!File.Exists(path)) continue;
            try
            {
                var s = JsonSerializer.Deserialize<ImporterSettingsFile>(File.ReadAllText(path),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web) { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (s is null) continue;
                merged = new ImporterSettingsFile(s.ApiUrl ?? merged.ApiUrl, s.Token ?? merged.Token, s.Delay ?? merged.Delay,
                    s.MissDelay ?? merged.MissDelay, s.MaxViewMetres ?? merged.MaxViewMetres, s.Browser ?? merged.Browser,
                    s.Offline ?? merged.Offline);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                throw new ArgumentException($"Settings file {path} could not be read: {ex.Message}");
            }
        }

        return merged;
    }

    /// <summary>
    /// Keeps the token from "Connect to Nadlan" in the user's own importer.json, so the next start is connected at once.
    /// Other keys in that file are kept; the package's importer.json (next to the program) is never written.
    /// </summary>
    public static void SaveUserToken(string token)
    {
        var path = Path.Combine(UserFolder, SettingsFileName);
        JsonObject settings;
        try
        {
            settings = File.Exists(path) && JsonNode.Parse(File.ReadAllText(path),
                documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is JsonObject o
                ? o : new JsonObject();
        }
        catch (JsonException)
        {
            settings = new JsonObject(); // unreadable: rewritten with just the token rather than failing the connect
        }

        foreach (var key in settings.Where(p => string.Equals(p.Key, "token", StringComparison.OrdinalIgnoreCase)).Select(p => p.Key).ToList())
        {
            settings.Remove(key);
        }

        settings["token"] = token;
        Directory.CreateDirectory(UserFolder);
        File.WriteAllText(path, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Everything written to the console also goes to a daily log file; returns its path.</summary>
    public static string StartLog()
    {
        var folder = Path.Combine(UserFolder, "logs");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"importer-{DateTime.Now:yyyyMMdd}.log");
        // Shared, so a second importer can write to it too. An older importer still running holds it exclusively:
        // then this one logs to its own file rather than failing to start.
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        }
        catch (IOException)
        {
            path = Path.Combine(folder, $"importer-{DateTime.Now:yyyyMMdd}-{Environment.ProcessId}.log");
            stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        }

        var file = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
        file.WriteLine($"---- {DateTime.Now:yyyy-MM-dd HH:mm:ss} start, version {typeof(AppHost).Assembly.GetName().Version}");
        Console.SetOut(TextWriter.Synchronized(new TeeWriter(Console.Out, file)));
        Console.SetError(TextWriter.Synchronized(new TeeWriter(Console.Error, file)));
        return path;
    }

    /// <summary>
    /// Shows a startup error where the user will see it: a dialog on macOS (the app has no window of its own),
    /// or the console kept open on Windows until Enter is pressed.
    /// </summary>
    public static void ShowFatal(string message)
    {
        Console.Error.WriteLine(message);
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                var text = message.Replace("\\", "\\\\").Replace("\"", "\\\"");
                using var p = Process.Start(new ProcessStartInfo("osascript")
                {
                    ArgumentList = { "-e", $"display alert \"Nadlan KAEK Importer\" message \"{text}\" as critical" },
                    UseShellExecute = false,
                });
                p?.WaitForExit();
            }
            else if (!Console.IsInputRedirected)
            {
                Console.WriteLine("Press Enter to close.");
                Console.ReadLine();
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            // Nowhere better to show it; it is in the log.
        }
    }

    private sealed class TeeWriter(TextWriter first, TextWriter second) : TextWriter
    {
        public override Encoding Encoding => first.Encoding;

        public override void Write(char value)
        {
            first.Write(value);
            second.Write(value);
        }

        public override void Write(string? value)
        {
            first.Write(value);
            second.Write(value);
        }

        public override void WriteLine(string? value)
        {
            first.WriteLine(value);
            second.WriteLine(value);
        }

        public override void Flush()
        {
            first.Flush();
            second.Flush();
        }
    }
}

using Microsoft.Extensions.Configuration;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

namespace FMSISSTransactionService.Helpers
{
    /// <summary>
    /// Same file format as the ISS Transaction Service logger: "yy-MM-dd HH:mm:ss  message".
    /// BeginCycle/EndCycle pin one file per cycle, so a cycle that runs past midnight
    /// does not split across two daily files.
    /// </summary>
    public static class Logger
    {
        private const string FallbackLogDirectory = @"C:\FMSLogs";

        private static readonly IConfigurationRoot configuration =
            new ConfigurationBuilder()
                .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                .Build();

        // Log file prefix: "RAAST-log dd-MM-yyyy.txt"
        private static readonly string DefaultFolder = "RAAST";

        private static readonly CultureInfo USCulture = CultureInfo.GetCultureInfo("en-us");

        private static readonly object _lock = new object();

        // RaastSettings:LogPath is AES-encrypted in appsettings.json
        private static string _logDirectory = ReadLogDirectory();

        // Set between BeginCycle and EndCycle; null means "today's file"
        private static string? _cycleFilePath;

        public static string LogDirectory
        {
            get => _logDirectory;
            set
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    // GetFullPath collapses doubled separators, e.g. "D:\\FMSLogs" -> "D:\FMSLogs"
                    _logDirectory = Path.GetFullPath(value.Trim());
                }
            }
        }

        /// <summary>
        /// Pins the current day's log file until EndCycle so the whole cycle lands in one file.
        /// </summary>
        public static void BeginCycle()
        {
            lock (_lock)
            {
                _cycleFilePath = BuildFilePath(DateTime.Now);
            }
        }

        public static void EndCycle()
        {
            lock (_lock)
            {
                _cycleFilePath = null;
            }
        }

        /// <summary>
        /// Writes "yy-MM-dd HH:mm:ss  message" (the ISS Info line format). An empty message writes a blank line.
        /// </summary>
        public static void Simple(string message)
        {
            try
            {
                string logEntry = string.IsNullOrEmpty(message)
                    ? string.Empty
                    : $"{DateTime.Now.ToString("yy-MM-dd HH:mm:ss")}  {message}";

                WriteLine(logEntry);

                // Prettified console output for banners
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine(logEntry);
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed to write simple log: {ex.Message}");
            }
        }

        public static void Log(string message, string level = "INFO", string filepath = "")
        {
            try
            {
                string timestamp = DateTime.Now.ToString("yy-MM-dd HH:mm:ss");
                WriteLine($"{timestamp}  {message}");

                // Prettified console output
                WriteToConsole(timestamp, level, message);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed to write log: {ex.Message}");
            }
        }

        public static void LogException(Exception ex, string context = "")
        {
            string message = string.IsNullOrEmpty(context)
                ? $"Exception: {ex.Message}\nStack Trace: {ex.StackTrace}"
                : $"Context: {context} | Exception: {ex.Message}\nStack Trace: {ex.StackTrace}";

            // Keep the root cause (e.g. the OracleException behind a retry failure)
            if (ex.InnerException != null)
                message += $"\nInner Exception: {ex.InnerException.Message}";

            Log(message, "ERROR");
        }

        public static void Info(
            string message,
            [CallerFilePath] string filePath = "",
            [CallerLineNumber] int line = 0,
            [CallerMemberName] string caller = "")
            => Log(message, "INFO", filePath);

        public static void Warning(
            string message,
            [CallerFilePath] string filePath = "",
            [CallerLineNumber] int line = 0,
            [CallerMemberName] string caller = "")
            => Log(message, "WARN", filePath);

        public static void Error(
            string message,
            [CallerFilePath] string filePath = "",
            [CallerLineNumber] int line = 0,
            [CallerMemberName] string caller = "")
            => Log(message, "ERROR", filePath);

        private static string ReadLogDirectory()
        {
            string encrypted = configuration["RaastSettings:LogPath"];
            if (string.IsNullOrWhiteSpace(encrypted))
                return FallbackLogDirectory;

            try
            {
                // GetFullPath collapses doubled separators, e.g. "D:\\FMSLogs" -> "D:\FMSLogs"
                return Path.GetFullPath(Decryption.DecryptString(encrypted).Trim());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] RaastSettings:LogPath could not be decrypted, using {FallbackLogDirectory}: {ex.Message}");
                return FallbackLogDirectory;
            }
        }

        // e.g. D:\FMSLogs\2026\Oct\16\RAAST-log 16-10-2026.txt
        private static string BuildFilePath(DateTime date) =>
            Path.Combine(
                LogDirectory,
                date.ToString("yyyy"),
                date.ToString("MMM"),
                date.ToString("dd"),
                $"{DefaultFolder}-log {date.ToString("dd-MM-yyyy", USCulture)}.txt");

        private static void WriteLine(string line)
        {
            lock (_lock)
            {
                string logFilePath = _cycleFilePath ?? BuildFilePath(DateTime.Now);

                string dir = Path.GetDirectoryName(logFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                // ReadWrite share so the file can be opened in an editor while the service runs
                using (var stream = new FileStream(logFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                using (var writer = new StreamWriter(stream))
                {
                    writer.WriteLine(line);
                }
            }
        }

        private static void WriteToConsole(string timestamp, string level, string message)
        {
            lock (_lock)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"[{timestamp}] ");

                switch (level.ToUpperInvariant())
                {
                    case "INFO":
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.Write("[INFO] ");
                        break;
                    case "WARN":
                    case "WARNING":
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.Write("[WARN] ");
                        break;
                    case "ERROR":
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.Write("[FAIL] ");
                        break;
                    default:
                        Console.ForegroundColor = ConsoleColor.White;
                        Console.Write($"[{level}] ");
                        break;
                }

                Console.ResetColor();
                Console.WriteLine(message);
            }
        }
    }
}

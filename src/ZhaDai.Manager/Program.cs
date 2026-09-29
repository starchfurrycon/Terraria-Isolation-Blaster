using System.Globalization;
using System.Text;

namespace ZhaDai.Manager;

/// <summary>
/// Entry point of the desktop front end. Two modes: the interactive window, and the headless
/// <c>--ui-smoke</c> renderer used to prove the owner drawn layout still paints without a display.
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitFailure = 2;

    private static string? _errorLog;

    [STAThread]
    private static int Main(string[] args)
    {
        // The window is a WinExe, so a console is not guaranteed; attach to the parent console when
        // there is one so --ui-smoke can still print its artifact paths.
        TryUseUtf8Console();

        if (TryReadSmokeDirectory(args, out string? smokeDirectory))
        {
            return UiSmokeTest.Run(smokeDirectory!);
        }

        Application.ThreadException += (_, e) => ReportCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception);

        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
            return ExitOk;
        }
        catch (Exception ex)
        {
            ReportCrash(ex);
            return ExitFailure;
        }
    }

    private static void TryUseUtf8Console()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // No console is attached (the normal double click case); nothing to configure.
        }
    }

    /// <summary>
    /// Reads <c>--ui-smoke</c>, <c>--ui-smoke=&lt;dir&gt;</c> or <c>--ui-smoke &lt;dir&gt;</c>. The
    /// bare form returns the default artifact directory.
    /// </summary>
    private static bool TryReadSmokeDirectory(string[] args, out string? directory)
    {
        directory = null;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (!arg.StartsWith("--ui-smoke", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string suffix = arg["--ui-smoke".Length..];
            if (suffix.StartsWith('='))
            {
                suffix = suffix[1..];
            }

            if (suffix.Length > 0)
            {
                directory = suffix;
                return true;
            }

            if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
            {
                directory = args[i + 1];
                return true;
            }

            directory = Path.Combine("artifacts", "ui-smoke");
            return true;
        }

        return false;
    }

    /// <summary>Writes a crash report next to the user's temporary files and shows it once.</summary>
    private static void ReportCrash(Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        try
        {
            _errorLog ??= Path.Combine(
                Path.GetTempPath(),
                "zhadai",
                "manager-error-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".log");
            Directory.CreateDirectory(Path.GetDirectoryName(_errorLog)!);
            File.WriteAllText(
                _errorLog,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                Environment.NewLine +
                exception,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (IOException)
        {
            // Reporting must never be the thing that kills the process.
        }

        string message = exception.Message + Environment.NewLine + Environment.NewLine +
            "详细堆栈已写入：" + _errorLog;
        MessageBox.Show(message, "炸带管理器出错", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}

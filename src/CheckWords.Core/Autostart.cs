using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;

namespace CheckWords.Core;

/// <summary>A Task Scheduler task as seen by <see cref="Autostart.QueryTask"/>.</summary>
/// <param name="Elevated">Runs "with highest privileges".</param>
/// <param name="Command">The exe the task starts.</param>
public sealed record ScheduledTaskInfo(bool Enabled, bool Elevated, string Command);

/// <summary>
/// Two ways to start at logon:
/// <list type="bullet">
/// <item>the HKCU Run registry value: no admin rights needed, but no access to elevated windows;</item>
/// <item>a Task Scheduler task "with highest privileges": works in admin windows too, needs UAC to create or delete.</item>
/// </list>
/// Only one should be active: the Run entry starts first and its instance would block the elevated one.
/// </summary>
public static class Autostart
{
    public const string DefaultTaskName = "CheckWords";
    const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValueName = "CheckWords";
    const string TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    const int ErrorCancelled = 1223; // the user declined the UAC prompt

    // ------------------------------------------------------------------ registry (Run key)

    public static bool RegistryEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) is string;
        }
    }

    public static void SetRegistry(bool enabled, string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled) key.SetValue(RunValueName, $"\"{exePath}\"");
        else key.DeleteValue(RunValueName, throwOnMissingValue: false);
    }

    // ------------------------------------------------------------------ Task Scheduler

    /// <summary>Reads the task through the Task Scheduler COM API (in-process, a few ms). Null if there is none.</summary>
    public static ScheduledTaskInfo? QueryTask(string taskName = DefaultTaskName)
    {
        object? service = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null) return null;
            service = Activator.CreateInstance(type);
            dynamic scheduler = service!;
            scheduler.Connect();
            dynamic task = scheduler.GetFolder("\\").GetTask(taskName);
            bool enabled = task.Enabled;
            string xml = task.Xml;
            return ParseTaskXml(xml, enabled);
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException)
        {
            // The COM binder maps HRESULTs to .NET exceptions: "not found" (0x80070002) arrives as
            // FileNotFoundException, "access denied" as UnauthorizedAccessException.
            return null;
        }
        finally
        {
            if (service is not null && Marshal.IsComObject(service)) Marshal.ReleaseComObject(service);
        }
    }

    public static ScheduledTaskInfo ParseTaskXml(string xml, bool enabled)
    {
        XNamespace ns = TaskNamespace;
        var doc = XDocument.Parse(xml);
        var command = doc.Descendants(ns + "Exec").Elements(ns + "Command").FirstOrDefault()?.Value ?? "";
        command = Environment.ExpandEnvironmentVariables(command.Trim().Trim('"'));
        var runLevel = doc.Descendants(ns + "Principal").Elements(ns + "RunLevel").FirstOrDefault()?.Value;
        return new ScheduledTaskInfo(enabled, runLevel == "HighestAvailable", command);
    }

    /// <summary>Task definition: at logon of the current user, +10 s, no time limit, also on battery, normal priority.</summary>
    public static string BuildTaskXml(string exePath, bool elevated, string? userId = null)
    {
        userId ??= WindowsIdentity.GetCurrent().Name;
        XNamespace ns = TaskNamespace;
        var doc = new XDocument(
            new XElement(ns + "Task", new XAttribute("version", "1.2"),
                new XElement(ns + "RegistrationInfo",
                    new XElement(ns + "Description", "CheckWords: warns about the wrong keyboard layout (tray app).")),
                new XElement(ns + "Triggers",
                    new XElement(ns + "LogonTrigger",
                        new XElement(ns + "Enabled", "true"),
                        new XElement(ns + "UserId", userId),
                        new XElement(ns + "Delay", "PT10S"))),
                new XElement(ns + "Principals",
                    new XElement(ns + "Principal", new XAttribute("id", "Author"),
                        new XElement(ns + "UserId", userId),
                        new XElement(ns + "LogonType", "InteractiveToken"),
                        new XElement(ns + "RunLevel", elevated ? "HighestAvailable" : "LeastPrivilege"))),
                new XElement(ns + "Settings",
                    new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(ns + "DisallowStartIfOnBatteries", "false"),
                    new XElement(ns + "StopIfGoingOnBatteries", "false"),
                    new XElement(ns + "StartWhenAvailable", "true"),
                    new XElement(ns + "ExecutionTimeLimit", "PT0S"),
                    new XElement(ns + "Priority", "4")),
                new XElement(ns + "Actions", new XAttribute("Context", "Author"),
                    new XElement(ns + "Exec",
                        new XElement(ns + "Command", exePath),
                        new XElement(ns + "WorkingDirectory", Path.GetDirectoryName(exePath))))));
        return doc.ToString();
    }

    /// <summary>
    /// Creates (or replaces) the task via schtasks. With <paramref name="asAdmin"/> Windows shows a UAC prompt.
    /// Blocks until schtasks finishes: call it off the UI thread. Returns false if it failed or UAC was declined.
    /// </summary>
    public static bool CreateTask(string exePath, bool elevated, bool asAdmin, string taskName = DefaultTaskName)
    {
        var file = Path.Combine(Path.GetTempPath(), $"CheckWords-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(file, BuildTaskXml(exePath, elevated), Encoding.Unicode); // schtasks expects UTF-16
            return RunSchtasks($"/Create /TN \"{taskName}\" /XML \"{file}\" /F", asAdmin) == 0;
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    /// <summary>Deletes the task (UAC prompt with <paramref name="asAdmin"/>). Call it off the UI thread.</summary>
    public static bool DeleteTask(bool asAdmin, string taskName = DefaultTaskName) =>
        RunSchtasks($"/Delete /TN \"{taskName}\" /F", asAdmin) == 0;

    public static bool SamePath(string? a, string? b) =>
        a is not null && b is not null &&
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    public static bool IsProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    static int? RunSchtasks(string arguments, bool asAdmin)
    {
        var psi = new ProcessStartInfo("schtasks.exe", arguments)
        {
            UseShellExecute = asAdmin, // "runas" needs ShellExecute
            Verb = asAdmin ? "runas" : "",
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var process = Process.Start(psi);
            if (process is null) return null;
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return null;
        }
    }
}

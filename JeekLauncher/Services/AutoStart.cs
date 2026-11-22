using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using ZLogger;

namespace JeekLauncher.Services;

/// <summary>
/// Toggles "start with Windows" via a per-user Task Scheduler logon task. No admin rights needed.
/// <para>The HKCU Run key is not used: Explorer runs those entries one at a time, waiting on each,
/// so a launcher listed after a few slow programs starts minutes after logon. A logon task starts
/// right away. A Run value left by older versions is migrated to the task by
/// <see cref="MigrateLegacyRunValue"/>.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "JeekLauncher";

    private static readonly ILogger Log = JeekTools.LogManager.CreateLogger(nameof(AutoStart));

    /// <summary>Per-user so accounts sharing a machine do not overwrite each other's task.</summary>
    internal static string TaskName => $"JeekLauncher-{Environment.UserName}";

    public static bool IsEnabled() => TaskExists() || ReadRunValue() is not null;

    public static void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            string? exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe) && RegisterTask(exe))
                DeleteRunValue();
        }
        else
        {
            if (TaskExists())
                RunSchtasks($"/delete /tn \"{TaskName}\" /f", out _);
            DeleteRunValue();
        }
    }

    /// <summary>
    /// Replaces the Run value written by older versions with the logon task, keeping the
    /// target it pointed at. The Run value is removed only once the task exists, so a failed
    /// migration still leaves the app starting with Windows.
    /// </summary>
    public static void MigrateLegacyRunValue()
    {
        string? target = ReadRunValue();
        if (target is null)
            return;

        string exe = target.Trim().Trim('"');
        if (TaskExists() || RegisterTask(exe))
        {
            DeleteRunValue();
            Log.ZLogInformation($"Migrated autostart from the Run key to logon task {TaskName}");
        }
    }

    /// <summary>Diagnostic summary of both autostart mechanisms, for the Debug MCP.</summary>
    public static string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"enabled={IsEnabled()}");
        sb.AppendLine($"taskName={TaskName}");
        if (RunSchtasks($"/query /tn \"{TaskName}\" /xml", out string xml) == 0)
        {
            try
            {
                var doc = XDocument.Parse(xml);
                XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
                sb.AppendLine($"taskCommand={doc.Descendants(ns + "Command").FirstOrDefault()?.Value}");
                sb.AppendLine($"taskPriority={doc.Descendants(ns + "Priority").FirstOrDefault()?.Value}");
                sb.AppendLine($"taskLogonUser={doc.Descendants(ns + "LogonTrigger").Descendants(ns + "UserId").FirstOrDefault()?.Value}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"task=(unparseable xml: {ex.Message})");
            }
        }
        else
        {
            sb.AppendLine("task=(none)");
        }
        sb.AppendLine($"legacyRunValue={ReadRunValue() ?? "(none)"}");
        return sb.ToString();
    }

    private static bool TaskExists() => TaskExists(TaskName);

    internal static bool TaskExists(string taskName) => RunSchtasks($"/query /tn \"{taskName}\"", out _) == 0;

    internal static bool DeleteTask(string taskName) => RunSchtasks($"/delete /tn \"{taskName}\" /f", out _) == 0;

    private static bool RegisterTask(string exe) => RegisterTask(exe, TaskName);

    internal static bool RegisterTask(string exe, string taskName)
    {
        string xmlPath = Path.Combine(Path.GetTempPath(), $"{taskName}-{Environment.ProcessId}.xml");
        try
        {
            // schtasks requires the XML file to be UTF-16.
            File.WriteAllText(xmlPath, BuildTaskXml(exe), Encoding.Unicode);
            int code = RunSchtasks($"/create /tn \"{taskName}\" /xml \"{xmlPath}\" /f", out string output);
            if (code != 0)
                Log.ZLogWarning($"Could not register logon task {taskName} (exit {code}): {output}");
            return code == 0;
        }
        catch (Exception ex)
        {
            Log.ZLogWarning($"Could not register logon task {taskName}: {ex.Message}");
            return false;
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { /* ignore */ }
        }
    }

    private static string BuildTaskXml(string exe)
    {
        string user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        string dir = Path.GetDirectoryName(exe) ?? "";
        // Priority 4 is normal priority; the Task Scheduler default (7) would run the app below normal.
        // The trigger is limited to this user, which is what lets a non-admin register it.
        // The security descriptor gives the user full control even when an elevated process
        // registered the task; otherwise a normal-token app could never change or remove it.
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts JeekLauncher at logon.</Description>
                <SecurityDescriptor>D:(A;;FA;;;BA)(A;;FA;;;SY)(A;;FA;;;{SecurityElement.Escape(user)})</SecurityDescriptor>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(exe)}</Command>
                  <WorkingDirectory>{SecurityElement.Escape(dir)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static int RunSchtasks(string arguments, out string output)
    {
        output = "";
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
                return -1;
            var stderrTask = process.StandardError.ReadToEndAsync();
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = stderrTask.GetAwaiter().GetResult();
            output = (stdout + stderr).Trim();
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            output = ex.Message;
            return -1;
        }
    }

    private static string? ReadRunValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string value && value.Length > 0 ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private static void DeleteRunValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
            // Registry may be locked down; best-effort.
        }
    }
}

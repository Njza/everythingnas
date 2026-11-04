using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace NasSearchBar;

// Start at Windows login through a Task Scheduler task with the account's highest rights: the app asks for admin
// rights (for the NTFS change journal), and Windows will not start such an app from the Run key, while a task
// starts it without a UAC prompt. The task itself is the setting, so it never goes out of sync with a saved copy.
public static class AutoStart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "NasSearch";
    static readonly string Task = $"NasSearch ({Environment.UserName})";  // task names are per PC, not per user

    public static bool Enabled
    {
        get => Schtasks("/Query", "/TN", Task) == 0;
        set
        {
            if (value)
            {
                var xml = Path.Combine(Path.GetTempPath(), "NasSearch-task.xml");
                File.WriteAllText(xml, TaskXml(), Encoding.Unicode);
                try
                {
                    if (Schtasks("/Create", "/F", "/TN", Task, "/XML", xml) != 0) throw new IOException("计划任务建不了");
                }
                finally { File.Delete(xml); }
            }
            else if (Enabled && Schtasks("/Delete", "/F", "/TN", Task) != 0) throw new IOException("计划任务删不了");
        }
    }

    // Versions before 1.2 used the Run key, which Windows now skips: move it over to a task once
    public static void MigrateRunKey()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(Name) is null) return;
        try { Enabled = true; }
        catch (IOException) { return; }  // keep the old entry; settings can try again
        key.DeleteValue(Name, throwOnMissingValue: false);
    }

    static string TaskXml()
    {
        var user = SecurityElement.Escape($@"{Environment.UserDomainName}\{Environment.UserName}");
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers><LogonTrigger><UserId>{user}</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="Author">
                <UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel>
              </Principal></Principals>
              <Settings>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
              </Settings>
              <Actions Context="Author"><Exec><Command>"{SecurityElement.Escape(Environment.ProcessPath)}"</Command></Exec></Actions>
            </Task>
            """;
    }

    static int Schtasks(params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }
}

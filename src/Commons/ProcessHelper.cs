using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace LLMUtilities.Commons;

public static partial class ProcessHelper
{
    static string? _cachedLlmClient;

    public static string? DetectLlmClient()
    {
        if (_cachedLlmClient is not null)
            return _cachedLlmClient;

        _cachedLlmClient = DetectLlmClientImpl();
        return _cachedLlmClient;
    }

    public static void ClearCache() => _cachedLlmClient = null;

    static string? DetectLlmClientImpl()
    {
        try
        {
            var pid = Environment.ProcessId;
            if (OperatingSystem.IsWindows())
                return WalkTreeWindows(pid);
            if (OperatingSystem.IsLinux())
                return WalkTreeLinux(pid);
            if (OperatingSystem.IsMacOS())
                return WalkTreeMac(pid);
            return null;
        }
        catch
        {
            return null;
        }
    }

    static string? WalkTreeWindows(int pid)
    {
        var seen = new HashSet<int>();
        while (pid > 0 && seen.Add(pid))
        {
            var (name, cmdLine) = GetProcessInfoWindows(pid);
            if (name is null) break;

            var client = MatchLlmClient(name, cmdLine);
            if (client is not null) return client;

            pid = GetParentPidWindows(pid);
        }
        return null;
    }

    static string? WalkTreeLinux(int pid)
    {
        var seen = new HashSet<int>();
        while (pid > 0 && seen.Add(pid))
        {
            var name = GetProcessNameSimple(pid);
            if (name is null) break;

            string? cmdLine = null;
            try
            {
                var cmdPath = $"/proc/{pid}/cmdline";
                if (File.Exists(cmdPath))
                    cmdLine = File.ReadAllText(cmdPath).Replace('\0', ' ').Trim();
            }
            catch { }

            var client = MatchLlmClient(name, cmdLine);
            if (client is not null) return client;

            pid = GetParentPidLinux(pid);
        }
        return null;
    }

    static string? WalkTreeMac(int pid)
    {
        var seen = new HashSet<int>();
        while (pid > 0 && seen.Add(pid))
        {
            var name = GetProcessNameSimple(pid);
            if (name is null) break;

            var client = MatchLlmClient(name, null);
            if (client is not null) return client;

            pid = GetParentPidMac(pid);
        }
        return null;
    }

    static (string? Name, string? CmdLine) GetProcessInfoWindows(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            var name = proc.ProcessName;

            // Try WMI for command line
            try
            {
                var mgmtType = Type.GetType("System.Management.ManagementObjectSearcher, System.Management");
                if (mgmtType is not null)
                {
                    var searcher = Activator.CreateInstance(mgmtType,
                        $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
                    if (searcher is not null)
                    {
                        var getMethod = mgmtType.GetMethod("Get");
                        if (getMethod is null) return (name, null);
                        var results = getMethod.Invoke(searcher, null) as System.Collections.IEnumerable;
                        if (results is not null)
                        {
                            foreach (var r in results)
                            {
                                var cmdProp = r.GetType().GetProperty("CommandLine");
                                var cmd = cmdProp?.GetValue(r) as string;
                                if (cmd is not null)
                                    return (name, cmd);
                                break;
                            }
                        }
                    }
                }
            }
            catch { /* WMI not available */ }

            return (name, null);
        }
        catch
        {
            return (null, null);
        }
    }

    static string? GetProcessNameSimple(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return proc.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    static int GetParentPidWindows(int pid)
    {
        // Use NtQueryInformationProcess (ntdll) — fast, no WMI dependency
        try
        {
            using var proc = Process.GetProcessById(pid);
            var handle = proc.Handle;
            var pbi = default(PROCESS_BASIC_INFORMATION);
            var size = Marshal.SizeOf<PROCESS_BASIC_INFORMATION>();
            if (NtQueryInformationProcess(handle, 0, ref pbi, size, out _) == 0)
                return (int)pbi.InheritedFromUniqueProcessId;
        }
        catch { }
        return -1;
    }

    static int GetParentPidLinux(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            // Format: pid (comm) state ppid ...
            var match = LinuxStatPpid().Match(stat);
            if (match.Success)
                return int.Parse(match.Groups[1].Value);
        }
        catch { }
        return -1;
    }

    static int GetParentPidMac(int pid)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ps",
                Arguments = $"-o ppid= -p {pid}",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is not null)
            {
                var output = proc.StandardOutput.ReadToEnd().Trim();
                if (int.TryParse(output, out var ppid))
                    return ppid;
            }
        }
        catch { }
        return -1;
    }

    static string? MatchLlmClient(string processName, string? commandLine)
    {
        var lower = processName.ToLowerInvariant();
        var cmdLower = commandLine?.ToLowerInvariant() ?? "";

        return lower switch
        {
            "claude" => "Claude Code",
            "opencode" => "OpenCode",
            "cursor" => "Cursor",
            "windsurf" => "Windsurf",
            "zed" or "zed-editor" or "zeditor" => "Zed",
            "python" or "python3" => cmdLower.Contains("gemini") ? "Gemini CLI" : null,
            "node" => cmdLower.Contains("claude") ? "Claude Code" : null,
            _ => null,
        };
    }

    [DllImport("ntdll.dll", SetLastError = true)]
    static extern int NtQueryInformationProcess(IntPtr handle, int processInformationClass,
        ref PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);

    struct PROCESS_BASIC_INFORMATION
    {
        public int ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public int BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [GeneratedRegex(@"\d+\s+\([^)]+\)\s+\S\s+(\d+)")]
    private static partial Regex LinuxStatPpid();
}

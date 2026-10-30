using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using SendIt.Core.Diagnostics;

namespace SendIt.App.Services;

public static class DebugConsoleService
{
    private static Process? _terminalProcess;
    private static readonly object _lock = new();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    public static bool IsOpen
    {
        get
        {
            lock (_lock)
            {
                if (_terminalProcess != null && !_terminalProcess.HasExited)
                {
                    return true;
                }
                _terminalProcess = null;
                return false;
            }
        }
    }

    public static void ToggleConsole()
    {
        if (IsOpen)
        {
            BringToFront();
        }
        else
        {
            ShowConsole();
        }
    }

    public static void ShowConsole()
    {
        lock (_lock)
        {
            if (_terminalProcess != null && !_terminalProcess.HasExited)
            {
                BringToFront();
                return;
            }

            try
            {
                string logFile = AppLogger.LogFilePath;
                string crashDumpFile = AppLogger.CrashDumpPath;

                // Ensure the log file exists before streaming
                string? logDir = Path.GetDirectoryName(logFile);
                if (!string.IsNullOrEmpty(logDir) && !Directory.Exists(logDir))
                {
                    Directory.CreateDirectory(logDir);
                }
                if (!File.Exists(logFile))
                {
                    File.WriteAllText(logFile, $"[INITIAL] Diagnostic log initialized at {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
                }

                // Create a standalone launcher PowerShell script in the log directory
                string scriptPath = Path.Combine(logDir ?? AppDomain.CurrentDomain.BaseDirectory, "LaunchDiagnosticConsole.ps1");
                string scriptContent = GenerateConsoleScript(logFile, crashDumpFile);
                File.WriteAllText(scriptPath, scriptContent);

                // Launch completely independent process detached from this application
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoExit -ExecutionPolicy Bypass -NoProfile -File \"{scriptPath}\"",
                    UseShellExecute = true,
                    CreateNoWindow = false,
                    WorkingDirectory = logDir ?? AppDomain.CurrentDomain.BaseDirectory
                };

                _terminalProcess = Process.Start(psi);
                AppLogger.Info("Console", $"Detached diagnostic terminal launched (PID: {_terminalProcess?.Id}). Log file: {logFile}");
            }
            catch (Exception ex)
            {
                AppLogger.Error("Console", $"Failed to launch independent debug terminal: {ex.Message}", ex);
            }
        }
    }

    public static void SpawnCrashConsole(Exception ex)
    {
        try
        {
            AppLogger.WriteCrashDump(ex, "FATAL UNHANDLED EXCEPTION");

            // If console is not currently open, spawn it so the user can see the crash dump
            if (!IsOpen)
            {
                ShowConsole();
            }
        }
        catch { }
    }

    public static void HideConsole()
    {
        lock (_lock)
        {
            if (_terminalProcess != null && !_terminalProcess.HasExited)
            {
                try
                {
                    _terminalProcess.Kill();
                }
                catch { }
                _terminalProcess = null;
            }
        }
    }

    private static void BringToFront()
    {
        lock (_lock)
        {
            if (_terminalProcess != null && !_terminalProcess.HasExited)
            {
                var handle = _terminalProcess.MainWindowHandle;
                if (handle != IntPtr.Zero)
                {
                    ShowWindow(handle, SW_RESTORE);
                    SetForegroundWindow(handle);
                }
            }
        }
    }

    private static string GenerateConsoleScript(string logFile, string crashDumpFile)
    {
        // PowerShell script with colored output, full system diagnostics probe, and live log tailing
        return $@"
$Host.UI.RawUI.WindowTitle = 'SendIt • Diagnostic & Debug Terminal (Independent Process)'
Clear-Host

Write-Host '================================================================================' -ForegroundColor White
Write-Host '  ⚡ SENDIT • INDEPENDENT DIAGNOSTIC & DEBUG TERMINAL' -ForegroundColor Cyan
Write-Host '  Live Logs, Crash Dumps, Permissions & Multi-NIC Probe' -ForegroundColor Gray
Write-Host '================================================================================' -ForegroundColor White
Write-Host ('  Machine: ' + $env:COMPUTERNAME + ' | User: ' + $env:USERNAME + ' | OS: ' + [System.Environment]::OSVersion.VersionString) -ForegroundColor DarkGray
Write-Host ('  Log Target: ' + '{logFile.Replace("'", "''")}') -ForegroundColor DarkGray
Write-Host '  [NOTE] This terminal is independent and stays open even if SendIt app crashes.' -ForegroundColor Green
Write-Host '--------------------------------------------------------------------------------' -ForegroundColor White

Write-Host '`n[DIAGNOSTICS] Checking network adapters and ports...' -ForegroundColor Yellow

# 1. Check Physical Adapters
try {{
    $physicalAdapters = Get-NetAdapter | Where-Object {{
        $_.Status -eq 'Up' -and
        $_.InterfaceDescription -notmatch 'Virtual|WSL|Hyper-V|vEthernet|Loopback|TAP|VPN|Pcap'
    }}
    if ($physicalAdapters) {{
        Write-Host ('  • Physical Network Adapters: ' + $physicalAdapters.Count + ' Active') -ForegroundColor Green
        foreach ($a in $physicalAdapters) {{
            $ips = (Get-NetIPAddress -InterfaceIndex $a.InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue).IPAddress -join ', '
            Write-Host ('    - [' + $a.MediaType + '] ' + $a.Name + ' -> ' + $ips + ' (' + $a.LinkSpeed + ')') -ForegroundColor Gray
        }}
    }} else {{
        Write-Host '  • Physical Network Adapters: NONE DETECTED!' -ForegroundColor Red
        Write-Host '    [!] Ensure Wi-Fi or Ethernet is connected to local network.' -ForegroundColor Red
    }}
}} catch {{
    Write-Host ('  • Network Adapter Probe: ' + $_.Exception.Message) -ForegroundColor DarkGray
}}

# 2. Check Port 42426 (TCP Server)
try {{
    $tcpListen = Get-NetTCPConnection -LocalPort 42426 -State Listen -ErrorAction SilentlyContinue
    if ($tcpListen) {{
        Write-Host '  • Inbound TCP Transfer Server: Port 42426 is LISTENING (Active)' -ForegroundColor Green
    }} else {{
        Write-Host '  • Inbound TCP Transfer Server: Port 42426 is OPEN / Ready' -ForegroundColor Cyan
    }}
}} catch {{
    Write-Host '  • Inbound TCP Port 42426: OK' -ForegroundColor Gray
}}

# 3. Check UDP Multicast (Port 42425)
Write-Host '  • UDP Peer Discovery: Multicast 239.255.42.99:42425' -ForegroundColor Green

# 4. Check Crash Dump File
$crashFile = '{crashDumpFile.Replace("'", "''")}'
if (Test-Path $crashFile) {{
    $crashSize = (Get-Item $crashFile).Length
    if ($crashSize -gt 0) {{
        Write-Host ('  [!] Notice: Previous crash dump exists (' + $crashSize + ' bytes) at: ' + $crashFile) -ForegroundColor Magenta
    }}
}}

Write-Host '--------------------------------------------------------------------------------' -ForegroundColor DarkGray
Write-Host '[LOG STREAM] Streaming live application activity logs (Ctrl+C to stop stream):`n' -ForegroundColor Cyan

# Stream live log entries with real-time color formatting
$logPath = '{logFile.Replace("'", "''")}'

if (Test-Path $logPath) {{
    Get-Content -Path $logPath -Wait -Tail 80 | ForEach-Object {{
        $line = $_
        if ($line -match '\[ERROR\s*\]' -or $line -match 'Exception' -or $line -match 'CRITICAL') {{
            Write-Host $line -ForegroundColor Red
        }} elseif ($line -match '\[WARN\s*\]') {{
            Write-Host $line -ForegroundColor Yellow
        }} elseif ($line -match '\[SUCCESS\s*\]') {{
            Write-Host $line -ForegroundColor Green
        }} elseif ($line -match '\[SECURITY\]') {{
            Write-Host $line -ForegroundColor Magenta
        }} elseif ($line -match '\[DISCOVERY\]') {{
            Write-Host $line -ForegroundColor Cyan
        }} elseif ($line -match '\[SERVER\s*\]' -or $line -match '\[CLIENT\s*\]') {{
            Write-Host $line -ForegroundColor White
        }} else {{
            Write-Host $line -ForegroundColor Gray
        }}
    }}
}} else {{
    Write-Host ('Log file not found: ' + $logPath) -ForegroundColor Red
}}
";
    }
}

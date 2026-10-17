using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Web;

namespace LoopKey;

internal static class BrowserCapture
{
    public const string CapturePathVariable = "LOOPKEY_CAPTURE_PATH";

    public static async Task<int> CaptureAsync()
    {
        var capturePath = Environment.GetEnvironmentVariable(CapturePathVariable);
        if (string.IsNullOrWhiteSpace(capturePath))
        {
            return 2;
        }

        var parentId = GetParentProcessId((uint)Environment.ProcessId);
        if (parentId == 0)
        {
            return 1;
        }

        var commandLine = await ReadCommandLineAsync(parentId);
        var authorizationUrl = ExtractAuthorizationUrl(commandLine);
        await File.WriteAllTextAsync(capturePath, authorizationUrl);
        return 0;
    }

    internal static string ExtractAuthorizationUrl(string commandLine)
    {
        const string prefix = "https://login.microsoftonline.com/";
        var start = commandLine.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            throw new CliException("The browser launcher command did not contain an Entra authorization URL.");
        }

        var value = commandLine[start..].Trim().TrimEnd('"');
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new CliException("The captured browser URL was not a valid Entra authorization URL.");
        }

        return uri.AbsoluteUri;
    }

    internal static void ValidateAuthorizationUrl(string authorizationUrl, int callbackPort)
    {
        var uri = new Uri(authorizationUrl);
        var query = HttpUtility.ParseQueryString(uri.Query);
        var redirectUri = query["redirect_uri"];
        var expected = $"http://localhost:{callbackPort}";

        if (!string.Equals(redirectUri, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new CliException(
                $"azd requested redirect URI '{redirectUri}', but LoopKey expected '{expected}'.");
        }

        if (string.IsNullOrWhiteSpace(query["state"])
            || string.IsNullOrWhiteSpace(query["code_challenge"])
            || !string.Equals(query["code_challenge_method"], "S256", StringComparison.Ordinal))
        {
            throw new CliException("The authorization URL is missing required state or PKCE parameters.");
        }
    }

    private static async Task<string> ReadCommandLineAsync(uint processId)
    {
        var script =
            $"[Console]::Out.Write((Get-CimInstance Win32_Process -Filter \"ProcessId = {processId}\").CommandLine)";
        var result = await ProcessRunner.RunAsync(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", script],
            environment: null,
            CancellationToken.None);

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            throw new CliException("Unable to inspect the azd browser launcher command line.");
        }

        return result.StandardOutput;
    }

    private static uint GetParentProcessId(uint processId)
    {
        var snapshot = CreateToolhelp32Snapshot(0x00000002, 0);
        if (snapshot == new IntPtr(-1))
        {
            return 0;
        }

        try
        {
            var entry = new ProcessEntry32
            {
                Size = (uint)Marshal.SizeOf<ProcessEntry32>(),
            };

            if (!Process32First(snapshot, ref entry))
            {
                return 0;
            }

            do
            {
                if (entry.ProcessId == processId)
                {
                    return entry.ParentProcessId;
                }
            }
            while (Process32Next(snapshot, ref entry));

            return 0;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}

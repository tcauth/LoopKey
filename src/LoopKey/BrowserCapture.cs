using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Web;

namespace LoopKey;

internal static class BrowserCapture
{
    public const string CapturePathVariable = "LOOPKEY_CAPTURE_PATH";
    public const string AllowedHostsVariable = "LOOPKEY_ALLOWED_AUTH_HOSTS";

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
        var allowedHosts = (Environment.GetEnvironmentVariable(AllowedHostsVariable) ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (allowedHosts.Length == 0)
        {
            throw new CliException("No authorization hosts were configured for browser capture.");
        }

        var authorizationUrl = ExtractAuthorizationUrl(commandLine, allowedHosts);
        await File.WriteAllTextAsync(capturePath, authorizationUrl);
        return 0;
    }

    internal static string ExtractAuthorizationUrl(
        string commandLine,
        IReadOnlyCollection<string> allowedHosts)
    {
        var start = allowedHosts
            .Select(host => commandLine.IndexOf(
                $"https://{host}",
                StringComparison.OrdinalIgnoreCase))
            .Where(index => index >= 0)
            .DefaultIfEmpty(-1)
            .Min();
        if (start < 0)
        {
            throw new CliException(
                "The browser launcher command did not contain an authorization URL for an allowed host.");
        }

        var value = commandLine[start..].Trim().TrimEnd('"');
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !allowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            throw new CliException("The captured browser URL was not valid for an allowed host.");
        }

        return uri.AbsoluteUri;
    }

    internal static void ValidateAuthorizationUrl(
        string authorizationUrl,
        int callbackPort,
        AuthorizationPolicy policy)
    {
        var uri = new Uri(authorizationUrl);
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !policy.AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            throw new CliException("The authorization URL host is not allowed by the profile.");
        }

        var query = HttpUtility.ParseQueryString(uri.Query);
        var redirectUri = query[policy.RedirectUriParameter];
        var expected = policy.RedirectUriTemplate.Replace(
            "{callbackPort}",
            callbackPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            StringComparison.Ordinal);

        if (!string.Equals(redirectUri, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new CliException(
                $"The login command requested redirect URI '{redirectUri}', but LoopKey expected '{expected}'.");
        }

        foreach (var parameter in policy.RequiredQueryParameters)
        {
            if (string.IsNullOrWhiteSpace(query[parameter]))
            {
                throw new CliException(
                    $"The authorization URL is missing required query parameter '{parameter}'.");
            }
        }
        foreach (var pair in policy.RequiredQueryValues)
        {
            if (!string.Equals(query[pair.Key], pair.Value, StringComparison.Ordinal))
            {
                throw new CliException(
                    $"The authorization URL query parameter '{pair.Key}' did not match the configured value.");
            }
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
            throw new CliException("Unable to inspect the login command's browser launcher command line.");
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

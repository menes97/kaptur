using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Kaptur;

/// Chrome'un native messaging host'u. Chrome, eklenti runtime.sendNativeMessage çağırdığında Kaptur.exe'yi
/// "chrome-extension://&lt;id&gt;/" argümanıyla başlatır; bu modda pencere açılmaz, stdin/stdout üzerinden konuşulur.
/// Tek iş: {"action":"launchApp"} gelince Kaptur'u tepside başlatmak. URL ya da çerez buradan geçmez.
///
/// Protokol: her mesaj 32 bit uzunluk (yerel bayt sırası) + UTF-8 JSON.
static class NativeHost
{
    public static bool IsInvocation(string[] args) =>
        args.Length > 0 && args[0].StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase);

    public static int Run()
    {
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        // sendNativeMessage tek mesaj gönderip stdin'i kapatır; connectNative açık tutar. EOF'a kadar hizmet et.
        while (Read(input) is { } message)
        {
            var reply = (message["action"] as JsonValue)?.ToString() switch
            {
                "launchApp" => LaunchApp(),
                "ping" => new JsonObject { ["ok"] = true },
                _ => new JsonObject { ["ok"] = false, ["error"] = "bilinmeyen işlem" },
            };
            Write(output, reply);
        }
        return 0;
    }

    static JsonObject LaunchApp()
    {
        if (Mutex.TryOpenExisting(Program.MutexName, out var running))
        {
            running.Dispose();
            return new JsonObject { ["ok"] = true, ["alreadyRunning"] = true };
        }
        var exe = Environment.ProcessPath;
        var ok = exe != null && Spawn(exe, "--tray");
        return new JsonObject { ["ok"] = ok, ["alreadyRunning"] = false };
    }

    static JsonNode? Read(Stream input)
    {
        var header = ReadExactly(input, 4);
        if (header == null) return null;
        var length = BitConverter.ToInt32(header);
        if (length is <= 0 or > 1 << 20) return null;
        var body = ReadExactly(input, length);
        return body == null ? null : JsonNode.Parse(body);
    }

    static byte[]? ReadExactly(Stream s, int count)
    {
        var buf = new byte[count];
        var have = 0;
        while (have < count)
        {
            var n = s.Read(buf, have, count - have);
            if (n == 0) return null;
            have += n;
        }
        return buf;
    }

    static void Write(Stream output, JsonNode reply)
    {
        var body = Encoding.UTF8.GetBytes(reply.ToJsonString());
        output.Write(BitConverter.GetBytes(body.Length));
        output.Write(body);
        output.Flush();
    }

    // Process.Start, tanıtıcıları (handle) devralınabilir yapar: Kaptur, Chrome'un stdout borusunu açık tutar ve
    // Chrome host'un bittiğini anlamaz. Ayrıca Chrome host'u bir iş nesnesine (job object) koyarsa, host
    // kapanınca Kaptur da öldürülebilir. Bu yüzden CreateProcess'i doğrudan, devralma kapalı ve
    // mümkünse iş nesnesinden ayrılarak (CREATE_BREAKAWAY_FROM_JOB) çağırıyoruz.
    const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
    const uint DETACHED_PROCESS = 0x00000008;
    const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessW(string? application, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    static bool Spawn(string exe, string args)
    {
        uint[] attempts =
        [
            CREATE_BREAKAWAY_FROM_JOB | DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP,
            DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP,   // iş nesnesi ayrılmaya izin vermiyorsa
        ];
        foreach (var flags in attempts)
        {
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
            var cmd = new StringBuilder($"\"{exe}\" {args}");
            if (CreateProcessW(exe, cmd, IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero,
                    Path.GetDirectoryName(exe), ref si, out var pi))
            {
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
                return true;
            }
        }
        return false;
    }
}

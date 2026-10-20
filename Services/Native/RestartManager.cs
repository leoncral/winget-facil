using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Asa.Services.Native;

internal static class RestartManager
{
    private const int ErrorSuccess = 0;
    private const int ErrorMoreData = 234;
    private const int MaxNameLength = 256;
    private const int MaxServiceNameLength = 64;

    public static IReadOnlyList<RestartManagerProcess> FindProcesses(IReadOnlyList<string> files)
    {
        if (files.Count == 0)
        {
            return [];
        }

        var sessionKey = Guid.NewGuid().ToString("N");
        var result = RmStartSession(out var session, 0, sessionKey);
        if (result != ErrorSuccess)
        {
            throw new Win32Exception(result);
        }

        try
        {
            var fileArray = files.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            result = RmRegisterResources(
                session,
                (uint)fileArray.Length,
                fileArray,
                0,
                null,
                0,
                null);

            if (result != ErrorSuccess)
            {
                throw new Win32Exception(result);
            }

            uint needed = 0;
            uint count = 0;
            result = RmGetList(session, out needed, ref count, null, out _);

            if (result == ErrorSuccess)
            {
                return [];
            }

            if (result != ErrorMoreData)
            {
                throw new Win32Exception(result);
            }

            for (var attempt = 0; attempt < 4; attempt++)
            {
                count = needed;
                var buffer = new RmProcessInfo[(int)count];
                result = RmGetList(session, out needed, ref count, buffer, out _);

                if (result == ErrorSuccess)
                {
                    return buffer
                        .Take((int)count)
                        .Select(info => new RestartManagerProcess(
                            info.Process.ProcessId,
                            info.ApplicationName,
                            info.ServiceShortName,
                            (RestartManagerAppType)info.ApplicationType))
                        .Where(process => process.ProcessId > 0)
                        .ToList();
                }

                if (result != ErrorMoreData)
                {
                    throw new Win32Exception(result);
                }
            }

            return [];
        }
        finally
        {
            RmEndSession(session);
        }
    }

    internal enum RestartManagerAppType
    {
        Unknown = 0,
        MainWindow = 1,
        OtherWindow = 2,
        Service = 3,
        Explorer = 4,
        Console = 5,
        Critical = 6
    }

    internal sealed record RestartManagerProcess(
        int ProcessId,
        string ApplicationName,
        string ServiceShortName,
        RestartManagerAppType Type);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public int LowDateTime;
        public int HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int ProcessId;
        public NativeFileTime ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxNameLength)]
        public string ApplicationName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxServiceNameLength)]
        public string ServiceShortName;

        public int ApplicationType;
        public uint AppStatus;
        public uint SessionId;
        public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint sessionHandle, int sessionFlags, string sessionKey);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint sessionHandle,
        uint fileCount,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[]? fileNames,
        uint applicationCount,
        [MarshalAs(UnmanagedType.LPArray)] RmUniqueProcess[]? applications,
        uint serviceCount,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[]? serviceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint sessionHandle,
        out uint processesNeeded,
        ref uint processCount,
        [MarshalAs(UnmanagedType.LPArray)] RmProcessInfo[]? affectedApps,
        out uint rebootReasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint sessionHandle);
}

using System.Runtime.InteropServices;

namespace Asa.Services.Native;

internal static class WindowsServiceControl
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint ServiceControlStop = 0x00000001;
    private const uint ServiceStopped = 1;
    private const uint ServiceStopPending = 3;
    private const uint ServiceRunning = 4;
    private const uint ServiceStartPending = 2;
    private const int ErrorServiceNotActive = 1062;
    private const int ErrorServiceAlreadyRunning = 1056;

    public static bool IsRunning(string serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return false;
        }

        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var service = OpenService(manager, serviceName, ServiceQueryStatus);
            if (service == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                return TryQueryState(service, out var state) && state == ServiceRunning;
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    public static bool Stop(string serviceName, TimeSpan timeout)
    {
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var service = OpenService(manager, serviceName, ServiceQueryStatus | ServiceStop);
            if (service == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                if (!TryQueryState(service, out var state) || state == ServiceStopped)
                {
                    return true;
                }

                if (!ControlService(service, ServiceControlStop, out _))
                {
                    return false;
                }

                var deadline = DateTime.UtcNow + timeout;
                while (DateTime.UtcNow < deadline)
                {
                    if (!TryQueryState(service, out state) || state == ServiceStopped)
                    {
                        return true;
                    }

                    Thread.Sleep(250);
                }

                return !TryQueryState(service, out state) || state == ServiceStopped;
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    public static bool Start(string serviceName, TimeSpan timeout)
    {
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var service = OpenService(manager, serviceName, ServiceQueryStatus | ServiceStart);
            if (service == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                if (TryQueryState(service, out var state) && state == ServiceRunning)
                {
                    return true;
                }

                if (!StartService(service, 0, null))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != ErrorServiceAlreadyRunning)
                    {
                        return false;
                    }
                }

                var deadline = DateTime.UtcNow + timeout;
                while (DateTime.UtcNow < deadline)
                {
                    if (!TryQueryState(service, out state))
                    {
                        return false;
                    }

                    if (state == ServiceRunning)
                    {
                        return true;
                    }

                    if (state is not (ServiceStartPending or ServiceStopPending))
                    {
                        return false;
                    }

                    Thread.Sleep(250);
                }

                return TryQueryState(service, out state) && state == ServiceRunning;
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    private static bool TryQueryState(IntPtr service, out uint state)
    {
        state = 0;
        if (!QueryServiceStatus(service, out var status))
        {
            return false;
        }

        state = status.CurrentState;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr manager, string serviceName, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(IntPtr service, uint control, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartService(IntPtr service, uint argumentCount, string[]? arguments);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}

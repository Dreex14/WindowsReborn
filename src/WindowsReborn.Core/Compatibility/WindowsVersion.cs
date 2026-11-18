using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WindowsReborn.Core.Compatibility;

/// <summary>Información de la versión de Windows en ejecución.</summary>
public sealed record WindowsVersion(int Major, int Minor, int Build, int Revision, string DisplayVersion, string ProductName)
{
    /// <summary>Windows 11 se identifica por compilación ≥ 22000 (el número mayor sigue siendo 10).</summary>
    public bool IsWindows11 => Major == 10 && Build >= 22000;

    public string FriendlyName => $"{(IsWindows11 ? "Windows 11" : "Windows 10")} {DisplayVersion} ({Build}.{Revision})".Trim();

    public static WindowsVersion Current { get; } = Detect();

    private static WindowsVersion Detect()
    {
        var info = new OsVersionInfoEx { dwOSVersionInfoSize = (uint)Marshal.SizeOf<OsVersionInfoEx>() };
        int major = Environment.OSVersion.Version.Major, minor = Environment.OSVersion.Version.Minor, build = Environment.OSVersion.Version.Build;
        if (RtlGetVersion(ref info) == 0)
        {
            major = (int)info.dwMajorVersion;
            minor = (int)info.dwMinorVersion;
            build = (int)info.dwBuildNumber;
        }

        string display = "", product = "";
        var revision = 0;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            display = key?.GetValue("DisplayVersion") as string ?? key?.GetValue("ReleaseId") as string ?? "";
            product = key?.GetValue("ProductName") as string ?? "";
            if (key?.GetValue("UBR") is int ubr)
                revision = ubr;
        }
        catch
        {
            // Lectura opcional.
        }

        // ProductName sigue diciendo «Windows 10» en Windows 11; se corrige.
        if (build >= 22000 && product.StartsWith("Windows 10", StringComparison.Ordinal))
            product = "Windows 11" + product["Windows 10".Length..];

        return new WindowsVersion(major, minor, build, revision, display, product);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OsVersionInfoEx
    {
        public uint dwOSVersionInfoSize, dwMajorVersion, dwMinorVersion, dwBuildNumber, dwPlatformId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szCSDVersion;
        public ushort wServicePackMajor, wServicePackMinor, wSuiteMask;
        public byte wProductType, wReserved;
    }

    [DllImport("ntdll.dll")]
    private static extern int RtlGetVersion(ref OsVersionInfoEx versionInfo);
}

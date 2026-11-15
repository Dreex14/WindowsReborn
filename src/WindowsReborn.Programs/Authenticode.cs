using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace WindowsReborn.Programs;

public enum SignatureStatus
{
    /// <summary>Tipo de archivo que no lleva firma Authenticode (zip, etc.).</summary>
    NotApplicable,
    Valid,
    Unsigned,
    /// <summary>Firma presente pero no válida (archivo alterado, certificado revocado o no fiable).</summary>
    Invalid,
}

public sealed record SignatureInfo(SignatureStatus Status, string? Signer);

/// <summary>Verificación de firmas Authenticode mediante <c>WinVerifyTrust</c>.</summary>
public static class Authenticode
{
    private static readonly string[] SignableExtensions = [".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".dll", ".sys", ".cab"];

    public static SignatureInfo Verify(string path)
    {
        if (!SignableExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            return new SignatureInfo(SignatureStatus.NotApplicable, null);

        var result = WinVerify(path);
        string? signer = null;
        try
        {
#pragma warning disable SYSLIB0057 // No hay alternativa en X509CertificateLoader para leer la firma de un archivo.
            using var cert = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            using var cert2 = new X509Certificate2(cert);
            signer = cert2.GetNameInfo(X509NameType.SimpleName, false);
        }
        catch
        {
            // Sin firma incrustada.
        }

        return result switch
        {
            0 => new SignatureInfo(SignatureStatus.Valid, signer),
            TrustENoSignature or TrustESubjectFormUnknown => new SignatureInfo(signer is null ? SignatureStatus.Unsigned : SignatureStatus.Invalid, signer),
            _ => new SignatureInfo(SignatureStatus.Invalid, signer),
        };
    }

    public static bool PublisherMatches(SignatureInfo info, string? expected) =>
        string.IsNullOrWhiteSpace(expected) ||
        (info.Status == SignatureStatus.Valid && info.Signer is not null &&
         info.Signer.Contains(expected.Trim(), StringComparison.OrdinalIgnoreCase));

    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustESubjectFormUnknown = unchecked((int)0x800B0003);

    private static int WinVerify(string path)
    {
        var fileInfo = new WinTrustFileInfo
        {
            cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            pcwszFilePath = path,
        };
        var fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);
            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = 2,            // WTD_UI_NONE
                fdwRevocationChecks = 0,   // WTD_REVOKE_NONE (comprobación de revocación de la cadena con el flag siguiente)
                dwUnionChoice = 1,         // WTD_CHOICE_FILE
                pFile = fileInfoPtr,
                dwStateAction = 0,
                dwProvFlags = 0x00000080,  // WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            return WinVerifyTrust(new IntPtr(-1), action, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(fileInfoPtr);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, ref WinTrustData pWVTData);
}

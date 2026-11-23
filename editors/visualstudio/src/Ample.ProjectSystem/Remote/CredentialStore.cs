using System.Runtime.InteropServices;
using System.Text;

namespace Ample.VisualStudio.Remote;

/// <summary>The Windows Credential Manager: the place for the password of a server's AMPLE (never a project file, never the log).</summary>
public static class CredentialStore
{
    private const int GenericCredential = 1;
    private const int PersistLocalMachine = 2;

    /// <summary>Gets the target name under which the password of one server and user is kept.</summary>
    public static string Target(string server, string user) => "AMPLE:" + server.TrimEnd('/') + ":" + user;

    /// <summary>Stores the password.</summary>
    public static void Write(string target, string user, string password)
    {
        byte[] blob = Encoding.Unicode.GetBytes(password);
        IntPtr memory = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, memory, blob.Length);
            CREDENTIAL credential = new()
            {
                Type = GenericCredential,
                TargetName = target,
                UserName = user,
                CredentialBlob = memory,
                CredentialBlobSize = blob.Length,
                Persist = PersistLocalMachine,
            };
            if (!CredWrite(ref credential, 0)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    /// <summary>Reads the password, or null when there is none.</summary>
    public static string? Read(string target)
    {
        if (!CredRead(target, GenericCredential, 0, out IntPtr pointer)) return null;
        try
        {
            CREDENTIAL credential = Marshal.PtrToStructure<CREDENTIAL>(pointer);
            byte[] blob = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
            return Encoding.Unicode.GetString(blob);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    /// <summary>Removes the password.</summary>
    public static void Delete(string target) => CredDelete(target, GenericCredential, 0);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}

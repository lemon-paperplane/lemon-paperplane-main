using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace PaperplaneKit;

/// <summary>
/// Windows 凭据管理器（普通凭据，CRED_TYPE_GENERIC）：远程服务器的访问密钥存这里，不落普通文件（docs/remote.md 第 2 节）。
/// 内容按 UTF-16 保存（和 cmdkey、凭据管理器界面一致），只对当前用户可见，随用户配置漫游时只留在本机（CRED_PERSIST_LOCAL_MACHINE）。
/// </summary>
public static class CredentialStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    /// <summary>写入（已有则覆盖）。失败抛 Win32Exception（例如 ssh 等没有凭据的登录会话里是 1312）。</summary>
    public static void Write(string target, string secret, string? userName = null, string? comment = null)
    {
        var bytes = Encoding.Unicode.GetBytes(secret);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var c = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                Comment = comment,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = userName ?? Environment.UserName,
            };
            if (!CredWrite(ref c, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            // 擦掉明文再释放
            Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
            Marshal.FreeHGlobal(blob);
        }
    }

    /// <summary>读取；没有这条时返回 null，其他错误抛 Win32Exception。</summary>
    public static string? Read(string target)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out var ptr))
        {
            var err = Marshal.GetLastWin32Error();
            if (err == ErrorNotFound) return null;
            throw new Win32Exception(err);
        }
        try
        {
            var c = Marshal.PtrToStructure<Credential>(ptr);
            if (c.CredentialBlob == IntPtr.Zero || c.CredentialBlobSize == 0) return "";
            var bytes = new byte[c.CredentialBlobSize];
            Marshal.Copy(c.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally
        {
            CredFree(ptr);
        }
    }

    /// <summary>删除；没有这条时返回 false。</summary>
    public static bool Delete(string target)
    {
        if (CredDelete(target, CredTypeGeneric, 0)) return true;
        var err = Marshal.GetLastWin32Error();
        if (err == ErrorNotFound) return false;
        throw new Win32Exception(err);
    }
}

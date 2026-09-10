using System.Security.AccessControl;
using System.Security.Principal;

namespace FederationCompanion;

internal static class PrivateFile
{
    public static FileStream Create(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var stream = new FileStream(path, options);
        try { Restrict(path); return stream; }
        catch { stream.Dispose(); throw; }
    }

    public static void Restrict(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var sid = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException("Cannot identify the current Windows user.");
            var acl = new FileSecurity();
            acl.SetOwner(sid);
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(acl);
        }
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public static async Task WriteTextAsync(string path, string value, CancellationToken ct)
    {
        await using var file = Create(path);
        await using var writer = new StreamWriter(file);
        await writer.WriteAsync(value.AsMemory(), ct);
    }
}

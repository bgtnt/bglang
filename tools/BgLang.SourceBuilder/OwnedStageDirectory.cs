using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BgLang.SourceBuilder;

/// <summary>Exclusive ownership of one sibling stage; rename commits its publication.</summary>
internal sealed class OwnedStageDirectory : IDisposable
{
    private readonly string _parent;
    private readonly string _name;
    private bool _published;
    internal string Path { get; }

    private OwnedStageDirectory(string parent, string name)
    {
        _parent = parent; _name = name;
        Path = System.IO.Path.Combine(parent, name);
    }

    internal static OwnedStageDirectory Create(string parent)
    {
        ValidateAncestry(parent);
        string name = ".bglang-build-" + Guid.NewGuid().ToString("N");
        string path = System.IO.Path.Combine(parent, name);
        // Directory.CreateDirectory accepts existing directories. The OS primitive instead
        // fails on any collision, so failed acquisition never grants recursive-cleanup rights.
        bool created = CreateDirectoryWindows(path, IntPtr.Zero);
        if (!created) throw new IOException("Cannot acquire a new stage: " + new Win32Exception(Marshal.GetLastPInvokeError()).Message);
        return new(parent, name);
    }

    internal void Publish(string target)
    {
        VerifyOwnership();
        Directory.Move(Path, target);
        _published = true;
    }

    public void Dispose()
    {
        if (_published || !Directory.Exists(Path)) return;
        VerifyOwnership();
        Directory.Delete(Path, recursive: true);
    }

    internal void VerifyOwnership()
    {
        ValidateAncestry(_parent);
        string fullPath = System.IO.Path.GetFullPath(Path);
        if (System.IO.Path.GetDirectoryName(fullPath) != _parent || System.IO.Path.GetFileName(fullPath) != _name ||
            (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Stage ownership cannot be verified for cleanup.");
    }

    private static void ValidateAncestry(string parent)
    {
        for (DirectoryInfo? directory = new(parent); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Output ancestry must not contain reparse points.");
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryWindows(string path, IntPtr securityAttributes);

}

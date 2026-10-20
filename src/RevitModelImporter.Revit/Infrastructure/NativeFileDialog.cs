using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace RevitModelImporter.Revit.Infrastructure;

/// <summary>Open-file dialog with an explicit native owner window. WPF's OpenFileDialog only accepts a WPF
/// Window as owner; Revit's main window is a native handle, so we call GetOpenFileNameW directly.</summary>
internal static class NativeFileDialog
{
    private const int OFN_PATHMUSTEXIST = 0x00000800;
    private const int OFN_FILEMUSTEXIST = 0x00001000;
    private const int OFN_HIDEREADONLY = 0x00000004;
    private const int OFN_NOCHANGEDIR = 0x00000008;
    private const int OFN_EXPLORER = 0x00080000;
    private const int OFN_ENABLESIZING = 0x00800000;
    private const int OFN_DONTADDTORECENT = 0x02000000;

    /// <param name="filters">(Label, "*.obj;*.fbx") pairs.</param>
    /// <returns>The chosen file or null when cancelled.</returns>
    public static string? OpenFile(IntPtr owner, string title, IReadOnlyList<(string Label, string Patterns)> filters, string? initialDirectory)
    {
        var filter = new StringBuilder();
        foreach (var (label, patterns) in filters)
        {
            filter.Append(label).Append('\0').Append(patterns).Append('\0');
        }

        filter.Append('\0');

        const int bufferChars = 32768;
        var fileBuffer = Marshal.AllocHGlobal(bufferChars * 2);
        var filterPtr = Marshal.StringToHGlobalUni(filter.ToString());
        var titlePtr = Marshal.StringToHGlobalUni(title);
        var dirPtr = initialDirectory is not null && Directory.Exists(initialDirectory) ? Marshal.StringToHGlobalUni(initialDirectory) : IntPtr.Zero;
        try
        {
            Marshal.WriteInt16(fileBuffer, 0);
            var ofn = new OpenFileName
            {
                lStructSize = Marshal.SizeOf<OpenFileName>(),
                hwndOwner = owner,
                lpstrFilter = filterPtr,
                nFilterIndex = 1,
                lpstrFile = fileBuffer,
                nMaxFile = bufferChars,
                lpstrTitle = titlePtr,
                lpstrInitialDir = dirPtr,
                Flags = OFN_EXPLORER | OFN_PATHMUSTEXIST | OFN_FILEMUSTEXIST | OFN_HIDEREADONLY | OFN_NOCHANGEDIR | OFN_ENABLESIZING | OFN_DONTADDTORECENT,
            };
            return GetOpenFileNameW(ref ofn) ? Marshal.PtrToStringUni(fileBuffer) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(fileBuffer);
            Marshal.FreeHGlobal(filterPtr);
            Marshal.FreeHGlobal(titlePtr);
            if (dirPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(dirPtr);
            }
        }
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileNameW(ref OpenFileName ofn);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public IntPtr lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        public IntPtr lpstrInitialDir;
        public IntPtr lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public IntPtr lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public IntPtr lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }
}

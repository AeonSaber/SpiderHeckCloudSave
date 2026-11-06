using System.Runtime.InteropServices;

namespace CloudSave.Junction
{
    // The mod only reads link state. Creating and removing links is the helper's job, after the
    // game has closed (see HelperLauncher).
    public static class JunctionHelper
    {
        private const uint FileAttributeDirectory = 0x10;
        private const uint FileAttributeReparsePoint = 0x400;
        private const uint InvalidAttributes = 0xFFFFFFFF;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFileAttributes(string lpFileName);

        public static bool IsJunction(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            uint attributes = GetFileAttributes(path);
            return attributes != InvalidAttributes
                && (attributes & FileAttributeReparsePoint) == FileAttributeReparsePoint
                && (attributes & FileAttributeDirectory) == FileAttributeDirectory;
        }
    }
}

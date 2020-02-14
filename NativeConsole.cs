using System ;
using System.Runtime.InteropServices ;
using Microsoft.Win32.SafeHandles ;

namespace SendGmail
{
    static class NativeConsole
    {
        [DllImport ("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile (
              string lpFileName,
              UInt32 dwDesiredAccess,
              UInt32 dwShareMode,
              IntPtr lpSecurityAttributes,
              UInt32 dwCreationDisposition,
              UInt32 dwFlagsAndAttributes,
              IntPtr hTemplateFile
        ) ;

        [DllImport ("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleMode (IntPtr handle, out UInt32 mode) ;

        [DllImport ("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleMode (SafeFileHandle handle, out UInt32 mode) ;

        [DllImport ("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleMode (SafeFileHandle handle, UInt32 mode) ;

        [DllImport ("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle (StdHandleKind kind) ;

        [DllImport ("kernel32.dll", SetLastError = true)]
        private static extern bool SetStdHandle (StdHandleKind kind, SafeFileHandle handle) ;

        [DllImport ("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle (IntPtr handle) ;

        enum StdHandleKind
        {
            Input  = -10,
            Output = -11,
            Error  = -12,
        }

        const UInt32 GENERIC_WRITE     = 0x40000000 ;
        const UInt32 GENERIC_READ      = 0x80000000 ;
        const UInt32 OPEN_EXISTING     = 0x00000003 ;
        const UInt32 FILE_SHARE_READ   = 0x00000001 ;
        const UInt32 FILE_SHARE_WRITE  = 0x00000002 ;
        const UInt32 FILE_SHARE_DELETE = 0x00000004 ;

        const UInt32 ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004 ;

        static NativeConsole ()
        {
            // NB: Console caches the standard input handle at the first call
            // to IsConsoleInputRedirected, ReadKey and some other methods,
            // which makes replacing it later impossible
            var stdin = GetStdHandle (StdHandleKind.Input) ;
            if (GetConsoleMode (stdin, out _))
            {
                HaveInput = true ;
            }
            else
            using (var conin = CreateFile ("CONIN$", GENERIC_READ, FILE_SHARE_READ, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
            if (conin != null && SetStdHandle (StdHandleKind.Input, conin))
            {
                // the process now owns the handle
                conin.SetHandleAsInvalid () ;

                CloseHandle (stdin) ;

                HaveInput = true ;
            }

            var stdout = GetStdHandle (StdHandleKind.Output) ;
            if (GetConsoleMode (stdout, out _))
            {
                // ok
            }
            else
            using (var conout = CreateFile ("CONOUT$", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
            if (conout != null && SetStdHandle (StdHandleKind.Output, conout))
            {
                if (GetConsoleMode (conout, out var mode))
                    SetConsoleMode (conout, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING) ;

                // the process now owns the handle
                conout.SetHandleAsInvalid () ;

                CloseHandle (stdout) ;
            }
        }

        public static void ReattachToTerminal () {}

        public static bool HaveInput { get ; }
    }
}

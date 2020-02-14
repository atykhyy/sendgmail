using System ;
using System.Collections ;
using System.Collections.Generic ;
using System.Runtime.InteropServices ;
using Com = System.Runtime.InteropServices.ComTypes ;

namespace SendGmail
{
    public enum CredentialType
    {
        Generic           = 1,
        DomainPassword    = 2,
        DomainCertificate = 3,
    }

    public enum Persistence
    {
        Session      = 1,
        LocalMachine = 2,
        Enterprise   = 3,
    }

    public interface IBorrowedNativeCredential : IDisposable
    {
        ref NativeCredential NativeCredential { get ; }
    }

    public interface IBorrowedNativeCredentials : IEnumerable<NativeCredential>, IDisposable
    {
    }

    [StructLayout (LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NativeCredential
    {
        #region --[Fields]------------------------------------------------
        private UInt32          Flags ;
        public  CredentialType  Type  ;
        public  string          TargetName ;
        public  string          Comment ;
        private Com.FILETIME    LastWrittenFT ;
        private Int32           CredentialBlobSize ;
        private IntPtr          CredentialBlobPtr  ;
        public  Persistence     Persistence ;
        private UInt32          AttributeCount ;
        private IntPtr          Attributes  ;
        public  string          TargetAlias ;
        public  string          UserName ;
        #endregion

        #region --[Members: Public]---------------------------------------
        public DateTime LastWritten
        {
            get
            {
                var ticks = ((long)LastWrittenFT.dwHighDateTime << 32) | (uint)LastWrittenFT.dwLowDateTime ;
                if (ticks == 0)
                    return default ;

                return DateTime.FromFileTimeUtc (ticks) ;
            }
            set
            {
                var ticks = value != default ? value.ToFileTimeUtc () : default ;
                LastWrittenFT.dwHighDateTime = (int)(ticks >> 32) ;
                LastWrittenFT.dwLowDateTime  = (int)(uint)(ticks) ;
            }
        }

        public string CredentialBlobStr
        {
            get
            {
                if (CredentialBlobPtr == IntPtr.Zero)
                    return null ;

                return Marshal.PtrToStringUni (CredentialBlobPtr, CredentialBlobSize / 2) ;
            }
        }

        public byte[] CredentialBlobBytes
        {
            get
            {
                if (CredentialBlobPtr == IntPtr.Zero)
                    return null ;

                var bytes = new byte[CredentialBlobSize] ;
                Marshal.Copy (CredentialBlobPtr, bytes, 0, CredentialBlobSize) ;
                return bytes ;
            }
        }

        public unsafe void Save (string credentialBlob)
        {
            bool succ ;
            if (credentialBlob == null)
            {
                CredentialBlobPtr  = default ;
                CredentialBlobSize = default ;

                succ = CredWrite (ref this, 0) ;
            }
            else
            fixed (char* ptr = credentialBlob)
            {
                CredentialBlobPtr  = new IntPtr (ptr) ;
                CredentialBlobSize = credentialBlob.Length * 2 ;

                succ = CredWrite (ref this, 0) ;
            }

            if (succ)
                return ;

            throw Marshal.GetExceptionForHR (Marshal.GetHRForLastWin32Error ()) ;
        }

        public unsafe void Save (byte[] credentialBlob, int? length = null)
        {
            bool succ ;
            if (credentialBlob == null)
            {
                CredentialBlobPtr  = default ;
                CredentialBlobSize = default ;

                succ = CredWrite (ref this, 0) ;
            }
            else
            fixed (byte* ptr = credentialBlob)
            {
                if (length > credentialBlob.Length)
                    throw new ArgumentOutOfRangeException (nameof (length)) ;

                CredentialBlobPtr  = new IntPtr (ptr) ;
                CredentialBlobSize = length ?? credentialBlob.Length ;

                succ = CredWrite (ref this, 0) ;
            }

            if (succ)
                return ;

            throw Marshal.GetExceptionForHR (Marshal.GetHRForLastWin32Error ()) ;
        }

        public void Delete ()
        {
            Delete (TargetName, Type) ;
        }
        #endregion

        #region --[Methods: Public, static]-------------------------------
        public static void Delete (string target, CredentialType type)
        {
            if (CredDelete (target, type, 0))
                return ;

            throw Marshal.GetExceptionForHR (Marshal.GetHRForLastWin32Error ()) ;
        }

        public static IBorrowedNativeCredentials Enumerate (string filter)
        {
            if (CredEnumerate (filter, 0, out var count, out var credentials))
            {
                credentials.Initialize<IntPtr> (count) ;
                return credentials ;
            }

            var error  = Marshal.GetLastWin32Error () ;
            if (error == 1168)
            {
                var empty = new EnumeratedCredBuffer () ;
                empty.Initialize (0) ;
                return empty ;
            }

            throw Marshal.GetExceptionForHR (Marshal.GetHRForLastWin32Error ()) ;
        }

        public static IBorrowedNativeCredential Get (string target, CredentialType type)
        {
            return TryGet (target, type, out var credential) ? credential : null ;
        }

        public static bool TryGet (string target, CredentialType type, out IBorrowedNativeCredential credential)
        {
            if (CredRead (target, type, 0, out var handle))
            {
                credential = handle ;
                return true ;
            }

            var error  = Marshal.GetLastWin32Error () ;
            if (error == 1168)
            {
                credential = null ;
                return false ;
            }

            throw Marshal.GetExceptionForHR (Marshal.GetHRForLastWin32Error ()) ;
        }
        #endregion

        #region --[Classes: Private]--------------------------------------
        sealed class SafeHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid, IBorrowedNativeCredential
        {
            private bool             m_read  ;
            private NativeCredential m_value ;

            private SafeHandle () : base (true) {}

            public ref NativeCredential NativeCredential
            {
                get
                {
                    if(!m_read)
                    {
                        m_value = Marshal.PtrToStructure<NativeCredential> (handle) ;
                        m_read  = true ;
                    }

                    return ref m_value ;
                }
            }

            protected override bool ReleaseHandle ()
            {
                return CredFree (handle) ;
            }
        }

        sealed class EnumeratedCredBuffer : SafeBuffer, IBorrowedNativeCredentials
        {
            internal EnumeratedCredBuffer () : base (true) {}

            protected override bool ReleaseHandle ()
            {
                return CredFree (handle) ;
            }

            public IEnumerator<NativeCredential> GetEnumerator ()
            {
                for (var i = 0UL ; i < ByteLength ; i += (uint) IntPtr.Size)
                    yield return Marshal.PtrToStructure<NativeCredential> (Read<IntPtr> (i)) ;
            }

            IEnumerator IEnumerable.GetEnumerator ()
            {
                return GetEnumerator () ;
            }
        }
        #endregion

        #region --[Methods: Interop]--------------------------------------
        [DllImport ("advapi32.dll", SetLastError = true)]
        private static extern bool CredFree (IntPtr cred) ;

        [DllImport ("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CredDelete (string target, CredentialType type, int reservedFlag) ;

        [DllImport ("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CredWrite ([In] ref NativeCredential userCredential, UInt32 flags) ;

        [DllImport ("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CredRead (string target, CredentialType type, int reservedFlag, out SafeHandle credential) ;

        [DllImport ("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CredEnumerate (string filter, UInt32 flags, out UInt32 count, out EnumeratedCredBuffer credentials) ;
        #endregion
    }
}

using System ;
using System.IO ;
using System.Collections.Generic ;
using System.Globalization ;
using System.Text ;
using System.Threading.Tasks ;
using System.Net.Http ;
using System.Runtime.Serialization ;

namespace SendGmail
{
    static class Program
    {
        [STAThread]
        static async Task<int> Main (string[] args)
        {
            #if !DEBUG
            AppDomain.CurrentDomain.AssemblyResolve += (sender, e) =>
            {
                using (var stream  = typeof (Program).Assembly.GetManifestResourceStream (
                    new System.Reflection.AssemblyName (e.Name).Name.ToLowerInvariant () + ".dll"))
                {
                    if (stream == null)
                        return null ;

                    var bytes = new byte[stream.Length] ;
                    stream.Read (bytes, 0, (int) stream.Length) ;
                    return System.Reflection.Assembly.Load (bytes) ;
                }
            } ;
            #endif

            try
            {
                if (args.Length == 0)
                    return await InstallAsync () ;

                return await SendGmail.RunAsync (Console.OpenStandardInput ()) ;
            }
            catch (MyException e)
            {
                Console.Error.WriteLine (e.Message) ;
                return 1 ;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine (e) ;
                return 1 ;
            }
        }

        static async Task<int> InstallAsync ()
        {
            if (Console.IsInputRedirected)
            {
                Console.Error.WriteLine ($"Run {nameof (SendGmail)} in interactive mode to install.") ;
                return 1 ;
            }

            string server ;
            try
            {
                if (!GitGetConfig ("sendemail.smtpserver", out server) && server != "")
                {
                    Console.Error.WriteLine ("git config failed with message " + server) ;
                    return 1 ;
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine ("git not found in %PATH%, or other problems running git:") ;
                Console.Error.WriteLine (e.Message) ;
                return 1 ;
            }

            if (!server.Contains (nameof (SendGmail)) || !File.Exists (server.Replace ('/', '\\')))
            {
                if (!SendGmail.Confirm ($"Install {nameof (SendGmail)} as your git-send-email plug-in?"))
                    return 1 ;

                var folder = Path.Combine (Environment.GetFolderPath (Environment.SpecialFolder.ApplicationData), "atykhyy", nameof (SendGmail)) ;
                Directory.CreateDirectory (folder) ;
                var target = Path.Combine (folder, nameof (SendGmail) + ".exe") ;
                File.Copy (System.Reflection.Assembly.GetExecutingAssembly ().Location, target) ;

                // git-send-email wants its slashes straight
                // add outer quotes in case of spaces in path
                GitSetConfig ("sendemail.smtpserver", $"\"{target.Replace ('\\', '/')}\"") ;
            }

            if (!GitGetConfig ("user.name", out var username) || !GitGetConfig ("user.email", out var useremail))
            {
                Console.Error.WriteLine ("user.name and/or user.email are not configured.") ;
                Console.Error.WriteLine ($"Please configure and rerun {nameof (SendGmail)}.") ;
                return 1 ;
            }

            if (!SendGmail.Confirm ($"Send a test email to {useremail} to set up credentials (recommended)?"))
                return 0 ;

            if (!GitGetConfig ("sendemail.smtpuser", out var smtpuser) && smtpuser == "")
            {
                for (smtpuser = useremail ; !SendGmail.Confirm ($"Is {smtpuser} the Gmail address you want to use with git-send-email?") ; )
                {
                    Console.WriteLine ("Enter your Gmail address for git-send-email:") ;
                    smtpuser = Console.ReadLine () ;
                }

                GitSetConfig ("sendemail.smtpuser", smtpuser) ;
            }

            var now   = DateTimeOffset.Now ;
            var email = $"From: {username} <{smtpuser}>\nTo: {useremail}\nSubject: Hello from {nameof (SendGmail)}\n" +
                        $"Date: {now.ToString ("ddd, dd MMM yyyy HH':'mm':'ss K", CultureInfo.InvariantCulture)}\n" +
                        $"Message-Id: <{now.UtcDateTime.ToString ("yyyyMMddHHmmss'.'ffff", CultureInfo.InvariantCulture)}-1-{smtpuser}>\n" +
                        $"X-Mailer: {nameof (SendGmail)} 1.0\n" +
                        "MIME-Version: 1.0\n" +
                        "Content-Transfer-Encoding: 8bit\n\n" +
                        $"Hello from {nameof (SendGmail)}!\n" ;

            return await SendGmail.RunAsync (new MemoryStream (Encoding.ASCII.GetBytes (email))) ;
        }

        static bool GitGetConfig (string key, out string value)
        {
            if (GitExec ("config --global --get " + key, out value))
            {
                value = value.Trim () ;
                return true ;
            }
            else
                return false ;
        }

        static void GitSetConfig (string key, string value)
        {
            if (!GitExec ("config --global " + key + " " + value, out value))
                throw new InvalidOperationException ($"Failed to configure {key}. Git output: {value}") ;
        }

        static bool GitExec (string arguments, out string result)
        {
            using (var p = System.Diagnostics.Process.Start (new System.Diagnostics.ProcessStartInfo ("git", arguments)
            {
                UseShellExecute        = false,
                RedirectStandardError  = true,
                RedirectStandardOutput = true,
            }))
            {
                if (!p.WaitForExit (1000))
                    throw new TimeoutException () ;

                if (p.ExitCode != 0)
                {
                    result = p.StandardError.ReadToEnd () ;
                    return false ;
                }
                else
                {
                    result = p.StandardOutput.ReadToEnd () ;
                    return true ;
                }
            }
        }
    }

    sealed class MyException : Exception
    {
        public MyException (string message) : base (message) {}
    }

    static class SendGmail
    {
        const string TargetNamePrefix   = "git:" ;
        const string SendGmailEndpoint  = "https://content.googleapis.com/gmail/v1/users/me/messages/send" ;
        const string AuthGmailEndpoint  = "https://accounts.google.com/o/oauth2/auth" ;
        const string TokenGmailEndpoint = "https://oauth2.googleapis.com/token" ;
        const string SendGmailScope     = "https://www.googleapis.com/auth/gmail.send" ;
        const string OobRedirectUri     = "urn:ietf:wg:oauth:2.0:oob" ;

        static readonly Newtonsoft.Json.JsonSerializer Json = Newtonsoft.Json.JsonSerializer.CreateDefault () ;

        public static async Task<int> RunAsync (Stream unbufferedInput)
        {
            ArraySegment<byte> content ;
            using (var input  = new BufferedStream (unbufferedInput, 0x10000))
            using (var memory = new MemoryStream   ())
            {
                memory.WriteAscii ("{\"raw\":\"") ;
                WriteAsBase64Url  (memory, input) ;
                memory.WriteAscii ('"') ;
                memory.WriteAscii ('}') ;

                content = new ArraySegment<byte> (memory.GetBuffer (), 0, checked ((int) memory.Length)) ;
            }

            NativeConsole.ReattachToTerminal () ;

            Token token ;
            using (var bnc = NativeCredential.Get (TargetNamePrefix + SendGmailEndpoint, CredentialType.Generic))
            if    (bnc == null)
            {
                token = new Token
                {
                    ClientId     = ReadPassword ("Enter Client ID:\r\n(Hint: follow the steps listed in https://github.com/atykhyy/sendgmail#Installation to create a client ID if you haven't already.)"),
                    ClientSecret = ReadPassword ("Enter Client Secret:"),
                } ;

                token.Save () ;
            }
            else
                token = bnc.NativeCredential.GetToken () ;

            var client  = new HttpClient () ;
            var autoRefreshOnce = true ;

            while (true)
            {
                if (token.RefreshToken == null)
                {
                    var authUrl = AuthGmailEndpoint +
                        "?client_id="    + token.ClientId.ToUrlEncoded () +
                        "&redirect_uri=" + OobRedirectUri.ToUrlEncoded () +
                        "&scope="        + SendGmailScope.ToUrlEncoded () +
                        "&response_type=code" ;

                    using (var response = await client.PostAsync (TokenGmailEndpoint, new FormUrlEncodedContent (new Dictionary<string, string>
                    {
                        { "grant_type",    "authorization_code" },
                        { "client_id",     token.ClientId },
                        { "client_secret", token.ClientSecret },
                        { "redirect_uri",  OobRedirectUri },
                        { "code",          ReadPassword ($"Go to {authUrl}, authorize the application and enter the resulting authorization code:") },
                    })))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            if (await PrintErrorAndConfirmAsync (response, "token request", "error_description", "error"))
                                continue ;

                            return 1 ;
                        }

                        using (var reader = new Newtonsoft.Json.JsonTextReader (new StreamReader (await response.Content.ReadAsStreamAsync ())))
                        {
                            var temp          = Json.Deserialize<Token> (reader) ;
                            temp.ClientId     = token.ClientId ;
                            temp.ClientSecret = token.ClientSecret ;

                            token = temp  ;
                            token.Save () ;
                        }
                    }
                }

                if (token.AccessToken == null || token.Timestamp.AddSeconds (token.ExpiresSeconds) < DateTime.UtcNow)
                {
                    using (var response = await client.PostAsync (TokenGmailEndpoint, new FormUrlEncodedContent (new Dictionary<string, string>
                    {
                        { "grant_type",    "refresh_token" },
                        { "client_id",     token.ClientId },
                        { "client_secret", token.ClientSecret },
                        { "refresh_token", token.RefreshToken },
                    })))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            if (await PrintErrorAndConfirmAsync (response, "token refresh", "error_description", "error"))
                                continue ;

                            return 1 ;
                        }

                        using (var reader = new Newtonsoft.Json.JsonTextReader (new StreamReader (await response.Content.ReadAsStreamAsync ())))
                        {
                            var temp          = Json.Deserialize<Token> (reader) ;
                            temp.ClientId     = token.ClientId ;
                            temp.ClientSecret = token.ClientSecret ;
                            temp.RefreshToken = token.RefreshToken ;

                            token = temp  ;
                            token.Save () ;
                        }
                    }
                }

                var request     = new HttpRequestMessage (HttpMethod.Post, SendGmailEndpoint) ;
                request.Content = new ByteArrayContent   (content.Array, content.Offset, content.Count) ;
                request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse ("application/json") ;
                request.Headers.Authorization   = new System.Net.Http.Headers.AuthenticationHeaderValue  ("Bearer", token.AccessToken) ;

                using (var response = await client.SendAsync (request))
                {
                    if (response.IsSuccessStatusCode)
                        return 0 ;

                    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && autoRefreshOnce)
                    {
                        token.AccessToken = null  ;
                        autoRefreshOnce   = false ;
                        continue ;
                    }

                    if (await PrintErrorAndConfirmAsync (response, "send email request", "error.message", "error.errors[0].reason"))
                        continue ;

                    return 1 ;
                }
            }
        }

        #region --[Helper methods]----------------------------------------
        private static async Task<bool> PrintErrorAndConfirmAsync (HttpResponseMessage response, string action, string messagePath, string codePath)
        {
            string error ;
            if (response.Content.Headers.ContentType.MediaType != "application/json")
            {
                error = $"unexpected error of type {response.Content.Headers.ContentType.MediaType}" ;
            }
            else
            try
            {
                using (var reader = new Newtonsoft.Json.JsonTextReader (new StreamReader (await response.Content.ReadAsStreamAsync ())))
                {
                    var token = Json.Deserialize<Newtonsoft.Json.Linq.JToken> (reader) ;
                    var terr  = token.SelectToken (messagePath) ;
                    var tcode = token.SelectToken (codePath) ;

                    error = terr  != null ? $"{terr}" : "unexpected error" ;
                    error = tcode != null ? $"{error} (code '{tcode}')" : error ;
                }
            }
            catch
            {
                error = "unreadable error" ;
            }

            error = $"Google {action} returned an {error} with status code {response.StatusCode}." ;

            if (!NativeConsole.HaveInput)
            {
                Console.Error.WriteLine (error) ;
                return false ;
            }
            else
                return Confirm (error + " Keep trying?") ;
        }

        internal static bool Confirm (string prompt)
        {
            Console.Out.WriteLine (prompt + " (y/n)") ;
            var key = Console.ReadKey (false).Key ;
            Console.Out.WriteLine () ;
            return key == ConsoleKey.Y ;
        }

        private static string ReadPassword (string prompt)
        {
            if (!NativeConsole.HaveInput)
                throw new MyException ($"Run {nameof (SendGmail)} in interactive mode to acquire credentials.") ;

            Console.Out.WriteLine (prompt) ;
            var sb = new StringBuilder  () ;

            while (true)
            {
                var ch = Console.ReadKey (true) ;
                if (ch.Key == ConsoleKey.Enter)
                {
                    Console.Out.WriteLine () ;
                    return sb.ToString () ;
                }

                sb.Append (ch.KeyChar) ;
                Console.Out.Write ('*') ;
            }
        }

        private static void Save (this Token token)
        {
            var nc = new NativeCredential
            {
                TargetName  = TargetNamePrefix + SendGmailEndpoint,
                UserName    = "PersonalAccessToken",
                Type        = CredentialType.Generic,
                Persistence = Persistence.LocalMachine,
            } ;

            nc.Save (token) ;

            token.Timestamp = DateTime.UtcNow ;
        }

        private static Token GetToken (this ref NativeCredential nc)
        {
            using (var reader = new Newtonsoft.Json.JsonTextReader (new StreamReader (new MemoryStream (nc.CredentialBlobBytes))))
            {
                var token       = Json.Deserialize<Token> (reader) ;
                token.Timestamp = nc.LastWritten ;
                return token ;
            }
        }

        private static void Save (this ref NativeCredential nc, Token token)
        {
            using (var memory = new MemoryStream ())
            using (var writer = new Newtonsoft.Json.JsonTextWriter (new StreamWriter (memory)))
            {
                Json.Serialize (writer, token) ;
                writer.Flush   () ;

                nc.Save (memory.GetBuffer (), (int) memory.Length) ;
            }
        }

        private static void WriteAscii (this Stream stream, string s)
        {
            var bytes = Encoding.ASCII.GetBytes (s) ;
            stream.Write (bytes, 0, bytes.Length) ;
        }

        private static void WriteAscii (this Stream stream, int c)
        {
            stream.WriteByte ((byte)c) ;
        }

        private static string ToUrlEncoded (this string value)
        {
            return Uri.EscapeDataString (value) ;
        }

        private static void WriteAsBase64Url (Stream output, Stream input)
        {
            while (true)
            {
                // https://tools.ietf.org/html/rfc4648
                var b1 = input.ReadByte () ;
                var b2 = input.ReadByte () ;
                var b3 = input.ReadByte () ;

                if (b1 < 0)
                    break ;

                if (b2 < 0)
                {
                    output.EncodeQuantum ((b1 << 16)) ;
                    output.WriteAscii    ('=') ;
                    output.WriteAscii    ('=') ;
                    break ;
                }

                if (b3 < 0)
                {
                    output.EncodeQuantum ((b1 << 16) | (b2 << 8)) ;
                    output.WriteAscii    ('=') ;
                    break ;
                }
                else
                    output.EncodeQuantum ((b1 << 16) | (b2 << 8) | b3) ;
            }
        }

        private static void EncodeQuantum (this Stream stream, int quantum)
        {
            stream.WriteAscii (EncodeQuantum ((quantum >> 18))) ;
            stream.WriteAscii (EncodeQuantum ((quantum >> 12) & 0x3F)) ;
            stream.WriteAscii (EncodeQuantum ((quantum >>  6) & 0x3F)) ;
            stream.WriteAscii (EncodeQuantum ((quantum)       & 0x3F)) ;
        }

        private static int EncodeQuantum (int group)
        {
            if (group < 26) return 'A' + group ;
            if (group < 52) return 'a' + group - 26 ;
            if (group < 62) return '0' + group - 52 ;
            if (group < 63) return '-' ;
            /*************/ return '_' ;
        }
        #endregion
    }

    [DataContract]
    public class Token
    {
        [DataMember (Name = "access_token")]
        public string AccessToken { get ; set ; }

        [DataMember (Name = "expires_in")]
        public int ExpiresSeconds { get ; set ; }

        [DataMember (Name = "refresh_token")]
        public string RefreshToken { get ; set ; }

        [DataMember (Name = "scope")]
        public string Scope { get ; set ; }

        [DataMember (Name = "token_type")]
        public string Type { get ; set ; }

        [DataMember (Name = "client_id")]
        public string ClientId { get ; set ; }

        [DataMember (Name = "client_secret")]
        public string ClientSecret { get ; set ; }

        [IgnoreDataMember]
        public DateTime Timestamp { get ; set ; }
    }
}

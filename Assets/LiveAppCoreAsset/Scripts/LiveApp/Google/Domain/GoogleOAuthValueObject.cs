using System.Collections;
using System;
using System.Net.Sockets;

namespace LiveAppCore.Google.Domain
{
    public enum AuthorizationWaitStatus
    {
        CallbackReceived,
        BrowserClosed
    }

    public class OAuthConstValue
    {
        public static readonly string ConfigPath = "Config/";
        public static readonly string BinFileName = "auth.bin";
        public static readonly string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
        public static readonly string TokenEndpoint = "https://oauth2.googleapis.com/token";
        public static readonly string DefaultIP = "127.0.0.1";
        public static readonly string Dimension = "?majorDimension=ROWS&valueRenderOption=FORMATTED_VALUE";
        public static readonly string SheetsApiBaseUrl = "https://sheets.googleapis.com/v4/spreadsheets";

        public static float TimeSpanSecond = 60f;
    }

    public class OAuthResponseMessage
    {
        public static readonly string COMPLETE = "<html><body>Google authorization completed. You can close this window.</body></html>";
        public static readonly string AUTH_FAILED = "<html><body>Google authorization failed. You can close this window.</body></html>";
        public static readonly string INVALID_AUTH = "<html><body>Invalid OAuth state. You can close this window.</body></html>";
        public static readonly string CODE_NOT_FOUND = "<html><body>Authorization code not found. You can close this window.</body></html>";
    }

    public sealed class AuthorizationWaitResult
    {
        public AuthorizationWaitStatus Status { get; }
        public TcpClient Client { get; }

        private AuthorizationWaitResult(
            AuthorizationWaitStatus status,
            TcpClient client = null
        )
        {
            Status = status;
            Client = client;
        }

        public static AuthorizationWaitResult CallbackReceived( TcpClient client )
        {
            return new AuthorizationWaitResult( AuthorizationWaitStatus.CallbackReceived, client );
        }

        public static AuthorizationWaitResult BrowserClosed()
        {
            return new AuthorizationWaitResult( AuthorizationWaitStatus.BrowserClosed );
        }
    }
}
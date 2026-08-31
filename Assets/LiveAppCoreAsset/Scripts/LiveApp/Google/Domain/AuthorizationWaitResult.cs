using System;

namespace LiveAppCore.Google.Domain
{
    /// <summary>
    /// Google OAuth 설정을 위한 데이터 클래스
    /// </summary>
    [Serializable]
    public sealed class GoogleOAuthSettings
    {
        public string desktopClientId;
        public string desktopClientSecret;
        public string iosClientId;
        public string sheetsReadonlyScope = "https://www.googleapis.com/auth/spreadsheets.readonly";

        public string DesktopClientId => desktopClientId;
        public string DesktopClientSecret => desktopClientSecret;
        public string IOSClientId => iosClientId;
        public string SheetsReadonlyScope => sheetsReadonlyScope;
    }
}
using System;

namespace LiveAppCore.Google.Domain
{
    public sealed class OAuthBrowserClosedException : Exception
    {
        public OAuthBrowserClosedException()
            : base( "OAuth browser was closed before authorization completed." )
        {
        }
    }
}
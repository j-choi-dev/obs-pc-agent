using System;

namespace ObsAgent
{
    [Serializable]
    public sealed class ObsAgentAuthData
    {
        public string client_id;
        public string project_id;

        public string auth_uri;
        public string token_uri;

        public string auth_provider_x509_cert_url;

        public string client_secret;

        public string[] redirect_uris;
    }
}
using System;

namespace ObsAgent
{
    public enum YoutubeLiveState
    {
        IDLE,
        PREPARING,
        READY,
        STARTING,
        LIVE,
        STOPPING,
        FAILED
    }

    [Serializable]
    public sealed class YoutubeLivePrepareRequest
    {
        public string title;
        public int width;
        public int height;
        public string streamKey;
    }

    [Serializable]
    public sealed class YoutubeLiveStatusResponse
    {
        public bool success;
        public string state;
        public string message;
        public string broadcastId;
        public string utcTime;

        public static YoutubeLiveStatusResponse Create(
            bool success,
            YoutubeLiveState state,
            string message,
            string broadcastId = "" )
        {
            return new YoutubeLiveStatusResponse
            {
                success = success,
                state = state.ToString(),
                message = message ?? string.Empty,
                broadcastId = broadcastId ?? string.Empty,
                utcTime = DateTime.UtcNow.ToString( "O" )
            };
        }
    }

    public sealed class YoutubeLiveStreamInfo
    {
        public string StreamId;
        public string RtmpsIngestionAddress;
    }

    internal sealed class YoutubePreparedSession
    {
        public string BroadcastId;
        public string StreamId;
        public string Title;

        public int Width;
        public int Height;
    }
}
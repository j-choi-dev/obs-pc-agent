using System;
using System.Collections.Concurrent;
using System.Threading;

namespace ObsAgent
{
    /// <summary>
    /// WebRTC Offer와 Answer를 sessionId별로 메모리에 보관한다.
    ///
    /// Unity GameObject 또는 Component를 생성하지 않는다.
    /// 별도의 Background Thread도 생성하지 않는다.
    /// </summary>
    public sealed class ObsVideoSessionStore
    {
        private const int MaxSessionIdLength = 128;
        // SDP가 비정상적으로 커져 Agent 메모리를 소모하지 않도록 제한한다.
        private const int MaxSdpCharacterCount = 256 * 1024;
        private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(30);
        private readonly ConcurrentDictionary<string, SessionState> _sessions = new ConcurrentDictionary<string, SessionState>( StringComparer.Ordinal );

        private int _operationCount;

        public void Reset( string sessionId )
        {
            string normalizedSessionId = NormalizeSessionId(sessionId);
            _sessions[normalizedSessionId] = new SessionState();
            CleanupExpiredSessionsIfNeeded();
        }

        public void SetOffer( string sessionId, string sdp )
        {
            string normalizedSessionId = NormalizeSessionId(sessionId);
            ValidateSdp( sdp, "Offer" );
            SessionState state = _sessions.GetOrAdd( normalizedSessionId, _ => new SessionState() );

            lock( state.Gate )
            {
                state.OfferSdp = sdp;
                state.OfferUpdatedUtc = DateTime.UtcNow;

                // 새로운 Offer가 등록되면
                // 이전 Answer는 더 이상 유효하지 않다.
                state.AnswerSdp = null;
                state.AnswerUpdatedUtc = null;
                state.LastTouchedUtc = DateTime.UtcNow;
            }
            CleanupExpiredSessionsIfNeeded();
        }

        public void SetAnswer( string sessionId, string sdp )
        {
            string normalizedSessionId = NormalizeSessionId(sessionId);

            ValidateSdp( sdp, "Answer" );

            if( !_sessions.TryGetValue( normalizedSessionId, out SessionState state ) )
            {
                throw new InvalidOperationException( "해당 WebRTC 세션이 존재하지 않습니다. 먼저 세션을 초기화해야 합니다." );
            }

            lock( state.Gate )
            {
                if( string.IsNullOrWhiteSpace( state.OfferSdp ) )
                {
                    throw new InvalidOperationException( "Offer가 등록되지 않은 세션에는 Answer를 등록할 수 없습니다." );
                }

                state.AnswerSdp = sdp;
                state.AnswerUpdatedUtc = DateTime.UtcNow;
                state.LastTouchedUtc = DateTime.UtcNow;
            }

            CleanupExpiredSessionsIfNeeded();
        }

        public VideoSessionValue GetOffer( string sessionId )
        {
            string normalizedSessionId = NormalizeSessionId(sessionId);
            VideoSessionValue result = GetDescription( normalizedSessionId, true );
            CleanupExpiredSessionsIfNeeded();
            return result;
        }

        public VideoSessionValue GetAnswer( string sessionId )
        {
            string normalizedSessionId = NormalizeSessionId(sessionId);

            VideoSessionValue result = GetDescription( normalizedSessionId, false );

            CleanupExpiredSessionsIfNeeded();

            return result;
        }

        public void Clear()
        {
            _sessions.Clear();
        }

        public static string NormalizeSessionId( string sessionId )
        {
            if( string.IsNullOrWhiteSpace( sessionId ) )
            {
                throw new InvalidOperationException( "sessionId가 비어 있습니다." );
            }

            string value = sessionId.Trim();
            if( value.Length > MaxSessionIdLength )
            {
                throw new InvalidOperationException( $"sessionId는 {MaxSessionIdLength}자를 초과할 수 없습니다." );
            }
            foreach( char character in value )
            {
                bool valid = char.IsLetterOrDigit(character) || character == '-' || character == '_' || character == '.';
                if( !valid )
                {
                    throw new InvalidOperationException( "sessionId에는 영문자, 숫자, '-', '_', '.'만 사용할 수 있습니다." );
                }
            }

            return value;
        }

        private VideoSessionValue GetDescription( string sessionId, bool isOffer )
        {
            if( !_sessions.TryGetValue( sessionId, out SessionState state ) )
            {
                return VideoSessionValue.Empty();
            }

            lock( state.Gate )
            {
                state.LastTouchedUtc = DateTime.UtcNow;

                string sdp = isOffer ? state.OfferSdp : state.AnswerSdp;
                DateTime? updatedUtc = isOffer ? state.OfferUpdatedUtc : state.AnswerUpdatedUtc;
                if( string.IsNullOrWhiteSpace( sdp ) )
                {
                    return VideoSessionValue.Empty();
                }
                return VideoSessionValue.Value( sdp, updatedUtc );
            }
        }

        private static void ValidateSdp( string sdp, string descriptionName )
        {
            if( string.IsNullOrWhiteSpace( sdp ) )
            {
                throw new InvalidOperationException( $"{descriptionName} SDP가 비어 있습니다." );
            }
            if( sdp.Length > MaxSdpCharacterCount )
            {
                throw new InvalidOperationException( $"{descriptionName} SDP가 허용 크기를 초과했습니다." );
            }
        }

        private void CleanupExpiredSessionsIfNeeded()
        {
            int count = Interlocked.Increment( ref _operationCount );
            // 매 요청마다 전체 Dictionary를 순회하지 않는다.
            if( count % 32 != 0 )
            {
                return;
            }

            DateTime threshold = DateTime.UtcNow - SessionLifetime;
            foreach( var pair in _sessions )
            {
                DateTime lastTouchedUtc;
                lock( pair.Value.Gate )
                {
                    lastTouchedUtc = pair.Value.LastTouchedUtc;
                }
                if( lastTouchedUtc >= threshold )
                {
                    continue;
                }
                _sessions.TryRemove(pair.Key, out _ );
            }
        }

        private sealed class SessionState
        {
            public readonly object Gate = new object();

            public string OfferSdp;
            public string AnswerSdp;

            public DateTime? OfferUpdatedUtc;
            public DateTime? AnswerUpdatedUtc;

            public DateTime LastTouchedUtc = DateTime.UtcNow;
        }

        public sealed class VideoSessionValue
        {
            public bool HasValue { get; private set; }
            public string Sdp { get; private set; }
            public DateTime? UpdatedUtc { get; private set; }

            public static VideoSessionValue Empty()
            {
                return new VideoSessionValue
                {
                    HasValue = false,
                    Sdp = string.Empty,
                    UpdatedUtc = null
                };
            }

            public static VideoSessionValue Value( string sdp, DateTime? updatedUtc )
            {
                return new VideoSessionValue
                {
                    HasValue = true,
                    Sdp = sdp,
                    UpdatedUtc = updatedUtc
                };
            }
        }
    }
}
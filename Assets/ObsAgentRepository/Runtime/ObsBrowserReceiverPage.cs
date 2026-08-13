using System;
using System.Text;

namespace ObsAgent
{
    public static class ObsBrowserReceiverPage
    {
        public static string Build( string sessionId, string agentToken )
        {
            string normalizedSessionId = ObsVideoSessionStore .NormalizeSessionId( sessionId );

            if( string.IsNullOrWhiteSpace( agentToken ) )
            {
                throw new InvalidOperationException( "Agent Token이 비어 있습니다." );
            }

            return HtmlTemplate .Replace( "__SESSION_ID__", ToJavaScriptString( normalizedSessionId ) )
                .Replace( "__AGENT_TOKEN__", ToJavaScriptString( agentToken ) );
        }


        private static string ToJavaScriptString( string value )
        {
            var builder = new StringBuilder();
            builder.Append( '"' );
            foreach( char character in value )
            {
                switch( character )
                {
                    case '\\':
                        builder.Append( "\\\\" );
                        break;
                    case '"':
                        builder.Append( "\\\"" );
                        break;
                    case '\r':
                        builder.Append( "\\r" );
                        break;
                    case '\n':
                        builder.Append( "\\n" );
                        break;
                    case '\t':
                        builder.Append( "\\t" );
                        break;
                    case '<':
                        builder.Append( "\\u003C" );
                        break;
                    case '>':
                        builder.Append( "\\u003E" );
                        break;
                    case '&':
                        builder.Append( "\\u0026" );
                        break;
                    default:
                        builder.Append( character );
                        break;
                }
            }
            builder.Append( '"' );
            return builder.ToString();
        }


        private const string HtmlTemplate = @"<!doctype html>
<html lang='ko'>
<head>
    <meta charset='utf-8'>
    <meta
        name='viewport'
        content='width=device-width, initial-scale=1'>
    <title>OBS WebRTC Receiver</title>
    <style>
        html,
        body
        {
            width: 100%;
            height: 100%;
            margin: 0;
            padding: 0;
            overflow: hidden;
            background: transparent;
        }

        #receiver-video
        {
            display: block;
            width: 100%;
            height: 100%;
            object-fit: contain;
            background: transparent;
        }

        #status
        {
            position: fixed;
            left: 12px;
            top: 12px;
            padding: 8px 10px;
            color: white;
            background: rgba(0, 0, 0, 0.72);
            font-family: sans-serif;
            font-size: 14px;
            white-space: pre-wrap;
            pointer-events: none;
        }
    </style>
</head>


<body>

    <video
        id='receiver-video'
        autoplay
        muted
        playsinline>
    </video>

    <div id='status'>
        Receiver 초기화 중
    </div>


    <script>
        'use strict';
        const sessionId = __SESSION_ID__;
        const agentToken = __AGENT_TOKEN__;
        const pollIntervalMilliseconds = 250;
        const videoElement = document.getElementById( 'receiver-video');
        const statusElement = document.getElementById( 'status');
        let peerConnection = null;
        let activeOfferSdp = '';
        let isStopped = false;

        function setStatus( message, hide)
        {
            statusElement.textContent = message;
            statusElement.style.display = hide ? 'none' : 'block';
        }

        function sleep(milliseconds)
        {
            return new Promise( resolve => setTimeout( resolve, milliseconds));
        }

        async function callApi( path, options )
        {
            options = options || {};
            const headers = new Headers( options.headers || {});
            headers.set( 'Authorization', 'Bearer ' + agentToken);
            if (options.body)
            {
                headers.set( 'Content-Type', 'application/json');
            }
            const response = await fetch( path,
                    {
                        method: options.method || 'GET',
                        headers: headers,
                        body: options.body || null,
                        cache: 'no-store'
                    });

            const responseText = await response.text();
            let responseData = null;
            if (responseText)
            {
                try
                {
                    responseData = JSON.parse( responseText );
                }
                catch
                {
                    responseData = null;
                }
            }

            if (!response.ok)
            {
                let detail = response.statusText;
                if (responseData && responseData.message)
                {
                    detail = responseData.message;
                }
                else if (responseText)
                {
                    detail = responseText;
                }

                throw new Error( 'HTTP ' + response.status + ': ' + detail );
            }

            if (responseData && responseData.success === false)
            {
                throw new Error( responseData.message || 'Agent API 실패');
            }
            return responseData;
        }

        function closePeerConnection()
        {
            const closingPeer = peerConnection;
            peerConnection = null;

            if (closingPeer)
            {
                closingPeer.ontrack = null;
                closingPeer.onconnectionstatechange = null;
                closingPeer.oniceconnectionstatechange = null;
                try
                {
                    closingPeer.close();
                }
                catch
                {
                }
            }
            videoElement.srcObject = null;
        }

        function waitForIceGatheringComplete( peer, timeoutMilliseconds)
        {
            timeoutMilliseconds = timeoutMilliseconds || 10000;
            if (peer.iceGatheringState === 'complete')
            {
                return Promise.resolve();
            }
            return new Promise( (resolve, reject) => {
                    const timeout = setTimeout( () =>
                            {
                                cleanup();
                                reject( new Error( 'Browser ICE Gathering 시간 초과'));
                            },
                            timeoutMilliseconds);


                    function cleanup()
                    {
                        clearTimeout( timeout);
                        peer.removeEventListener( 'icegatheringstatechange', onStateChanged);
                    }


                    function onStateChanged()
                    {
                        if ( peer.iceGatheringState !== 'complete')
                        {
                            return;
                        }
                        cleanup();
                        resolve();
                    }
                    peer.addEventListener( 'icegatheringstatechange', onStateChanged);
                });
        }

        async function processOffer( offerResponse)
        {
            if (!offerResponse || !offerResponse.hasValue || !offerResponse.sdp)
            {
                return;
            }
            if (offerResponse.sdp === activeOfferSdp && peerConnection)
            {
                return;
            }
            activeOfferSdp = offerResponse.sdp;
            closePeerConnection();
            setStatus( 'iPhone Offer 적용 중', false);

            const nextPeer = new RTCPeerConnection(
                    {
                        iceServers: []
                    });

            peerConnection = nextPeer;
            nextPeer.ontrack = event => {
                    let stream = null;
                    if (event.streams && event.streams.length > 0)
                    {
                        stream = event.streams[0];
                    }
                    else
                    {
                        stream = new MediaStream( [ event.track ]);
                    }
                    videoElement.srcObject = stream;
                    videoElement.play()
                        .catch( error => { setStatus( 'Video 재생 실패\n' + error.message, false); });
                };



            nextPeer.oniceconnectionstatechange = () => {
                    const state = nextPeer.iceConnectionState;
                    if (state === 'failed')
                    {
                        setStatus( 'ICE 연결 실패', false);
                    }
                };

            nextPeer.onconnectionstatechange = () => {
                    if (peerConnection !== nextPeer)
                    {
                        return;
                    }
                    const state = nextPeer.connectionState;
                    if (state === 'connected')
                    {
                        setStatus( 'WebRTC 연결됨', true);
                        return;
                    }

                    if (state === 'failed' || state === 'closed')
                    {
                        setStatus( 'WebRTC 연결 상태: ' + state, false);
                        activeOfferSdp = '';
                        closePeerConnection();
                        return;
                    }

                    if (state === 'disconnected')
                    {
                        setStatus( 'WebRTC 연결이 일시적으로 끊어졌습니다.', false);
                        return;
                    }
                    setStatus( 'WebRTC 연결 상태: ' + state, false);
                };

            await nextPeer.setRemoteDescription( { type: 'offer', sdp: offerResponse.sdp });
            const answer = await nextPeer.createAnswer();

            await nextPeer.setLocalDescription( answer);
            await waitForIceGatheringComplete( nextPeer);
            if (!nextPeer.localDescription || !nextPeer.localDescription.sdp)
            {
                throw new Error( 'Browser Local SDP Answer가 없습니다.');
            }

            await callApi( '/api/video/answer',
                {
                    method: 'POST',
                    body: JSON.stringify(
                            {
                                sessionId: sessionId,
                                type: 'answer',
                                sdp: nextPeer.localDescription .sdp
                            })
                });
            setStatus( 'Answer 등록 완료, Peer 연결 대기 중', false);
        }

        async function pollOfferLoop()
        {
            setStatus( 'Offer 대기 중\nSession: ' + sessionId, false);
            while (!isStopped)
            {
                try
                {
                    const encodedSessionId = encodeURIComponent( sessionId);
                    const offerResponse = await callApi( '/api/video/offer' + '?sessionId=' + encodedSessionId, { method: 'GET' });
                    await processOffer( offerResponse);
                    await sleep( pollIntervalMilliseconds);
                }
                catch (error)
                {
                    setStatus('Receiver 오류\n' +error.message, false);
                    await sleep( 1000);
                }
            }
        }
        window.addEventListener( 'beforeunload', () => { isStopped = true; closePeerConnection(); });
        pollOfferLoop();
    </script>
</body>
</html>";
    }
}
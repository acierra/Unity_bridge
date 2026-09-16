using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GstWebRtcReceiver.Core;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using UnityEngine;

using CoreReceiver = GstWebRtcReceiver.Core.GstWebRtcReceiver;

[RequireComponent(typeof(EventProcessor))]
public sealed class UnityWebRtcSmokeReceiver : MonoBehaviour
{
    private const float SummaryIntervalSeconds = 5.0f;

    [SerializeField] private string m_SignallingUrl = "ws://localhost:8443";
    [SerializeField] private string m_RoomId = string.Empty;
    [SerializeField] private float m_TimeoutSeconds = 45.0f;
    [SerializeField] private bool m_VerboseFrames = true;
    [SerializeField] private string m_TargetQuadName = "WebRtcVideoQuad";

    private EventProcessor m_EventProcessor;
    private CoreReceiver m_Receiver;
    private CancellationTokenSource m_RunCancellation;
    private Task m_RunTask;
    private bool m_IsDestroying;
    private int m_EncodedFrameCount;
    private bool m_HasLoggedEncodedFrameSemantics;
    private int? m_LastVideoPayloadType;
    private bool m_HasLoggedActiveCodec;
    private readonly Dictionary<int, string> m_RemotePayloadTypes = new Dictionary<int, string>();
    private readonly Dictionary<int, string> m_LocalPayloadTypes = new Dictionary<int, string>();
    private int m_RtpPacketCount;
    private int m_LastEncodedFrameLength;
    private string m_ActiveCodec = "<unknown>";
    private float m_NextSummaryAt;
    private Vp8DecoderBridge m_Vp8Decoder;
    private RTCPeerConnection m_TappedPeerConnection;
    private int m_DecodedFrameCount;
    private int m_DecoderFrameCallbackCount;
    private readonly object m_FrameLock = new object();
    private byte[] m_PendingRgbaFrame;
    private int m_PendingFrameWidth;
    private int m_PendingFrameHeight;
    private bool m_HasPendingFrame;
    private Texture2D m_VideoTexture;
    private Renderer m_TargetRenderer;
    private Material m_TargetMaterial;
    private int m_DisplayedFrameCount;
    private int m_MainThreadId;

    private void Start()
    {
        m_MainThreadId = Thread.CurrentThread.ManagedThreadId;
        m_EventProcessor = GetComponent<EventProcessor>();
        m_NextSummaryAt = Time.realtimeSinceStartup + SummaryIntervalSeconds;

        try
        {
            Uri signallingUri = BuildSignallingUri();
            TimeSpan timeout = TimeSpan.FromSeconds(Mathf.Max(1.0f, m_TimeoutSeconds));

            m_RunCancellation = new CancellationTokenSource(timeout);
            m_Receiver = new CoreReceiver(new GstWebRtcReceiverConfig(signallingUri, timeout, m_VerboseFrames));
            SubscribeToReceiverEvents(m_Receiver);
            m_Vp8Decoder = new Vp8DecoderBridge();
            bool copyRgbaResolved = Vp8DecoderBridge.TryResolveCopyRgbaExport(out string copyRgbaDiagnostic);
            Log($"VP8 native export vp8_decoder_copy_rgba resolved={copyRgbaResolved} detail={copyRgbaDiagnostic}");
            m_TargetRenderer = FindTargetRenderer();
            Log($"[texture] [texture.init] targetRenderer assigned={m_TargetRenderer != null} name={(m_TargetRenderer != null ? m_TargetRenderer.name : "<null>")}");

            if (!m_Vp8Decoder.IsAvailable)
            {
                Log("VP8 decoder bridge handle was not created.");
            }

            if (m_TargetRenderer == null)
            {
                Log($"Video target renderer '{m_TargetQuadName}' was not found.");
            }

            Log($"Starting receiver. signalling={signallingUri}; roomId={GetRoomLabel()}; timeout={timeout.TotalSeconds:N0}s.");
            m_RunTask = RunReceiverAsync(m_Receiver, m_RunCancellation.Token);
        }
        catch (Exception ex)
        {
            Log($"Startup failed: {ex}");
        }
    }

    private void Update()
    {
        if (Time.realtimeSinceStartup < m_NextSummaryAt)
        {
            ApplyPendingFrame();
            return;
        }

        m_NextSummaryAt += SummaryIntervalSeconds;
        ApplyPendingFrame();

        if (m_RtpPacketCount == 0 && m_EncodedFrameCount == 0)
        {
            TryAttachVideoFrameTap();
            return;
        }

        TryAttachVideoFrameTap();
        Log($"Summary: Codec = {m_ActiveCodec}; PayloadType = {GetPayloadTypeText()}; RTP packet count = {m_RtpPacketCount}; Encoded frame count = {m_EncodedFrameCount}; Last encoded frame size = {GetEncodedFrameSizeText()}");
    }

    private void OnDestroy()
    {
        m_IsDestroying = true;

        if (m_RunCancellation != null && !m_RunCancellation.IsCancellationRequested)
        {
            m_RunCancellation.Cancel();
        }

        if (m_Receiver != null)
        {
            TryDetachVideoFrameTap();
            UnsubscribeFromReceiverEvents(m_Receiver);
            m_Receiver.Dispose();
            m_Receiver = null;
        }

        if (m_RunCancellation != null)
        {
            m_RunCancellation.Dispose();
            m_RunCancellation = null;
        }

        if (m_Vp8Decoder != null)
        {
            m_Vp8Decoder.Dispose();
            m_Vp8Decoder = null;
        }

        if (m_VideoTexture != null)
        {
            Destroy(m_VideoTexture);
            m_VideoTexture = null;
        }

        if (m_TargetMaterial != null)
        {
            Destroy(m_TargetMaterial);
            m_TargetMaterial = null;
        }

        m_RunTask = null;
    }

    private async Task RunReceiverAsync(CoreReceiver receiver, CancellationToken cancellationToken)
    {
        try
        {
            await receiver.RunAsync(cancellationToken);
            Log("Receiver run completed.");
        }
        catch (OperationCanceledException) when (m_IsDestroying || cancellationToken.IsCancellationRequested)
        {
            Log("Receiver cancelled.");
        }
        catch (Exception ex)
        {
            Log($"Receiver failed: {ex}");
        }
    }

    private void SubscribeToReceiverEvents(CoreReceiver receiver)
    {
        receiver.OnLog += HandleReceiverLog;
        receiver.OnConnected += HandleConnected;
        receiver.OnIceStateChanged += HandleIceStateChanged;
        receiver.OnDtlsStateChanged += HandleDtlsStateChanged;
        receiver.OnRemoteTrackNegotiated += HandleRemoteTrackNegotiated;
        receiver.OnRtpPacket += HandleRtpPacket;
        receiver.OnSessionEnded += HandleSessionEnded;
        receiver.OnError += HandleError;
    }

    private void UnsubscribeFromReceiverEvents(CoreReceiver receiver)
    {
        receiver.OnLog -= HandleReceiverLog;
        receiver.OnConnected -= HandleConnected;
        receiver.OnIceStateChanged -= HandleIceStateChanged;
        receiver.OnDtlsStateChanged -= HandleDtlsStateChanged;
        receiver.OnRemoteTrackNegotiated -= HandleRemoteTrackNegotiated;
        receiver.OnRtpPacket -= HandleRtpPacket;
        receiver.OnSessionEnded -= HandleSessionEnded;
        receiver.OnError -= HandleError;
    }

    private void HandleReceiverLog(ReceiverLogMessage message)
    {
        if (message.Step == "signal.recv")
        {
            TryLogSdpNegotiation("Remote", message.Message, m_RemotePayloadTypes);
        }
        else if (message.Step == "signal.send")
        {
            TryLogSdpNegotiation("Local", message.Message, m_LocalPayloadTypes);
        }
        else if (message.Step == "track.video")
        {
            Log($"Negotiated video codec set: {message.Message}");
            return;
        }

        if (message.Step == "video.frame")
        {
            m_EncodedFrameCount++;

            if (!m_HasLoggedEncodedFrameSemantics)
            {
                m_HasLoggedEncodedFrameSemantics = true;
                Log("SIPSorcery OnVideoFrameReceived is firing, but it delivers an encoded frame payload reconstructed from RTP, not a decoded RawImage. width=<unavailable>; height=<unavailable>; pixelFormat=<encoded VP8/H264 payload>.");
            }

            if (TryParseEncodedFrameLog(message.Message, out string codec, out int encodedFrameLength))
            {
                m_ActiveCodec = codec;
                m_LastEncodedFrameLength = encodedFrameLength;
                int payloadType = ResolvePayloadType(codec);
                string payloadTypeText = payloadType >= 0 ? payloadType.ToString() : "<unknown>";

                if (!m_HasLoggedActiveCodec)
                {
                    m_HasLoggedActiveCodec = true;
                    Log($"Codec = {codec}");
                }

                Log($"Codec = {codec}; PayloadType = {payloadTypeText}; EncodedFrameSize = {encodedFrameLength} bytes; FrameCount = {m_EncodedFrameCount}");
            }
            else
            {
                Log($"Encoded frame callback count {m_EncodedFrameCount}. {message.Message} width=<unavailable>; height=<unavailable>; pixelFormat=<encoded payload>.");
            }

            return;
        }

        Log($"[{message.Step}] {message.Message}");
    }

    private void HandleConnected()
    {
        Log("Connected");
    }

    private void HandleIceStateChanged(RTCIceConnectionState state)
    {
        Log($"ICE {state}");

        if (state == RTCIceConnectionState.connected)
        {
            Log("ICE connected");
        }
    }

    private void HandleDtlsStateChanged(DtlsStateChangedEvent state)
    {
        Log($"DTLS state peer={state.PeerConnectionState}; negotiated={state.IsNegotiationComplete}; srtpEncoder={state.IsSrtpEncoderActive}; srtpDecoder={state.IsSrtpDecoderActive}");

        if (state.IsNegotiationComplete &&
            state.IsSrtpEncoderActive &&
            state.IsSrtpDecoderActive)
        {
            Log("DTLS connected");
        }
    }

    private void HandleRemoteTrackNegotiated(RemoteTrackNegotiatedEvent track)
    {
        TryAttachVideoFrameTap();
        Log($"Remote track negotiated. SSRC={track.Ssrc}; fingerprint={track.RemoteDtlsFingerprintAlgorithm} {track.RemoteDtlsFingerprintValue}");
    }

    private void HandleRtpPacket(RtpPacketReceivedEvent packet)
    {
        m_LastVideoPayloadType = packet.Packet.Header.PayloadType;
        m_RtpPacketCount = packet.PacketCount;

        if (packet.PacketCount == 1 || packet.PacketCount % 100 == 0)
        {
            string codec = TryGetCodecForPayloadType(packet.Packet.Header.PayloadType, out string mappedCodec)
                ? mappedCodec
                : "<unknown>";
            if (codec != "<unknown>")
            {
                m_ActiveCodec = codec;
            }
            Log($"RTP packet count {packet.PacketCount} (payloadType={packet.Packet.Header.PayloadType}, codec={codec}, ssrc={packet.Packet.Header.SyncSource})");
        }
    }

    private void HandleSessionEnded(SessionEndedEvent session)
    {
        Log($"Session ended. sessionId={session.SessionId}; remote={session.IsRemoteInitiated}; reason={session.Reason}");
    }

    private void HandleError(Exception ex)
    {
        Log($"Error: {ex}");
    }

    private Uri BuildSignallingUri()
    {
        UriBuilder builder = new UriBuilder(m_SignallingUrl.Trim());
        string roomId = m_RoomId == null ? string.Empty : m_RoomId.Trim();

        if (string.IsNullOrEmpty(roomId) || HasRoomIdQuery(builder.Query))
        {
            return builder.Uri;
        }

        string trimmedQuery = builder.Query.TrimStart('?');
        string roomQuery = "roomId=" + Uri.EscapeDataString(roomId);
        builder.Query = string.IsNullOrEmpty(trimmedQuery)
            ? roomQuery
            : trimmedQuery + "&" + roomQuery;

        return builder.Uri;
    }

    private void Log(string message)
    {
        if (m_EventProcessor != null)
        {
            m_EventProcessor.QueueEvent(() =>
            {
                if (this != null)
                {
                    Debug.Log("[UnityWebRtcSmokeReceiver] " + message, this);
                }
            });
            return;
        }

        Debug.Log("[UnityWebRtcSmokeReceiver] " + message, this);
    }

    private string GetRoomLabel()
    {
        return string.IsNullOrWhiteSpace(m_RoomId) ? "<none>" : m_RoomId.Trim();
    }

    private void TryLogSdpNegotiation(string direction, string json, Dictionary<int, string> payloadTypes)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (!root.TryGetProperty("sdp", out JsonElement sdpElement) ||
                !sdpElement.TryGetProperty("sdp", out JsonElement sdpTextElement))
            {
                return;
            }

            string sdpType = sdpElement.TryGetProperty("type", out JsonElement typeElement)
                ? typeElement.GetString()
                : "<unknown>";
            string sdpText = sdpTextElement.GetString();

            if (string.IsNullOrEmpty(sdpText))
            {
                return;
            }

            SdpVideoSection videoSection = ParseVideoSection(sdpText);
            if (videoSection == null)
            {
                return;
            }

            payloadTypes.Clear();
            foreach (KeyValuePair<int, string> entry in videoSection.PayloadCodecs)
            {
                payloadTypes[entry.Key] = entry.Value;
            }

            Log($"{direction} SDP {sdpType} video codecs: {videoSection.CodecOrderText}");
            Log($"{direction} SDP payload mapping: {videoSection.PayloadMappingText}");
            Log($"{direction} SDP frame size: {videoSection.FrameSizeText}");
        }
        catch (Exception ex)
        {
            Log($"{direction} SDP parse failed: {ex.Message}");
        }
    }

    private static SdpVideoSection ParseVideoSection(string sdp)
    {
        string[] lines = sdp.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        bool inVideoSection = false;
        List<int> payloadOrder = new List<int>();
        Dictionary<int, string> codecs = new Dictionary<int, string>();
        Dictionary<int, string> formatParameters = new Dictionary<int, string>();
        Dictionary<int, string> frameSizes = new Dictionary<int, string>();

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();

            if (line.StartsWith("m=", StringComparison.Ordinal))
            {
                inVideoSection = line.StartsWith("m=video ", StringComparison.Ordinal);

                if (inVideoSection)
                {
                    string[] parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 3; i < parts.Length; i++)
                    {
                        if (int.TryParse(parts[i], out int payloadType))
                        {
                            payloadOrder.Add(payloadType);
                        }
                    }
                }

                continue;
            }

            if (!inVideoSection)
            {
                continue;
            }

            if (line.StartsWith("a=rtpmap:", StringComparison.Ordinal))
            {
                int separator = line.IndexOf(' ');
                if (separator > 9 &&
                    int.TryParse(line.Substring(9, separator - 9), out int payloadType))
                {
                    string encoding = line.Substring(separator + 1);
                    int slashIndex = encoding.IndexOf('/');
                    codecs[payloadType] = slashIndex > 0 ? encoding.Substring(0, slashIndex) : encoding;
                }
            }
            else if (line.StartsWith("a=fmtp:", StringComparison.Ordinal))
            {
                int separator = line.IndexOf(' ');
                if (separator > 7 &&
                    int.TryParse(line.Substring(7, separator - 7), out int payloadType))
                {
                    formatParameters[payloadType] = line.Substring(separator + 1);
                }
            }
            else if (line.StartsWith("a=framesize:", StringComparison.Ordinal))
            {
                int separator = line.IndexOf(' ');
                if (separator > 12 &&
                    int.TryParse(line.Substring(12, separator - 12), out int payloadType))
                {
                    frameSizes[payloadType] = line.Substring(separator + 1);
                }
            }
        }

        if (payloadOrder.Count == 0 && codecs.Count == 0)
        {
            return null;
        }

        List<string> codecOrderEntries = new List<string>();
        List<string> payloadMappingEntries = new List<string>();
        List<string> frameSizeEntries = new List<string>();

        foreach (int payloadType in payloadOrder)
        {
            string codec = codecs.TryGetValue(payloadType, out string codecName) ? codecName : "<unknown>";
            codecOrderEntries.Add($"{payloadType}({codec})");

            string mapping = $"{payloadType}={codec}";
            if (formatParameters.TryGetValue(payloadType, out string parameters))
            {
                mapping += $" [{parameters}]";
            }

            payloadMappingEntries.Add(mapping);

            if (frameSizes.TryGetValue(payloadType, out string frameSize))
            {
                frameSizeEntries.Add($"{payloadType}={frameSize}");
            }
        }

        foreach (KeyValuePair<int, string> codecEntry in codecs)
        {
            if (!payloadOrder.Contains(codecEntry.Key))
            {
                payloadMappingEntries.Add($"{codecEntry.Key}={codecEntry.Value}");
            }
        }

        return new SdpVideoSection(
            codecs,
            codecOrderEntries.Count > 0 ? string.Join(", ", codecOrderEntries) : "<none>",
            payloadMappingEntries.Count > 0 ? string.Join(", ", payloadMappingEntries) : "<none>",
            frameSizeEntries.Count > 0 ? string.Join(", ", frameSizeEntries) : "<not present in SDP>");
    }

    private bool TryParseEncodedFrameLog(string message, out string codec, out int encodedFrameLength)
    {
        codec = "<unknown>";
        encodedFrameLength = 0;

        const string bytesToken = " bytes ";
        int bytesIndex = message.IndexOf(bytesToken, StringComparison.Ordinal);
        if (bytesIndex > 0)
        {
            int start = bytesIndex - 1;
            while (start >= 0 && char.IsDigit(message[start]))
            {
                start--;
            }

            string lengthText = message.Substring(start + 1, bytesIndex - start - 1);
            int.TryParse(lengthText, out encodedFrameLength);
        }

        const string codecToken = "codec=";
        int codecIndex = message.IndexOf(codecToken, StringComparison.Ordinal);
        if (codecIndex >= 0)
        {
            codec = message.Substring(codecIndex + codecToken.Length).Trim();
        }

        return encodedFrameLength > 0 || codec != "<unknown>";
    }

    private int ResolvePayloadType(string codec)
    {
        if (m_LastVideoPayloadType.HasValue &&
            TryGetCodecForPayloadType(m_LastVideoPayloadType.Value, out string mappedCodec) &&
            string.Equals(mappedCodec, codec, StringComparison.OrdinalIgnoreCase))
        {
            return m_LastVideoPayloadType.Value;
        }

        foreach (KeyValuePair<int, string> entry in m_RemotePayloadTypes)
        {
            if (string.Equals(entry.Value, codec, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Key;
            }
        }

        foreach (KeyValuePair<int, string> entry in m_LocalPayloadTypes)
        {
            if (string.Equals(entry.Value, codec, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Key;
            }
        }

        return -1;
    }

    private bool TryGetCodecForPayloadType(int payloadType, out string codec)
    {
        if (m_RemotePayloadTypes.TryGetValue(payloadType, out codec))
        {
            return true;
        }

        if (m_LocalPayloadTypes.TryGetValue(payloadType, out codec))
        {
            return true;
        }

        codec = null;
        return false;
    }

    private void TryAttachVideoFrameTap()
    {
        if (m_Receiver == null || m_TappedPeerConnection != null)
        {
            return;
        }

        FieldInfo peerConnectionField = typeof(CoreReceiver).GetField("_peerConnection", BindingFlags.Instance | BindingFlags.NonPublic);
        Log($"[decoder.attach] RTCPeerConnection field found={peerConnectionField != null}");
        if (peerConnectionField == null)
        {
            Log("[decoder.attach] Subscription successful=False reason=peer connection field not found");
            return;
        }

        RTCPeerConnection peerConnection = peerConnectionField.GetValue(m_Receiver) as RTCPeerConnection;
        Log($"[decoder.attach] RTCPeerConnection found={peerConnection != null}");
        if (peerConnection == null)
        {
            Log("[decoder.attach] Subscription successful=False reason=peer connection instance is null");
            return;
        }

        EventInfo onVideoFrameReceivedEvent = typeof(RTCPeerConnection).GetEvent("OnVideoFrameReceived", BindingFlags.Instance | BindingFlags.Public);
        Log($"[decoder.attach] OnVideoFrameReceived found={onVideoFrameReceivedEvent != null}");
        if (onVideoFrameReceivedEvent == null)
        {
            Log("[decoder.attach] Subscription successful=False reason=OnVideoFrameReceived event not found");
            return;
        }

        try
        {
            peerConnection.OnVideoFrameReceived += HandleEncodedVideoFrame;
            m_TappedPeerConnection = peerConnection;
            Log("[decoder.attach] Subscription successful=True");
        }
        catch (Exception ex)
        {
            Log($"[decoder.attach] Subscription successful=False reason={ex.Message}");
        }
    }

    private void TryDetachVideoFrameTap()
    {
        if (m_TappedPeerConnection == null)
        {
            return;
        }

        m_TappedPeerConnection.OnVideoFrameReceived -= HandleEncodedVideoFrame;
        m_TappedPeerConnection = null;
    }

    private void HandleEncodedVideoFrame(IPEndPoint remoteEndPoint, uint timestamp, byte[] frame, VideoFormat format)
    {
        m_DecoderFrameCallbackCount++;
        string codecName = format.Codec.ToString();
        Log($"[decoder.frame-enter] size={(frame != null ? frame.Length : 0)} codec={codecName}");
        Log($"[decoder.frame] count={m_DecoderFrameCallbackCount} size={(frame != null ? frame.Length : 0)}");

        if (frame == null || frame.Length == 0)
        {
            Log("[decoder.frame-skip] reason=empty frame");
            return;
        }

        if (!string.Equals(codecName, "VP8", StringComparison.OrdinalIgnoreCase))
        {
            Log($"[decoder.frame-skip] reason=unsupported codec codec={codecName}");
            return;
        }

        if (m_Vp8Decoder == null || !m_Vp8Decoder.IsAvailable)
        {
            Log($"[decoder.frame-skip] reason=decoder unavailable decoderNull={m_Vp8Decoder == null} isAvailable={(m_Vp8Decoder != null ? m_Vp8Decoder.IsAvailable : false)}");
            return;
        }

        Log($"[decoder.decode] calling vp8_decoder_decode size={frame.Length} codec={codecName}");

        bool decodeSucceeded = m_Vp8Decoder.TryDecode(frame, out DecodedFrameInfo decodedFrame);
        int decodedWidth = decodeSucceeded
            ? (decodedFrame.Width > 0 ? decodedFrame.Width : m_Vp8Decoder.GetWidth())
            : m_Vp8Decoder.GetWidth();
        int decodedHeight = decodeSucceeded
            ? (decodedFrame.Height > 0 ? decodedFrame.Height : m_Vp8Decoder.GetHeight())
            : m_Vp8Decoder.GetHeight();
        string decodeError = m_Vp8Decoder.GetLastError();
        string decodeDebug = m_Vp8Decoder.GetLastDebug();
        Log($"[decoder.result] success={decodeSucceeded} width={decodedWidth} height={decodedHeight} error={(string.IsNullOrEmpty(decodeError) ? "<none>" : decodeError)}");

        if (decodeSucceeded)
        {
            m_DecodedFrameCount++;
            Log($"[texture] [texture.decode-ok] width={decodedWidth} height={decodedHeight}");
            byte[] rgbaFrame = CopyDecodedFrameToRgba(decodedFrame, out int width, out int height);
            Log("[texture] [texture.call-upload]");
            TryUploadOrEnqueueTexture(rgbaFrame, width, height);

            if (m_DecodedFrameCount == 1 || m_DecodedFrameCount % 30 == 0)
            {
                Log(
                    $"Decoded frame: width={decodedFrame.Width} height={decodedFrame.Height} " +
                    $"format={decodedFrame.FormatName} y_stride={decodedFrame.YStride} u_stride={decodedFrame.UStride} v_stride={decodedFrame.VStride} " +
                    $"y_plane=0x{decodedFrame.YPlane.ToInt64():X} u_plane=0x{decodedFrame.UPlane.ToInt64():X} v_plane=0x{decodedFrame.VPlane.ToInt64():X}");
                if (!string.IsNullOrEmpty(decodeDebug))
                {
                    Log($"VP8 decoder debug: {decodeDebug}");
                }
            }
        }
        else
        {
            if (!string.IsNullOrEmpty(decodeError))
            {
                Log($"VP8 decode prototype failed: {decodeError}");
            }
            else
            {
                Log("VP8 decode prototype failed: decoder returned false without an error string.");
            }

            if (!string.IsNullOrEmpty(decodeDebug))
            {
                Log($"VP8 decoder debug: {decodeDebug}");
            }
        }
    }

    private byte[] CopyDecodedFrameToRgba(DecodedFrameInfo decodedFrame, out int width, out int height)
    {
        Log("[texture] [rgba.copy-enter]");
        width = decodedFrame.Width > 0 ? decodedFrame.Width : m_Vp8Decoder.GetWidth();
        height = decodedFrame.Height > 0 ? decodedFrame.Height : m_Vp8Decoder.GetHeight();
        if (width <= 0 || height <= 0)
        {
            Log("VP8 RGBA copy skipped because decoded frame dimensions are invalid.");
            Log("[texture] upload skipped: width/height invalid");
            return null;
        }

        byte[] rgbaFrame = new byte[width * height * 4];
        Log($"[texture] [rgba.try-copy] expectedBytes={rgbaFrame.Length}");

        bool copySucceeded;
        try
        {
            copySucceeded = m_Vp8Decoder.TryCopyRgba(rgbaFrame);
        }
        catch (Exception ex)
        {
            Log($"[texture] [rgba.copy-exception] {ex}");
            return null;
        }

        string error = m_Vp8Decoder.GetLastError();
        Log($"[texture] [rgba.copy-result] success={copySucceeded} error={(string.IsNullOrEmpty(error) ? "<none>" : error)}");

        if (!copySucceeded)
        {
            if (!string.IsNullOrEmpty(error))
            {
                Log($"VP8 RGBA copy failed: {error}");
            }

            Log("[texture] upload skipped: RGBA copy failed");
            return null;
        }

        Log($"RGBA copied: bytes={rgbaFrame.Length}");
        return rgbaFrame;
    }

    private void ApplyPendingFrame()
    {
        byte[] rgbaFrame = null;
        int width = 0;
        int height = 0;

        lock (m_FrameLock)
        {
            if (!m_HasPendingFrame)
            {
                return;
            }

            rgbaFrame = m_PendingRgbaFrame;
            width = m_PendingFrameWidth;
            height = m_PendingFrameHeight;
            m_HasPendingFrame = false;
        }

        if (rgbaFrame == null)
        {
            return;
        }

        Log($"[texture] [texture.update-drain] width={width} height={height} bytes={rgbaFrame.Length}");
        TryUploadTexture(rgbaFrame, width, height);
    }

    private void EnsureVideoTexture(int width, int height)
    {
        if (m_VideoTexture != null && m_VideoTexture.width == width && m_VideoTexture.height == height)
        {
            return;
        }

        if (m_VideoTexture != null)
        {
            Destroy(m_VideoTexture);
            m_VideoTexture = null;
        }

        m_VideoTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        m_VideoTexture.wrapMode = TextureWrapMode.Clamp;
        m_VideoTexture.filterMode = FilterMode.Bilinear;
        Log($"[texture] texture created: width={width} height={height}");

        if (m_TargetRenderer != null)
        {
            Shader shader = Shader.Find("Unlit/Texture");
            if (shader == null)
            {
                shader = Shader.Find("Standard");
            }

            m_TargetMaterial = new Material(shader);
            m_TargetMaterial.mainTexture = m_VideoTexture;
            m_TargetRenderer.material = m_TargetMaterial;
            Log("[texture] texture assigned to renderer");
            Log($"[texture] renderer name={m_TargetRenderer.name} material name={m_TargetRenderer.material.name}");
        }
    }

    private bool TryUploadTexture(byte[] rgbaFrame, int width, int height)
    {
        Log($"[texture] [texture.upload-enter] width={width} height={height} bytes={(rgbaFrame != null ? rgbaFrame.Length : 0)} thread={Thread.CurrentThread.ManagedThreadId}");
        if (width <= 0 || height <= 0)
        {
            Log("[texture] upload skipped: width/height invalid");
            return false;
        }

        if (rgbaFrame == null)
        {
            Log("[texture] upload skipped: RGBA copy failed");
            return false;
        }

        if (Thread.CurrentThread.ManagedThreadId != m_MainThreadId)
        {
            Log("[texture] upload skipped: not on main thread");
            return false;
        }

        if (m_TargetRenderer == null)
        {
            m_TargetRenderer = FindTargetRenderer();
            if (m_TargetRenderer == null)
            {
                Log("[texture] upload skipped: no renderer");
                return false;
            }
        }

        int expectedSize = width * height * 4;
        if (rgbaFrame == null || rgbaFrame.Length != expectedSize)
        {
            Log($"[texture] upload skipped: buffer size mismatch expected={expectedSize} actual={(rgbaFrame != null ? rgbaFrame.Length : 0)}");
            return false;
        }

        EnsureVideoTexture(width, height);
        if (m_VideoTexture == null)
        {
            Log("[texture] upload skipped: no renderer");
            return false;
        }

        Log($"[texture] uploading texture: width={width} height={height}");
        int nextDisplayedFrame = m_DisplayedFrameCount + 1;
        if (nextDisplayedFrame % 30 == 0)
        {
            uint checksum = ComputeFrameChecksum(rgbaFrame);
            byte firstR = rgbaFrame.Length > 0 ? rgbaFrame[0] : (byte)0;
            byte firstG = rgbaFrame.Length > 1 ? rgbaFrame[1] : (byte)0;
            byte firstB = rgbaFrame.Length > 2 ? rgbaFrame[2] : (byte)0;
            byte firstA = rgbaFrame.Length > 3 ? rgbaFrame[3] : (byte)0;
            Log($"[texture.frame-uploaded] frame={nextDisplayedFrame} checksum={checksum} firstRGBA={firstR},{firstG},{firstB},{firstA}");
        }

        m_VideoTexture.LoadRawTextureData(rgbaFrame);
        m_VideoTexture.Apply(false, false);
        Log("[texture] texture applied");
        m_DisplayedFrameCount++;

        if (m_TargetRenderer != null && m_TargetRenderer.material != null)
        {
            Log("[texture] texture assigned to renderer");
            Log($"[texture] renderer name={m_TargetRenderer.name} material name={m_TargetRenderer.material.name}");
        }

        if (m_DisplayedFrameCount == 1 || m_DisplayedFrameCount % 30 == 0)
        {
            Log($"[texture] displayed frame {m_DisplayedFrameCount} on quad. width={width} height={height}");
        }

        if (m_DisplayedFrameCount % 100 == 0)
        {
            Log($"[texture] status: frame={m_DisplayedFrameCount} textureWidth={m_VideoTexture.width} textureHeight={m_VideoTexture.height}");
        }

        return true;
    }

    private static uint ComputeFrameChecksum(byte[] rgbaFrame)
    {
        unchecked
        {
            uint hash = 2166136261;
            for (int i = 0; i < rgbaFrame.Length; i++)
            {
                hash ^= rgbaFrame[i];
                hash *= 16777619;
            }

            return hash;
        }
    }

    private void TryUploadOrEnqueueTexture(byte[] rgbaFrame, int width, int height)
    {
        Log("[texture] [texture.enter-upload-helper]");
        if (Thread.CurrentThread.ManagedThreadId == m_MainThreadId)
        {
            TryUploadTexture(rgbaFrame, width, height);
            return;
        }

        Log($"[texture] [texture.enqueue] reason=not-main-thread width={width} height={height} bytes={(rgbaFrame != null ? rgbaFrame.Length : 0)}");

        if (rgbaFrame == null)
        {
            TryUploadTexture(rgbaFrame, width, height);
            return;
        }

        lock (m_FrameLock)
        {
            m_PendingRgbaFrame = rgbaFrame;
            m_PendingFrameWidth = width;
            m_PendingFrameHeight = height;
            m_HasPendingFrame = true;
        }

        TryUploadTexture(rgbaFrame, width, height);
    }

    private Renderer FindTargetRenderer()
    {
        GameObject targetObject = GameObject.Find(m_TargetQuadName);
        return targetObject != null ? targetObject.GetComponent<Renderer>() : null;
    }

    private string GetPayloadTypeText()
    {
        return m_LastVideoPayloadType.HasValue ? m_LastVideoPayloadType.Value.ToString() : "<unknown>";
    }

    private string GetEncodedFrameSizeText()
    {
        return m_LastEncodedFrameLength > 0 ? m_LastEncodedFrameLength + " bytes" : "<unknown>";
    }

    private static bool HasRoomIdQuery(string query)
    {
        return query.IndexOf("roomId=", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private sealed class SdpVideoSection
    {
        public SdpVideoSection(
            Dictionary<int, string> payloadCodecs,
            string codecOrderText,
            string payloadMappingText,
            string frameSizeText)
        {
            PayloadCodecs = payloadCodecs;
            CodecOrderText = codecOrderText;
            PayloadMappingText = payloadMappingText;
            FrameSizeText = frameSizeText;
        }

        public Dictionary<int, string> PayloadCodecs { get; }
        public string CodecOrderText { get; }
        public string PayloadMappingText { get; }
        public string FrameSizeText { get; }
    }
}

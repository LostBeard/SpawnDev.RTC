using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SpawnDev.MultiMedia;

namespace SpawnDev.RTC.Desktop
{
    /// <summary>
    /// Desktop implementation of IRTCPeerConnection.
    /// Wraps SipSorcery's RTCPeerConnection.
    /// </summary>
    public class DesktopRTCPeerConnection : IRTCPeerConnection
    {
        private readonly List<MultiMediaAudioSource> _audioSources = new();

        /// <summary>
        /// MultiMedia audio sources attached to this peer connection via <see cref="AddTrack(IAudioTrack)"/>.
        /// Each one bridges a single <see cref="IAudioTrack"/> into SipSorcery's RTP audio encoder
        /// path. Exposed for diagnostics (encoded-frame counters) and for consumer code that needs
        /// to pause/resume an individual source without stopping the whole peer connection.
        /// </summary>
        public IReadOnlyList<MultiMediaAudioSource> AudioSources => _audioSources;


        /// <summary>
        /// Direct access to the underlying SipSorcery RTCPeerConnection.
        /// Use this for advanced SipSorcery features not exposed through the abstraction.
        /// </summary>
        public RTCPeerConnection NativeConnection { get; }

        private bool _disposed;

        public string ConnectionState => NativeConnection.connectionState.ToString();
        public string IceConnectionState => NativeConnection.iceConnectionState.ToString();
        public string IceGatheringState => NativeConnection.iceGatheringState.ToString();
        public string SignalingState => NativeConnection.signalingState.ToString();

        public RTCSessionDescriptionInit? LocalDescription
        {
            get
            {
                var desc = NativeConnection.localDescription;
                if (desc == null) return null;
                return new RTCSessionDescriptionInit { Type = desc.type.ToString(), Sdp = desc.sdp?.ToString() ?? "" };
            }
        }

        public RTCSessionDescriptionInit? RemoteDescription
        {
            get
            {
                var desc = NativeConnection.remoteDescription;
                if (desc == null) return null;
                return new RTCSessionDescriptionInit { Type = desc.type.ToString(), Sdp = desc.sdp?.ToString() ?? "" };
            }
        }

        public bool? CanTrickleIceCandidates => true;
        public RTCSessionDescriptionInit? CurrentLocalDescription => LocalDescription;
        public RTCSessionDescriptionInit? CurrentRemoteDescription => RemoteDescription;
        public RTCSessionDescriptionInit? PendingLocalDescription => null;
        public RTCSessionDescriptionInit? PendingRemoteDescription => null;

        public event Action<RTCIceCandidateInit>? OnIceCandidate;
        public event Action<RTCIceCandidateError>? OnIceCandidateError;
        public event Action<IRTCDataChannel>? OnDataChannel;
        public event Action<RTCTrackEventInit>? OnTrack;
        public event Action<string>? OnConnectionStateChange;
        public event Action<string>? OnSignalingStateChange;
        public event Action<string>? OnIceConnectionStateChange;
        public event Action<string>? OnIceGatheringStateChange;
        public event Action? OnNegotiationNeeded;

        public DesktopRTCPeerConnection(RTCPeerConnectionConfig? config = null)
        {
            var sipConfig = new RTCConfiguration();
            if (config != null)
            {
                if (config.IceServers != null)
                {
                    sipConfig.iceServers = new List<RTCIceServer>();
                    foreach (var server in config.IceServers)
                    {
                        // SipSorcery takes a single URL string per entry
                        // Expand multiple URLs into separate entries
                        foreach (var url in server.Urls)
                        {
                            sipConfig.iceServers.Add(new RTCIceServer
                            {
                                urls = url,
                                username = server.Username,
                                credential = server.Credential,
                            });
                        }
                    }
                }
                // Map additional config properties
                if (config.IceTransportPolicy == "relay")
                    sipConfig.iceTransportPolicy = RTCIceTransportPolicy.relay;
                if (config.BundlePolicy == "max-bundle")
                    sipConfig.bundlePolicy = RTCBundlePolicy.max_bundle;
                else if (config.BundlePolicy == "max-compat")
                    sipConfig.bundlePolicy = RTCBundlePolicy.max_compat;
            }
            NativeConnection = new RTCPeerConnection(sipConfig);
            NativeConnection.onicecandidate += HandleIceCandidate;
            NativeConnection.ondatachannel += HandleDataChannel;
            NativeConnection.onconnectionstatechange += HandleConnectionStateChange;
            NativeConnection.onnegotiationneeded += HandleNegotiationNeeded;
            NativeConnection.onsignalingstatechange += HandleSignalingStateChange;
            NativeConnection.oniceconnectionstatechange += HandleIceConnectionStateChange;
            NativeConnection.onicegatheringstatechange += HandleIceGatheringStateChange;
            NativeConnection.OnRemoteDescriptionChanged += HandleRemoteDescriptionChanged;
        }

        public IRTCDataChannel CreateDataChannel(string label, RTCDataChannelConfig? options = null)
        {
            SIPSorcery.Net.RTCDataChannelInit? sipInit = null;
            if (options != null)
            {
                sipInit = new SIPSorcery.Net.RTCDataChannelInit
                {
                    ordered = options.Ordered,
                    maxPacketLifeTime = options.MaxPacketLifeTime,
                    maxRetransmits = options.MaxRetransmits,
                    protocol = options.Protocol,
                    negotiated = options.Negotiated,
                    id = options.Id,
                };
            }
            // createDataChannel is async in SipSorcery but we need sync creation
            // for the interface. The channel is returned immediately in pending state
            // when the connection isn't established yet.
            var task = NativeConnection.createDataChannel(label, sipInit);
            // If the connection is not yet established, the task completes synchronously
            // with a pending channel. If connected, it waits for DCEP ACK.
            var channel = task.GetAwaiter().GetResult();
            return new DesktopRTCDataChannel(channel);
        }

        public Task<RTCSessionDescriptionInit> CreateOffer()
        {
            return CreateOffer(new RTCOfferOptions());
        }

        public Task<RTCSessionDescriptionInit> CreateAnswer()
        {
            return CreateAnswer(new RTCAnswerOptions());
        }

        public async Task SetLocalDescription(RTCSessionDescriptionInit description)
        {
            var sdpType = Enum.Parse<RTCSdpType>(description.Type, true);
            var init = new SIPSorcery.Net.RTCSessionDescriptionInit { type = sdpType, sdp = description.Sdp };
            await NativeConnection.setLocalDescription(init);
        }

        public Task SetRemoteDescription(RTCSessionDescriptionInit description)
        {
            var sdpType = Enum.Parse<RTCSdpType>(description.Type, true);
            var init = new SIPSorcery.Net.RTCSessionDescriptionInit { type = sdpType, sdp = description.Sdp };
            var result = NativeConnection.setRemoteDescription(init);
            if (result != SetDescriptionResultEnum.OK)
            {
                throw new InvalidOperationException($"setRemoteDescription failed: {result}");
            }
            return Task.CompletedTask;
        }

        public Task AddIceCandidate(RTCIceCandidateInit candidate)
        {
            var sipCandidate = new SIPSorcery.Net.RTCIceCandidateInit
            {
                candidate = candidate.Candidate,
                sdpMid = candidate.SdpMid,
                sdpMLineIndex = (ushort)(candidate.SdpMLineIndex ?? 0),
                usernameFragment = candidate.UsernameFragment,
            };
            NativeConnection.addIceCandidate(sipCandidate);
            return Task.CompletedTask;
        }

        public Task<RTCSessionDescriptionInit> CreateOffer(RTCOfferOptions options)
        {
            var sipOptions = new SIPSorcery.Net.RTCOfferOptions
            {
                X_WaitForIceGatheringToComplete = options.WaitForIceGatheringToComplete,
            };
            // SipSorcery createOffer is synchronous. When X_WaitForIceGatheringToComplete is set,
            // it blocks internally until _iceCompletedGatheringTask fires (STUN binding done),
            // then embeds all gathered candidates (host + srflx) into the returned SDP.
            var offer = NativeConnection.createOffer(sipOptions);
            return Task.FromResult(new RTCSessionDescriptionInit
            {
                Type = offer.type.ToString(),
                Sdp = offer.sdp,
            });
        }

        public Task<RTCSessionDescriptionInit> CreateAnswer(RTCAnswerOptions options)
        {
            var sipOptions = new SIPSorcery.Net.RTCAnswerOptions
            {
                X_WaitForIceGatheringToComplete = options.WaitForIceGatheringToComplete,
            };
            var answer = NativeConnection.createAnswer(sipOptions);
            return Task.FromResult(new RTCSessionDescriptionInit
            {
                Type = answer.type.ToString(),
                Sdp = answer.sdp,
            });
        }

        public async Task SetLocalDescription()
        {
            // Implicit: create offer or answer based on signaling state
            var state = NativeConnection.signalingState;
            if (state == RTCSignalingState.stable || state == RTCSignalingState.have_local_offer)
            {
                var offer = await CreateOffer();
                await SetLocalDescription(offer);
            }
            else if (state == RTCSignalingState.have_remote_offer)
            {
                var answer = await CreateAnswer();
                await SetLocalDescription(answer);
            }
        }

        public IRTCRtpTransceiver[] GetTransceivers()
        {
            return _transceivers.ToArray();
        }

        public IRTCRtpTransceiver AddTransceiver(string kind)
        {
            // Create a SipSorcery track for the specified media kind
            var mediaType = kind == "audio" ? SDPMediaTypesEnum.audio : SDPMediaTypesEnum.video;
            List<SDPAudioVideoMediaFormat> formats;
            if (mediaType == SDPMediaTypesEnum.audio)
            {
                formats = new List<SDPAudioVideoMediaFormat>
                {
                    new SDPAudioVideoMediaFormat(SDPWellKnownMediaFormatsEnum.PCMU),
                    new SDPAudioVideoMediaFormat(SDPWellKnownMediaFormatsEnum.PCMA),
                };
            }
            else
            {
                formats = new List<SDPAudioVideoMediaFormat>
                {
                    new SDPAudioVideoMediaFormat(new VideoFormat(VideoCodecsEnum.VP8, 96)),
                    new SDPAudioVideoMediaFormat(new VideoFormat(VideoCodecsEnum.H264, 100)),
                };
            }
            var track = new MediaStreamTrack(mediaType, false, formats, MediaStreamStatusEnum.SendRecv);
            NativeConnection.addTrack(track);
            var transceiver = new DesktopRTCRtpTransceiver(NativeConnection, track);
            _transceivers.Add(transceiver);
            return transceiver;
        }

        public IRTCRtpTransceiver AddTransceiver(IRTCMediaStreamTrack track)
        {
            if (track is DesktopRTCMediaStreamTrack desktopTrack)
            {
                NativeConnection.addTrack(desktopTrack.NativeTrack);
                var transceiver = new DesktopRTCRtpTransceiver(NativeConnection, desktopTrack.NativeTrack);
                _transceivers.Add(transceiver);
                return transceiver;
            }
            throw new ArgumentException("Track must be a DesktopRTCMediaStreamTrack on desktop.");
        }

        private readonly List<IRTCRtpTransceiver> _transceivers = new();

        public void RestartIce()
        {
            NativeConnection.restartIce();
        }

        public IRTCRtpSender AddTrack(IRTCMediaStreamTrack track, params IRTCMediaStream[] streams)
        {
            if (track is DesktopRTCMediaStreamTrack desktopTrack)
            {
                NativeConnection.addTrack(desktopTrack.NativeTrack);
                return new DesktopRtpSender(track, NativeConnection);
            }
            throw new ArgumentException("Track must be a DesktopRTCMediaStreamTrack on desktop.");
        }

        /// <summary>
        /// Attaches a SpawnDev.MultiMedia <see cref="IAudioTrack"/> (real microphone capture)
        /// to this peer connection. Raw PCM frames from the track are routed through a
        /// <see cref="MultiMediaAudioSource"/> bridge that encodes them (Opus by default for
        /// browser WebRTC interop) and feeds the encoded packets into SipSorcery's RTP sender.
        /// </summary>
        public IRTCRtpSender AddTrack(IAudioTrack audioTrack)
        {
            return AddTrack(new MultiMediaAudioSource(audioTrack));
        }

        /// <summary>
        /// Overload that accepts a preconstructed <see cref="MultiMediaAudioSource"/>, giving the
        /// caller explicit control over the set of advertised codecs (via the SipSorcery
        /// <see cref="AudioEncoder"/> passed into the source ctor). Use this when a specific
        /// codec restriction is required - for example in tests that want to lock negotiation
        /// onto Opus so peer-side codec preference quirks cannot pick a different format.
        /// </summary>
        public IRTCRtpSender AddTrack(MultiMediaAudioSource source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            _audioSources.Add(source);

            var sipsorceryTrack = new MediaStreamTrack(source.GetAudioSourceFormats());
            NativeConnection.addTrack(sipsorceryTrack);

            source.OnAudioSourceEncodedSample += NativeConnection.SendAudio;
            NativeConnection.OnAudioFormatsNegotiated += negotiated =>
            {
                if (negotiated != null && negotiated.Count > 0)
                {
                    source.SetAudioSourceFormat(negotiated[0]);
                }
            };

            _ = source.StartAudio();

            var wrappedTrack = new DesktopRTCMediaStreamTrack(sipsorceryTrack);
            return new DesktopRtpSender(wrappedTrack, NativeConnection);
        }

        public void RemoveTrack(IRTCRtpSender sender)
        {
            if (sender is DesktopRtpSender desktopSender && desktopSender.Track is DesktopRTCMediaStreamTrack desktopTrack)
            {
                NativeConnection.removeTrack(desktopTrack.NativeTrack);
            }
        }

        public IRTCRtpSender[] GetSenders()
        {
            var senders = new List<IRTCRtpSender>();
            if (NativeConnection.AudioLocalTrack != null)
                senders.Add(new DesktopRtpSender(new DesktopRTCMediaStreamTrack(NativeConnection.AudioLocalTrack), NativeConnection));
            if (NativeConnection.VideoLocalTrack != null)
                senders.Add(new DesktopRtpSender(new DesktopRTCMediaStreamTrack(NativeConnection.VideoLocalTrack), NativeConnection));
            return senders.ToArray();
        }

        public IRTCRtpReceiver[] GetReceivers()
        {
            var receivers = new List<IRTCRtpReceiver>();
            if (NativeConnection.AudioRemoteTrack != null)
                receivers.Add(new DesktopRtpReceiver(new DesktopRTCMediaStreamTrack(NativeConnection.AudioRemoteTrack)));
            if (NativeConnection.VideoRemoteTrack != null)
                receivers.Add(new DesktopRtpReceiver(new DesktopRTCMediaStreamTrack(NativeConnection.VideoRemoteTrack)));
            return receivers.ToArray();
        }

        public Task<IRTCStatsReport> GetStats()
        {
            // SipSorcery doesn't expose browser-style per-codec / per-candidate-pair
            // counters, but we can surface connection-level + transport-level state
            // as `peer-connection` and `transport` entries. Richer than empty.
            return Task.FromResult<IRTCStatsReport>(new DesktopRTCStatsReport(NativeConnection));
        }

        public void Close() => NativeConnection.close();

        private void HandleIceCandidate(RTCIceCandidate candidate)
        {
            OnIceCandidate?.Invoke(new RTCIceCandidateInit
            {
                Candidate = candidate.ToString(),
                SdpMid = candidate.sdpMid,
                SdpMLineIndex = candidate.sdpMLineIndex,
                UsernameFragment = candidate.usernameFragment,
            });
        }

        private void HandleDataChannel(RTCDataChannel channel)
        {
            OnDataChannel?.Invoke(new DesktopRTCDataChannel(channel));
        }

        private void HandleConnectionStateChange(RTCPeerConnectionState state)
        {
            OnConnectionStateChange?.Invoke(state.ToString());
        }

        private void HandleIceConnectionStateChange(RTCIceConnectionState state)
        {
            OnIceConnectionStateChange?.Invoke(state.ToString());
        }

        private void HandleIceGatheringStateChange(RTCIceGatheringState state)
        {
            OnIceGatheringStateChange?.Invoke(state.ToString());
        }

        private void HandleSignalingStateChange()
        {
            OnSignalingStateChange?.Invoke(NativeConnection.signalingState.ToString().Replace("_", "-"));
        }

        private void HandleNegotiationNeeded()
        {
            OnNegotiationNeeded?.Invoke();
        }

        private void HandleRemoteDescriptionChanged(SIPSorcery.Net.SDP sdp)
        {
            // Fire OnTrack for any remote tracks that have been set
            if (NativeConnection.AudioRemoteTrack != null && !_notifiedRemoteTracks.Contains("audio"))
            {
                _notifiedRemoteTracks.Add("audio");
                var track = new DesktopRTCMediaStreamTrack(NativeConnection.AudioRemoteTrack);
                OnTrack?.Invoke(new RTCTrackEventInit
                {
                    Track = track,
                    Receiver = new DesktopRtpReceiver(track),
                    Streams = System.Array.Empty<IRTCMediaStream>(),
                });
            }
            if (NativeConnection.VideoRemoteTrack != null && !_notifiedRemoteTracks.Contains("video"))
            {
                _notifiedRemoteTracks.Add("video");
                var track = new DesktopRTCMediaStreamTrack(NativeConnection.VideoRemoteTrack);
                OnTrack?.Invoke(new RTCTrackEventInit
                {
                    Track = track,
                    Receiver = new DesktopRtpReceiver(track),
                    Streams = System.Array.Empty<IRTCMediaStream>(),
                });
            }
        }
        private readonly HashSet<string> _notifiedRemoteTracks = new();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var src in _audioSources)
            {
                try { src.Dispose(); } catch { }
            }
            _audioSources.Clear();
            NativeConnection.onicecandidate -= HandleIceCandidate;
            NativeConnection.ondatachannel -= HandleDataChannel;
            NativeConnection.onconnectionstatechange -= HandleConnectionStateChange;
            NativeConnection.onsignalingstatechange -= HandleSignalingStateChange;
            NativeConnection.onnegotiationneeded -= HandleNegotiationNeeded;
            NativeConnection.OnRemoteDescriptionChanged -= HandleRemoteDescriptionChanged;
            NativeConnection.oniceconnectionstatechange -= HandleIceConnectionStateChange;
            NativeConnection.onicegatheringstatechange -= HandleIceGatheringStateChange;
            NativeConnection.close();
            NativeConnection.Dispose();
        }
    }
}

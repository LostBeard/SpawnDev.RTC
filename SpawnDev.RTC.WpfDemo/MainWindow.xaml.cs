using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MM = SpawnDev.MultiMedia;
using SpawnDev.RTC.Signaling;

namespace SpawnDev.RTC.WpfDemo
{
    public partial class MainWindow : Window
    {
        private TrackerSignalingClient? _signal;
        private RtcPeerConnectionRoomHandler? _handler;
        private RoomKey _roomKey;
        private string _roomName = "";
        private bool _micOn;
        private bool _camOn;
        private MM.IMediaStream? _localStream;
        private WpfVideoRenderer? _localRenderer;
        private readonly ObservableCollection<PeerInfo> _peers = new();
        private readonly ObservableCollection<ChatMessage> _messages = new();
        private readonly Dictionary<string, IRTCDataChannel> _chatChannels = new();

        public MainWindow()
        {
            InitializeComponent();
            PeerList.ItemsSource = _peers;
            ChatMessages.ItemsSource = _messages;
            RoomNameInput.Focus();
        }

        private void RoomNameInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) JoinRoom();

            // Update swarm ID preview
            var name = RoomNameInput.Text.Trim();
            SwarmIdPreview.Text = string.IsNullOrEmpty(name) ? "" : $"Swarm ID: {ComputeInfoHash(name)[..16]}...";
        }

        private void JoinRoom_Click(object sender, RoutedEventArgs e) => JoinRoom();

        private async void JoinRoom()
        {
            _roomName = RoomNameInput.Text.Trim();
            if (string.IsNullOrEmpty(_roomName)) return;

            _roomKey = RoomKey.FromString(_roomName);
            var infoHash = _roomKey.ToHex();
            var signalBase = SignalServerInput.Text.Trim();

            JoinPanel.Visibility = Visibility.Collapsed;
            RoomPanel.Visibility = Visibility.Visible;
            RoomTitle.Text = _roomName;
            RoomSubtitle.Text = $"Swarm: {infoHash[..12]}... - Connecting...";

            var config = new RTCPeerConnectionConfig
            {
                IceServers = new[] { new RTCIceServerConfig { Urls = new[] { "stun:stun.l.google.com:19302" } } }
            };

            _handler = new RtcPeerConnectionRoomHandler(config);

            _handler.OnPeerConnection += (pc, peerId) =>
            {
                Dispatcher.Invoke(() =>
                {
                    var name = peerId.Length >= 6 ? peerId[..6] : peerId;
                    _peers.Add(new PeerInfo { PeerId = peerId, DisplayName = name, Status = "connected" });
                    AddMessage("System", $"{name} connected", false);
                    UpdateSubtitle();
                });
            };

            _handler.OnDataChannel += (channel, peerId) =>
            {
                _chatChannels[peerId] = channel;
                var name = peerId.Length >= 6 ? peerId[..6] : peerId;

                channel.OnStringMessage += msg => Dispatcher.Invoke(() =>
                {
                    AddMessage(name, msg, false);
                });

                channel.OnOpen += () => Dispatcher.Invoke(() =>
                {
                    AddMessage("System", $"Chat ready with {name}", false);
                });
            };

            _handler.OnPeerDisconnected += peerId =>
            {
                Dispatcher.Invoke(() =>
                {
                    _chatChannels.Remove(peerId);
                    var peer = _peers.FirstOrDefault(p => p.PeerId == peerId);
                    if (peer != null) _peers.Remove(peer);
                    var name = peerId.Length >= 6 ? peerId[.. 6] : peerId;
                    AddMessage("System", $"{name} left", false);
                    UpdateSubtitle();
                });
            };

            var localPeerId = new byte[20];
            RandomNumberGenerator.Fill(localPeerId);
            _signal = new TrackerSignalingClient(signalBase, localPeerId);

            _signal.OnConnected += () => Dispatcher.Invoke(() =>
            {
                RoomSubtitle.Text = $"Swarm: {infoHash[..12]}... - Connected to tracker";
                AddMessage("System", "Connected to tracker. Waiting for peers...", false);
            });

            _signal.OnDisconnected += () => Dispatcher.Invoke(() =>
            {
                AddMessage("System", "Disconnected from signal server", false);
            });

            _signal.OnWarning += warn => Dispatcher.Invoke(() =>
            {
                AddMessage("Warning", warn, false);
            });

            _signal.Subscribe(_roomKey, _handler);

            try
            {
                await _signal.AnnounceAsync(_roomKey, new AnnounceOptions { Event = "started", NumWant = 5 });
            }
            catch (Exception ex)
            {
                AddMessage("Error", ex.Message, false);
            }
        }

        private void SendChat_Click(object sender, RoutedEventArgs e) => SendChat();

        private void ChatInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) SendChat();
        }

        private void SendChat()
        {
            var text = ChatInput.Text.Trim();
            if (string.IsNullOrEmpty(text)) return;

            AddMessage("You", text, true);

            foreach (var dc in _chatChannels.Values)
            {
                if (dc.ReadyState == "open")
                {
                    try { dc.Send(text); } catch { }
                }
            }

            ChatInput.Text = "";
            ChatInput.Focus();
        }

        private async void ToggleMic_Click(object sender, RoutedEventArgs e)
        {
            _micOn = !_micOn;
            MicButton.Content = _micOn ? "Mic On" : "Mic Off";
            MicButton.Background = _micOn ? new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)) : new SolidColorBrush(Color.FromRgb(0xc0, 0x39, 0x2b));

            await UpdateLocalStreamAsync();
        }

        private async void ToggleCam_Click(object sender, RoutedEventArgs e)
        {
            _camOn = !_camOn;
            CamButton.Content = _camOn ? "Cam On" : "Cam Off";
            CamButton.Background = _camOn ? new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)) : new SolidColorBrush(Color.FromRgb(0xc0, 0x39, 0x2b));

            await UpdateLocalStreamAsync();
        }

        private async Task UpdateLocalStreamAsync()
        {
            StopLocalStream();

            if (!_micOn && !_camOn) return;

            try
            {
                var constraints = new MM.MediaStreamConstraints
                {
                    Audio = _micOn,
                    Video = _camOn,
                };
                _localStream = await MM.MediaDevices.GetUserMedia(constraints);

                if (_camOn)
                {
                    var videoTracks = _localStream.GetVideoTracks();
                    if (videoTracks.Length > 0 && videoTracks[0] is MM.IVideoTrack videoTrack)
                    {
                        _localRenderer = new WpfVideoRenderer();
                        _localRenderer.OnFrameRendered += OnLocalFrameRendered;
                        _localRenderer.Attach(videoTrack);
                        LocalVideoTile.Visibility = Visibility.Visible;
                    }
                }
            }
            catch (Exception ex)
            {
                AddMessage("Error", $"Failed to start media: {ex.Message}", false);
                _micOn = false;
                _camOn = false;
                MicButton.Content = "Mic Off";
                CamButton.Content = "Cam Off";
                MicButton.Background = new SolidColorBrush(Color.FromRgb(0xc0, 0x39, 0x2b));
                CamButton.Background = new SolidColorBrush(Color.FromRgb(0xc0, 0x39, 0x2b));
            }
        }

        private void OnLocalFrameRendered()
        {
            if (_localRenderer?.Bitmap != null && LocalVideoImage.Source != _localRenderer.Bitmap)
            {
                LocalVideoImage.Source = _localRenderer.Bitmap;
            }
        }

        private void StopLocalStream()
        {
            if (_localRenderer != null)
            {
                _localRenderer.OnFrameRendered -= OnLocalFrameRendered;
                _localRenderer.Dispose();
                _localRenderer = null;
            }

            if (_localStream != null)
            {
                _localStream.Dispose();
                _localStream = null;
            }

            LocalVideoImage.Source = null;
            LocalVideoTile.Visibility = Visibility.Collapsed;
        }

        private async void LeaveRoom_Click(object sender, RoutedEventArgs e)
        {
            StopLocalStream();
            _micOn = false;
            _camOn = false;
            MicButton.Content = "Mic Off";
            CamButton.Content = "Cam Off";
            MicButton.Background = new SolidColorBrush(Color.FromRgb(0xc0, 0x39, 0x2b));
            CamButton.Background = new SolidColorBrush(Color.FromRgb(0xc0, 0x39, 0x2b));

            if (_signal != null)
            {
                try { await _signal.AnnounceAsync(_roomKey, new AnnounceOptions { Event = "stopped", NumWant = 0 }); }
                catch { /* best effort */ }
                _signal.Unsubscribe(_roomKey);
                await _signal.DisposeAsync();
                _signal = null;
            }
            _handler?.Dispose();
            _handler = null;

            _chatChannels.Clear();
            _peers.Clear();
            _messages.Clear();

            RoomPanel.Visibility = Visibility.Collapsed;
            JoinPanel.Visibility = Visibility.Visible;
            RoomNameInput.Focus();
        }

        private void DisconnectPeer_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is string peerId)
            {
                if (_chatChannels.TryGetValue(peerId, out var dc))
                {
                    dc.Close();
                    dc.Dispose();
                    _chatChannels.Remove(peerId);
                }
                var peer = _peers.FirstOrDefault(p => p.PeerId == peerId);
                if (peer != null) _peers.Remove(peer);
                AddMessage("System", $"Disconnected from {peerId[..6]}", false);
                UpdateSubtitle();
            }
        }

        private void AddMessage(string sender, string text, bool isLocal)
        {
            _messages.Add(new ChatMessage
            {
                Sender = sender,
                Text = text,
                SenderColor = isLocal ? "#9ece6a" : sender == "System" ? "#888" : "#7aa2f7",
            });

            if (_messages.Count > 500) _messages.RemoveAt(0);

            // Auto-scroll to bottom
            if (ChatMessages.Items.Count > 0)
                ChatMessages.ScrollIntoView(ChatMessages.Items[^1]);
        }

        private void UpdatePeerStatus(string peerId, string status)
        {
            var peer = _peers.FirstOrDefault(p => p.PeerId == peerId);
            if (peer != null)
            {
                var idx = _peers.IndexOf(peer);
                _peers[idx] = new PeerInfo { PeerId = peerId, DisplayName = peerId[..6], Status = status };
            }
        }

        private void UpdateSubtitle()
        {
            var infoHash = ComputeInfoHash(_roomName);
            RoomSubtitle.Text = $"Swarm: {infoHash[..12]}... - {_peers.Count + 1} participant{(_peers.Count != 0 ? "s" : "")}";
        }

        private static string ComputeInfoHash(string roomName)
            => RoomKey.FromString(roomName.Trim()).ToHex();
    }

    public class PeerInfo
    {
        public string PeerId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Status { get; set; } = "";
    }

    public class ChatMessage
    {
        public string Sender { get; set; } = "";
        public string Text { get; set; } = "";
        public string SenderColor { get; set; } = "#ccc";
    }
}

using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace SpawnDev.RTC.WpfDemo
{
    public partial class MainWindow : Window
    {
        private RTCTrackerClient? _signal;
        private string _roomName = "";
        private bool _micMuted;
        private bool _camMuted;
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

            var infoHash = ComputeInfoHash(_roomName);
            var signalBase = SignalServerInput.Text.Trim();

            JoinPanel.Visibility = Visibility.Collapsed;
            RoomPanel.Visibility = Visibility.Visible;
            RoomTitle.Text = _roomName;
            RoomSubtitle.Text = $"Swarm: {infoHash[..12]}... - Connecting...";

            var config = new RTCPeerConnectionConfig
            {
                IceServers = new[] { new RTCIceServerConfig { Urls = new[] { "stun:stun.l.google.com:19302" } } }
            };

            _signal = new RTCTrackerClient(signalBase, _roomName, config);

            _signal.OnPeerConnection += (pc, peerId) =>
            {
                Dispatcher.Invoke(() =>
                {
                    var name = peerId.Length >= 6 ? peerId[..6] : peerId;
                    _peers.Add(new PeerInfo { PeerId = peerId, DisplayName = name, Status = "connected" });
                    AddMessage("System", $"{name} connected", false);
                    UpdateSubtitle();
                });

            };

            _signal.OnDataChannel += (channel, peerId) =>
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

            _signal.OnPeerDisconnected += peerId =>
            {
                Dispatcher.Invoke(() =>
                {
                    _chatChannels.Remove(peerId);
                    var peer = _peers.FirstOrDefault(p => p.PeerId == peerId);
                    if (peer != null) _peers.Remove(peer);
                    var name = peerId.Length >= 6 ? peerId[..6] : peerId;
                    AddMessage("System", $"{name} left", false);
                    UpdateSubtitle();
                });
            };

            _signal.OnConnected += () => Dispatcher.Invoke(() =>
            {
                RoomSubtitle.Text = $"Swarm: {infoHash[..12]}... - Connected to tracker";
                AddMessage("System", "Connected to tracker. Waiting for peers...", false);
            });

            _signal.OnDisconnected += () => Dispatcher.Invoke(() =>
            {
                AddMessage("System", "Disconnected from signal server", false);
            });

            try
            {
                await _signal.JoinAsync();
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

        private void ToggleMic_Click(object sender, RoutedEventArgs e)
        {
            _micMuted = !_micMuted;
            MicButton.Content = _micMuted ? "Mic Off" : "Mic On";
            MicButton.Background = _micMuted ? new SolidColorBrush(Color.FromRgb(0xc0, 0x39, 0x2b)) : new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        }

        private void ToggleCam_Click(object sender, RoutedEventArgs e)
        {
            _camMuted = !_camMuted;
            CamButton.Content = _camMuted ? "Cam Off" : "Cam On";
            CamButton.Background = _camMuted ? new SolidColorBrush(Color.FromRgb(0xc0, 0x39, 0x2b)) : new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        }

        private async void LeaveRoom_Click(object sender, RoutedEventArgs e)
        {
            if (_signal != null)
            {
                await _signal.LeaveAsync();
                _signal.Dispose();
                _signal = null;
            }

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
        {
            var bytes = Encoding.UTF8.GetBytes(roomName.Trim().ToLowerInvariant());
            var hash = SHA1.HashData(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
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

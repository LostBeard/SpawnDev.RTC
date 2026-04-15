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
        private RTCSignalClient? _signal;
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

            _signal = new RTCSignalClient($"{signalBase}/signal/{infoHash}", config);

            _signal.OnPeerConnectionCreated = async (pc, peerId) =>
            {
                var dc = pc.CreateDataChannel("chat");
                _chatChannels[peerId] = dc;

                dc.OnOpen += () => Dispatcher.Invoke(() =>
                {
                    AddMessage("System", $"{peerId[..6]} connected", false);
                    UpdatePeerStatus(peerId, "connected");
                });

                dc.OnStringMessage += msg => Dispatcher.Invoke(() =>
                {
                    AddMessage(peerId[..6], msg, false);
                });

                dc.OnClose += () => Dispatcher.Invoke(() =>
                {
                    UpdatePeerStatus(peerId, "disconnected");
                });

                await Task.CompletedTask;
            };

            _signal.OnPeerConnection += (pc, peerId) =>
            {
                Dispatcher.Invoke(() =>
                {
                    _peers.Add(new PeerInfo
                    {
                        PeerId = peerId,
                        DisplayName = peerId[..6],
                        Status = "connecting...",
                    });
                    UpdateSubtitle();
                });
            };

            _signal.OnDataChannel += (channel, peerId) =>
            {
                _chatChannels[peerId] = channel;
                channel.OnStringMessage += msg => Dispatcher.Invoke(() =>
                {
                    AddMessage(peerId[..6], msg, false);
                });
            };

            _signal.OnPeerDisconnected += peerId =>
            {
                Dispatcher.Invoke(() =>
                {
                    _chatChannels.Remove(peerId);
                    var peer = _peers.FirstOrDefault(p => p.PeerId == peerId);
                    if (peer != null) _peers.Remove(peer);
                    AddMessage("System", $"{peerId[..6]} left", false);
                    UpdateSubtitle();
                });
            };

            _signal.OnConnected += () => Dispatcher.Invoke(() =>
            {
                RoomSubtitle.Text = $"Swarm: {infoHash[..12]}... - Connected";
                AddMessage("System", "Connected to signal server", false);
            });

            _signal.OnDisconnected += () => Dispatcher.Invoke(() =>
            {
                AddMessage("System", "Disconnected from signal server", false);
            });

            try
            {
                await _signal.ConnectAsync();
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
                await _signal.DisconnectAsync();
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

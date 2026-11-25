using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using demo;
using AgingTest.Models;

namespace AgingTest.Views
{
    /// <summary>
    /// ZLG CANFDNET-800U 连接页。
    /// 每台 CAN 盒只暴露两个可编辑项：IP 地址、起始端口（该盒通道0的端口，通道 n = 起始 + n）。
    /// 「连接」按钮内部完成：打开设备 → 下发 8 通道的 work_mode/ip/work_port → InitCAN → StartCAN。
    /// </summary>
    public partial class UC_CanZlg800H : UserControl
    {
        private readonly List<CanCardItem> _cards = new();
        private readonly string _jsonConfigPath = Path.Combine(Environment.CurrentDirectory, "Config.json");
        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions { WriteIndented = true };

        private readonly Dictionary<int, TextBox> _txtIp = new();
        private readonly Dictionary<int, TextBox> _txtPort = new();
        private readonly Dictionary<int, TextBlock> _txtRange = new();
        private readonly Dictionary<int, TextBlock> _txtStatus = new();
        private readonly Dictionary<int, Button> _btnDisconnect = new();

        private static readonly Brush BrushOn = new SolidColorBrush(Color.FromRgb(0x28, 0xA7, 0x45));
        private static readonly Brush BrushOff = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
        private static readonly Brush BrushErr = new SolidColorBrush(Color.FromRgb(0xDC, 0x35, 0x45));

        public UC_CanZlg800H()
        {
            InitializeComponent();
            BuildRows();
            LoadConfigFromJson();
            RefreshAll();
            CanComm.StateChanged += OnCanStateChanged;
        }

        private void OnCanStateChanged()
        {
            if (Dispatcher.CheckAccess()) RefreshAll();
            else Dispatcher.Invoke(RefreshAll);
        }

        // ================================================================
        // 界面：每台 CAN 盒一行（IP + 端口 + 连接/断开 + 状态）
        // ================================================================
        private void BuildRows()
        {
            for (int i = 0; i < CanComm.CardCount; i++)
            {
                int canIndex = i + 1;
                var card = new CanCardItem
                {
                    CanIndex = canIndex,
                    DeviceType = "CANFDNET-800U-TCP",
                    DeviceIndex = i,
                    IpAddress = $"192.168.0.{9 + canIndex}",   // 默认 192.168.0.10 ~ .15（6 台）
                    BasePort = 4001
                };
                card.EnsureChannels();
                _cards.Add(card);

                var box = new Border
                {
                    Background = Brushes.White,
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xD7, 0xE3)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(12, 8, 12, 8),
                    Margin = new Thickness(0, 0, 0, 8)
                };

                var row = new StackPanel { Orientation = Orientation.Horizontal };

                row.Children.Add(new TextBlock
                {
                    Text = $"CAN{canIndex}",
                    Width = 58,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                });

                row.Children.Add(new TextBlock
                {
                    Text = "IP 地址",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63)),
                    VerticalAlignment = VerticalAlignment.Center
                });

                var txtIp = new TextBox
                {
                    Width = 150,
                    Height = 26,
                    FontSize = 12,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Text = card.IpAddress
                };
                _txtIp[canIndex] = txtIp;
                row.Children.Add(txtIp);

                row.Children.Add(new TextBlock
                {
                    Text = "端口号",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(14, 0, 0, 0)
                });

                var txtPort = new TextBox
                {
                    Width = 70,
                    Height = 26,
                    FontSize = 12,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Text = card.BasePort.ToString()
                };
                txtPort.TextChanged += (s, e) => UpdateRangeText(canIndex);
                _txtPort[canIndex] = txtPort;
                row.Children.Add(txtPort);

                var txtRange = new TextBlock
                {
                    Text = RangeText(card.BasePort),
                    Width = 150,
                    FontSize = 11,
                    Foreground = BrushOff,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0)
                };
                _txtRange[canIndex] = txtRange;
                row.Children.Add(txtRange);

                var btnConnect = new Button
                {
                    Content = "连接",
                    Width = 70,
                    Height = 26,
                    FontSize = 12,
                    Margin = new Thickness(12, 0, 0, 0),
                    Background = new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xED)),
                    Foreground = Brushes.White,
                    BorderThickness = new Thickness(0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Tag = canIndex
                };
                btnConnect.Click += BtnConnect_Click;
                row.Children.Add(btnConnect);

                var btnDisconnect = new Button
                {
                    Content = "断开",
                    Width = 70,
                    Height = 26,
                    FontSize = 12,
                    Margin = new Thickness(8, 0, 0, 0),
                    Background = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0)),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    IsEnabled = false,
                    Tag = canIndex
                };
                btnDisconnect.Click += BtnDisconnect_Click;
                _btnDisconnect[canIndex] = btnDisconnect;
                row.Children.Add(btnDisconnect);

                var txtStatus = new TextBlock
                {
                    Text = "未连接",
                    FontSize = 12,
                    Foreground = BrushOff,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(12, 0, 0, 0)
                };
                _txtStatus[canIndex] = txtStatus;
                row.Children.Add(txtStatus);

                box.Child = row;
                WrapCanCards.Children.Add(box);
            }
        }

        private static string RangeText(int basePort)
        {
            if (basePort < 1 || basePort + CanComm.ChannelCount - 1 > 65535) return "端口范围无效";
            return $"通道端口 {basePort}~{basePort + CanComm.ChannelCount - 1}";
        }

        private void UpdateRangeText(int canIndex)
        {
            if (!_txtRange.TryGetValue(canIndex, out var tb)) return;
            int p;
            tb.Text = int.TryParse(_txtPort[canIndex].Text.Trim(), out p) ? RangeText(p) : "端口范围无效";
        }

        /// <summary>刷新连接状态、断开按钮可用性。</summary>
        private void RefreshAll()
        {
            foreach (var card in _cards)
            {
                var rt = CanComm.GetCard(card.CanIndex);
                bool online = rt?.IsOnline == true;

                if (_btnDisconnect.TryGetValue(card.CanIndex, out var btn)) btn.IsEnabled = online;
                if (_txtStatus.TryGetValue(card.CanIndex, out var st))
                {
                    if (online)
                    {
                        st.Text = $"● 已连接 {rt!.StartedCount}/{CanComm.ChannelCount} 通道";
                        st.Foreground = rt.StartedCount == CanComm.ChannelCount ? BrushOn : BrushErr;
                    }
                    else
                    {
                        st.Text = "○ 未连接";
                        st.Foreground = BrushOff;
                    }
                }
                if (_txtRange.TryGetValue(card.CanIndex, out var range))
                {
                    int p;
                    range.Text = int.TryParse(_txtPort[card.CanIndex].Text.Trim(), out p) ? RangeText(p) : "端口范围无效";
                }
            }
        }

        // ================================================================
        // 连接 / 断开（所有初始化细节都在 CanComm.ConnectCard 内）
        // ================================================================
        private void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not int canIndex) return;
            var card = _cards.FirstOrDefault(c => c.CanIndex == canIndex);
            if (card == null) return;

            string ip = _txtIp[canIndex].Text.Trim();
            int basePort;
            if (!int.TryParse(_txtPort[canIndex].Text.Trim(), out basePort) || basePort < 1 ||
                basePort + CanComm.ChannelCount - 1 > 65535)
            {
                MessageBox.Show($"端口号需在 1~{65535 - CanComm.ChannelCount + 1} 之间（8 个通道连续占用）", "参数错误");
                return;
            }

            bool isCanFd = CmbProtocol.SelectedIndex == 1;
            int canFdAcc = isCanFd && CmbFdAcc.SelectedIndex == 0 ? 1 : 0;

            btn.IsEnabled = false;
            try
            {
                var (ok, msg) = CanComm.ConnectCard(canIndex, ip, basePort,
                                                    ZlgCan.ZCAN_CANFDNET_800U_TCP, isCanFd, canFdAcc);

                // 连接参数写回配置：IP、起始端口，并重建 8 通道
                card.IpAddress = ip;
                card.BasePort = basePort;
                card.IsCanFd = isCanFd;
                card.CanFdAcc = canFdAcc;
                card.Channels = new List<CanChannelItem>();
                card.EnsureChannels();
                SaveCardConfig(card);

                if (_txtStatus.TryGetValue(canIndex, out var st))
                {
                    st.Text = (ok ? "● " : "○ ") + msg;
                    st.Foreground = ok ? BrushOn : BrushErr;
                }
                if (!ok) MessageBox.Show($"CAN{canIndex} {msg}", "连接失败");
            }
            finally
            {
                btn.IsEnabled = true;
                RefreshAll();
            }
        }

        private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not int canIndex) return;
            CanComm.CloseCard(canIndex);
            if (_txtStatus.TryGetValue(canIndex, out var st))
            {
                st.Text = "○ 已断开";
                st.Foreground = BrushOff;
            }
            RefreshAll();
        }

        private void CmbProtocol_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbFdAcc != null) CmbFdAcc.IsEnabled = CmbProtocol.SelectedIndex == 1;
        }

        // ================================================================
        // 配置读写（统一 Config.json，保护 GjdaDevices / Comm 分区）
        // ================================================================
        private void SaveCardConfig(CanCardItem card)
        {
            try
            {
                Config root = File.Exists(_jsonConfigPath)
                    ? JsonSerializer.Deserialize<Config>(File.ReadAllText(_jsonConfigPath), _jsonOptions) ?? new Config()
                    : new Config();

                var exist = root.CanCardList.FirstOrDefault(x => x.CanIndex == card.CanIndex);
                if (exist == null)
                {
                    root.CanCardList.Add(card);
                }
                else
                {
                    exist.DeviceType = card.DeviceType;
                    exist.DeviceIndex = card.DeviceIndex;
                    exist.IpAddress = card.IpAddress;
                    exist.BasePort = card.BasePort;
                    exist.WorkMode = card.WorkMode;
                    exist.IsCanFd = card.IsCanFd;
                    exist.CanFdAcc = card.CanFdAcc;
                    exist.Channels = card.Channels;
                }

                foreach (var item in root.CanCardList) item.IsConnected = false;
                File.WriteAllText(_jsonConfigPath, JsonSerializer.Serialize(root, _jsonOptions));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存配置失败：{ex.Message}", "警告");
            }
        }

        private void LoadConfigFromJson()
        {
            if (!File.Exists(_jsonConfigPath)) return;
            try
            {
                var root = JsonSerializer.Deserialize<Config>(File.ReadAllText(_jsonConfigPath), _jsonOptions);
                if (root?.CanCardList == null) return;

                foreach (var saved in root.CanCardList)
                {
                    var card = _cards.FirstOrDefault(c => c.CanIndex == saved.CanIndex);
                    if (card == null) continue;

                    card.IpAddress = string.IsNullOrWhiteSpace(saved.IpAddress) ? card.IpAddress : saved.IpAddress;
                    // 旧配置无 BasePort 时，从首个通道端口推断，否则用 4001
                    card.BasePort = saved.BasePort > 0
                        ? saved.BasePort
                        : (saved.Channels?.FirstOrDefault()?.WorkPort ?? 4001);
                    card.IsCanFd = saved.IsCanFd;
                    card.CanFdAcc = saved.CanFdAcc;
                    card.DeviceType = "CANFDNET-800U-TCP";
                    card.DeviceIndex = card.CanIndex - 1;
                    card.Channels = new List<CanChannelItem>();
                    card.EnsureChannels();

                    if (_txtIp.TryGetValue(card.CanIndex, out var tIp)) tIp.Text = card.IpAddress;
                    if (_txtPort.TryGetValue(card.CanIndex, out var tPort)) tPort.Text = card.BasePort.ToString();
                    if (_txtRange.TryGetValue(card.CanIndex, out var tRange)) tRange.Text = RangeText(card.BasePort);
                }

                // 协议取第一个已保存设备的设置（全局共用）
                var first = root.CanCardList.FirstOrDefault();
                if (first != null)
                {
                    CmbProtocol.SelectedIndex = first.IsCanFd ? 1 : 0;
                    CmbFdAcc.SelectedIndex = first.CanFdAcc == 1 ? 0 : 1;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载CAN配置失败：{ex.Message}", "警告");
            }
        }
    }
}

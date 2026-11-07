using AgingTest.Models;
using System;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AgingTest.Views
{
    /// <summary>
    /// 设备通讯设置窗口：按真实硬件拓扑集中编辑并保存所有设备通讯参数。
    /// 负载2台 / 485总线6路(电阻+继电器共用) / N36100×6 / N36600×6 / CAN卡×6。
    /// 支持单设备连接/断开与连接全部/断开全部；连接句柄统一由 DeviceComm 持有。
    /// </summary>
    public partial class CommunicationSettingsWindow : Window
    {
        private readonly ObservableCollection<string> ComPortList = new();
        private readonly ObservableCollection<int> BaudList = new() { 9600, 19200, 38400, 57600, 115200 };

        // 负载（1-based，下标0不用）
        private readonly ComboBox[] _loadCom = new ComboBox[DeviceComm.LoadCount + 1];
        private readonly ComboBox[] _loadBaud = new ComboBox[DeviceComm.LoadCount + 1];
        private readonly TextBox[] _loadSlave = new TextBox[DeviceComm.LoadCount + 1];
        private readonly TextBlock[] _loadState = new TextBlock[DeviceComm.LoadCount + 1];

        // 485 总线（1-based）
        private readonly ComboBox[] _rrCom = new ComboBox[DeviceComm.RrPortCount + 1];
        private readonly ComboBox[] _rrBaud = new ComboBox[DeviceComm.RrPortCount + 1];
        private readonly TextBlock[] _rrState = new TextBlock[DeviceComm.RrPortCount + 1];

        // 电源（1-based）
        private readonly TextBox[] _n36100Ip = new TextBox[DeviceComm.N36100Count + 1];
        private readonly TextBox[] _n36100Port = new TextBox[DeviceComm.N36100Count + 1];
        private readonly TextBlock[] _n36100State = new TextBlock[DeviceComm.N36100Count + 1];
        private readonly TextBox[] _n36600Ip = new TextBox[DeviceComm.N36600Count + 1];
        private readonly TextBox[] _n36600Port = new TextBox[DeviceComm.N36600Count + 1];
        private readonly TextBlock[] _n36600State = new TextBlock[DeviceComm.N36600Count + 1];

        public CommunicationSettingsWindow()
        {
            InitializeComponent();
            DeviceComm.LoadSettings();
            DeviceComm.StateChanged += DeviceComm_StateChanged;

            RefreshComList();
            BuildRows();
            LoadConfigToUI();
            RefreshStates();
            Closed += (_, _) => DeviceComm.StateChanged -= DeviceComm_StateChanged;
        }

        private void DeviceComm_StateChanged()
        {
            if (Dispatcher.CheckAccess()) RefreshStates();
            else Dispatcher.Invoke(RefreshStates);
        }

        private void RefreshComList()
        {
            ComPortList.Clear();
            foreach (var p in SerialPort.GetPortNames()) ComPortList.Add(p);
        }

        // ==================== 程序化生成配置行 ====================
        private void BuildRows()
        {
            // 负载
            for (int i = 1; i <= DeviceComm.LoadCount; i++)
            {
                var com = new ComboBox { Style = (Style)FindResource("InputCombo"), ItemsSource = ComPortList };
                var baud = new ComboBox { Style = (Style)FindResource("InputCombo"), ItemsSource = BaudList };
                var slave = new TextBox { Style = (Style)FindResource("InputTextBox"), Width = 52 };
                var state = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new Thickness(14, 0, 0, 0) };
                _loadCom[i] = com; _loadBaud[i] = baud; _loadSlave[i] = slave; _loadState[i] = state;

                var btnConn = MakeButton("连接", "SuccessButton", i, BtnLoadConnect_Click);
                var btnDisc = MakeButton("断开", "DangerButton", i, BtnLoadDisconnect_Click);
                LoadPanel.Children.Add(BuildLabeledRow($"负载{i}", com, baud, slave, btnConn, btnDisc, state));
            }

            // 485 总线
            for (int i = 1; i <= DeviceComm.RrPortCount; i++)
            {
                var com = new ComboBox { Style = (Style)FindResource("InputCombo"), ItemsSource = ComPortList };
                var baud = new ComboBox { Style = (Style)FindResource("InputCombo"), ItemsSource = BaudList };
                var state = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new Thickness(14, 0, 0, 0) };
                _rrCom[i] = com; _rrBaud[i] = baud; _rrState[i] = state;

                var btnConn = MakeButton("连接", "SuccessButton", i, BtnRrConnect_Click);
                var btnDisc = MakeButton("断开", "DangerButton", i, BtnRrDisconnect_Click);
                RrPanel.Children.Add(BuildLabeledRow($"485-{i}", com, baud, null, btnConn, btnDisc, state));
            }

            // 大电流电源
            for (int i = 1; i <= DeviceComm.N36100Count; i++)
            {
                var ip = new TextBox { Style = (Style)FindResource("InputTextBox"), Width = 150 };
                var port = new TextBox { Style = (Style)FindResource("InputTextBox"), Width = 70 };
                var state = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new Thickness(14, 0, 0, 0) };
                _n36100Ip[i] = ip; _n36100Port[i] = port; _n36100State[i] = state;

                var btnConn = MakeButton("连接", "SuccessButton", i, BtnN36100Connect_Click);
                var btnDisc = MakeButton("断开", "DangerButton", i, BtnN36100Disconnect_Click);
                N36100Panel.Children.Add(BuildNetRow($"N36100-{i}", ip, port, btnConn, btnDisc, state));
            }

            // 高精度电源
            for (int i = 1; i <= DeviceComm.N36600Count; i++)
            {
                var ip = new TextBox { Style = (Style)FindResource("InputTextBox"), Width = 150 };
                var port = new TextBox { Style = (Style)FindResource("InputTextBox"), Width = 70 };
                var state = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new Thickness(14, 0, 0, 0) };
                _n36600Ip[i] = ip; _n36600Port[i] = port; _n36600State[i] = state;

                var btnConn = MakeButton("连接", "SuccessButton", i, BtnN36600Connect_Click);
                var btnDisc = MakeButton("断开", "DangerButton", i, BtnN36600Disconnect_Click);
                N36600Panel.Children.Add(BuildNetRow($"N36600-{i}", ip, port, btnConn, btnDisc, state));
            }
        }

        private Button MakeButton(string content, string styleKey, int tag, RoutedEventHandler handler)
        {
            var b = new Button
            {
                Content = content,
                Style = (Style)FindResource(styleKey),
                Tag = tag,
                Margin = new Thickness(6, 0, 0, 0)
            };
            b.Click += handler;
            return b;
        }

        /// <summary>串口类行：标签 + 串口 + 波特率(+从站) + 连接/断开 + 状态。</summary>
        private static StackPanel BuildLabeledRow(string label, ComboBox com, ComboBox baud, TextBox? slave,
            Button btnConn, Button btnDisc, TextBlock state)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            sp.Children.Add(new TextBlock { Text = label + "  串口:", VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Width = 78 });
            sp.Children.Add(com);
            sp.Children.Add(new TextBlock { Text = "波特率:", VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new Thickness(10, 0, 0, 0) });
            sp.Children.Add(baud);
            if (slave != null)
            {
                sp.Children.Add(new TextBlock { Text = "从站:", VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new Thickness(10, 0, 0, 0) });
                sp.Children.Add(slave);
            }
            sp.Children.Add(btnConn);
            sp.Children.Add(btnDisc);
            sp.Children.Add(state);
            return sp;
        }

        /// <summary>网口类行：标签 + IP + 端口 + 连接/断开 + 状态。</summary>
        private static StackPanel BuildNetRow(string label, TextBox ip, TextBox port, Button btnConn, Button btnDisc, TextBlock state)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            sp.Children.Add(new TextBlock { Text = label + "  IP:", VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Width = 90 });
            sp.Children.Add(ip);
            sp.Children.Add(new TextBlock { Text = "端口:", VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new Thickness(10, 0, 0, 0) });
            sp.Children.Add(port);
            sp.Children.Add(btnConn);
            sp.Children.Add(btnDisc);
            sp.Children.Add(state);
            return sp;
        }

        // ==================== 回填配置 ====================
        private void LoadConfigToUI()
        {
            for (int i = 1; i <= DeviceComm.LoadCount; i++)
            {
                var rt = DeviceComm.Loads.FirstOrDefault(x => x.Cfg.DevId == i);
                if (rt == null) continue;
                _loadCom[i].Text = rt.Cfg.ComPort;
                _loadBaud[i].Text = rt.Cfg.BaudRate.ToString();
                _loadSlave[i].Text = rt.Cfg.SlaveAddr.ToString();
            }
            for (int i = 1; i <= DeviceComm.RrPortCount; i++)
            {
                _rrCom[i].Text = DeviceComm.RrPorts[i - 1].Cfg.ComPort;
                _rrBaud[i].Text = DeviceComm.RrPorts[i - 1].Cfg.Baud.ToString();
            }
            for (int i = 1; i <= DeviceComm.N36100Count; i++)
            {
                _n36100Ip[i].Text = DeviceComm.N36100[i - 1].Cfg.Ip;
                _n36100Port[i].Text = DeviceComm.N36100[i - 1].Cfg.Port.ToString();
            }
            for (int i = 1; i <= DeviceComm.N36600Count; i++)
            {
                _n36600Ip[i].Text = DeviceComm.N36600[i - 1].Cfg.Ip;
                _n36600Port[i].Text = DeviceComm.N36600[i - 1].Cfg.Port.ToString();
            }
        }

        // ==================== 保存 ====================
        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                for (int i = 1; i <= DeviceComm.LoadCount; i++)
                {
                    var rt = DeviceComm.Loads.FirstOrDefault(x => x.Cfg.DevId == i);
                    if (rt == null) continue;
                    rt.Cfg.ComPort = _loadCom[i].Text.Trim();
                    if (int.TryParse(_loadBaud[i].Text, out int b)) rt.Cfg.BaudRate = b;
                    if (byte.TryParse(_loadSlave[i].Text, out byte sa)) rt.Cfg.SlaveAddr = sa;
                }
                for (int i = 1; i <= DeviceComm.RrPortCount; i++)
                {
                    var cfg = DeviceComm.RrPorts[i - 1].Cfg;
                    cfg.ComPort = _rrCom[i].Text.Trim();
                    if (int.TryParse(_rrBaud[i].Text, out int b)) cfg.Baud = b;
                }
                for (int i = 1; i <= DeviceComm.N36100Count; i++)
                {
                    var cfg = DeviceComm.N36100[i - 1].Cfg;
                    cfg.Ip = _n36100Ip[i].Text.Trim();
                    if (int.TryParse(_n36100Port[i].Text, out int p)) cfg.Port = p;
                }
                for (int i = 1; i <= DeviceComm.N36600Count; i++)
                {
                    var cfg = DeviceComm.N36600[i - 1].Cfg;
                    cfg.Ip = _n36600Ip[i].Text.Trim();
                    if (int.TryParse(_n36600Port[i].Text, out int p)) cfg.Port = p;
                }
                DeviceComm.SaveCommSettings();
                MessageBox.Show("通讯配置已保存到 Config.json", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ==================== 连接/断开 ====================
        private void BtnLoadConnect_Click(object sender, RoutedEventArgs e)
        {
            int id = (int)((Button)sender).Tag;
            var err = DeviceComm.ConnectLoad(id);
            if (err != null) MessageBox.Show(err, "连接失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            else RefreshStates();
        }
        private void BtnLoadDisconnect_Click(object sender, RoutedEventArgs e)
            => DeviceComm.DisconnectLoad((int)((Button)sender).Tag);

        private void BtnRrConnect_Click(object sender, RoutedEventArgs e)
        {
            int idx = (int)((Button)sender).Tag;
            var err = DeviceComm.ConnectRrPort(idx);
            if (err != null) MessageBox.Show(err, "连接失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            else RefreshStates();
        }
        private void BtnRrDisconnect_Click(object sender, RoutedEventArgs e)
            => DeviceComm.DisconnectRrPort((int)((Button)sender).Tag);

        private void BtnN36100Connect_Click(object sender, RoutedEventArgs e)
        {
            int idx = (int)((Button)sender).Tag;
            var err = DeviceComm.ConnectN36100(idx);
            if (err != null) MessageBox.Show(err, "连接失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            else RefreshStates();
        }
        private void BtnN36100Disconnect_Click(object sender, RoutedEventArgs e)
            => DeviceComm.DisconnectN36100((int)((Button)sender).Tag);

        private void BtnN36600Connect_Click(object sender, RoutedEventArgs e)
        {
            int idx = (int)((Button)sender).Tag;
            var err = DeviceComm.ConnectN36600(idx);
            if (err != null) MessageBox.Show(err, "连接失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            else RefreshStates();
        }
        private void BtnN36600Disconnect_Click(object sender, RoutedEventArgs e)
            => DeviceComm.DisconnectN36600((int)((Button)sender).Tag);

        // ==================== 批量 ====================
        private void BtnConnectAll_Click(object sender, RoutedEventArgs e)
        {
            var errors = DeviceComm.ConnectAll();
            RefreshStates();
            if (errors.Count > 0)
                MessageBox.Show("部分设备连接失败：\n" + string.Join("\n", errors), "连接结果", MessageBoxButton.OK, MessageBoxImage.Warning);
            else
                MessageBox.Show("全部设备已连接", "连接成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnDisconnectAll_Click(object sender, RoutedEventArgs e)
        {
            DeviceComm.DisconnectAll();
            RefreshStates();
        }

        // ==================== 状态刷新 ====================
        private void RefreshStates()
        {
            for (int i = 1; i <= DeviceComm.LoadCount; i++)
            {
                var rt = DeviceComm.Loads.FirstOrDefault(x => x.Cfg.DevId == i);
                SetStateText(_loadState[i], rt?.Connected == true);
            }
            for (int i = 1; i <= DeviceComm.RrPortCount; i++)
                SetStateText(_rrState[i], DeviceComm.RrPorts[i - 1].Connected);
            for (int i = 1; i <= DeviceComm.N36100Count; i++)
                SetStateText(_n36100State[i], DeviceComm.N36100[i - 1].Connected);
            for (int i = 1; i <= DeviceComm.N36600Count; i++)
                SetStateText(_n36600State[i], DeviceComm.N36600[i - 1].Connected);
        }

        private static void SetStateText(TextBlock tb, bool connected)
        {
            if (tb == null) return;
            tb.Text = connected ? "已连接" : "未连接";
            tb.Foreground = connected
                ? new SolidColorBrush(Color.FromRgb(0x28, 0xA7, 0x45))
                : new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        }
    }
}

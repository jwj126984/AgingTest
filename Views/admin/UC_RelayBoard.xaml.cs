using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AgingTest.Models;

namespace AgingTest.Views
{
    /// <summary>
    /// 继电器板 3 10A-24 控制（Modbus RTU）。
    /// 硬件拓扑：6 个 485 总线，每总线挂 4 块继电器板（地址17~20）+ 4 块电阻卡（地址1~4）。
    /// 本页通过“485列 + 地址”选择 24 块继电器板中的一块；
    /// 24 路继电器寄存器 0x0700~0x0717（各 1 bit）：FC05 单路开/关、FC0F 批量/全关。
    /// 软件 PWM：0x0720(PWM1)/0x0722(PWM2)，uint32=[频率Hz, 占空比×100]；回采 0x0728/0x072A。
    /// 设备状态 0x07F0(uint16)、固件版本 0x07F1(uint32)。
    /// 连接由 DeviceComm 统一管理（通讯设置窗口/主界面操作）。
    /// </summary>
    public partial class UC_RelayBoard : UserControl
    {
        private int PortIndex => (int)CmbCol.SelectedItem;
        /// <summary>从站地址：可编辑，解析失败时回退默认 17。</summary>
        private int Addr => int.TryParse(CmbAddr.Text.Trim(), out var a) ? a : DeviceComm.RelayAddrBase;

        private readonly List<ToggleButton> _relays = new();

        public UC_RelayBoard()
        {
            InitializeComponent();
            CmbCol.ItemsSource = Enumerable.Range(1, DeviceComm.RrPortCount).ToList();
            CmbAddr.ItemsSource = Enumerable.Range(DeviceComm.RelayAddrBase, DeviceComm.RelayAddrCount).ToList();
            CmbCol.SelectedIndex = 0;
            CmbAddr.SelectedIndex = 0;
            CmbCol.SelectionChanged += (_, _) => RefreshConnState();
            CmbAddr.SelectionChanged += (_, _) => RefreshConnState();
            BuildRelays();
            DeviceComm.StateChanged += DeviceComm_StateChanged;
            RefreshConnState();
        }

        private void DeviceComm_StateChanged()
        {
            if (Dispatcher.CheckAccess()) RefreshConnState();
            else Dispatcher.Invoke(RefreshConnState);
        }

        /// <summary>刷新所选485列通讯状态（只读显示，不在此处连接）。</summary>
        private void RefreshConnState()
        {
            bool conn = CmbCol.SelectedItem != null && DeviceComm.RrPorts[PortIndex - 1].Connected;
            TxtConnState.Text = conn ? "已连接" : "未连接";
            TxtConnState.Foreground = conn
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x28, 0xA7, 0x45))
                : System.Windows.Media.Brushes.Gray;
        }

        private void BuildRelays()
        {
            for (int i = 0; i < 24; i++)
            {
                int idx = i;
                var tb = new ToggleButton
                {
                    Content = $"CTL{i + 1}",
                    Style = (Style)FindResource("RelayToggle")
                };
                tb.Checked += (_, _) => RelayChanged(idx, true);
                tb.Unchecked += (_, _) => RelayChanged(idx, false);
                RelayPanel.Children.Add(tb);
                _relays.Add(tb);
            }
        }

        private void Log(string msg)
        {
            LstLog.Items.Insert(0, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
        }

        private bool EnsureConnected()
        {
            if (!DeviceComm.RrPorts[PortIndex - 1].Connected)
            {
                Log($"485-{PortIndex} 未连接，请先在通讯设置中连接");
                return false;
            }
            return true;
        }

        /// <summary>单路继电器：FC05 写线圈 0x0700+idx（当前所选板卡）。</summary>
        private void RelayChanged(int idx, bool on)
        {
            if (!EnsureConnected()) return;
            try
            {
                ushort coil = (ushort)(0x0700 + idx);
                DeviceComm.RelayMb(PortIndex, Addr).WriteSingleCoil(coil, on);
                Log($"列{PortIndex} 地址{Addr} CTL{idx + 1} → {(on ? "闭合" : "断开")} (FC05 0x{coil:X4})");
                UpdateRelayState();
            }
            catch (Exception ex)
            {
                Log($"CTL{idx + 1} 控制失败：" + ex.Message);
            }
        }

        /// <summary>全开/全关：FC0F 批量写 24 线圈（当前所选板卡）。</summary>
        private void SetAll(bool on)
        {
            if (!EnsureConnected()) return;
            try
            {
                var vals = new bool[24];
                for (int i = 0; i < 24; i++) vals[i] = on;
                DeviceComm.RelayMb(PortIndex, Addr).WriteMultipleCoils(0x0700, vals);
                Log($"列{PortIndex} 地址{Addr} {(on ? "全部闭合" : "全部断开")} (FC0F 0x0700×24)");
                foreach (var t in _relays) t.IsChecked = on;
            }
            catch (Exception ex)
            {
                Log("批量控制失败：" + ex.Message);
            }
        }

        private void BtnAllOn_Click(object sender, RoutedEventArgs e) => SetAll(true);
        private void BtnAllOff_Click(object sender, RoutedEventArgs e) => SetAll(false);

        private void UpdateRelayState()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < _relays.Count; i++)
            {
                if (_relays[i].IsChecked == true) sb.Append($"{i + 1} ");
            }
            TxtRelayState.Text = sb.Length == 0 ? "当前无闭合" : "当前闭合: " + sb.ToString();
        }

        /// <summary>设置 PWM：FC10 写 [频率, 占空比×100] 到 0x0720/0x0722（当前所选板卡）。</summary>
        private void SetPwm(ushort reg, string freqText, string dutyText)
        {
            if (!EnsureConnected()) return;
            try
            {
                ushort freq = ushort.Parse(freqText.Trim());
                ushort duty = (ushort)(double.Parse(dutyText.Trim()) * 100);
                DeviceComm.RelayMb(PortIndex, Addr).WriteRegisters(reg, new[] { freq, duty });
                Log($"列{PortIndex} 地址{Addr} 设置 PWM 0x{reg:X4} = {freq}Hz / {double.Parse(dutyText.Trim()):F1}% (0x{duty:X4})");
            }
            catch (Exception ex)
            {
                Log("设置 PWM 失败：" + ex.Message);
            }
        }

        private void BtnSetPwm1_Click(object sender, RoutedEventArgs e) => SetPwm(0x0720, TxtPwm1Freq.Text, TxtPwm1Duty.Text);
        private void BtnSetPwm2_Click(object sender, RoutedEventArgs e) => SetPwm(0x0722, TxtPwm2Freq.Text, TxtPwm2Duty.Text);

        /// <summary>读回软件 PWM：FC03 读 2 寄存器 [频率, 占空比]。reg=0x0728(IPWM1)/0x072A(IPWM2)。</summary>
        private void ReadIpwm(ushort reg, TextBlock target)
        {
            if (!EnsureConnected()) return;
            try
            {
                var d = DeviceComm.RelayMb(PortIndex, Addr).ReadRegisters(reg, 2);
                target.Text = $"{d[0]}Hz / {(d[1] / 100.0):F1}%";
                Log($"列{PortIndex} 地址{Addr} 回采 0x{reg:X4} = {d[0]}Hz / {(d[1] / 100.0):F1}%");
            }
            catch (Exception ex)
            {
                Log("读取 PWM 回采失败：" + ex.Message);
            }
        }

        private void BtnReadIpwm1_Click(object sender, RoutedEventArgs e) => ReadIpwm(0x0728, TxtIpwm1);
        private void BtnReadIpwm2_Click(object sender, RoutedEventArgs e) => ReadIpwm(0x072A, TxtIpwm2);

        private void BtnReadStatus_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureConnected()) return;
            try
            {
                var v = DeviceComm.RelayMb(PortIndex, Addr).ReadRegisters(0x07F0, 1)[0];
                TxtStatus.Text = v == 0x0001 ? "就绪 (0x0001)" : $"0x{v:X4}";
                Log($"列{PortIndex} 地址{Addr} 设备状态 0x07F0 = 0x{v:X4} {(v == 1 ? "(就绪)" : "")}");
            }
            catch (Exception ex)
            {
                Log("读取状态失败：" + ex.Message);
            }
        }

        private void BtnReadVersion_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureConnected()) return;
            try
            {
                var d = DeviceComm.RelayMb(PortIndex, Addr).ReadRegisters(0x07F1, 2);
                uint ver = (uint)((d[0] << 16) | d[1]);
                TxtStatus.Text = $"固件 V{(ver >> 16) & 0xFF}.{(ver >> 8) & 0xFF}.{ver & 0xFF}";
                Log($"列{PortIndex} 地址{Addr} 固件版本 0x07F1 = 0x{ver:X8}");
            }
            catch (Exception ex)
            {
                Log("读取固件失败：" + ex.Message);
            }
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e) { }

        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            DeviceComm.StateChanged -= DeviceComm_StateChanged;
        }
    }
}

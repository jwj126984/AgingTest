using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AgingTest.Models;

namespace AgingTest.Views
{
    /// <summary>
    /// 电阻模拟卡控制（Modbus RTU）。
    /// 硬件拓扑：6 个 485 总线，每总线挂 4 块电阻卡（地址1~4）+ 4 块继电器板（地址17~20）。
    /// 本页通过“485列 + 地址”选择 24 块电阻卡中的一块；
    /// 8 路电阻按两通道一组共 4 组，每组寄存器写 uint32 电阻值(Ω)：
    /// 组1=0x0200(CH1-2)、组2=0x0202(CH3-4)、组3=0x0204(CH5-6)、组4=0x0206(CH7-8)。
    /// 设备状态 0x02F0(uint16)、固件版本 0x02F1(uint32)。
    /// 连接由 DeviceComm 统一管理（通讯设置窗口/主界面操作）。
    /// </summary>
    public partial class UC_ResistorBoard : UserControl
    {
        private int PortIndex => (int)CmbCol.SelectedItem;
        /// <summary>从站地址：可编辑，解析失败时回退默认 1。</summary>
        private int Addr => int.TryParse(CmbAddr.Text.Trim(), out var a) ? a : DeviceComm.ResistorAddrBase;

        public UC_ResistorBoard()
        {
            InitializeComponent();
            CmbCol.ItemsSource = Enumerable.Range(1, DeviceComm.RrPortCount).ToList();
            CmbAddr.ItemsSource = Enumerable.Range(DeviceComm.ResistorAddrBase, DeviceComm.ResistorAddrCount).ToList();
            CmbCol.SelectedIndex = 0;
            CmbAddr.SelectedIndex = 0;
            CmbCol.SelectionChanged += (_, _) => RefreshConnState();
            CmbAddr.SelectionChanged += (_, _) => RefreshConnState();
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

        private void Log(string msg)
        {
            LstLog.Items.Insert(0, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
        }

        /// <summary>设置当前所选板卡一组电阻：FC10 写 2 个保持寄存器，uint32 大端 = 电阻值(Ω)。</summary>
        private void BtnSetResistor_Click(object sender, RoutedEventArgs e)
        {
            if (!DeviceComm.RrPorts[PortIndex - 1].Connected) { Log($"485-{PortIndex} 未连接，请先在通讯设置中连接"); return; }
            var btn = (Button)sender;
            ushort start = Convert.ToUInt16((string)btn.Tag, 16);
            var tb = btn.Parent is StackPanel sp ? (TextBox)sp.Children[1] : null;
            try
            {
                uint ohms = uint.Parse(tb!.Text.Trim());
                if (ohms > 409500) { Log("电阻值超出上限 409500Ω"); return; }
                var mb = DeviceComm.ResistorMb(PortIndex, Addr);
                mb.WriteRegisters(start, new[] { (ushort)((ohms >> 16) & 0xFFFF), (ushort)(ohms & 0xFFFF) });
                Log($"列{PortIndex} 地址{Addr} 设置 0x{start:X4} = {ohms}Ω");
            }
            catch (Exception ex)
            {
                Log("设置电阻失败：" + ex.Message);
            }
        }

        private void BtnReadStatus_Click(object sender, RoutedEventArgs e)
        {
            if (!DeviceComm.RrPorts[PortIndex - 1].Connected) { Log($"485-{PortIndex} 未连接，请先在通讯设置中连接"); return; }
            try
            {
                var v = DeviceComm.ResistorMb(PortIndex, Addr).ReadRegisters(0x02F0, 1)[0];
                TxtStatus.Text = v == 0x0001 ? "就绪 (0x0001)" : $"0x{v:X4}";
                Log($"列{PortIndex} 地址{Addr} 设备状态 0x02F0 = 0x{v:X4} {(v == 1 ? "(就绪)" : "")}");
            }
            catch (Exception ex)
            {
                Log("读取状态失败：" + ex.Message);
            }
        }

        private void BtnReadVersion_Click(object sender, RoutedEventArgs e)
        {
            if (!DeviceComm.RrPorts[PortIndex - 1].Connected) { Log($"485-{PortIndex} 未连接，请先在通讯设置中连接"); return; }
            try
            {
                var d = DeviceComm.ResistorMb(PortIndex, Addr).ReadRegisters(0x02F1, 2);
                uint ver = (uint)((d[0] << 16) | d[1]);
                TxtStatus.Text = $"固件 V{(ver >> 16) & 0xFF}.{(ver >> 8) & 0xFF}.{ver & 0xFF}";
                Log($"列{PortIndex} 地址{Addr} 固件版本 0x02F1 = 0x{ver:X8}");
            }
            catch (Exception ex)
            {
                Log("读取固件失败：" + ex.Message);
            }
        }

        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            DeviceComm.StateChanged -= DeviceComm_StateChanged;
        }
    }
}

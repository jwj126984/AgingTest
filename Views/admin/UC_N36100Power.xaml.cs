using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AgingTest.Models;

namespace AgingTest.Views
{
    /// <summary>
    /// N36100 系列宽范围可编程直流电源控制（SCPI · 网口 TCP 7000）。
    /// 输出设定(SOUR:VOLT/CURR/INT)、输出开关(OUTP:ONOFF)、普通模式(OUTP:MODE)、
    /// 回读(MEAS:VOLT?/CURR?/POW?)、保护(PROT:VOLT/CURR/POW)、告警(OUTP:EVENt)。
    /// 通讯连接由 DeviceComm 统一管理（通讯设置窗口/主界面操作）。
    /// </summary>
    public partial class UC_N36100Power : UserControl
    {
        private int Index => (int)CmbDev.SelectedItem;
        private N36100PowerClient Pwr => DeviceComm.N36100[Index - 1].Client;

        public UC_N36100Power()
        {
            InitializeComponent();
            CmbDev.ItemsSource = Enumerable.Range(1, DeviceComm.N36100Count).ToList();
            CmbDev.SelectedIndex = 0;
            CmbDev.SelectionChanged += (_, _) => RefreshConnState();
            // 回读改为按钮手动触发，不在 UI 线程定时同步 TCP 查询（避免界面卡顿）
            DeviceComm.StateChanged += DeviceComm_StateChanged;
            RefreshConnState();
        }

        private void DeviceComm_StateChanged()
        {
            if (Dispatcher.CheckAccess()) RefreshConnState();
            else Dispatcher.Invoke(RefreshConnState);
        }

        /// <summary>刷新通讯状态（只读显示，不在此处连接）。</summary>
        private void RefreshConnState()
        {
            TxtConnState.Text = Pwr.IsConnected ? "已连接" : "未连接";
            TxtConnState.Foreground = Pwr.IsConnected
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x28, 0xA7, 0x45))
                : System.Windows.Media.Brushes.Gray;
        }

        private void Log(string msg)
        {
            LstLog.Items.Insert(0, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
        }

        private void SetParam(Func<string> getCmd, Action<double> setter, string name)
        {
            if (!Pwr.IsConnected) { Log("未连接，请先在通讯设置中连接"); return; }
            try { setter(double.Parse(getCmd().Trim())); Log($"设定{name} 完成"); }
            catch (Exception ex) { Log($"设定{name}失败：" + ex.Message); }
        }

        private void BtnSetVolt_Click(object sender, RoutedEventArgs e)
            => SetParam(() => TxtVolt.Text, Pwr.SetVoltage, "输出电压");
        private void BtnSetCurr_Click(object sender, RoutedEventArgs e)
            => SetParam(() => TxtCurr.Text, Pwr.SetCurrent, "限流电流");
        private void BtnSetRes_Click(object sender, RoutedEventArgs e)
            => SetParam(() => TxtRes.Text, Pwr.SetInternalRes, "模拟内阻");
        private void BtnSetVolRt_Click(object sender, RoutedEventArgs e)
            => SetParam(() => TxtVolRt.Text, v => Pwr.SetVolRisetime((int)v), "电压上升时间");
        private void BtnSetCurRt_Click(object sender, RoutedEventArgs e)
            => SetParam(() => TxtCurRt.Text, v => Pwr.SetCurRisetime((int)v), "电流上升时间");

        private void BtnOutOn_Click(object sender, RoutedEventArgs e)
        {
            if (!Pwr.IsConnected) { Log("未连接"); return; }
            try { Pwr.SetOutputOnOff(true); Log("输出已开启"); } catch (Exception ex) { Log("开启输出失败：" + ex.Message); }
        }

        private void BtnOutOff_Click(object sender, RoutedEventArgs e)
        {
            if (!Pwr.IsConnected) { Log("未连接"); return; }
            try { Pwr.SetOutputOnOff(false); Log("输出已关闭"); } catch (Exception ex) { Log("关闭输出失败：" + ex.Message); }
        }

        private void BtnModeNormal_Click(object sender, RoutedEventArgs e)
        {
            if (!Pwr.IsConnected) { Log("未连接"); return; }
            try { Pwr.SetMode("NORMAL"); Log("已设为普通模式"); } catch (Exception ex) { Log("设模式失败：" + ex.Message); }
        }

        private void RefreshMeasure()
        {
            try
            {
                var v = Pwr.MeasVoltage();
                var a = Pwr.MeasCurrent();
                var w = Pwr.MeasPower();
                TxtMeasV.Text = double.IsNaN(v) ? "—" : v.ToString("0.000");
                TxtMeasA.Text = double.IsNaN(a) ? "—" : a.ToString("0.000");
                TxtMeasW.Text = double.IsNaN(w) ? "—" : w.ToString("0.000");
            }
            catch { }
        }

        private void BtnMeasure_Click(object sender, RoutedEventArgs e)
        {
            if (!Pwr.IsConnected) { Log("未连接"); return; }
            try
            {
                RefreshMeasure();
                Log($"回读 V={TxtMeasV.Text} A={TxtMeasA.Text} W={TxtMeasW.Text}");
            }
            catch (Exception ex) { Log("回读失败：" + ex.Message); }
        }

        private void BtnOutState_Click(object sender, RoutedEventArgs e)
        {
            if (!Pwr.IsConnected) { Log("未连接"); return; }
            try { Log("输出状态 → " + Pwr.GetOutputState()); } catch (Exception ex) { Log("读取输出状态失败：" + ex.Message); }
        }

        private void BtnSetOvp_Click(object sender, RoutedEventArgs e)
            => SetParam(() => TxtOvp.Text, Pwr.SetOvp, "过压保护");
        private void BtnSetOcp_Click(object sender, RoutedEventArgs e)
            => SetParam(() => TxtOcp.Text, Pwr.SetOcp, "过流保护");
        private void BtnSetOpp_Click(object sender, RoutedEventArgs e)
            => SetParam(() => TxtOpp.Text, Pwr.SetOpp, "过功率保护");

        private void BtnAlarm_Click(object sender, RoutedEventArgs e)
        {
            if (!Pwr.IsConnected) { Log("未连接"); return; }
            try
            {
                int ev = int.TryParse(Pwr.GetAlarm(), out var v) ? v : -1;
                var msg = new System.Text.StringBuilder();
                if ((ev & 2) != 0) msg.Append("OVP ");
                if ((ev & 4) != 0) msg.Append("OCP ");
                if ((ev & 8) != 0) msg.Append("OPP ");
                if ((ev & 16) != 0) msg.Append("OTP");
                string desc = ev < 0 ? "无(或不可解析)" : (msg.Length == 0 ? "无告警" : msg.ToString().Trim());
                Log($"告警状态(0x{ev:X}) → {desc}");
            }
            catch (Exception ex) { Log("读取告警失败：" + ex.Message); }
        }

        private void BtnClearAlarm_Click(object sender, RoutedEventArgs e)
        {
            if (!Pwr.IsConnected) { Log("未连接"); return; }
            try { Pwr.ClearAlarm(); Log("告警已清除"); } catch (Exception ex) { Log("清除告警失败：" + ex.Message); }
        }

        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            DeviceComm.StateChanged -= DeviceComm_StateChanged;
        }
    }
}

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AgingTest.Models;

namespace AgingTest.Views
{
    /// <summary>
    /// N36600 高精度可编程直流电源控制（SCPI · 网口 TCP）。
    /// 输出设定(VOLT/CURR/POW/VOLT:LIMIT)、输出开关(OUTP ON/OFF)、模式(OUTP:MODE CV/CC)、
    /// 回读(MEAS?/MEAS:CURR?/MEAS:POW?)、保护(VOLT:PROT/CURR:PROT及使能/清除)、
    /// 状态(STAT:OPER?/*ALL?)、错误读取(SYST:ERR?)。
    /// 通讯连接由 DeviceComm 统一管理（通讯设置窗口/主界面操作）。
    /// </summary>
    public partial class UC_N36600Power : UserControl
    {
        private int Index => (int)CmbDev.SelectedItem;
        private N36600PowerClient Pwr => DeviceComm.N36600[Index - 1].Client;

        public UC_N36600Power()
        {
            InitializeComponent();
            CmbDev.ItemsSource = Enumerable.Range(1, DeviceComm.N36600Count).ToList();
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

        private void SetParam(string name, Action<double> setter, string text)
        {
            if (!Pwr.IsConnected) { Log("未连接，请先在通讯设置中连接"); return; }
            try { setter(double.Parse(text.Trim())); Log($"设定{name} 完成"); }
            catch (Exception ex) { Log($"设定{name}失败：" + ex.Message); }
        }

        private void BtnSetVolt_Click(object sender, RoutedEventArgs e) => SetParam("输出电压", Pwr.SetVoltage, TxtVolt.Text);
        private void BtnSetCurr_Click(object sender, RoutedEventArgs e) => SetParam("输出电流", Pwr.SetCurrent, TxtCurr.Text);
        private void BtnSetPow_Click(object sender, RoutedEventArgs e) => SetParam("恒功率", Pwr.SetPower, TxtPow.Text);
        private void BtnSetVoltLim_Click(object sender, RoutedEventArgs e) => SetParam("电压上限", Pwr.SetVoltLimit, TxtVoltLim.Text);

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

        private void BtnModeCv_Click(object sender, RoutedEventArgs e)
        {
            if (!Pwr.IsConnected) { Log("未连接"); return; }
            try { Pwr.SetMode("CV"); Log("已设为 CV 优先"); } catch (Exception ex) { Log("设模式失败：" + ex.Message); }
        }

        private void BtnModeCc_Click(object sender, RoutedEventArgs e)
        {
            if (!Pwr.IsConnected) { Log("未连接"); return; }
            try { Pwr.SetMode("CC"); Log("已设为 CC 优先"); } catch (Exception ex) { Log("设模式失败：" + ex.Message); }
        }

        private void RefreshMeasure()
        {
            try
            {
                var v = Pwr.MeasVoltage();
                var a = Pwr.MeasCurrent();
                var w = Pwr.MeasPower();
                TxtMeasV.Text = double.IsNaN(v) ? "—" : v.ToString("0.0000");
                TxtMeasA.Text = double.IsNaN(a) ? "—" : a.ToString("0.0000");
                TxtMeasW.Text = double.IsNaN(w) ? "—" : w.ToString("0.0000");
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

        private void BtnOper_Click(object sender, RoutedEventArgs e)
        {
            if (!Pwr.IsConnected) { Log("未连接"); return; }
            try
            {
                int op = int.TryParse(Pwr.GetOper(), out var v) ? v : -1;
                var sb = new System.Text.StringBuilder();
                if ((op & 1) != 0) sb.Append("输出开 ");
                if ((op & 2) != 0) sb.Append("CV ");
                if ((op & 4) != 0) sb.Append("CC ");
                if ((op & 8) != 0) sb.Append("OVP ");
                if ((op & 16) != 0) sb.Append("OCP ");
                if ((op & 32) != 0) sb.Append("过温 ");
                if ((op & 64) != 0) sb.Append("保护!");
                Log($"状态(0x{op:X}) → {(op < 0 ? "不可解析" : (sb.Length == 0 ? "输出关/无告警" : sb.ToString().Trim()))}");
            }
            catch (Exception ex) { Log("读取状态失败：" + ex.Message); }
        }

        private void BtnSetOvp_Click(object sender, RoutedEventArgs e) => SetParam("过压保护", Pwr.SetOvp, TxtOvp.Text);
        private void BtnSetOcp_Click(object sender, RoutedEventArgs e) => SetParam("过流保护", Pwr.SetOcp, TxtOcp.Text);
        private void BtnOvpEnable_Click(object sender, RoutedEventArgs e)
        {
            if (!Pwr.IsConnected) { Log("未连接"); return; }
            try { Pwr.SetOvpEnable(true); Log("已使能过压保护"); } catch (Exception ex) { Log("使能OVP失败：" + ex.Message); }
        }
        private void BtnOcpEnable_Click(object sender, RoutedEventArgs e)
        {
            if (!Pwr.IsConnected) { Log("未连接"); return; }
            try { Pwr.SetOcpEnable(true); Log("已使能过流保护"); } catch (Exception ex) { Log("使能OCP失败：" + ex.Message); }
        }
        private void BtnOvpClear_Click(object sender, RoutedEventArgs e)
        {
            if (!Pwr.IsConnected) { Log("未连接"); return; }
            try { Pwr.ClearVoltProt(); Log("已清除电压保护状态"); } catch (Exception ex) { Log("清除OVP失败：" + ex.Message); }
        }
        private void BtnOcpClear_Click(object sender, RoutedEventArgs e)
        {
            if (!Pwr.IsConnected) { Log("未连接"); return; }
            try { Pwr.ClearCurrProt(); Log("已清除电流保护状态"); } catch (Exception ex) { Log("清除OCP失败：" + ex.Message); }
        }

        private void BtnErr_Click(object sender, RoutedEventArgs e)
        {
            if (!Pwr.IsConnected) { Log("未连接"); return; }
            try { Log("SYST:ERR? → " + Pwr.GetError()); } catch (Exception ex) { Log("读取错误失败：" + ex.Message); }
        }

        private void BtnAll_Click(object sender, RoutedEventArgs e)
        {
            if (!Pwr.IsConnected) { Log("未连接"); return; }
            try { Log("*ALL? → " + Pwr.GetAll()); } catch (Exception ex) { Log("读取*ALL?失败：" + ex.Message); }
        }

        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            DeviceComm.StateChanged -= DeviceComm_StateChanged;
        }
    }
}

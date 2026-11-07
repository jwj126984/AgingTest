using System;
using System.Net.Sockets;
using System.Text;

namespace AgingTest.Models
{
    /// <summary>
    /// N36100 系列宽范围可编程直流电源 SCPI 客户端（NGI 恩智 · 网口 TCP）。
    /// 通讯：LAN TCP 端口 7000，命令以换行符 \n 结束；响应以 \n 结束。
    /// 封装常用命令：*IDN?、SOUR:VOLT/CURR、OUTP:ONOFF/MODE、MEAS:VOLT/CURR/POW、
    /// PROT:OVP/OCP/OPP、OUTP:STATe?。
    /// </summary>
    public class N36100PowerClient : IDisposable
    {
        private TcpClient? _tcp;
        private NetworkStream? _ns;

        public bool IsConnected => _tcp != null && _tcp.Connected && _ns != null;

        /// <summary>连接网口（默认端口 7000）。</summary>
        public void Connect(string ip, int port)
        {
            if (IsConnected) return;
            _tcp = new TcpClient();
            _tcp.Connect(ip, port);
            _tcp.ReceiveTimeout = 2000;
            _tcp.SendTimeout = 1000;
            _ns = _tcp.GetStream();
        }

        public void Disconnect()
        {
            try { _ns?.Close(); } catch { }
            try { _tcp?.Close(); } catch { }
            _ns = null;
            _tcp = null;
        }

        public void Dispose() => Disconnect();

        /// <summary>发送写命令（不等待返回）。</summary>
        public void Send(string cmd)
        {
            Ensure();
            var bytes = Encoding.ASCII.GetBytes(cmd + "\n");
            _ns!.Write(bytes, 0, bytes.Length);
        }

        /// <summary>发送查询命令并读取一行响应（去掉结尾换行/回车）。</summary>
        public string Query(string cmd)
        {
            Ensure();
            var bytes = Encoding.ASCII.GetBytes(cmd + "\n");
            _ns!.Write(bytes, 0, bytes.Length);
            var sb = new StringBuilder();
            while (true)
            {
                int b = _ns.ReadByte();
                if (b < 0 || b == '\n') break;   // 流结束或命令结束符
                if (b == '\r') continue;
                sb.Append((char)b);
            }
            return sb.ToString();
        }

        /// <summary>查询并解析为 double（非法返回 NaN）。</summary>
        public double QueryDouble(string cmd)
        {
            var s = Query(cmd).Trim();
            return double.TryParse(s, out var v) ? v : double.NaN;
        }

        private void Ensure()
        {
            if (!IsConnected) throw new InvalidOperationException("N36100 网口未连接");
        }

        #region 常用命令封装

        /// <summary>*IDN? 读取设备标识：制造商,型号,SN,版本</summary>
        public string GetIdn() => Query("*IDN?");

        /// <summary>设定输出电压(SOUR:VOLT, V)</summary>
        public void SetVoltage(double v) => Send($"SOUR:VOLT {v.ToString("0.###")}");

        /// <summary>设定输出限流(SOUR:CURR, A)</summary>
        public void SetCurrent(double a) => Send($"SOUR:CURR {a.ToString("0.###")}");

        /// <summary>设定输出内阻(SOUR:INT, mΩ)</summary>
        public void SetInternalRes(double mr) => Send($"SOUR:INT {mr.ToString("0.###")}");

        /// <summary>输出开关(OUTP:ONOFF)：on=true开启</summary>
        public void SetOutputOnOff(bool on) => Send(on ? "OUTP:ONOFF 1" : "OUTP:ONOFF 0");

        /// <summary>设置工作模式(OUTP:MODE)：NORMAL/SEQUENCE/CPOWer/STEP</summary>
        public void SetMode(string mode) => Send($"OUTP:MODE {mode}");

        /// <summary>电压上升时间(OUTP:VOLR, ms, 50~2000)</summary>
        public void SetVolRisetime(int ms) => Send($"OUTP:VOLR {ms}");

        /// <summary>电流上升时间(OUTP:CURR, ms, 50~2000)</summary>
        public void SetCurRisetime(int ms) => Send($"OUTP:CURR {ms}");

        /// <summary>回读输出电压(MEAS:VOLT?, V)</summary>
        public double MeasVoltage() => QueryDouble("MEAS:VOLT?");

        /// <summary>回读输出电流(MEAS:CURR?, A)</summary>
        public double MeasCurrent() => QueryDouble("MEAS:CURR?");

        /// <summary>回读输出功率(MEAS:POW?, W)</summary>
        public double MeasPower() => QueryDouble("MEAS:POW?");

        /// <summary>输出状态(OUTP:STATe?)：Bit0=开, Bit5=CC环</summary>
        public string GetOutputState() => Query("OUTP:STATe?");

        /// <summary>过压保护值(PROT:VOLT, V)</summary>
        public void SetOvp(double v) => Send($"PROT:VOLT {v.ToString("0.###")}");

        /// <summary>过流保护值(PROT:CURR, A)</summary>
        public void SetOcp(double a) => Send($"PROT:CURR {a.ToString("0.###")}");

        /// <summary>过功率保护值(PROT:POW, W)</summary>
        public void SetOpp(double w) => Send($"PROT:POW {w.ToString("0.###")}");

        /// <summary>告警状态(OUTP:EVENt?)：Bit1=OVP,Bit2=OCP,Bit3=OPP,Bit4=OTP；传0清除告警</summary>
        public string GetAlarm() => Query("OUTP:EVENt?");

        /// <summary>清除告警</summary>
        public void ClearAlarm() => Send("OUTP:EVENt 0");

        #endregion
    }
}

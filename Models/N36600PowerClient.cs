using System;
using System.Net.Sockets;
using System.Text;

namespace AgingTest.Models
{
    /// <summary>
    /// N36600 系列高精度可编程直流电源 SCPI 客户端（NGI 恩智 · 网口 TCP）。
    /// 通讯：LAN/TCP，命令以换行符 \n(0x0A) 结束；响应以 \n 结束。
    /// 命令体系与 N36100 不同：设定用 VOLT/CURR/POW、输出用 OUTP ON/OFF、
    /// 测量用 MEAS?/MEAS:CURR?/MEAS:POW?/MEAS:VAP?、状态用 STAT:OPER?、*ALL?。
    /// </summary>
    public class N36600PowerClient : IDisposable
    {
        private TcpClient? _tcp;
        private NetworkStream? _ns;

        public bool IsConnected => _tcp != null && _tcp.Connected && _ns != null;

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

        /// <summary>发送写命令（无返回）。</summary>
        public void Send(string cmd)
        {
            Ensure();
            var bytes = Encoding.ASCII.GetBytes(cmd + "\n");
            _ns!.Write(bytes, 0, bytes.Length);
        }

        /// <summary>发送查询命令并读取一行响应。</summary>
        public string Query(string cmd)
        {
            Ensure();
            var bytes = Encoding.ASCII.GetBytes(cmd + "\n");
            _ns!.Write(bytes, 0, bytes.Length);
            var sb = new StringBuilder();
            while (true)
            {
                int b = _ns.ReadByte();
                if (b < 0 || b == '\n') break;
                if (b == '\r') continue;
                sb.Append((char)b);
            }
            return sb.ToString();
        }

        public double QueryDouble(string cmd)
        {
            var s = Query(cmd).Trim();
            return double.TryParse(s, out var v) ? v : double.NaN;
        }

        private void Ensure()
        {
            if (!IsConnected) throw new InvalidOperationException("N36600 网口未连接");
        }

        #region 常用命令封装

        /// <summary>*IDN? 设备标识</summary>
        public string GetIdn() => Query("*IDN?");

        /// <summary>设置输出电压 VOLT &lt;v&gt;</summary>
        public void SetVoltage(double v) => Send($"VOLT {v.ToString("0.###")}");

        /// <summary>设置输出电流 CURR &lt;a&gt;</summary>
        public void SetCurrent(double a) => Send($"CURR {a.ToString("0.###")}");

        /// <summary>设置输出功率(恒功率) POW &lt;w&gt;</summary>
        public void SetPower(double w) => Send($"POW {w.ToString("0.###")}");

        /// <summary>同时设置电压电流 APPL &lt;v&gt;,&lt;a&gt;</summary>
        public void Apply(double v, double a) => Send($"APPL {v.ToString("0.###")},{a.ToString("0.###")}");

        /// <summary>电压输出上限 VOLT:LIMIT &lt;v&gt;</summary>
        public void SetVoltLimit(double v) => Send($"VOLT:LIMIT {v.ToString("0.###")}");

        /// <summary>输出开关 OUTP ON/OFF</summary>
        public void SetOutputOnOff(bool on) => Send(on ? "OUTP ON" : "OUTP OFF");

        /// <summary>输出模式 OUTP:MODE CV/CC</summary>
        public void SetMode(string mode) => Send($"OUTP:MODE {mode}");

        /// <summary>回读电压 MEAS?</summary>
        public double MeasVoltage() => QueryDouble("MEAS?");

        /// <summary>回读电流 MEAS:CURR?</summary>
        public double MeasCurrent() => QueryDouble("MEAS:CURR?");

        /// <summary>回读功率 MEAS:POW?</summary>
        public double MeasPower() => QueryDouble("MEAS:POW?");

        /// <summary>回读电压,电流,功率 MEAS:VAP?（返回逗号分隔三值）</summary>
        public string MeasVap() => Query("MEAS:VAP?");

        /// <summary>过压保护阈值 VOLT:PROT &lt;v&gt;</summary>
        public void SetOvp(double v) => Send($"VOLT:PROT {v.ToString("0.###")}");

        /// <summary>过流保护阈值 CURR:PROT &lt;a&gt;</summary>
        public void SetOcp(double a) => Send($"CURR:PROT {a.ToString("0.###")}");

        /// <summary>过压保护使能 VOLT:PROT:STAT ON/OFF</summary>
        public void SetOvpEnable(bool on) => Send(on ? "VOLT:PROT:STAT ON" : "VOLT:PROT:STAT OFF");

        /// <summary>过流保护使能 CURR:PROT:STAT ON/OFF</summary>
        public void SetOcpEnable(bool on) => Send(on ? "CURR:PROT:STAT ON" : "CURR:PROT:STAT OFF");

        /// <summary>清除电压保护状态 VOLT:PROT:CLE</summary>
        public void ClearVoltProt() => Send("VOLT:PROT:CLE");

        /// <summary>清除电流保护状态 CURR:PROT:CLE</summary>
        public void ClearCurrProt() => Send("CURR:PROT:CLE");

        /// <summary>操作状态寄存器 STAT:OPER?（位：0开 1CV 2CC 3OVP 4OCP 5过温 6保护）</summary>
        public string GetOper() => Query("STAT:OPER?");

        /// <summary>*ALL? 返回 状态,电压,电流</summary>
        public string GetAll() => Query("*ALL?");

        /// <summary>读取错误信息 SYST:ERR?</summary>
        public string GetError() => Query("SYST:ERR?");

        #endregion
    }
}

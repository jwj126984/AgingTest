using System;
using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace AgingTest.Models
{
    /// <summary>
    /// N36600 系列高精度可编程直流电源 SCPI 客户端（NGI 恩智 · 网口 TCP）。
    /// 协议依据：《N36600_SCPI通讯协议_V1.01_20251129》。
    /// 每条命令以 0x0A(\n) 结束；查询命令返回以 \n 结束的一行。
    /// 所有 SCPI 仪器特定命令均带前导冒号（:），与文档示例一致。
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

        #region 常用命令封装（命令格式严格对照 V1.01 文档）

        private static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>*IDN? 设备标识（IEEE 488.2 公用命令，不带冒号）。</summary>
        public string GetIdn() => Query("*IDN?");

        /// <summary>设置输出电压 :SOURce:VOLTage[:LEVel][:IMMediate][:AMPLitude]，短形 :VOLT。</summary>
        public void SetVoltage(double v) => Send($":VOLT {Fmt(v)}");

        /// <summary>设置输出电流 :SOURce:CURRent[:LEVel][:IMMediate][:AMPLitude]，短形 :CURR。</summary>
        public void SetCurrent(double a) => Send($":CURR {Fmt(a)}");

        /// <summary>设置输出功率 :SOURce:POWer[:LEVel][:IMMediate][:AMPLitude]，短形 :POW。</summary>
        public void SetPower(double w) => Send($":POW {Fmt(w)}");

        /// <summary>同时设置电压电流 :APPLy &lt;voltage&gt;,&lt;current&gt;。</summary>
        public void Apply(double v, double a) => Send($":APPL {Fmt(v)},{Fmt(a)}");

        /// <summary>电压输出上限 :SOURce:VOLTage:LIMIT。</summary>
        public void SetVoltLimit(double v) => Send($":VOLT:LIMIT {Fmt(v)}");

        /// <summary>输出开关 :OUTPut[:STATe] ON/OFF。</summary>
        public void SetOutputOnOff(bool on) => Send(on ? ":OUTP ON" : ":OUTP OFF");

        /// <summary>输出模式 :OUTPut:MODE CV/CC。</summary>
        public void SetMode(string mode) => Send($":OUTP:MODE {mode}");

        /// <summary>回读电压 :MEASure[:SCALar][:VOLTage][:DC]?。</summary>
        public double MeasVoltage() => QueryDouble(":MEAS?");

        /// <summary>回读电流 :MEASure[:SCALar]:CURRent[:DC]?。</summary>
        public double MeasCurrent() => QueryDouble(":MEAS:CURR?");

        /// <summary>回读功率 :MEASure[:SCALar]:POWer[:DC]?。</summary>
        public double MeasPower() => QueryDouble(":MEAS:POW?");

        /// <summary>回读电压/电流/功率组合值 :MEASure[:SCALar]:VAP?（一次查询返回三个值，逗号分隔）。</summary>
        public string MeasVap() => Query(":MEAS:VAP?");

        /// <summary>过压保护阈值 :SOURce:VOLTage:PROTection[:LEVel]。</summary>
        public void SetOvp(double v) => Send($":VOLT:PROT {Fmt(v)}");

        /// <summary>过流保护阈值 :SOURce:CURRent:PROTection[:LEVel]。</summary>
        public void SetOcp(double a) => Send($":CURR:PROT {Fmt(a)}");

        /// <summary>过压保护使能 :SOURce:VOLTage:PROTection:STATe ON/OFF。</summary>
        public void SetOvpEnable(bool on) => Send(on ? ":VOLT:PROT:STAT ON" : ":VOLT:PROT:STAT OFF");

        /// <summary>过流保护使能 :SOURce:CURRent:PROTection:STATe ON/OFF。</summary>
        public void SetOcpEnable(bool on) => Send(on ? ":CURR:PROT:STAT ON" : ":CURR:PROT:STAT OFF");

        /// <summary>清除过压保护状态 :SOURce:VOLTage:PROTection:CLEar。</summary>
        public void ClearVoltProt() => Send(":VOLT:PROT:CLE");

        /// <summary>清除过流保护状态 :SOURce:CURRent:PROTection:CLEar。</summary>
        public void ClearCurrProt() => Send(":CURR:PROT:CLE");

        /// <summary>操作状态寄存器 :STATus:OPERation[:EVENt]?（返回整数位掩码）。</summary>
        public string GetOper() => Query(":STAT:OPER?");

        /// <summary>*ALL? 返回 状态,电压,电流。</summary>
        public string GetAll() => Query("*ALL?");

        /// <summary>读取错误信息 :SYSTem:ERRor[:NEXT]?。</summary>
        public string GetError() => Query(":SYST:ERR?");

        #endregion
    }
}

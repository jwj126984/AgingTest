using System;
using System.Collections.Generic;
using System.Linq;

namespace AgingTest.Models
{
    /// <summary>73B5 电流类型取值。</summary>
    public enum BmsCurrentType : byte
    {
        Original = 0x11,
        Average = 0x22,
        Calibration20A = 0x33,
        Calibration100A = 0x44,
        Calibration200A = 0x55,
        Calibration400A = 0x66,
    }

    /// <summary>
    /// 附录1 中 BMS EOL 控制序列的封装：进入扩展会话、安全解锁、清故障、EEPROM 例程、休眠例程、
    /// 强控 MOS/BUCK、强控均衡、SOC 标定、电流类型/标定数组、序列号与硬件版本写入。
    /// 每个方法内部自行完成 10 03 会话与帧组装，调用方只需持有已连接的 <see cref="UdsClient"/>。
    /// </summary>
    public static class BmsEolService
    {
        /// <summary>附录1 的 CAN 唤醒帧：0x400，8 字节全 0，500ms 周期（经典 CAN）。</summary>
        public const uint WakeupFrameId = 0x400;

        // 例程 ID
        public const ushort RoutineSaveCurrentParams = 0x7415;   // 保存电流参数到 EEPROM
        public const ushort RoutineResetCurrentParams = 0x7416;  // 重置 EEPROM 电流参数
        public const ushort RoutineResetEeprom = 0x7414;         // 74 14 FF 重置 EEPROM；74 14 14 清除 F18C
        public const ushort RoutineShallowSleep = 0x7413;        // 74 13 0A / 1A / 0D

        /// <summary>创建默认 UDS 客户端（附录1：0x775 请求、0x77D 响应、CANFD）。</summary>
        public static UdsClient CreateUds(int canIndex, int channel)
            => new(canIndex, channel, 0x775, 0x77D, isFd: true);

        /// <summary>创建 UDS 客户端并按运行环境配置超时、日志。</summary>
        public static UdsClient CreateUds(int canIndex, int channel, Action<string>? log)
        {
            var uds = CreateUds(canIndex, channel);
            if (log != null) uds.Log += log;
            return uds;
        }

        // ================================================================
        // 会话与解锁
        // ================================================================
        /// <summary>10 03 进入扩展会话（S3=5000ms）。</summary>
        public static void EnterExtendedSession(UdsClient uds) => uds.EnterExtendedSession();

        /// <summary>
        /// EOL 前置：10 03 → 27 01/27 02 解锁。seedToKey 为空时只切会话，后续 2E/31 会被 ECU 拒绝（NRC 0x33）。
        /// </summary>
        public static void PrepareDiagnosis(UdsClient uds, Func<byte[], byte[]>? seedToKey, byte level = 0x01)
        {
            uds.EnterExtendedSession();
            if (seedToKey != null) uds.Unlock(level, seedToKey);
        }

        /// <summary>用厂商算法库（ZLGKey）计算密钥并完成 27 01/27 02。</summary>
        public static void UnlockByAlgorithm(UdsClient uds, byte level = 0x01, string? algorithmPath = null)
            => uds.Unlock(level, seed => SeedKeyAlgorithm.Generate(seed, level, algorithmPath));

        /// <summary>10 03 + 厂商算法库解锁，EOL 全流程入口。</summary>
        public static void PrepareDiagnosisWithAlgorithm(UdsClient uds, string? algorithmPath = null, byte level = 0x01)
        {
            uds.EnterExtendedSession();
            UnlockByAlgorithm(uds, level, algorithmPath);
        }

        // ================================================================
        // 故障码 / EEPROM
        // ================================================================
        /// <summary>14 FF FF FF 清除全部 DTC。</summary>
        public static void ClearDtc(UdsClient uds) => uds.ClearDiagnosticInformation(0x00FFFFFF);

        /// <summary>19 02 09 读当前故障码。</summary>
        public static List<UdsDtc> ReadDtc(UdsClient uds) => uds.ReadDtcByStatusMask(0x09);

        /// <summary>31 01 74 14 FF：重置 EEPROM。</summary>
        public static void ResetEeprom(UdsClient uds)
            => uds.RoutineControl(RoutineResetEeprom, 0x01, new byte[] { 0xFF });

        /// <summary>31 01 74 14 14：清除 F18C（ECU 序列号）。</summary>
        public static void ClearEcuSerial(UdsClient uds)
            => uds.RoutineControl(RoutineResetEeprom, 0x01, new byte[] { 0x14 });

        /// <summary>31 01 74 15：保存电流参数到 EEPROM。</summary>
        public static void SaveCurrentParameters(UdsClient uds)
            => uds.RoutineControl(RoutineSaveCurrentParams, 0x01);

        /// <summary>31 01 74 16：重置 EEPROM 电流参数。</summary>
        public static void ResetCurrentParameters(UdsClient uds)
            => uds.RoutineControl(RoutineResetCurrentParams, 0x01);

        // ================================================================
        // 休眠（RTC 唤醒测试用）
        // ================================================================
        /// <summary>31 01 74 13 0A：浅休眠。</summary>
        public static void EnterShallowSleep(UdsClient uds)
            => uds.RoutineControl(RoutineShallowSleep, 0x01, new byte[] { 0x0A });

        /// <summary>31 01 74 13 1A：浅休眠（配合 RTC 定时唤醒测试）。</summary>
        public static void EnterShallowSleepRtc(UdsClient uds)
            => uds.RoutineControl(RoutineShallowSleep, 0x01, new byte[] { 0x1A });

        /// <summary>31 01 74 13 0D：深休眠。</summary>
        public static void EnterDeepSleep(UdsClient uds)
            => uds.RoutineControl(RoutineShallowSleep, 0x01, new byte[] { 0x0D });

        /// <summary>发送 CAN 唤醒帧 0x400（8 字节全 0）。</summary>
        public static bool SendWakeupFrame(int canIndex, int channel, bool isFd = false)
            => CanComm.Send(canIndex, channel, WakeupFrameId, new byte[8], false, false, isFd);

        // ================================================================
        // 强控：MOS / BUCK（DID 0x73BA）
        // ================================================================
        /// <summary>
        /// 2F 73 BA 03 [状态位] 07：使能强控。
        /// 状态位 Bit0 充电 MOS、Bit1 放电 MOS、Bit2 BUCK，1=断开、0=闭合。
        /// 附录中的示例误写成 73 9A，DID 表为 73BA，这里按 73BA 实现。
        /// </summary>
        public static void StrongControlMosBuck(UdsClient uds, bool chargeMosClosed, bool dischargeMosClosed, bool buckClosed)
        {
            byte mask = 0;
            if (!chargeMosClosed) mask |= 0x01;
            if (!dischargeMosClosed) mask |= 0x02;
            if (!buckClosed) mask |= 0x04;

            uds.WriteDataByIdentifier(BmsDids.StrongMosBuck, new byte[] { 0x03, mask, 0x07 });
        }

        /// <summary>2F 73 BA 00：取消强控（交回 BMS 自控）。</summary>
        public static void CancelStrongMosBuck(UdsClient uds)
            => uds.WriteDataByIdentifier(BmsDids.StrongMosBuck, new byte[] { 0x00 });

        // ================================================================
        // 强控：均衡（DID 0x73BB）
        // ================================================================
        /// <summary>
        /// 2F 73 BB 03 [位图] 0F：使能强控均衡。
        /// 位图 Bit0~3 对应电芯 1~4，1=开启均衡。
        /// </summary>
        public static void StrongControlBalance(UdsClient uds, int cellMask)
        {
            byte mask = (byte)(cellMask & 0x0F);
            uds.WriteDataByIdentifier(BmsDids.StrongBalance, new byte[] { 0x03, mask, 0x0F });
        }

        /// <summary>2F 73 BB 00：取消强控均衡。</summary>
        public static void CancelStrongBalance(UdsClient uds)
            => uds.WriteDataByIdentifier(BmsDids.StrongBalance, new byte[] { 0x00 });

        // ================================================================
        // SOC 标定（31 01 74 10 xx）
        // ================================================================
        /// <summary>标定 SOC，percent 取 0~100（100% → 0x64）。</summary>
        public static void CalibrateSoc(UdsClient uds, int percent)
        {
            if (percent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(percent), "SOC 必须在 0~100 之间");
            uds.RoutineControl(BmsDids.SocCalibrationRoutine, 0x01, new byte[] { (byte)percent });
        }

        // ================================================================
        // 电流类型 / 电流标定数组（DID 0x73B5 / 0x73B6）
        // ================================================================
        public static void SetCurrentType(UdsClient uds, BmsCurrentType type)
            => uds.WriteDataByIdentifier(BmsDids.CurrentType, new byte[] { (byte)type });

        public static BmsCurrentType ReadCurrentType(UdsClient uds)
        {
            byte[] data = uds.ReadDidData(BmsDids.CurrentType);
            return data.Length == 0 ? BmsCurrentType.Original : (BmsCurrentType)data[0];
        }

        /// <summary>
        /// 写 10 个电流标定点。每个点写入值 = mA + 1,000,000（uint32 大端），共 40 字节。
        /// 读取时反向 − 1,000,000。
        /// </summary>
        public static void WriteCurrentCalibration(UdsClient uds, IReadOnlyList<int> milliAmps)
        {
            if (milliAmps.Count != 10)
                throw new ArgumentException("电流标定必须是 10 个点", nameof(milliAmps));

            byte[] data = new byte[40];
            for (int i = 0; i < 10; i++)
            {
                uint raw = unchecked((uint)(milliAmps[i] + 1000000));
                data[i * 4 + 0] = (byte)(raw >> 24);
                data[i * 4 + 1] = (byte)(raw >> 16);
                data[i * 4 + 2] = (byte)(raw >> 8);
                data[i * 4 + 3] = (byte)raw;
            }
            uds.WriteDataByIdentifier(BmsDids.CurrentCalibration, data);
        }

        /// <summary>读 10 个电流标定点（mA）。</summary>
        public static int[] ReadCurrentCalibration(UdsClient uds)
        {
            byte[] data = uds.ReadDidData(BmsDids.CurrentCalibration);
            if (data.Length < 40) throw new InvalidOperationException($"电流标定数据长度 {data.Length}，应为 40 字节");

            int[] result = new int[10];
            for (int i = 0; i < 10; i++)
                result[i] = unchecked((int)CanSignalCodec.U32(data, i * 4) - 1000000);
            return result;
        }

        // ================================================================
        // 序列号 / 硬件版本
        // ================================================================
        /// <summary>2E F1 8C：写 36 字节 ASCII 序列号（格式 10_807001954AA_11_9DQ_12_YYMMDDXXXXX）。</summary>
        public static void WriteEcuSerial(UdsClient uds, string serial)
        {
            if (serial.Length > 36) throw new ArgumentException("序列号不能超过 36 字符", nameof(serial));
            uds.WriteDataByIdentifier(BmsDids.EcuSerial, CanSignalCodec.AsciiBytes(serial, 36));
        }

        /// <summary>
        /// 2E 73 B4：写硬件版本 3 字节。附录1 示例 "0.2.3" 实际写入 30 32 33（即 "023"，去掉小数点），
        /// 因此这里接受 "0.2.3" 或 "023" 两种写法。
        /// </summary>
        public static void WriteHardwareVersion(UdsClient uds, string version)
        {
            string digits = (version ?? "").Replace(".", "");
            if (digits.Length > 3) throw new ArgumentException("硬件版本最多 3 位数字，如 0.2.3", nameof(version));
            uds.WriteDataByIdentifier(BmsDids.HardwareVersionWrite, CanSignalCodec.AsciiBytes(digits, 3));
        }

        // ================================================================
        // 读取 + 解析
        // ================================================================
        /// <summary>22 读 DID 并按附录1 规则解析。</summary>
        public static DidValue ReadDid(UdsClient uds, ushort did)
            => BmsDids.Parse(did, uds.ReadDidData(did));

        /// <summary>批量读取并解析多个 DID（ECU 拒绝的 DID 会被跳过，不打断整体）。</summary>
        public static List<DidValue> ReadDids(UdsClient uds, IEnumerable<ushort> dids, Action<ushort, Exception>? onError = null)
        {
            var list = new List<DidValue>();
            foreach (ushort did in dids)
            {
                try { list.Add(ReadDid(uds, did)); }
                catch (Exception ex) { onError?.Invoke(did, ex); }
            }
            return list;
        }
    }
}

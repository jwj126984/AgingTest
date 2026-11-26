# BMS UDS 控制与 CAN 数据解析接入说明

依据《B2510-EHV-EOL测试用例 V1.11》的「附录1-数据解析」实现，协议参数、DID 表和例程编号均来自该表。

## 1. 新增文件

| 文件 | 作用 |
|---|---|
| `Models/IsoTpTransport.cs` | ISO 15765-2 传输层：单帧/首帧/连续帧/流控、CANFD 长单帧（0x00+长度）、0x78 响应挂起 |
| `Models/UdsClient.cs` | UDS 服务：10/11/14/19/22/27/28/2E/2F/31/3E，负响应异常与 NRC 文本、S3 会话保持 |
| `Models/BmsDids.cs` | 附录1 全部 DID 常量与解析规则（物理值换算、位域、ASCII、标定数组） |
| `Models/BmsEolService.cs` | EOL 控制序列：解锁、清 DTC、EEPROM 例程、休眠例程、强控 MOS/BUCK、强控均衡、SOC 标定、电流标定、序列号/硬件版本 |
| `Models/CanSignalCodec.cs` | 通用 CAN 信号解析：Intel/Motorola 位域、有符号、Factor/Offset、枚举、ASCII |
| `Models/SeedKeyAlgorithm.cs` | 厂商安全算法库封装：`ZLGKey` 导出、32 位加载、种子→密钥 |
| `Models/UdsStationManager.cs` | 48 工位诊断管理：工位↔CAN 盒/通道映射、每工位独立 UdsClient、单线程统一保活 |
| `Models/Config.cs`（改） | 新增 `UdsSetting`（0x775/0x77D/CANFD、算法库路径、48 工位映射参数） |
| `Models/DeviceComm.cs`（改） | 新增 `DeviceComm.CreateUds()`，按 `Comm.Uds` 创建客户端 |
| `Models/CanComm.cs`（改） | `CardCount` 6（6 台 CANFDNET-800U × 8 通道） |
| `AgingTest.csproj`（改） | 算法库随构建复制到输出目录 `SecurityAlgorithm\QR_C1068\` |

## 2. 通讯参数

| 项 | 值 |
|---|---|
| 请求 ID | `0x775`（CANFD，标准帧） |
| 响应 ID | `0x77D`（CANFD，标准帧） |
| 传输层 | ISO-TP，物理寻址；测试端收到首帧回 `30 00 00` |
| S3 | 5000 ms，超时需重发 `10 03` 并重新解锁 |
| 前置条件 | 安全访问、例程控制、2E/2F 写入前必须先 `10 03` 进入扩展会话 |

诊断走哪张卡、哪个通道在 `Config.json` 的 `Comm.Uds` 中配置（默认 `CanIndex=1, Channel=0`）。目标通道必须已由通讯设置页连接并启动。

## 3. 基本用法

```csharp
// 连接 CAN 盒之后（CanComm.ConnectCard 成功）
using var uds = DeviceComm.CreateUds(msg => Console.WriteLine(msg));
uds.Log += msg => TraceLog(msg);          // 可选：把 TX/RX 报文打到界面

// 10 03 进扩展会话（内部自动每 2s 发 3E 80 保活）
uds.EnterExtendedSession();

// 22 读 DID + 按附录1 解析
DidValue app = BmsEolService.ReadDid(uds, BmsDids.AppVersion);   // B2510_APP_R_2_3_0
DidValue soc = BmsEolService.ReadDid(uds, BmsDids.Soc);          // 100 %（Bit7 有效）
DidValue cur = BmsEolService.ReadDid(uds, BmsDids.Current);      // ×0.001 − 2000 A

// 批量读
var values = BmsEolService.ReadDids(uds, BmsDids.DefaultReadDids,
    (did, ex) => TraceLog($"0x{did:X4} 读取失败：{ex.Message}"));
```

### 安全访问（厂商算法库已接入）

算法库 `B2510_EHV_27_ZCANPRO.dll` 已接入：导出函数只有 `ZLGKey`（cdecl），与原有 FCT 工具调用方式一致；
种子 16 字节、密钥 16 字节。DLL 为 **32 位 x86**，`AgingTest.csproj` 已设置 `PlatformTarget=x86`，随构建复制到
`bin\Debug\net10.0-windows\SecurityAlgorithm\QR_C1068\`。

```csharp
// 10 03 + 27 01/27 02，密钥由 DLL 计算
BmsEolService.PrepareDiagnosisWithAlgorithm(uds);

// 指定 DLL 路径 / 安全等级
BmsEolService.PrepareDiagnosisWithAlgorithm(uds,
    @"D:\...\SecurityAlgorithm\QR_C1068\B2510_EHV_27_ZCANPRO.dll", 0x01);

// 只解锁、不切会话
BmsEolService.UnlockByAlgorithm(uds);

// 外部已算好密钥时
uds.UnlockWithKey(0x01, keyBytes);
```

调用链：`27 01` 取种子 → `SeedKeyAlgorithm.Generate(seed, level)` → `ZLGKey(seed, 16, level, null, key, ref 16)` → `27 02` 发密钥。
实测该 DLL 对同一 16 字节种子稳定返回 16 字节密钥（`79 5B C1 8F FE 78 6A 9A 84 94 24 3F 1F 08 04 62`），返回码 0。

> 若把项目改成 x64，加载会抛 `BadImageFormatException`（算法库是 PE32/x86）。`SeedKeyAlgorithm` 已把该异常翻译成中文提示。

## 3.1 48 工位诊断映射

`WorkStation` 是 6 层 × 8 列 = 48 工位，默认按工位号顺序映射到 CAN 盒/通道：

| 工位 | CAN 盒 | 通道 | 配置项 |
|---|---|---|---|
| 1~8 | CAN1 | ch0~ch7 | `FirstCanIndex=1, FirstChannel=0, StationsPerBox=8` |
| 9~16 | CAN2 | ch0~ch7 | 同上 |
| … | … | … |  |
| 41~48 | CAN6 | ch0~ch7 | 同上 |

6 台 CAN 盒 × 8 通道 = 48 工位，刚好一一对应；`CAN7/CAN8` 不再保留。

```csharp
var mgr = new UdsStationManager(DeviceComm.Root.Comm.Uds);
mgr.StartKeepAlive();                       // 单线程每 2s 给所有扩展会话发 3E 80

mgr.OpenAll(msg => TraceLog(msg));          // 建立 48 个客户端（各自过滤本通道报文）
mgr.Prepare(1);                             // 工位1：10 03 + 算法库解锁
var st = mgr[1];
var soc = BmsEolService.ReadDid(st.Client!, BmsDids.Soc);

mgr.Dispose();                              // 停保活、释放全部客户端
```

每个 `UdsClient` 按 `CanIndex + Channel + 0x77D` 过滤，48 个工位可并发收发，互不串帧。

`WorkStation` 已新增 `StationNo = (Layer-1)*8 + Column`，可直接作为 `mgr[stationNo]` 的索引；
也可以 `mgr.FindByWorkStation(layer, column)` 取到对应诊断通道。

## 3.2 老化期间周期报文与停止

| 报文 | 周期 | 启动时机 | 停止时机 |
|---|---|---|---|
| 唤醒 `0x400`（8 字节 0，经典 CAN） | **2000ms**（`ChargeTestSetting.WakeRepeatIntervalMs`） | 上电前序电流校验通过、状态进入 `Aging` 后 | `StopAsync` 进入时立即停；启动失败回退时也停 |
| 会话维持 `3E 80` | 2000ms（`UdsStationManager.StartKeepAlive`） | 首个工位 `10 03` 解锁后 | `738F` 确认 MOS 断开后停；异常停止在 `finally` 关闭 UDS 时也会停 |

- 会话维持 `3E 80` 默认**不逐帧写界面日志**（`TesterPresent` 临时关闭 `IsoTpTransport.LogEnabled`），只在启动/停止各记录一条；老化报文的收发在对应工位的 `DataMonitorWindow` 中按 CAN 盒/通道显示。

- 启动阶段的 `0x400` 仍按 500ms 重发，直到收到 BMS 回复；进入老化后改为每 2s 持续发送。
- `ChargeTestService` 内部用 `CancellationTokenSource` 管理周期唤醒任务，`StopPeriodicWake` 会取消并等待任务退出。
- 结束测试、手动停止、启动失败回退、窗口退出都会调用停止逻辑，不会遗留唤醒/会话维持报文。

**强控 MOS/BUCK、强控均衡是 2F 服务，不是 2E**：`2F 73 BA 03 mask 07`、`2F 73 BB 03 mask 0F`；取消强控为 `2F 73 BA 00` / `2F 73 BB 00`。`UdsClient.InputOutputControlByIdentifier` 专门发 2F，`WriteDataByIdentifier` 仍只用于 2E。

### EOL 控制序列

```csharp
BmsEolService.ClearDtc(uds);                       // 14 FF FF FF
var dtcs = BmsEolService.ReadDtc(uds);              // 19 02 09，返回 UdsDtc（RawHex + 标准 Code + Status）

BmsEolService.ResetEeprom(uds);                     // 31 01 74 14 FF
BmsEolService.ClearEcuSerial(uds);                  // 31 01 74 14 14（清 F18C）
BmsEolService.SaveCurrentParameters(uds);           // 31 01 74 15
BmsEolService.ResetCurrentParameters(uds);          // 31 01 74 16

BmsEolService.EnterShallowSleep(uds);               // 31 01 74 13 0A
BmsEolService.EnterShallowSleepRtc(uds);            // 31 01 74 13 1A（RTC 唤醒）
BmsEolService.EnterDeepSleep(uds);                  // 31 01 74 13 0D
```

### 强控 MOS / BUCK（DID 0x73BA）

状态位：Bit0 充电 MOS、Bit1 放电 MOS、Bit2 BUCK；**1 = 断开，0 = 闭合**。

```csharp
// 充电 MOS、放电 MOS 断开，BUCK 闭合 → 2F 73 BA 03 03 07
BmsEolService.StrongControlMosBuck(uds, chargeMosClosed: false, dischargeMosClosed: false, buckClosed: true);

// 取消强控 → 2F 73 BA 00
BmsEolService.CancelStrongMosBuck(uds);
```

> 附录1 的控制示例把 DID 写成 `73 9A`，但 DID 表为 `73BA`，且 `739A` 是"单体电压 4"，写入会误伤。代码按 `73BA` 实现。

### 强控均衡（DID 0x73BB）

```csharp
BmsEolService.StrongControlBalance(uds, 0b0101);    // 电芯 1、3 开启 → 2F 73 BB 03 05 0F
BmsEolService.CancelStrongBalance(uds);             // 2F 73 BB 00
```

### SOC 标定 / 电流类型 / 电流标定

```csharp
BmsEolService.CalibrateSoc(uds, 100);               // 31 01 74 10 64
BmsEolService.SetCurrentType(uds, BmsCurrentType.Calibration200A);  // 2E 73 B5 55

int[] points = { -20629, -10305, -2045, -1011, -190, 223, 1050, 2080, 10340, 20665 };
BmsEolService.WriteCurrentCalibration(uds, points); // 2E 73 B6，每点 +1,000,000 后按 uint32 大端
int[] readBack = BmsEolService.ReadCurrentCalibration(uds);          // 每点 −1,000,000 → mA
```

### 序列号 / 硬件版本

```csharp
BmsEolService.WriteEcuSerial(uds, "10_807001954AA_11_9DQ_12_25090400001");   // 2E F1 8C，36 字节 ASCII
BmsEolService.WriteHardwareVersion(uds, "0.2.3");                            // 2E 73 B4 → 30 32 33（"023"）
```

## 4. 附录1 DID 解析表

| DID | 名称 | 长度 | 解析 | 示例 |
|---|---|---|---|---|
| 73B3 | APP 版本 | 17 | ASCII | `B2510_APP_R_2_1_1` |
| 73B2 | BOOT 版本 | 17 | ASCII | `B2510_PBL_V_2_1_0` |
| 7396 | 电流 | 4 | uint32 ×0.001 − 2000 A | 0x1E8482 → 0.002 A |
| 739B / 739C / 739E | 内总压 / 外总压 / 中间点 | 1 | uint8 ×0.1 V | 0x81 → 12.9 V |
| 7397~739A | 单体电压 1~4 | 2 | uint16 ×0.001 V | 0x0CBC → 3.260 V |
| 73A3~73A8 | 电池温度 0/1、放电/充电 MOS、限流模块、板子温度 | 1 | uint8 − 40 ℃ | 0x48 → 32 ℃ |
| 738F | MOS/BUCK 状态 | 1 | Bit0~1 充电、Bit2~3 放电、Bit4~7 BUCK | `1 闭合 / 2 断开` |
| 7391 | SOC | 1 | Bit0~6 SOC，Bit7 有效位 | 0xE4 → 100 % 有效 |
| 73A1 / 73A2 | 放电/充电 MOS G 极电压 | 1 | uint8 ×1 V | 0x19 → 25 V |
| 73AA | 唤醒源 | 1 | 0 上电 / 1 RTC / 2 硬线 / 3 CAN NM / … / 0B 借电启动 | 0x03 → CAN NM |
| 73B0 | 锁包状态 | 1 | 0 NotLock / 1 Lock | 1 → 锁包 |
| 73B5 | 电流类型 | 1 | 11/22/33/44/55/66 | 0x55 标定 200 A 档 |
| 73B6 | 电流标定 | 40 | 10×uint32，读后 −1,000,000 → mA | 1,000,223 → 223 mA |
| F18C | ECU 序列号 | 36 | ASCII | `10_807001954AA_11_9DQ_12_…` |
| F089 | 硬件版本（读） | 24 | ASCII，前 5 字节有效 | `0.2.2` |

`BmsDids.Parse(did, data)` 统一返回 `DidValue`：`Numeric`（物理值）、`Unit`、`Text`（可直接上屏）、`RawHex`、`Details`（位域/标定点明细）。

## 5. CAN 信号解析

DID 之外，CAN/CANFD 原始报文按 DBC 定义解析：

```csharp
// 信号定义：名称、起始位、长度、系数、偏移、单位、Intel/Motorola、是否有符号
var defs = new List<CanSignalDef>
{
    new("PackVoltage", 0, 16, 0.1, 0, "V"),                       // Intel，byte0 起
    new("PackCurrent", 16, 16, 0.1, -3200, "A", true, true),      // 有符号
    new("CellTemp", 32, 8, 1, -40, "℃"),
    new("RelayState", 40, 2, 1, 0, "", true) { ValueMap = new() { [0] = "断开", [1] = "闭合" } },
};

byte[] payload = frame.Data;
foreach (string text in CanSignalCodec.DecodeAll(payload, defs))
    TraceLog(text);
```

单值读取：`CanSignalCodec.ExtractRaw(data, startBit, length, intel)`、`ExtractSigned(...)`、`Decode(data, def)`、`ToPhysical(raw, factor, offset)`。

> 整车信号需按附录2 指定的 `B2510_智界EHV_CANFD通讯矩阵_V2.0.0.dbc` 填 `CanSignalDef`；附录1 只给了诊断 DID，没有给 CAN 矩阵信号表。

## 6. 验证情况

- `dotnet build`：0 错误，新增文件无警告；算法 DLL 已复制到 `bin\Debug\net10.0-windows\SecurityAlgorithm\QR_C1068\`。
- 用附录1 示例值做了解析回归：7396=0.002 A、739B=12.9 V、739C=13.0 V、7397=3.260 V、73A3=32 ℃、73A1=25 V、7391=100 %（有效）、F089="0.2.2"、73B6 首点 1,000,223→223 mA、738F/73AA/73B5/73BA/73BB 文本、Intel/Motorola 位域与有符号提取，全部通过。
- 算法库实测：PE32/x86、仅导出 `ZLGKey`；16 字节测试种子 → 返回码 0、密钥长度 16；`SeedKeyAlgorithm` 包装（显式路径与输出目录默认路径）均已跑通。
- ISO-TP 收发尚未实机联调（需 BMS 或仿真 ECU）。

## 7. 联调排查

| 现象 | 排查 |
|---|---|
| 请求发出无响应 | 确认通道已启动（硬件控制 → CAN-800H 调试顶部"接收线程运行中"）；确认收发 ID/标准帧/CANFD 与 ECU 一致 |
| 收到 `7F 10 22` | 条件不满足：确认已 `10 03`，且 ECU 上电状态允许 |
| 收到 `7F 27 33` | 安全访问未通过：检查 DLL 路径是否正确、进程是否 x86、种子是否 16 字节 |
| 收到 `7F 31 33` / `7F 2E 33` | 未解锁或 S3 超时：重新 `10 03` + 解锁 |
| 多帧写失败 | 检查流控：ECU 是否回 `30 00 00`，或 BS/STmin 非 0 导致节奏不符 |
| CANFD 长单帧被拒 | 附录1 的 36/40 字节写用的是 `00 + 长度` 单帧；若 ECU 只支持多帧，将 `IsoTpTransport` 的单帧阈值改为 7 |
| 73B6 读出异常 | 原始值需 −1,000,000；负值表示放电方向 |
| 48 工位串到别的工位 | 核对 `StationsPerBox` 与实际接线；工位 n 的 CAN 盒/通道由该参数决定 |

## 8. 待确认

1. **48 工位接线方式（已确认）**：6 台 CAN 盒 × 8 通道，48 工位占满 CAN1~CAN6，默认 `StationsPerBox=8` 不再改。
2. 是否需要新增「BMS 诊断」硬件控制页（会话/解锁/DID 读取/例程按钮/报文日志）；当前只落了 Models 层方法。
3. 算法库已从 FCT 目录复制进本项目，随构建发布；请确认允许随本软件分发。
4. `73A9`（均衡）附录未给解析规则；`19 02 09` 的 DTC 状态位定义也未给，目前只解析故障码与状态字节原值。

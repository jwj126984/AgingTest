using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace AgingTest.Models
{
    /// <summary>
    /// 厂商安全访问算法库（B2510_EHV_27_ZCANPRO.dll，导出函数 ZLGKey，32 位 x86）。
    /// 与本项目原有的 FCT 工具保持同一调用方式：ZLGKey(seed, seedSize, level, variant, key, ref keySize)。
    /// 算法库随输出目录部署在 SecurityAlgorithm\QR_C1068\。
    /// </summary>
    public static class SeedKeyAlgorithm
    {
        public const string DefaultRelativePath = @"SecurityAlgorithm\QR_C1068\B2510_EHV_27_ZCANPRO.dll";
        public const int KeyLength = 16;   // 附录1：Seed/Key 均 16 字节

        private static readonly object Sync = new();
        private static IntPtr _handle = IntPtr.Zero;
        private static string _loadedPath = "";
        private static ZlgKeyDelegate? _zlgKey;
        private static GenerateKeyExDelegate? _generateKeyEx;

        public static string? LastError { get; private set; }
        public static bool IsLoaded => _handle != IntPtr.Zero;
        public static string LoadedPath => _loadedPath;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ZlgKeyDelegate(byte[] seed, ushort seedSize, uint level, string? variant,
            byte[] key, ref ushort keySize);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GenerateKeyExDelegate(byte[] seed, uint seedSize, uint level, byte[] variant,
            byte[] key, uint maxKeySize, out uint actualKeySize);

        /// <summary>算法库绝对路径：优先显式配置，其次输出目录，最后回退到源码目录（VS 直接运行时）。</summary>
        public static string ResolvePath(string? path = null)
        {
            if (!string.IsNullOrWhiteSpace(path))
                return Path.GetFullPath(path);

            string deployed = Path.Combine(AppContext.BaseDirectory, DefaultRelativePath);
            if (File.Exists(deployed)) return deployed;

            string inSource = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\", DefaultRelativePath));
            return File.Exists(inSource) ? inSource : deployed;
        }

        /// <summary>预先加载算法库（可在界面初始化时调用，提前暴露路径/位数问题）。</summary>
        public static void Load(string? path = null)
        {
            lock (Sync) EnsureLoaded(path);
        }

        /// <summary>由 16 字节种子计算 16 字节密钥。</summary>
        public static byte[] Generate(byte[] seed, uint level = 1, string? path = null)
        {
            if (seed == null || seed.Length == 0) throw new ArgumentException("种子不能为空", nameof(seed));

            lock (Sync)
            {
                EnsureLoaded(path);

                byte[] key = new byte[KeyLength];
                if (_zlgKey != null)
                {
                    ushort keySize = KeyLength;
                    int ret = _zlgKey(seed, (ushort)seed.Length, level, null, key, ref keySize);
                    if (ret != 0) throw new InvalidOperationException($"ZLGKey 计算失败，返回 {ret}");
                    if (keySize == 0 || keySize > KeyLength)
                        throw new InvalidOperationException($"ZLGKey 返回密钥长度异常：{keySize}");
                    return key.Take(keySize).ToArray();
                }

                if (_generateKeyEx != null)
                {
                    int ret = _generateKeyEx(seed, (uint)seed.Length, level, seed, key, KeyLength, out uint actual);
                    if (ret != 0) throw new InvalidOperationException($"GenerateKeyEx 计算失败，返回 {ret}");
                    if (actual == 0 || actual > KeyLength)
                        throw new InvalidOperationException($"GenerateKeyEx 返回密钥长度异常：{actual}");
                    return key.Take((int)actual).ToArray();
                }

                throw new EntryPointNotFoundException("算法库未导出 ZLGKey / GenerateKeyEx");
            }
        }

        private static void EnsureLoaded(string? path)
        {
            string full = ResolvePath(path);
            if (_handle != IntPtr.Zero && string.Equals(_loadedPath, full, StringComparison.OrdinalIgnoreCase))
                return;

            UnloadCore();
            if (!File.Exists(full))
                throw new FileNotFoundException($"未找到安全访问算法库：{full}", full);

            try
            {
                _handle = NativeLibrary.Load(full);
            }
            catch (BadImageFormatException ex)
            {
                throw new InvalidOperationException(
                    "算法库位数与程序不匹配。该 DLL 为 32 位，AgingTest 必须以 x86 运行（csproj 已设置 PlatformTarget=x86）。", ex);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"加载算法库失败：{ex.Message}", ex);
            }

            if (NativeLibrary.TryGetExport(_handle, "ZLGKey", out IntPtr zlg))
                _zlgKey = Marshal.GetDelegateForFunctionPointer<ZlgKeyDelegate>(zlg);
            else if (NativeLibrary.TryGetExport(_handle, "GenerateKeyEx", out IntPtr gen))
                _generateKeyEx = Marshal.GetDelegateForFunctionPointer<GenerateKeyExDelegate>(gen);
            else
            {
                UnloadCore();
                throw new EntryPointNotFoundException($"算法库 {Path.GetFileName(full)} 未导出 ZLGKey / GenerateKeyEx");
            }

            _loadedPath = full;
            LastError = null;
        }

        /// <summary>卸载算法库（下次 Generate 会重新加载）。</summary>
        public static void Unload()
        {
            lock (Sync) UnloadCore();
        }

        private static void UnloadCore()
        {
            _zlgKey = null;
            _generateKeyEx = null;
            if (_handle != IntPtr.Zero)
            {
                try { NativeLibrary.Free(_handle); } catch { /* 忽略卸载异常 */ }
                _handle = IntPtr.Zero;
            }
            _loadedPath = "";
        }
    }
}

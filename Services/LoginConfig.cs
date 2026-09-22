using System;
using System.IO;
using System.Text.Json;

namespace AgingTest.Services
{
    /// <summary>
    /// 登录记忆配置（login.json）：记住上次登录的用户名；勾选"记住密码"时一并记住密码。
    /// 注意：密码为本地明文存储，仅用于现场演示/单机便捷登录，正式环境建议改用 Windows 凭据管理器（DPAPI）。
    /// </summary>
    public class LoginConfig
    {
        private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "login.json");

        /// <summary>上次登录的用户名（始终记住）</summary>
        public string UserName { get; set; } = "";

        /// <summary>是否勾选了记住密码</summary>
        public bool RememberPassword { get; set; }

        /// <summary>记住的密码（仅当勾选记住密码时保存）</summary>
        public string Password { get; set; } = "";

        public static LoginConfig Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var cfg = JsonSerializer.Deserialize<LoginConfig>(File.ReadAllText(FilePath));
                    if (cfg != null) return cfg;
                }
            }
            catch { /* 配置损坏时忽略，按空配置处理 */ }
            return new LoginConfig();
        }

        public void Save()
        {
            try
            {
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* 保存失败不影响登录流程 */ }
        }
    }
}

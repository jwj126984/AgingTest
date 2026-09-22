using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AgingTest.Models;

namespace AgingTest.Services
{
    /// <summary>
    /// 用户管理服务：账号增删改查 + 角色权限设置 + 本地 JSON 持久化（users.json）。
    /// 单例使用：UserManager.Instance。
    /// </summary>
    public class UserManager
    {
        public static UserManager Instance { get; } = new();

        private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "users.json");

        public List<UserInfo> Users { get; private set; } = new();

        private UserManager() { }

        /// <summary>加载用户列表；首次运行或文件损坏时使用默认账号并生成配置文件</summary>
        public void Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var loaded = JsonSerializer.Deserialize<List<UserInfo>>(File.ReadAllText(FilePath));
                    if (loaded != null && loaded.Count > 0)
                    {
                        Users = loaded;
                        return;
                    }
                }
                Users = UserInfo.DefaultUsers();
                Save();
            }
            catch
            {
                Users = UserInfo.DefaultUsers();
            }
        }

        /// <summary>保存到 users.json（程序运行目录）</summary>
        public void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(Users, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(FilePath, json);
            }
            catch
            {
                // 保存失败不阻塞界面，由调用方日志提示
            }
        }

        /// <summary>登录验证</summary>
        public UserInfo? Validate(string userName, string password)
            => Users.FirstOrDefault(u =>
                u.UserName.Equals(userName, System.StringComparison.OrdinalIgnoreCase) && u.Password == password);

        /// <summary>新增用户</summary>
        public bool AddUser(UserInfo user, out string error)
        {
            error = "";
            if (string.IsNullOrWhiteSpace(user.UserName)) { error = "用户名不能为空"; return false; }
            if (string.IsNullOrEmpty(user.Password)) { error = "密码不能为空"; return false; }
            if (Users.Any(u => u.UserName.Equals(user.UserName, StringComparison.OrdinalIgnoreCase)))
            { error = "用户名已存在"; return false; }

            user.Id = Guid.NewGuid().ToString("N");
            Users.Add(user);
            Save();
            return true;
        }

        /// <summary>修改用户（显示名/密码/角色），用户名不可改（作为登录标识）</summary>
        public bool UpdateUser(UserInfo user, out string error)
        {
            error = "";
            var target = Users.FirstOrDefault(u => u.Id == user.Id);
            if (target == null) { error = "用户不存在"; return false; }
            if (string.IsNullOrEmpty(user.Password)) { error = "密码不能为空"; return false; }

            target.DisplayName = user.DisplayName;
            target.Password = user.Password;
            target.Role = user.Role;
            Save();
            return true;
        }

        /// <summary>删除用户（保护：不能删除当前登录用户；系统至少保留一个工程师）</summary>
        public bool DeleteUser(string id, string currentUserId, out string error)
        {
            error = "";
            if (id == currentUserId) { error = "不能删除当前登录用户"; return false; }

            var target = Users.FirstOrDefault(u => u.Id == id);
            if (target == null) { error = "用户不存在"; return false; }

            if (target.Role == UserRole.Engineer && Users.Count(u => u.Role == UserRole.Engineer) <= 1)
            { error = "系统至少保留一个工程师账号"; return false; }

            Users.Remove(target);
            Save();
            return true;
        }
    }
}

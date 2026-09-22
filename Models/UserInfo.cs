using System;
using System.Collections.Generic;

namespace AgingTest.Models
{
    /// <summary>用户角色（三级权限）</summary>
    public enum UserRole
    {
        /// <summary>作业员：可扫码绑定、启停测试</summary>
        Operator,
        /// <summary>技术员：作业员权限 + 测试/机种参数设置</summary>
        Technician,
        /// <summary>工程师：技术员权限 + 系统管理（用户管理等）</summary>
        Engineer
    }

    /// <summary>登录用户信息</summary>
    public class UserInfo
    {
        /// <summary>用户唯一标识（持久化用）</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string UserName { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Password { get; set; } = "";
        public UserRole Role { get; set; }

        public string RoleName => Role switch
        {
            UserRole.Operator => "作业员",
            UserRole.Technician => "技术员",
            UserRole.Engineer => "工程师",
            _ => "未知"
        };

        /// <summary>
        /// 默认账号（首次运行无配置文件时使用，随后由用户管理功能维护并持久化到 users.json）。
        /// 作业员 operator / 技术员 tech / 工程师 engineer，密码均为 123456。
        /// </summary>
        public static List<UserInfo> DefaultUsers() => new()
        {
            new UserInfo { UserName = "operator", DisplayName = "作业员", Password = "123456", Role = UserRole.Operator },
            new UserInfo { UserName = "tech", DisplayName = "技术员", Password = "123456", Role = UserRole.Technician },
            new UserInfo { UserName = "engineer", DisplayName = "工程师", Password = "123456", Role = UserRole.Engineer }
        };
    }
}

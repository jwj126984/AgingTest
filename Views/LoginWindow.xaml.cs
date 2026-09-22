using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AgingTest.Models;
using AgingTest.Services;

namespace AgingTest.Views
{
    /// <summary>
    /// 用户登录窗口：三级权限（作业员/技术员/工程师）。
    /// 账号从 UserManager（users.json）校验，首次运行使用默认账号。
    /// 登录成功通过 <see cref="LoggedUser"/> 返回用户信息。
    /// </summary>
    public partial class LoginWindow : Window
    {
        /// <summary>登录成功的用户</summary>
        public UserInfo? LoggedUser { get; private set; }

        private bool _loading;   // 初始化阶段标志（避免构造时勾选事件误触发清密码）

        public LoginWindow()
        {
            InitializeComponent();
            _loading = true;
            LoadRemembered();
            _loading = false;
            Loaded += (_, _) =>
            {
                // 自动加载了用户名时，聚焦密码框；否则聚焦用户名框
                if (!string.IsNullOrEmpty(TxtUserName.Text))
                    PwdPassword.Focus();
                else
                    TxtUserName.Focus();
            };
        }

        /// <summary>加载上次登录记忆：用户名始终自动显示；勾选过记住密码时自动填入密码</summary>
        private void LoadRemembered()
        {
            var cfg = LoginConfig.Load();
            if (string.IsNullOrEmpty(cfg.UserName))
                return;

            TxtUserName.Text = cfg.UserName;
            ChkRemember.IsChecked = cfg.RememberPassword;
            if (cfg.RememberPassword)
                PwdPassword.Password = cfg.Password;
        }

        /// <summary>取消勾选"记住密码"时，立即清除已保存的密码</summary>
        private void ChkRemember_Checked(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            if (sender is CheckBox cb && cb.IsChecked != true)
            {
                var cfg = LoginConfig.Load();
                cfg.RememberPassword = false;
                cfg.Password = "";
                cfg.Save();
            }
        }

        /// <summary>登录成功后保存记忆配置</summary>
        private void SaveRemembered(string userName, string password, bool remember)
        {
            var cfg = LoginConfig.Load();
            cfg.UserName = userName;
            cfg.RememberPassword = remember;
            cfg.Password = remember ? password : "";
            cfg.Save();
        }

        private void BtnLogin_Click(object sender, RoutedEventArgs e) => DoLogin();

        private void Input_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                DoLogin();
        }

        private void DoLogin()
        {
            string name = TxtUserName.Text.Trim();
            string pwd = PwdPassword.Password;

            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(pwd))
            {
                TxtLoginMsg.Text = "请输入用户名和密码";
                return;
            }

            var user = UserManager.Instance.Validate(name, pwd);

            if (user == null)
            {
                TxtLoginMsg.Text = "用户名或密码错误，请重试";
                PwdPassword.Clear();
                PwdPassword.Focus();
                return;
            }

            LoggedUser = user;
            SaveRemembered(user.UserName, pwd, ChkRemember.IsChecked == true);
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}

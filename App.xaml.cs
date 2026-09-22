using System;
using System.IO;
using System.Windows;
using AgingTest.Services;
using AgingTest.Views;

namespace AgingTest
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public App()
        {
            DispatcherUnhandledException += (_, e) =>
            {
                File.WriteAllText(@"C:\Users\111111\AppData\Local\Temp\aging_error.txt", e.Exception.ToString());
            };
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                File.WriteAllText(@"C:\Users\111111\AppData\Local\Temp\aging_error_domain.txt",
                    (e.ExceptionObject as Exception)?.ToString() ?? e.ExceptionObject.ToString());
            };
        }

        /// <summary>启动流程：先加载用户数据，再登录，成功后再打开主界面</summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 加载/初始化用户账号（首次运行生成默认账号并写入 users.json）
            UserManager.Instance.Load();

            // 登录窗口关闭时避免触发"最后窗口关闭即退出"
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var login = new LoginWindow();
            if (login.ShowDialog() == true && login.LoggedUser != null)
            {
                var main = new MainWindow(login.LoggedUser);
                MainWindow = main;
                ShutdownMode = ShutdownMode.OnMainWindowClose;
                main.Show();
            }
            else
            {
                Shutdown();
            }
        }
    }
}

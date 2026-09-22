using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AgingTest.Models;
using AgingTest.Services;

namespace AgingTest.Views
{
    /// <summary>
    /// 用户管理窗口（仅工程师可见入口）：
    /// 新增 / 修改 / 删除用户，设置角色权限，数据持久化到 users.json。
    /// </summary>
    public partial class UserManageWindow : Window
    {
        private readonly UserInfo _currentUser;
        private bool _editMode;                 // true=修改模式 false=新增模式
        private UserInfo? _editingUser;         // 修改模式下的目标用户

        public UserManageWindow(UserInfo currentUser)
        {
            InitializeComponent();
            _currentUser = currentUser;
            TxtCurrentUser.Text = $"当前操作者：{currentUser.RoleName}（{currentUser.UserName}）";
            RefreshGrid();
            SetAddMode();
        }

        // ==================== 列表 ====================

        private void RefreshGrid()
        {
            UserGrid.ItemsSource = UserManager.Instance.Users.ToList();
            TxtUserCount.Text = $"共 {UserManager.Instance.Users.Count} 个账号";
        }

        // ==================== 模式切换 ====================

        private void SetAddMode()
        {
            _editMode = false;
            _editingUser = null;
            TxtFormTitle.Text = "新增用户";
            TxtUserName.Text = "";
            TxtDisplayName.Text = "";
            PwdPassword.Password = "";
            CmbRole.SelectedIndex = 0;
            TxtUserName.IsReadOnly = false;
        }

        private void SetEditMode(UserInfo u)
        {
            _editMode = true;
            _editingUser = u;
            TxtFormTitle.Text = $"修改用户：{u.UserName}";
            TxtUserName.Text = u.UserName;
            TxtDisplayName.Text = u.DisplayName;
            PwdPassword.Password = u.Password;
            CmbRole.SelectedIndex = (int)u.Role;
            TxtUserName.IsReadOnly = true;   // 用户名作为登录标识，不可修改
        }

        // ==================== 按钮事件 ====================

        private void BtnAdd_Click(object sender, RoutedEventArgs e) => SetAddMode();

        private void BtnEdit_Click(object sender, RoutedEventArgs e)
        {
            if (UserGrid.SelectedItem is not UserInfo u)
            {
                MessageBox.Show(this, "请先在列表中选择要修改的用户", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            SetEditMode(u);
        }

        private void UserGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (UserGrid.SelectedItem is UserInfo u)
                SetEditMode(u);
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (UserGrid.SelectedItem is not UserInfo u)
            {
                MessageBox.Show(this, "请先在列表中选择要删除的用户", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show(this,
                $"确定删除用户【{u.UserName}（{u.RoleName}）】？\n该操作不可恢复。",
                "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            if (UserManager.Instance.DeleteUser(u.Id, _currentUser.Id, out var error))
            {
                Log($"[用户管理] 删除用户 {u.UserName}（{u.RoleName}）");
                if (_editingUser?.Id == u.Id) SetAddMode();
                RefreshGrid();
            }
            else
            {
                MessageBox.Show(this, error, "无法删除", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            string name = TxtUserName.Text.Trim();
            string display = TxtDisplayName.Text.Trim();
            string pwd = PwdPassword.Password;
            if (CmbRole.SelectedItem is not ComboBoxItem item ||
                !System.Enum.TryParse<UserRole>((string)item.Tag, out var role))
            {
                role = UserRole.Operator;
            }

            bool ok;
            string error;
            if (_editMode && _editingUser != null)
            {
                _editingUser.DisplayName = display;
                _editingUser.Password = pwd;
                _editingUser.Role = role;
                ok = UserManager.Instance.UpdateUser(_editingUser, out error);
                if (ok) Log($"[用户管理] 修改用户 {name}：角色={role}，显示名={display}");
            }
            else
            {
                var user = new UserInfo { UserName = name, DisplayName = display, Password = pwd, Role = role };
                ok = UserManager.Instance.AddUser(user, out error);
                if (ok) Log($"[用户管理] 新增用户 {name}（{role}）");
            }

            if (!ok)
            {
                MessageBox.Show(this, error, "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SetAddMode();
            RefreshGrid();
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e) => SetAddMode();

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        // ==================== 辅助 ====================

        private void Log(string msg) => (Owner as MainWindow)?.AddLog(msg);
    }
}

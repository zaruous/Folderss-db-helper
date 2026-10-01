using Folderss.Plugins;
using Oracle.ManagedDataAccess.Client;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace MyPlugin
{
    /// <summary>
    /// 설정 창의 "Oracle 접속" 탭. 편집은 메모리 사본에 하고 [저장]을 누를 때만 플러그인 설정에 쓴다(취소하면 버려짐).
    /// </summary>
    public sealed class ConnectionSettingsPage : IPluginSettingsPage
    {
        private const int TestTimeoutSeconds = 10;

        private readonly IPluginManager _manager;
        private List<OracleConnectionProfile> _profiles;
        private Dictionary<OracleConnectionProfile, string> _newPasswords; // 이번에 입력한 비밀번호. 저장할 때 암호화한다
        private string _loadError;

        private ListBox _list;
        private TextBox _name, _host, _port, _service, _user;
        private PasswordBox _password;
        private TextBlock _passwordHint, _status;
        private FrameworkElement _editor;
        private Button _test, _cancel;
        private bool _filling;
        private int _testRun;

        public ConnectionSettingsPage(IPluginManager manager)
        {
            _manager = manager;
        }

        public string Title { get { return "Oracle 접속"; } }

        public FrameworkElement CreateView()
        {
            _newPasswords = new Dictionary<OracleConnectionProfile, string>();
            _loadError = null;
            try
            {
                _profiles = OracleConnectionStore.Deserialize(_manager.GetSetting(OracleConnectionStore.SettingKey));
            }
            catch (Exception ex)
            {
                _profiles = new List<OracleConnectionProfile>();
                _loadError = "저장된 접속 정보를 읽지 못했습니다: " + ex.Message;
            }

            var root = new Grid { Margin = new Thickness(8) };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            root.Children.Add(BuildListPane());
            var editor = BuildEditor();
            Grid.SetColumn(editor, 1);
            root.Children.Add(editor);

            foreach (var p in _profiles)
                _list.Items.Add(new ListBoxItem { Content = p.Name, Tag = p });
            if (_list.Items.Count > 0)
                _list.SelectedIndex = 0;
            else
                FillEditor(null);
            if (_loadError != null)
                SetStatus(_loadError + Environment.NewLine + "이 상태에서는 덮어쓰지 않도록 저장을 막습니다.");
            return root;
        }

        public void Save()
        {
            if (_profiles == null)
                return;
            if (_loadError != null)
                throw new InvalidOperationException("Oracle 접속: " + _loadError + " 기존 설정을 덮어쓰지 않았습니다.");
            var errors = OracleConnectionStore.Validate(_profiles);
            if (errors.Count > 0)
                throw new InvalidOperationException("Oracle 접속: " + string.Join(" ", errors));

            foreach (var pair in _newPasswords)
                pair.Key.ProtectedPassword = OracleConnectionStore.ProtectPassword(pair.Value);
            _manager.SetSetting(OracleConnectionStore.SettingKey, OracleConnectionStore.Serialize(_profiles));
            _newPasswords.Clear();
        }

        private FrameworkElement BuildListPane()
        {
            var pane = new DockPanel { Margin = new Thickness(0, 0, 8, 0) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            var add = new Button { Content = "추가", Padding = new Thickness(12, 2, 12, 2) };
            var remove = new Button { Content = "삭제", Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(6, 0, 0, 0) };
            add.Click += (s, e) => AddProfile();
            remove.Click += (s, e) => RemoveSelected();
            buttons.Children.Add(add);
            buttons.Children.Add(remove);
            DockPanel.SetDock(buttons, Dock.Bottom);
            pane.Children.Add(buttons);

            _list = new ListBox();
            _list.SelectionChanged += (s, e) => FillEditor(Selected);
            pane.Children.Add(_list);
            return pane;
        }

        private FrameworkElement BuildEditor()
        {
            var panel = new StackPanel();
            _name = AddField(panel, "이름");
            _host = AddField(panel, "호스트");
            _port = AddField(panel, "포트");
            _service = AddField(panel, "서비스명");
            _user = AddField(panel, "사용자");

            panel.Children.Add(Label("비밀번호"));
            _password = new PasswordBox();
            _password.PasswordChanged += (s, e) =>
            {
                var p = Selected;
                if (_filling || p == null)
                    return;
                if (_password.Password.Length > 0)
                    _newPasswords[p] = _password.Password;
                else
                    _newPasswords.Remove(p);
            };
            panel.Children.Add(_password);
            _passwordHint = Label("");
            _passwordHint.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
            panel.Children.Add(_passwordHint);

            _name.TextChanged += (s, e) => Edit(p =>
            {
                p.Name = _name.Text;
                ((ListBoxItem)_list.SelectedItem).Content = p.Name;
            });
            _host.TextChanged += (s, e) => Edit(p => p.Host = _host.Text);
            _port.TextChanged += (s, e) => Edit(p =>
            {
                int port;
                p.Port = int.TryParse(_port.Text.Trim(), out port) ? port : 0;
            });
            _service.TextChanged += (s, e) => Edit(p => p.ServiceName = _service.Text);
            _user.TextChanged += (s, e) => Edit(p => p.UserId = _user.Text);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            _test = new Button { Content = "접속 테스트", Padding = new Thickness(12, 2, 12, 2) };
            _cancel = new Button { Content = "취소", Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(6, 0, 0, 0), IsEnabled = false };
            _test.Click += Test_Click;
            _cancel.Click += (s, e) => CancelTest("테스트를 취소했습니다.");
            buttons.Children.Add(_test);
            buttons.Children.Add(_cancel);
            panel.Children.Add(buttons);

            _editor = panel;

            var outer = new StackPanel();
            outer.Children.Add(panel);
            _status = Label("");
            _status.Margin = new Thickness(0, 8, 0, 0);
            outer.Children.Add(_status);
            return outer;
        }

        private TextBox AddField(Panel panel, string label)
        {
            panel.Children.Add(Label(label));
            var box = new TextBox();
            panel.Children.Add(box);
            return box;
        }

        private static TextBlock Label(string text)
        {
            var block = new TextBlock { Text = text, Margin = new Thickness(0, 6, 0, 2), TextWrapping = TextWrapping.Wrap };
            block.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryText");
            return block;
        }

        private OracleConnectionProfile Selected
        {
            get
            {
                var item = _list.SelectedItem as ListBoxItem;
                return item == null ? null : (OracleConnectionProfile)item.Tag;
            }
        }

        private void Edit(Action<OracleConnectionProfile> apply)
        {
            var p = Selected;
            if (!_filling && p != null)
                apply(p);
        }

        private void FillEditor(OracleConnectionProfile p)
        {
            CancelTest(null);
            _filling = true;
            try
            {
                _editor.IsEnabled = p != null;
                _name.Text = p == null ? "" : p.Name;
                _host.Text = p == null ? "" : p.Host;
                _port.Text = p == null ? "" : p.Port.ToString();
                _service.Text = p == null ? "" : p.ServiceName;
                _user.Text = p == null ? "" : p.UserId;
                string typed;
                _password.Password = p != null && _newPasswords.TryGetValue(p, out typed) ? typed : "";
                _passwordHint.Text = p != null && p.ProtectedPassword != null
                    ? "저장된 비밀번호가 있습니다. 바꾸려면 새로 입력하세요."
                    : "";
            }
            finally
            {
                _filling = false;
            }
        }

        private void AddProfile()
        {
            var name = "새 접속";
            for (var n = 2; _profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)); n++)
                name = "새 접속 " + n;
            var profile = new OracleConnectionProfile { Name = name };
            _profiles.Add(profile);
            var item = new ListBoxItem { Content = name, Tag = profile };
            _list.Items.Add(item);
            _list.SelectedItem = item;
            _host.Focus();
        }

        private void RemoveSelected()
        {
            var item = _list.SelectedItem as ListBoxItem;
            if (item == null)
                return;
            var index = _list.SelectedIndex;
            var profile = (OracleConnectionProfile)item.Tag;
            _profiles.Remove(profile);
            _newPasswords.Remove(profile);
            _list.Items.Remove(item);
            if (_list.Items.Count > 0)
                _list.SelectedIndex = Math.Min(index, _list.Items.Count - 1);
        }

        private string PasswordFor(OracleConnectionProfile p)
        {
            string typed;
            if (_newPasswords.TryGetValue(p, out typed))
                return typed;
            if (p.ProtectedPassword == null)
                return null;
            try
            {
                return OracleConnectionStore.UnprotectPassword(p.ProtectedPassword);
            }
            catch (CryptographicException)
            {
                throw new InvalidOperationException("저장된 비밀번호를 풀 수 없습니다(다른 PC나 다른 Windows 사용자가 저장함). 비밀번호를 다시 입력하세요.");
            }
        }

        // async void 이벤트 처리기라 예외가 밖으로 나가면 Folderss가 종료된다. 전체를 try/catch로 감싼다.
        private async void Test_Click(object sender, RoutedEventArgs e)
        {
            var run = ++_testRun;
            try
            {
                var p = Selected;
                if (p == null)
                    return;
                var connectionString = OracleConnectionStore.BuildConnectionString(p, PasswordFor(p), TestTimeoutSeconds);

                _test.IsEnabled = false;
                _cancel.IsEnabled = true;
                SetStatus("접속하는 중… (최대 " + TestTimeoutSeconds + "초)");

                string result;
                try
                {
                    // 드라이버 버전에 따라 OpenAsync가 동기로 동작할 수 있어 Task.Run으로 UI 스레드를 비운다.
                    await Task.Run(() =>
                    {
                        using (var connection = new OracleConnection(connectionString))
                            connection.Open();
                    });
                    result = "접속 성공";
                }
                catch (Exception ex)
                {
                    result = "접속 실패: " + ex.Message;
                }

                if (run != _testRun)
                    return; // 취소했거나 다른 접속을 골랐음
                _test.IsEnabled = true;
                _cancel.IsEnabled = false;
                SetStatus(result);
            }
            catch (Exception ex)
            {
                if (run != _testRun)
                    return;
                _test.IsEnabled = true;
                _cancel.IsEnabled = false;
                SetStatus(ex.Message);
            }
        }

        /// <summary>진행 중인 테스트의 결과를 버린다. 접속 시도 자체는 시간 제한까지 백그라운드에서 끝난다.</summary>
        private void CancelTest(string message)
        {
            _testRun++;
            _test.IsEnabled = true;
            _cancel.IsEnabled = false;
            SetStatus(message ?? "");
        }

        private void SetStatus(string text)
        {
            _status.Text = text;
        }
    }
}

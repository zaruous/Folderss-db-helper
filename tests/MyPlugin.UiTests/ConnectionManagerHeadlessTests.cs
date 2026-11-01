using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using MyPlugin.UiTests.Headless;
using Xunit;

namespace MyPlugin.UiTests
{
    /// <summary>
    /// 접속 관리 창의 SQL Server 암호화·인증서 설정(#34)을 헤드리스로 띄워 확인한다: 공급자별 표시, 이전 접속의 호환 기본값과 강화 안내,
    /// 새 접속의 기본값(필수 + 검증), 엄격·신뢰에 따른 칸 끄기. FMON_UI_MSSQL(host:port/db)·FMON_UI_MSSQL_PW가 있으면
    /// [접속 테스트]로 자체 서명 인증서 서버에 대고 기본값은 실패(인증서 안내)·[서버 인증서 신뢰]는 성공하는지도 본다.
    /// </summary>
    public class ConnectionManagerHeadlessTests
    {
        static T Named<T>(DependencyObject root, string name) where T : FrameworkElement
            => Ui.Find<T>(root, e => AutomationProperties.GetName(e) == name);
        static string Mode(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string;
        static void Choose(ComboBox combo, string mode) => combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == mode);

        [UiFact]
        public async Task SqlServerSecurity_Fields_Defaults_And_ConnectionTest()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            plugin.Manager.SeedConnections(
                new SeedConnection { Name = "예전 MSSQL", Provider = "mssql", Port = 1433, Service = "tempdb", User = "sa" },
                new SeedConnection { Name = "오라클", Provider = "oracle", Port = 1521, Service = "xe", User = "scott" });
            Window window = null;
            try
            {
                ui.Run(() =>
                {
                    var show = plugin.Assembly.GetType("MyPlugin.ConnectionManager", true).GetMethod("Show");
                    // 모달 창 — 중첩 메시지 루프가 도는 동안에도 아래 ui.Run이 처리된다
                    ui.Dispatcher.BeginInvoke(new Action(() => show.Invoke(null, new object[] { null, plugin.Manager })));
                });
                await ui.WaitUntil(() => Application.Current.Windows.Cast<Window>().Any(w => w.IsVisible && w.Title.StartsWith("접속 관리")), TimeSpan.FromSeconds(10), "접속 관리 창");
                ComboBox encrypt = null; CheckBox trust = null; TextBox hostName = null; ListBox list = null;
                ui.Run(() =>
                {
                    window = Application.Current.Windows.Cast<Window>().First(w => w.IsVisible && w.Title.StartsWith("접속 관리"));
                    encrypt = Named<ComboBox>(window, "암호화");
                    trust = Ui.Find<CheckBox>(window, c => (c.Content as string)?.StartsWith("서버 인증서 신뢰") == true);
                    hostName = Named<TextBox>(window, "인증서 이름");
                    list = Named<ListBox>(window, "접속 목록");
                    Assert.NotNull(encrypt); Assert.NotNull(trust); Assert.NotNull(hostName); Assert.NotNull(list);

                    // 1) 이 설정이 생기기 전에 저장한 SQL Server 접속: 이전 동작(선택 + 신뢰)으로 읽고 강화 안내(경고색)
                    list.SelectedIndex = 0;
                    window.UpdateLayout();
                    Assert.Equal(Visibility.Visible, encrypt.Visibility);
                    Assert.Equal("optional", Mode(encrypt));
                    Assert.True(trust.IsChecked);
                    Assert.False(hostName.IsEnabled); // 신뢰가 켜져 있으면 인증서 이름은 쓰지 않는다
                    var note = Ui.FindAll<TextBlock>(window).FirstOrDefault(t => t.Foreground == plugin.Assembly.GetType("MyPlugin.Theme", true).GetField("Warning").GetValue(null) && t.Text.Length > 0 && t.IsVisible);
                    Assert.NotNull(note);
                    Ui.Snap(window, HeadlessEnvironment.WorkFolder, "conn-mssql-legacy");

                    // 2) Oracle 접속에는 SQL Server 칸이 없다
                    list.SelectedIndex = 1;
                    window.UpdateLayout();
                    Assert.Equal(Visibility.Collapsed, encrypt.Visibility);
                    Assert.Equal(Visibility.Collapsed, trust.Visibility);
                    Ui.Snap(window, HeadlessEnvironment.WorkFolder, "conn-oracle");

                    // 3) 새 접속을 SQL Server로: 필수 + 검증이 기본, 엄격이면 [서버 인증서 신뢰]를 끈다
                    Click(Ui.Find<Button>(window, b => (b.Content as string) == "추가"));
                    var provider = Named<ComboBox>(window, "DBMS");
                    provider.SelectedItem = provider.Items.Cast<object>().First(p => (string)p.GetType().GetProperty("Kind").GetValue(p) == "mssql");
                    window.UpdateLayout();
                    Assert.Equal(Visibility.Visible, encrypt.Visibility);
                    Assert.Equal("mandatory", Mode(encrypt));
                    Assert.False(trust.IsChecked == true);
                    Assert.True(trust.IsEnabled);
                    Assert.True(hostName.IsEnabled);
                    Choose(encrypt, "strict");
                    Assert.False(trust.IsEnabled);
                    Choose(encrypt, "mandatory");
                    Assert.True(trust.IsEnabled);
                    trust.IsChecked = true;
                    Assert.False(hostName.IsEnabled);
                    trust.IsChecked = false;
                    Assert.True(hostName.IsEnabled);
                    window.UpdateLayout();
                });
                // SQL Server 칸이 생겨도 입력 칸 전체(접속 테스트·안내 포함)가 스크롤 없이 보이게 창이 늘어난다
                await ui.WaitUntil(() =>
                {
                    var form = Ui.FindAll<ScrollViewer>(window).First(v => v.Content is FrameworkElement f && Ui.Descendants(f).Contains(encrypt));
                    return form.ExtentHeight - form.ViewportHeight <= 1;
                }, TimeSpan.FromSeconds(10), "입력 칸이 스크롤 없이 보임");
                ui.Run(() =>
                {
                    Ui.Snap(window, HeadlessEnvironment.WorkFolder, "conn-mssql-new");
                });

                // 4) 실제 SQL Server(자체 서명 인증서)에 [접속 테스트]
                var dsn = Environment.GetEnvironmentVariable("FMON_UI_MSSQL");
                if (!string.IsNullOrWhiteSpace(dsn))
                {
                    TextBlock result = null; Button test = null;
                    ui.Run(() =>
                    {
                        var hostPort = dsn.Split('/')[0];
                        Named<TextBox>(window, "이름").Text = "시험 MSSQL";
                        Named<TextBox>(window, "호스트").Text = hostPort.Split(':')[0];
                        Named<TextBox>(window, "포트").Text = hostPort.Split(':')[1];
                        Ui.FindAll<TextBox>(window).First(t => AutomationProperties.GetName(t) == "데이터베이스" || AutomationProperties.GetName(t) == "서비스명").Text = dsn.Split('/')[1];
                        Named<TextBox>(window, "사용자").Text = Environment.GetEnvironmentVariable("FMON_UI_MSSQL_USER") ?? "sa";
                        Named<PasswordBox>(window, "비밀번호").Password = Environment.GetEnvironmentVariable("FMON_UI_MSSQL_PW") ?? "";
                        test = Ui.Find<Button>(window, b => (b.Content as string) == "접속 테스트");
                        result = Ui.FindAll<TextBlock>(window).First(t => VisualParentIs(t, test.Parent));
                        Click(test);
                    });
                    var theme = plugin.Assembly.GetType("MyPlugin.Theme", true);
                    var success = theme.GetField("Success").GetValue(null);
                    var danger = theme.GetField("Danger").GetValue(null);
                    await ui.WaitUntil(() => result.Foreground == success || result.Foreground == danger, TimeSpan.FromSeconds(40), "기본값(검증) 접속 테스트 결과");
                    ui.Run(() =>
                    {
                        Assert.Same(danger, result.Foreground);
                        Assert.Contains("서버 인증서를 신뢰할 수 없습니다", result.Text);
                        Assert.DoesNotContain(Environment.GetEnvironmentVariable("FMON_UI_MSSQL_PW") ?? "\u0001", result.Text);
                        window.UpdateLayout();
                        Ui.Snap(window, HeadlessEnvironment.WorkFolder, "conn-mssql-test-untrusted");
                        trust.IsChecked = true;
                        Click(test);
                    });
                    await ui.WaitUntil(() => result.Foreground == success || (result.Foreground == danger && !result.Text.Contains("인증서")), TimeSpan.FromSeconds(40), "신뢰 켠 접속 테스트 결과");
                    ui.Run(() =>
                    {
                        Assert.True(result.Foreground == success, result.Text);
                        window.UpdateLayout();
                        Ui.Snap(window, HeadlessEnvironment.WorkFolder, "conn-mssql-test-trusted");
                    });
                }
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                // 바꾼 내용이 있으면 한 번은 묻고(창 안 안내) 다시 닫으면 버린다
                if (window != null) ui.Run(() => { window.Close(); if (window.IsVisible) window.Close(); });
            }
        }

        static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        static bool VisualParentIs(DependencyObject child, DependencyObject parent)
            => System.Windows.Media.VisualTreeHelper.GetParent(child) == parent;
    }
}

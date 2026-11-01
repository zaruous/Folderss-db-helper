using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MyPlugin.UiTests.Headless;
using Xunit;

namespace MyPlugin.UiTests
{
    /// <summary>
    /// 트리 검색: 검색어를 넣어도 최상위 DB 행은 숨지 않는다(일치가 없으면 흐리게·접힘). "일치 없음" 안내는 행과 겹치지 않게 그 아래에 둔다.
    /// 연결하지 않은 DB 두 개로 본다(검색 결과가 없는 DB — 연결 안 됨 안내도 함께).
    /// </summary>
    public class TreeSearchHeadlessTests
    {
        [UiFact]
        public async Task Search_KeepsTopLevelDbRows_NoticeBelowRows()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            plugin.Manager.SeedConnections(
                new SeedConnection { Name = "개발 DB", Service = "dev", User = "scott" },
                new SeedConnection { Name = "운영 DB", Service = "prod", User = "scott" });
            Window main = null;
            try
            {
                ListBox list = null; TextBox search = null;
                main = ui.Run(() => plugin.OpenView());
                await ui.WaitUntil(() => ui.Run(() => (list = Ui.Find<ListBox>(main, l => l.Items.Count >= 2)) != null), TimeSpan.FromSeconds(10), "트리 DB 행");
                ui.Run(() =>
                {
                    search = Ui.Find<TextBox>(main, t => (AutomationProperties.GetName(t) ?? "").StartsWith("트리 검색", StringComparison.Ordinal));
                    search.Text = "emp";
                });
                // 검색은 입력 뒤 잠깐 기다렸다 돈다
                await ui.WaitUntil(() => ui.Run(() => Ui.Texts(main).Any(t => t.Contains("일치하는 항목이 없습니다"))), TimeSpan.FromSeconds(10), "일치 없음 안내");
                ui.Run(() =>
                {
                    var texts = Ui.Texts(list);
                    Assert.Contains(texts, t => t.Contains("localhost:1521/dev")); // 이름은 강조용 Run이라 주소 글로 본다
                    Assert.Contains(texts, t => t.Contains("localhost:1521/prod"));
                    // 안내 글이 보이는 행들보다 아래에서 시작한다
                    var notice = Ui.FindAll<TextBlock>(main).First(t => t.IsVisible && t.Text.Contains("일치하는 항목이 없습니다"));
                    var lastRow = (FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(list.Items.Count - 1);
                    var noticeTop = notice.TranslatePoint(new Point(0, 0), list).Y;
                    var rowsBottom = lastRow.TranslatePoint(new Point(0, lastRow.ActualHeight), list).Y;
                    Assert.True(noticeTop >= rowsBottom, "안내 " + noticeTop + " < 행 끝 " + rowsBottom);
                    Ui.Snap(main, HeadlessEnvironment.WorkFolder, "tree-search-keeps-dbs");
                    search.Text = "";
                });
                await ui.WaitUntil(() => ui.Run(() => !Ui.Texts(main).Any(t => t.Contains("일치하는 항목이 없습니다"))), TimeSpan.FromSeconds(10), "검색 지움");
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                if (main != null) ui.Run(() => main.Close());
            }
        }
    }
}

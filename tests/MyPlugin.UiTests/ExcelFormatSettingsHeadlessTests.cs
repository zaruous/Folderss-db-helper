using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using ClosedXML.Excel;
using MyPlugin.UiTests.Headless;
using Xunit;

namespace MyPlugin.UiTests
{
    /// <summary>
    /// 설정 창 "엑셀 서식": 처음 등록 항목(기본 서식)은 고정·편집 불가, [파일 추가…]는 관리 폴더로 복사해 등록,
    /// 이름·대상 테이블 수정, 삭제 → [저장]하면 설정 값(resultExcel.formats)에 남고 지운 파일은 관리 폴더에서 빠진다.
    /// 첫 행 컬럼명이 없는 파일은 등록하지 않는다.
    /// </summary>
    public class ExcelFormatSettingsHeadlessTests
    {
        [UiFact]
        public async Task Registry_DefaultEntry_AddEditDelete_Save()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var work = Path.Combine(plugin.ShotFolder, "excel-settings");
            Directory.CreateDirectory(work);
            var good = Path.Combine(work, "사원 서식.xlsx");
            using (var book = new XLWorkbook())
            {
                var sheet = book.AddWorksheet("Data");
                sheet.Cell(1, 1).Value = "ID"; sheet.Cell(1, 2).Value = "NAME";
                book.SaveAs(good);
            }
            var bad = Path.Combine(work, "빈 파일.xlsx");
            using (var book = new XLWorkbook()) { book.AddWorksheet("Data"); book.SaveAs(bad); }

            Window window = null;
            object page = null;
            try
            {
                window = ui.Run(() =>
                {
                    page = plugin.Manager.SettingsPages.First(p => p.Title == "엑셀 서식");
                    var view = (FrameworkElement)page.GetType().GetMethod("CreateView").Invoke(page, null);
                    var w = new Window { Title = "설정 시험 — 엑셀 서식 탭", Content = view, Width = 640, Height = 480, ShowActivated = false, ShowInTaskbar = false };
                    w.SetResourceReference(Window.BackgroundProperty, "WindowBackground");
                    w.Show();
                    return w;
                });
                ListBox list = null;
                var start = 0;
                var settingBefore = plugin.Manager.GetSetting("resultExcel.formats") ?? "[]";
                ui.Run(() =>
                {
                    // 같은 실행의 다른 시험이 등록한 서식이 있을 수 있다 — 처음 개수 기준으로 본다
                    list = Ui.Find<ListBox>(window, l => AutomationProperties.GetName(l) == "등록한 엑셀 서식");
                    start = list.Items.Count;
                    Assert.True(start >= 1);
                    list.SelectedIndex = 0;
                    Assert.StartsWith("기본 서식", (string)((ListBoxItem)list.Items[0]).Content);
                    Assert.False(Ui.Find<Button>(window, b => AutomationProperties.GetAutomationId(b) == "excel-format-delete").IsEnabled);
                    Assert.False(Ui.Find<TextBox>(window, t => AutomationProperties.GetName(t) == "서식 이름").IsEnabled);
                });

                // 첫 행 컬럼명이 없는 파일은 등록하지 않는다(안내 창)
                ui.Post(() => Ui.Click(Ui.Find<Button>(window, b => AutomationProperties.GetAutomationId(b) == "excel-format-add")));
                await NativeDialogs.Open(ui, bad, TimeSpan.FromSeconds(30));
                var notice = await ui.WaitFor(() => Ui.Window("엑셀 서식"), TimeSpan.FromSeconds(30), "등록 거부 안내");
                ui.Run(() =>
                {
                    Assert.Contains(Ui.Texts(notice), t => t.Contains("컬럼명이 없습니다"));
                    notice.Close();
                });

                // 정상 파일 등록 → 관리 폴더에 복사, 이름·대상 테이블 수정
                ui.Post(() => Ui.Click(Ui.Find<Button>(window, b => AutomationProperties.GetAutomationId(b) == "excel-format-add")));
                await NativeDialogs.Open(ui, good, TimeSpan.FromSeconds(30));
                await ui.WaitUntil(() => list.Items.Count == start + 1, TimeSpan.FromSeconds(30), "서식 등록");
                var folder = Path.Combine(plugin.Manager.DataDirectory, "excel-formats");
                Assert.True(File.Exists(Path.Combine(folder, "사원 서식.xlsx")));
                ui.Run(() =>
                {
                    Assert.Equal(start, list.SelectedIndex);
                    Ui.Find<TextBox>(window, t => AutomationProperties.GetName(t) == "서식 이름").Text = "사원 등록 서식";
                    Ui.Find<TextBox>(window, t => AutomationProperties.GetName(t) == "대상 테이블").Text = "SCOTT.EMP";
                    Assert.Contains("SCOTT.EMP", (string)((ListBoxItem)list.Items[start]).Content);
                    window.UpdateLayout();
                    Ui.Snap(window, HeadlessEnvironment.WorkFolder, "excel-format-settings");
                    page.GetType().GetMethod("Save").Invoke(page, null);
                });
                var saved = plugin.Manager.GetSetting("resultExcel.formats");
                Assert.Contains("\"name\":\"사원 등록 서식\"", saved);
                Assert.Contains("\"table\":\"SCOTT.EMP\"", saved);

                // 삭제 → 저장하면 목록·파일에서 빠진다
                ui.Run(() =>
                {
                    list.SelectedIndex = start;
                    Ui.Click(Ui.Find<Button>(window, b => AutomationProperties.GetAutomationId(b) == "excel-format-delete"));
                });
                // 자동화 Invoke의 Click은 다음 디스패처 차례에 처리된다
                await ui.WaitUntil(() => list.Items.Count == start, TimeSpan.FromSeconds(30), "서식 삭제");
                ui.Run(() => page.GetType().GetMethod("Save").Invoke(page, null));
                Assert.DoesNotContain("사원 등록 서식", plugin.Manager.GetSetting("resultExcel.formats"));
                Assert.Equal(settingBefore == "[]" ? "[]" : null, settingBefore == "[]" ? plugin.Manager.GetSetting("resultExcel.formats") : null);
                Assert.False(File.Exists(Path.Combine(folder, "사원 서식.xlsx")));
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                if (window != null) ui.Run(() => window.Close());
            }
        }
    }
}

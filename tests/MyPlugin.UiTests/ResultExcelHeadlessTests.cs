using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ClosedXML.Excel;
using MyPlugin.UiTests.Headless;
using Xunit;

namespace MyPlugin.UiTests
{
    /// <summary>
    /// 결과 편집의 엑셀 서식 흐름을 실제 Oracle에서(FMON_UI_ORACLE): 빠른 조회 → [엑셀 내보내기 ▾] 기본 서식(숫자·날짜는 엑셀 값, 코드 "007"은 글자)
    /// → "이 결과로 서식 만들기"(설정 "엑셀 서식"에 등록) → 서식 파일을 사람처럼 다듬음(열 순서 바꿈·날짜 열 뺌) → 그 서식으로 내보내기(순서 유지·뺀 열 안내)
    /// → 그 서식대로 만든 파일을 [엑셀 임포트](빠진 날짜 열은 DB 기본값) → [저장]·[커밋] → 다시 조회해 확인.
    /// </summary>
    public partial class SqlEditorHeadlessTests
    {
        const string XlTable = "DBH_XL_IT";

        [UiFact(requiresOracle: true)]
        public async Task X_ResultExcel_FormatExportImport_Oracle()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var connection = SeedConnection.Oracle("XE 시험", HeadlessEnvironment.Oracle, HeadlessEnvironment.OracleUser, HeadlessEnvironment.OraclePassword, "green");
            var (main, editor) = await OpenSqlTab(ui, plugin, connection);
            var owner = HeadlessEnvironment.OracleUser.ToUpperInvariant();
            var folder = Path.Combine(plugin.ShotFolder, "excel");
            Directory.CreateDirectory(folder);
            try
            {
                ui.Run(() => Ui.Click(Ui.Button(main, "연결")));
                await ui.WaitUntil(() => Ui.Texts(main).Any(t => t.Contains("연결 1개")), Wait, "XE 연결");
                await RunScript(ui, main, editor,
                    "BEGIN EXECUTE IMMEDIATE 'DROP TABLE " + XlTable + "'; EXCEPTION WHEN OTHERS THEN NULL; END;\r\n/\r\n"
                    + "CREATE TABLE " + XlTable + " (ID NUMBER(10) PRIMARY KEY, CODE VARCHAR2(10), CREATED DATE DEFAULT SYSDATE);\r\n"
                    + "INSERT INTO " + XlTable + " VALUES (1, '007', TIMESTAMP '2026-11-01 09:30:00');\r\n"
                    + "INSERT INTO " + XlTable + " VALUES (2, 'A-1', NULL);\r\n"
                    + "COMMIT;\r\n");

                await QuickQueryXl(ui, main, connection.Id, owner);

                // 1) 기본 서식으로 내보내기
                var basic = Path.Combine(folder, "basic.xlsx");
                await ExportWith(ui, main, item => item.StartsWith("기본 서식", StringComparison.Ordinal), basic);
                using (var book = new XLWorkbook(basic))
                {
                    var sheet = book.Worksheet(1);
                    Assert.Equal(new[] { "ID", "CODE", "CREATED" }, Enumerable.Range(1, 3).Select(c => sheet.Cell(1, c).GetString()));
                    Assert.Equal(XLDataType.Number, sheet.Cell(2, 1).DataType);
                    Assert.Equal("007", sheet.Cell(2, 2).GetString());
                    Assert.Equal(new DateTime(2026, 11, 1, 9, 30, 0), sheet.Cell(2, 3).GetDateTime());
                    Assert.True(sheet.Cell(3, 3).IsEmpty());
                }

                // 2) 이 결과로 서식 만들기 → 설정 "엑셀 서식"에 등록
                await OpenExportMenu(ui, main);
                ui.Run(() => MenuItems(main).First(m => Header(m).StartsWith("이 결과로 서식 만들기", StringComparison.Ordinal)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)));
                await ui.WaitUntil(() => Ui.Texts(main).Any(t => t.Contains("엑셀 서식을 만들어 등록했습니다")), Wait, "서식 등록");
                var formats = Path.Combine(plugin.Manager.DataDirectory, "excel-formats");
                var formatFile = Directory.GetFiles(formats, "*.xlsx").Single();
                Assert.Contains("\"table\":\"" + owner + "." + XlTable + "\"", plugin.Manager.GetSetting("resultExcel.formats"));
                // 사람이 서식을 다듬는다: 열 순서(CODE, ID), 날짜 열 뺌, 2행 색
                using (var book = new XLWorkbook(formatFile))
                {
                    var sheet = book.Worksheet("Data");
                    sheet.Column(3).Delete();
                    sheet.Cell(1, 1).Value = "CODE"; sheet.Cell(1, 2).Value = "ID";
                    sheet.Column(1).Style.NumberFormat.Format = "@"; sheet.Column(2).Style.NumberFormat.Format = "0";
                    sheet.Range(2, 1, 2, 2).Style.Fill.BackgroundColor = XLColor.LightYellow;
                    book.Save();
                }

                // 3) 그 서식으로 내보내기: 서식의 열 순서·색 유지, 뺀 열은 안내
                var shaped = Path.Combine(folder, "shaped.xlsx");
                await ExportWith(ui, main, item => item.Contains("서식") && !item.StartsWith("기본", StringComparison.Ordinal) && !item.StartsWith("이 결과로", StringComparison.Ordinal), shaped);
                Assert.Contains(ui.Run(() => Ui.Texts(main)), t => t.Contains("서식에 없어 뺀 열: CREATED"));
                using (var book = new XLWorkbook(shaped))
                {
                    var sheet = book.Worksheet("Data");
                    Assert.Equal("007", sheet.Cell(2, 1).GetString());
                    Assert.Equal(1, sheet.Cell(2, 2).GetDouble());
                    Assert.Equal("A-1", sheet.Cell(3, 1).GetString());
                    Assert.Equal(XLColor.LightYellow, sheet.Cell(3, 2).Style.Fill.BackgroundColor);
                    // 4) 그 파일을 고쳐 새 행만 남겨 가져오기(빠진 CREATED는 DB 기본값 SYSDATE)
                    sheet.Rows(2, 3).Delete();
                    sheet.Cell(2, 1).Value = "009"; sheet.Cell(2, 2).Value = 3;
                    book.SaveAs(Path.Combine(folder, "import.xlsx"));
                }
                ui.Post(() => Ui.Click((ButtonBase)Ui.Find<Button>(main, b => AutomationProperties.GetAutomationId(b) == "excel-import")));
                _out.WriteLine("열기: " + await NativeDialogs.Open(ui, Path.Combine(folder, "import.xlsx"), Wait));
                await ui.WaitUntil(() => Ui.Texts(main).Any(t => t.Contains("엑셀에서 1개 행을 추가했습니다")), Wait, "가져오기");
                Assert.Contains(ui.Run(() => Ui.Texts(main)), t => t.Contains("엑셀에 없는 열(CREATED)"));
                _out.WriteLine("캡처: " + ui.Run(() => Ui.Snap(main, plugin.ShotFolder, "excel-imported")));

                // 5) [저장] → 확인 창 [실행] → [커밋]
                ui.Post(() => Ui.Click(Ui.Button(main, "저장")));
                var confirm = await ui.WaitFor(() => Ui.Window("결과 편집 저장"), Wait, "저장 확인 창");
                ui.Run(() => Ui.Click(Ui.Button(confirm, "실행")));
                await ui.WaitUntil(() => !confirm.IsVisible && Ui.Texts(main).Any(t => t.Contains("커밋 대기")), Wait, "INSERT");
                ui.Run(() => Ui.Click(Ui.Button(main, "커밋")));
                await ui.WaitUntil(() => !Ui.Texts(main).Any(t => t.Contains("커밋 대기")), Wait, "커밋");

                // 6) 다시 조회해 확인: 3행, ID 3 = '009', CREATED는 기본값으로 채워짐
                await QuickQueryXl(ui, main, connection.Id, owner);
                ui.Run(() =>
                {
                    var rows = ((IEnumerable)Get<object>(Get<object>(Get<object>(Workspace(main), "_active"), "ActiveResult"), "Rows")).Cast<object>()
                        .Select(r => (string[])r.GetType().GetProperty("Values").GetValue(r)).ToList();
                    Assert.Equal(3, rows.Count);
                    var added = rows.Single(v => v[0] == "3");
                    Assert.Equal("009", added[1]);
                    Assert.False(string.IsNullOrEmpty(added[2]));
                });
            }
            finally
            {
                try { await RunScript(ui, main, editor, "DROP TABLE " + XlTable + ";\r\n"); } catch (Exception ex) { _out.WriteLine("정리 실패: " + ex.Message); }
                ui.Run(() => main.Close());
            }
            Assert.Empty(ui.TakeErrors());
        }

        /// <summary>
        /// 편집기 글을 바꾸고 F5. DROP 같은 위험 문장은 실행 전에 확인 창(모달)을 띄우므로 키는 Post로 보내고(ui.Run이면 창이 닫힐 때까지 막힌다),
        /// 확인 창이 뜨면 사람처럼 체크하고 [실행]한다.
        /// </summary>
        async Task RunScript(UiThread ui, Window main, FrameworkElement editor, string script)
        {
            ui.Run(() =>
            {
                SetEditorText(editor, script);
                dynamic ed = editor;
                ed.Select(0, 0);
            });
            ui.Post(() => Press(Input(editor), Key.F5, ModifierKeys.None));
            var deadline = DateTime.UtcNow + Wait;
            while (true)
            {
                await Task.Delay(200);
                var confirmed = ui.Run(() =>
                {
                    var danger = Ui.Window("위험한 문장 실행");
                    if (danger == null) return false;
                    Ui.Find<CheckBox>(danger).IsChecked = true;
                    Ui.Click(Ui.Button(danger, "실행"));
                    return true;
                });
                if (confirmed) continue;
                if (ui.Run(() => !Get<bool>(Get<object>(Workspace(main), "_active"), "Running") && Ui.Window("위험한 문장 실행") == null))
                    break;
                if (DateTime.UtcNow > deadline) throw new TimeoutException("스크립트 실행 — " + Wait.TotalSeconds + "초 안에 끝나지 않았습니다.");
            }
            await Idle(ui);
        }

        async Task QuickQueryXl(UiThread ui, Window main, string dbId, string owner)
        {
            // 앞 결과가 남아 있어도 조건을 채우므로, 새 결과 객체가 들어올 때까지 기다린다
            var before = ui.Run(() => Get<object>(Get<object>(Workspace(main), "_active"), "ActiveResult"));
            // 연결 직후엔 트리 로딩이 세션을 써서 "다른 작업이 진행 중"으로 거부될 수 있다 — 사람처럼 다시 누른다
            var deadline = DateTime.UtcNow + Wait;
            while (ui.Run(() => ReferenceEquals(Get<object>(Get<object>(Workspace(main), "_active"), "ActiveResult"), before)))
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("빠른 조회 — 결과가 오지 않았습니다.");
                ui.Run(() => Workspace(main).GetType().GetMethod("QuickQuery").Invoke(Workspace(main), new object[] { dbId, owner, XlTable, null }));
                for (int i = 0; i < 25 && ui.Run(() => ReferenceEquals(Get<object>(Get<object>(Workspace(main), "_active"), "ActiveResult"), before)); i++)
                    await Task.Delay(200);
            }
            await ui.WaitUntil(() => !ReferenceEquals(Get<object>(Get<object>(Workspace(main), "_active"), "ActiveResult"), before)
                && ResultHeaders(main).Any(h => h.Contains("CODE"))
                && Ui.Find<Button>(main, b => AutomationProperties.GetAutomationId(b) == "excel-export" && b.IsVisible && b.IsEnabled) != null
                && !Get<bool>(Get<object>(Workspace(main), "_active"), "Running"), Wait, "빠른 조회 결과·편집 막대");
            await Idle(ui);
        }

        /// <summary>[엑셀 내보내기 ▾]를 누르고 메뉴가 채워질 때까지 기다린다(자동화 Invoke의 Click은 다음 디스패처 차례에 처리된다).</summary>
        static async Task OpenExportMenu(UiThread ui, Window main)
        {
            ui.Run(() =>
            {
                var button = Ui.Find<Button>(main, b => AutomationProperties.GetAutomationId(b) == "excel-export");
                button.ContextMenu.Items.Clear();
                Ui.Click(button);
            });
            await ui.WaitUntil(() => MenuItems(main).Count > 0, Wait, "엑셀 내보내기 메뉴");
        }

        static System.Collections.Generic.List<MenuItem> MenuItems(Window main)
            => Ui.Find<Button>(main, b => AutomationProperties.GetAutomationId(b) == "excel-export").ContextMenu.Items.OfType<MenuItem>().ToList();

        static string Header(MenuItem item) => item.Header as string ?? "";

        async Task ExportWith(UiThread ui, Window main, Func<string, bool> pick, string path)
        {
            if (File.Exists(path)) File.Delete(path);
            await OpenExportMenu(ui, main);
            var item = ui.Run(() => MenuItems(main).First(m => pick(Header(m))));
            _out.WriteLine("내보내기 서식: " + ui.Run(() => Header(item)));
            ui.Post(() => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)));
            _out.WriteLine("저장: " + await NativeDialogs.SaveAs(ui, path, Wait));
            await ui.WaitUntil(() => File.Exists(path) && Ui.Texts(main).Any(t => t.Contains("엑셀로") && t.Contains("행을 저장했습니다")), Wait, "엑셀 저장 " + Path.GetFileName(path));
        }
    }
}

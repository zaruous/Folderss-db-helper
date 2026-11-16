using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Data.Sqlite;
using MyPlugin.UiTests.Headless;
using Xunit;

namespace MyPlugin.UiTests
{
    /// <summary>
    /// SQLite 접속을 실제 화면으로(DB 서버 없이 — CI의 DB 없는 단계에서 돈다): 없는 파일에 [연결] → "새 SQLite DB" 확인 창 → [만들기] → 연결,
    /// 미리 만든 샘플 DB에 연결해 트리거가 든 스크립트(F5) 실행·결과, 트리, F4 테이블 정보 창(구조 편집 없이 읽기 전용). 단계마다 캡처를 남긴다.
    /// </summary>
    public partial class SqlEditorHeadlessTests
    {
        [UiFact]
        public async Task L_Sqlite_CreateConnectRunTreeDescribe()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            var folder = Path.Combine(Path.GetTempPath(), "dbh-ui-sqlite-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var created = Path.Combine(folder, "new.db");
            var sample = Path.Combine(folder, "sample.db");
            using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sample, Pooling = false }.ToString()))
            {
                c.Open();
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "CREATE TABLE dept (id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE);"
                        + "CREATE TABLE emp (id INTEGER PRIMARY KEY, name TEXT NOT NULL, dept_id INTEGER REFERENCES dept(id), salary NUMERIC DEFAULT 0);"
                        + "CREATE INDEX emp_dept ON emp(dept_id);"
                        + "CREATE VIEW v_emp AS SELECT e.id, e.name, d.name AS dept FROM emp e JOIN dept d ON d.id = e.dept_id;"
                        + "INSERT INTO dept VALUES (1, '개발'), (2, '운영');"
                        + "INSERT INTO emp VALUES (1, '김', 1, 100), (2, '이', 2, 200);";
                    cmd.ExecuteNonQuery();
                }
            }
            var newDb = new SeedConnection { Name = "새 SQLite", Provider = "sqlite", Host = "", Port = 0, Service = created, User = "", Color = "green" };
            var sampleDb = new SeedConnection { Name = "샘플 SQLite", Provider = "sqlite", Host = "", Port = 0, Service = sample, User = "" };
            var (main, editor) = await OpenSqlTab(ui, plugin, newDb, sampleDb);
            Window info = null;
            try
            {
                // 1) 없는 파일 → 만들지 묻는다(모달이라 Post로 누른다)
                ui.Run(() => ChooseTarget(main, "새 SQLite"));
                ui.Post(() => Ui.Click(Ui.Button(main, "연결")));
                var ask = await ui.WaitFor(() => Ui.Window("새 SQLite DB"), Wait, "새 SQLite DB 확인 창");
                ui.Run(() =>
                {
                    Assert.Contains(Ui.Texts(ask), t => t.Contains(created));
                    Assert.False(File.Exists(created));   // 묻기 전에 만들지 않는다
                    Ui.Snap(ask, HeadlessEnvironment.WorkFolder, "sqlite-create-confirm");
                    Ui.Click(Ui.Button(ask, "만들기"));
                });
                await ui.WaitUntil(() => Ui.Texts(main).Any(t => t.Contains("연결 1개")), Wait, "새 파일 연결");
                Assert.True(File.Exists(created));

                // 2) 샘플 DB에 연결해 트리거가 든 스크립트 실행(F5) — 트리거 본문의 ';'에서 잘리면 실패한다
                // [연결]은 트리에서 고른 DB를 연결한다 — 트리에서 샘플 DB 행을 고른다
                ui.Run(() =>
                {
                    var tree = Ui.Find<ListBox>(main, l => l.Items.Count >= 2);
                    for (var i = 0; i < tree.Items.Count; i++)
                    {
                        var row = tree.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
                        if (row != null && Ui.Texts(row).Any(t => t.Contains("sample.db")))
                        {
                            tree.SelectedIndex = i;
                            break;
                        }
                    }
                    ChooseTarget(main, "샘플 SQLite");
                });
                await Idle(ui);
                ui.Run(() => Ui.Click(Ui.Button(main, "연결")));
                await ui.WaitUntil(() => Ui.Texts(main).Any(t => t.Contains("연결 2개")), Wait, "샘플 연결");
                await RunScript(ui, main, editor,
                    "CREATE TRIGGER emp_ins AFTER INSERT ON emp\r\nBEGIN\r\n  UPDATE emp SET salary = CASE WHEN new.salary IS NULL THEN 0 ELSE new.salary END WHERE id = new.id;\r\n\r\n  SELECT 1;\r\nEND;\r\n"
                    + "INSERT INTO emp (id, name, dept_id, salary) VALUES (3, '박', 1, NULL);\r\n"
                    + "COMMIT;\r\n"
                    + "SELECT e.id, e.name, e.salary AS salary_after_trigger, d.name AS dept FROM emp e JOIN dept d ON d.id = e.dept_id ORDER BY e.id;\r\n");
                await ui.WaitUntil(() => ResultHeaders(main).Any(h => h.Contains("salary_after_trigger")), Wait, "조회 결과");
                ui.Run(() =>
                {
                    Assert.Contains(Ui.Texts(main), t => t.Contains("sample.db"));   // 트리·상태에 파일 경로
                    _out.WriteLine("캡처: " + Ui.Snap(main, HeadlessEnvironment.WorkFolder, "sqlite-run-result"));
                });
                using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sample, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
                {
                    c.Open();
                    using (var cmd = c.CreateCommand())
                    {
                        cmd.CommandText = "SELECT salary FROM emp WHERE id = 3";
                        Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar()));   // 트리거가 돌았다
                        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'trigger'";
                        Assert.Equal(1L, Convert.ToInt64(cmd.ExecuteScalar()));
                    }
                }

                // 3) F4: SQLite는 구조 편집 없이 "테이블 정보" 창(읽기 전용)
                ui.Run(() =>
                {
                    SetEditorText(editor, "SELECT * FROM emp");
                    dynamic ed = editor;
                    ed.TextArea.Caret.Offset = "SELECT * FROM e".Length;
                    Press(Input(editor), Key.F4, ModifierKeys.None);
                });
                info = await ui.WaitFor(() => Ui.Window("테이블 정보 — ") ?? Ui.Window("테이블 편집 — "), Wait, "테이블 정보 창");
                await Idle(ui);
                await ui.WaitUntil(() => Ui.Texts(info).Any(t => t.Contains("dept_id")), Wait, "F4 컬럼");
                ui.Run(() =>
                {
                    Assert.StartsWith("테이블 정보 — ", info.Title);
                    Assert.Contains("emp", info.Title);
                    _out.WriteLine("캡처: " + Ui.Snap(info, HeadlessEnvironment.WorkFolder, "sqlite-f4-info"));
                });
                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                ui.Run(() =>
                {
                    if (info != null)
                        info.Close();
                    main.Close();
                });
                SqliteConnection.ClearAllPools();
                try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}

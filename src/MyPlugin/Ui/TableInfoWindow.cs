using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MyPlugin
{
    /// <summary>
    /// F4 테이블 정보 창(모달 아님 — 열어 둔 채 SQL을 쓸 수 있다, Esc로 닫음).
    /// 위: 이름·종류·통계·주석. 가운데: TableSchemaEditor(왼쪽 탭 [컬럼]·[인덱스]·[제약 조건] 표).
    /// Oracle 테이블이고 읽기 전용 접속이 아니며 editSchema가 있으면 표 안에서 구조를 고치고 [저장](Ctrl+S)으로 DDL을 실행한다
    /// (저장 때 검증 → 실행할 SQL 확인 → 차례로 실행, 문장마다 성공하면 커밋 — Oracle은 DDL 자동 커밋, PostgreSQL·SQL Server는 저장 경로가 커밋). 아래: [SELECT 문 넣기]·[빠른 조회]·[저장]·[닫기].
    /// </summary>
    internal static class TableInfoWindow
    {
        public static Window Show(Window owner, TableDescription info, OracleConnectionProfile profile, Action insertSelect, Action quickQuery, Action<string> copy, Func<string, Task> editSchema = null, Func<Task<TableDescription>> reload = null)
        {
            // 편집: 쓰기 가능한 접속의 테이블이고, 그 DBMS가 구조 편집 DDL을 지원할 때(Oracle·PostgreSQL·SQL Server).
            var ddl = profile == null ? null : DbProviders.For(profile).TableSchema?.Ddl;
            bool editable = editSchema != null && info.Type == "TABLE" && profile != null && !profile.ReadOnly && ddl != null;
            var qualified = WorkspaceLogic.QualifiedName(info.Owner, info.Name);
            var titleBase = (editable ? "테이블 편집 — " : "테이블 정보 — ") + qualified + (profile != null ? " (" + profile.Name + ")" : "");
            var window = new Window { Title = titleBase, Width = 1180, Height = 720, MinWidth = 820, MinHeight = 480, ShowInTaskbar = false, ResizeMode = ResizeMode.CanResizeWithGrip };
            Theme.ApplyWindow(window);
            DialogKit.SetOwner(window, owner);

            var root = new DockPanel { Margin = new Thickness(12) };
            var header = Header(info, qualified, profile);
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var editor = new TableSchemaEditor(editable ? ddl : null);
            var save = DialogKit.PrimaryButton("저장");
            save.Padding = new Thickness(14, 3, 14, 3);
            save.Margin = new Thickness(0, 0, 6, 0);
            save.IsEnabled = false;
            save.ToolTip = "검사한 뒤 실행할 DDL을 보여 주고 확인을 받습니다 (Ctrl+S)";
            bool saving = false;

            var buttons = Buttons(window, insertSelect, quickQuery);
            if (editable) buttons.Children.Insert(buttons.Children.Count - 1, save);
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);
            editor.View.Margin = new Thickness(0, 10, 0, 0);
            root.Children.Add(editor.View);
            window.Content = root;

            editor.Changed += () =>
            {
                var count = editor.ChangeCount;
                save.Content = count == 0 ? "저장" : "저장 (" + count.ToString(CultureInfo.InvariantCulture) + ")";
                save.IsEnabled = editable && count > 0 && !saving;
                window.Title = (count > 0 ? "* " : "") + titleBase;
            };
            editor.Load(info);

            save.Click += async (s, e) =>
            {
                if (saving || !editable) return;
                if (!editor.Validate()) return;
                var steps = SchemaPlan.Build(editor.Context, editor.Remove);
                if (steps.Count == 0) return;
                if (!Dialogs.ConfirmSchemaChanges(window, profile, qualified, steps.Select(step => step.Sql.TrimEnd().TrimEnd(';') + ";").ToList(), steps.Count(step => step.Destructive)))
                    return;

                saving = true;
                editor.View.IsEnabled = false;
                save.IsEnabled = false;
                int done = 0;
                try
                {
                    foreach (var step in steps)
                    {
                        editor.SetStatus("저장 중… (" + (done + 1).ToString(CultureInfo.InvariantCulture) + "/" + steps.Count.ToString(CultureInfo.InvariantCulture) + ")");
                        await editSchema(step.Sql);
                        step.Applied();
                        done++;
                    }
                    if (reload != null)
                    {
                        info = await reload();
                        editor.Load(info);
                    }
                }
                catch (Exception ex)
                {
                    var failed = done < steps.Count ? steps[done].Sql : null;
                    Dialogs.Show(window, "저장 실패",
                        (failed != null ? "실행하지 못한 문장:\n" + failed + "\n\n" : "") + ex.Message
                        + "\n\n" + (done > 0 ? "앞의 " + done.ToString(CultureInfo.InvariantCulture) + "문장은 반영되었습니다. " : "")
                        + "남은 변경은 표에 그대로 있으니 고친 뒤 다시 저장하세요.", true);
                }
                finally
                {
                    saving = false;
                    editor.View.IsEnabled = true;
                    editor.ClearErrors(); // 상태 문구·저장 단추 갱신
                }
            };

            window.PreviewKeyDown += (s, e) =>
            {
                if (!editable || saving) return;
                if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    e.Handled = true;
                    if (save.IsEnabled) save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
            };
            window.Closing += (s, e) =>
            {
                if (saving) { e.Cancel = true; return; }
                editor.CommitEdits();
                var count = editor.ChangeCount;
                if (count > 0 && !Dialogs.Confirm(window, "테이블 편집", "저장하지 않은 변경 " + count.ToString(CultureInfo.InvariantCulture) + "건이 있습니다. 버리고 닫을까요?", "버리고 닫기", danger: false))
                    e.Cancel = true;
            };
            window.Show();
            return window;
        }

        private static FrameworkElement Header(TableDescription info, string qualified, OracleConnectionProfile profile)
        {
            var panel = new StackPanel();
            var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            if (profile != null) { var badge = Theme.DbBadge(profile); DockPanel.SetDock(badge, Dock.Right); heading.Children.Add(badge); }
            var title = Theme.Text("▤  " + qualified + "   ");
            title.FontSize = 17; title.FontWeight = FontWeights.SemiBold;
            var kind = Theme.Secondary(info.Type ?? "");
            kind.VerticalAlignment = VerticalAlignment.Bottom; kind.Margin = new Thickness(0, 0, 0, 2);
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(title); line.Children.Add(kind);
            heading.Children.Add(line);
            panel.Children.Add(heading);
            var facts = Theme.Secondary(TableInfoText.Facts(info));
            panel.Children.Add(facts);
            if (!string.IsNullOrWhiteSpace(info.Comment)) { var comment = Theme.Text(info.Comment); comment.Margin = new Thickness(0, 4, 0, 0); comment.TextWrapping = TextWrapping.Wrap; panel.Children.Add(comment); }
            var border = new Border { Child = panel, Padding = new Thickness(12, 10, 12, 10), BorderThickness = new Thickness(1) };
            border.SetResourceReference(Border.BackgroundProperty, Theme.SurfaceBackground);
            border.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
            return border;
        }

        private static StackPanel Buttons(Window window, Action insertSelect, Action quickQuery)
        {
            var insert = new Button { Content = "SELECT 문 넣기", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 6, 0) };
            insert.Click += (s, e) => insertSelect();
            var quick = new Button { Content = "빠른 조회 (앞 " + QueryOptions.QuickRows.ToString(CultureInfo.InvariantCulture) + "행)", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 6, 0) };
            quick.Click += (s, e) => quickQuery();
            var close = new Button { Content = "닫기", Padding = new Thickness(16, 3, 16, 3), IsCancel = true };
            close.Click += (s, e) => window.Close();
            var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            panel.Children.Add(insert);
            panel.Children.Add(quick);
            panel.Children.Add(close);
            return panel;
        }
    }
}

using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MyPlugin.UiTests.Headless;
using Xunit;

namespace MyPlugin.UiTests
{
    public class SchemaGridHeadlessTests
    {
        static object Get(object row, string name) => row.GetType().GetProperty(name).GetValue(row);
        static void Set(object row, string name, object value) => row.GetType().GetProperty(name).SetValue(row, value);
        static T ById<T>(DependencyObject root, string id) where T : FrameworkElement => Ui.Find<T>(root, e => AutomationProperties.GetAutomationId(e) == id);
        static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        static void Menu(DataGrid grid, string header) =>
            grid.ContextMenu.Items.OfType<MenuItem>().First(m => (string)m.Header == header).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        static FrameworkElement Code(DataGrid grid, string field) => (FrameworkElement)Ui.Descendants(grid).FirstOrDefault(e => e.GetType().Name == "CodePopup" && AutomationProperties.GetAutomationId(e) == "code-" + field);
        static Popup PopupOf(object code) => (Popup)code.GetType().GetField("popup", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(code);
        static void Open(object code) { code.GetType().GetMethod("Open").Invoke(code, null); PopupOf(code).Child.UpdateLayout(); }
        static Border Row(Popup popup, string name) => Ui.FindAll<Border>(popup.Child).First(b => AutomationProperties.GetName(b) == name);
        static string Mark(Border row) => Ui.FindAll<TextBlock>(row).First().Text;
        static void Press(UIElement row) => row.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
        static void KeyPress(UIElement target, Key key) => target.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(target) ?? PresentationSource.CurrentSources.Cast<PresentationSource>().First(), 0, key) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
        static DataGrid Grid(Window window, int section) => ById<DataGrid>(window, "schema-grid-" + section);

        [UiFact]
        public async Task AddInGrid_ValidateOnSave_DeleteInRow_KeysAndIndexPicker()
        {
            var ui = UiThread.Instance;
            var plugin = PluginUnderTest.Load(ui);
            Window window = null;
            try
            {
                window = ui.Run(() =>
                {
                    var type = plugin.Assembly.GetType("MyPlugin.TableDescription", true);
                    var info = Activator.CreateInstance(type, true);
                    type.GetProperty("Owner").SetValue(info, "O"); type.GetProperty("Name").SetValue(info, "T"); type.GetProperty("Type").SetValue(info, "TABLE");
                    var profile = Activator.CreateInstance(plugin.Assembly.GetType("MyPlugin.OracleConnectionProfile", true));
                    profile.GetType().GetProperty("Name").SetValue(profile, "Oracle XE");
                    return (Window)plugin.Assembly.GetType("MyPlugin.TableInfoWindow", true).GetMethod("Show").Invoke(null,
                        new object[] { null, info, profile, (Action)(() => { }), (Action)(() => { }), (Action<string>)(_ => { }), (Func<string, Task>)(_ => Task.CompletedTask), null });
                });
                DataGrid columns = null;

                // 1) 표 아래 [＋ 컬럼 추가] → 빈 행이 생기고 이름 칸 편집이 시작된다.
                ui.Run(() =>
                {
                    columns = Grid(window, 0);
                    Assert.Equal(Dock.Left, Ui.Find<TabControl>(window).TabStripPlacement);
                    Click(ById<Button>(window, "schema-add-0"));
                });
                await ui.WaitUntil(() => columns.Items.Count == 1 && columns.CurrentCell.Column?.SortMemberPath == "Name" && Ui.Find<TextBox>(columns) != null, TimeSpan.FromSeconds(10), "새 행 이름 칸 편집");
                object first = null;
                ui.Run(() =>
                {
                    first = columns.Items[0];
                    var editor = Ui.Find<TextBox>(columns);
                    editor.Text = "FIRST_ID";
                    columns.CommitEdit(DataGridEditingUnit.Cell, true); columns.CommitEdit(DataGridEditingUnit.Row, true);
                    Assert.Equal("FIRST_ID", Get(first, "Name"));
                    Assert.Equal("추가", Get(first, "State"));
                    var container = (DataGridRow)columns.ItemContainerGenerator.ContainerFromItem(first);
                    Assert.Same(plugin.Assembly.GetType("MyPlugin.Theme", true).GetField("InsertedRow").GetValue(null), container.Background);
                    Assert.False((bool)Get(first, "HasErrors")); // 편집 중에는 검사하지 않는다
                });

                // 2) [저장] → 자료형이 비어 있어 대화상자 없이 칸·탭·오류 목록에 표시된다.
                ui.Run(() =>
                {
                    var save = Ui.FindAll<Button>(window).First(b => (b.Content as string)?.StartsWith("저장") == true);
                    Assert.True(save.IsEnabled);
                    Click(save);
                    Assert.True((bool)Get(first, "HasErrors"));
                    Assert.Single(((System.Collections.IEnumerable)first.GetType().GetMethod("GetErrors").Invoke(first, new object[] { "DataType" })).Cast<object>());
                    Assert.Equal(Visibility.Visible, ById<Border>(window, "schema-errors").Visibility);
                    Assert.DoesNotContain(Application.Current.Windows.Cast<Window>(), w => w != window && w.IsVisible);
                    window.UpdateLayout();
                    Ui.Snap(window, HeadlessEnvironment.WorkFolder, "schema-editor-errors");
                    // 그 칸을 고치면 오류가 사라진다.
                    Set(first, "DataType", "NUMBER");
                    Assert.False((bool)Get(first, "HasErrors"));
                    Assert.Equal(Visibility.Collapsed, ById<Border>(window, "schema-errors").Visibility);
                });

                // 3) 두 번째 컬럼을 넣고 행 끝 [✕]로 지우면(새 행) 바로 빠진다.
                ui.Run(() => Click(ById<Button>(window, "schema-add-0")));
                await ui.WaitUntil(() => columns.Items.Count == 2, TimeSpan.FromSeconds(10), "두 번째 컬럼");
                ui.Run(() =>
                {
                    columns.CommitEdit(DataGridEditingUnit.Cell, true); columns.CommitEdit(DataGridEditingUnit.Row, true);
                    var second = columns.Items[1];
                    Set(second, "Name", "SECOND_ID"); Set(second, "DataType", "NUMBER");
                    Set(second, "NotNull", true);
                    Assert.Equal("N", Get(second, "Nullable"));
                    window.UpdateLayout();
                    Ui.Snap(window, HeadlessEnvironment.WorkFolder, "schema-editor-columns");

                    // 세 번째를 넣었다가 ✕로 뺀다.
                    Click(ById<Button>(window, "schema-add-0"));
                    columns.CommitEdit(DataGridEditingUnit.Cell, true); columns.CommitEdit(DataGridEditingUnit.Row, true);
                    window.UpdateLayout();
                    var third = columns.Items[2];
                    var row = (DataGridRow)columns.ItemContainerGenerator.ContainerFromItem(third);
                    Click(Ui.FindAll<Button>(row).First(b => (b.Content as string) == "✕"));
                    Assert.Equal(2, columns.Items.Count);
                });

                // 4) 두 컬럼을 골라 우클릭 메뉴로 기본 키 → 제약 조건 탭에 복합 키 행.
                DataGrid keys = null;
                ui.Run(() =>
                {
                    columns.SelectAll();
                    Menu(columns, "선택 컬럼으로 기본 키 만들기");
                });
                await ui.WaitUntil(() => Ui.Find<TabControl>(window).SelectedIndex == 2 && Grid(window, 2) != null, TimeSpan.FromSeconds(10), "제약 조건 탭");
                ui.Run(() =>
                {
                    keys = Grid(window, 2);
                    keys.CommitEdit(DataGridEditingUnit.Cell, true); keys.CommitEdit(DataGridEditingUnit.Row, true);
                    var key = Assert.Single(keys.Items.Cast<object>());
                    Assert.Equal("P", Get(key, "Type"));
                    Assert.Equal("\"FIRST_ID\", \"SECOND_ID\"", Get(key, "Columns"));
                });
                // 처음 보이는 탭에서도 고정 폭 열이 최소 폭으로 줄어든 채 남지 않는다.
                await ui.WaitUntil(() => keys.Columns.Where(c => c.Width.IsAbsolute).All(c => Math.Abs(c.Width.Value - c.ActualWidth) < 1), TimeSpan.FromSeconds(10), "제약 조건 열 폭");
                ui.Run(() =>
                {
                    keys.UnselectAll(); window.UpdateLayout();
                    Ui.Snap(window, HeadlessEnvironment.WorkFolder, "schema-editor-constraints");
                    Ui.Find<TabControl>(window).SelectedIndex = 0;
                });

                // 5) 인덱스: 메뉴로 만들고 코드 보기 팝업에서 순서·DESC를 바꾼다. 행 어디를 눌러도 체크가 바뀐다.
                ui.Run(() => { columns.SelectAll(); Menu(columns, "선택 컬럼으로 인덱스 만들기"); });
                await ui.WaitUntil(() =>
                {
                    var grid = Grid(window, 1);
                    return Ui.Find<TabControl>(window).SelectedIndex == 1 && grid != null && Code(grid, "Columns") != null;
                }, TimeSpan.FromSeconds(10), "인덱스 컬럼 칸");
                DataGrid indexes = null; FrameworkElement code = null; Popup popup = null;
                ui.Run(() =>
                {
                    indexes = Grid(window, 1);
                    indexes.CommitEdit(DataGridEditingUnit.Cell, true); indexes.CommitEdit(DataGridEditingUnit.Row, true);
                    code = Code(indexes, "Columns");
                    popup = PopupOf(code);
                    Open(code);
                });
                await ui.WaitUntil(() => popup.IsOpen, TimeSpan.FromSeconds(10), "컬럼 팝업 열기");
                ui.Run(() =>
                {
                    var index = indexes.Items[0];
                    Assert.Equal("☑ 1", Mark(Row(popup, "FIRST_ID")));
                    Assert.Equal("☑ 2", Mark(Row(popup, "SECOND_ID")));
                    Click(Ui.FindAll<Button>(Row(popup, "FIRST_ID")).First(b => (string)b.Content == "ASC"));
                    Click(Ui.FindAll<Button>(Row(popup, "FIRST_ID")).First(b => (string)b.Content == "↓"));
                    Assert.Equal("\"FIRST_ID\", \"SECOND_ID\"", Get(index, "Columns")); // [확인] 전에는 값이 바뀌지 않는다
                    window.UpdateLayout(); popup.Child.UpdateLayout();
                    Ui.SnapElement((FrameworkElement)popup.Child, window.Background, HeadlessEnvironment.WorkFolder, "schema-index-column-picker");
                    Click(Ui.FindAll<Button>(popup.Child).First(b => (string)b.Content == "확인"));
                    Assert.False(popup.IsOpen);
                    Assert.Equal("\"SECOND_ID\", \"FIRST_ID\" DESC", Get(index, "Columns"));

                    // 행(이름 글자가 아닌 설명 쪽)을 눌러도 체크가 풀리고, Space로 다시 체크, [취소]는 버린다.
                    Open(code);
                    Press(Row(popup, "SECOND_ID"));
                    Assert.Equal("☐", Mark(Row(popup, "SECOND_ID")));
                    var search = Ui.FindAll<TextBox>(popup.Child).First();
                    search.Text = "SECOND";
                    KeyPress(search, Key.Space);
                    Assert.Equal("☑ 2", Mark(Row(popup, "SECOND_ID")));
                    KeyPress(search, Key.Escape);
                    Assert.False(popup.IsOpen);
                    Assert.Equal("\"SECOND_ID\", \"FIRST_ID\" DESC", Get(index, "Columns"));
                    indexes.UnselectAll(); window.UpdateLayout();
                    Ui.Snap(window, HeadlessEnvironment.WorkFolder, "schema-editor-indexes");
                });

                // 6) 단일 선택: 제약 종류는 누르면 바로 바뀌고 닫힌다. 자료형은 목록에 없는 값을 그대로 쓸 수 있다.
                ui.Run(() =>
                {
                    Ui.Find<TabControl>(window).SelectedIndex = 2;
                });
                await ui.WaitUntil(() => Grid(window, 2) != null && Code(Grid(window, 2), "Type") != null, TimeSpan.FromSeconds(10), "종류 칸");
                ui.Run(() =>
                {
                    var keys2 = Grid(window, 2);
                    var type = Code(keys2, "Type"); var typePopup = PopupOf(type);
                    Assert.Contains(Ui.FindAll<TextBlock>(type), t => t.Text.Contains("기본 키"));
                    // 칸 글자를 눌러 연다(Open 직접 호출이 아닌 클릭 경로 — 표 칸이 마우스를 잡아도 뗀 뒤에 열린다).
                    var label = Ui.FindAll<TextBlock>(type).First(t => t.Text.Contains("기본 키"));
                    // 실제 입력처럼 PreviewMouseDown(터널)·MouseDown(버블)로 — 표 칸(DataGridCell)의 누름 처리도 함께 돈다.
                    label.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent });
                    label.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseDownEvent });
                    label.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseUpEvent });
                });
                await ui.WaitUntil(() => PopupOf(Code(Grid(window, 2), "Type")).IsOpen, TimeSpan.FromSeconds(10), "칸 클릭으로 종류 팝업 열기");
                await Task.Delay(300);
                ui.Run(() =>
                {
                    var keys2 = Grid(window, 2);
                    var type = Code(keys2, "Type"); var typePopup = PopupOf(type);
                    Assert.True(typePopup.IsOpen); // 열린 채로 남는다
                    typePopup.Child.UpdateLayout();
                    Press(Row(typePopup, "U"));
                    Assert.False(typePopup.IsOpen);
                    Assert.Equal("U", Get(keys2.Items[0], "Type"));
                    Ui.Find<TabControl>(window).SelectedIndex = 0;
                });
                await ui.WaitUntil(() => Code(columns, "DataType") != null, TimeSpan.FromSeconds(10), "자료형 칸");
                ui.Run(() =>
                {
                    var dataType = Code(columns, "DataType"); var typePopup = PopupOf(dataType);
                    Open(dataType);
                    var search = Ui.FindAll<TextBox>(typePopup.Child).First();
                    search.Text = "VARCHAR2(30 CHAR)";
                    KeyPress(search, Key.Enter); // 첫 행 = "입력한 그대로 쓰기"
                    Assert.False(typePopup.IsOpen);
                    Assert.Equal("VARCHAR2(30 CHAR)", Get(columns.Items[0], "DataType"));
                });

                Assert.Empty(ui.TakeErrors());
            }
            finally
            {
                if (window != null) ui.Run(() =>
                {
                    // 변경을 버린 뒤 닫는다(닫기 확인 창을 띄우지 않게).
                    for (int section = 0; section < 3; section++)
                    {
                        Ui.Find<TabControl>(window).SelectedIndex = section;
                        window.UpdateLayout();
                        var grid = Grid(window, section);
                        if (grid == null || grid.Items.Count == 0) continue;
                        grid.SelectAll();
                        Menu(grid, "선택 행 되돌리기");
                    }
                    window.Close();
                });
            }
        }
    }
}

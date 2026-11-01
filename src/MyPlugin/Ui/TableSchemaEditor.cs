using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MyPlugin
{
    /// <summary>
    /// 테이블 정보 창 가운데: 왼쪽 세로 탭 [컬럼]·[인덱스]·[제약 조건]과 표.
    /// 편집 가능하면 표 안에서 바로 고친다 — 맨 아래 [＋ 추가] 줄(Insert), 행 끝 [✕]/[↶](Delete), 체크박스·종류는 한 번 클릭.
    /// 검증은 하지 않고 두었다가 [저장] 때 Validate가 한꺼번에 하고, 틀린 칸을 빨갛게 표시하며 아래 오류 목록에서 그 칸으로 간다.
    /// </summary>
    internal sealed class TableSchemaEditor
    {
        static readonly string[] Titles = { "컬럼", "인덱스", "제약 조건" };
        static readonly string[] Icons = { "▤", "≡", "◇" };
        static readonly Dictionary<string, string> FieldTitles = new Dictionary<string, string>
        {
            { nameof(SchemaRow.Name), "이름" }, { nameof(ColumnRow.DataType), "자료형" }, { nameof(ColumnRow.Default), "기본값" },
            { nameof(ColumnRow.Comment), "주석" }, { nameof(IndexRow.Columns), "컬럼" }, { nameof(ConstraintRow.Type), "종류" },
            { nameof(ConstraintRow.Reference), "참조" }, { nameof(ConstraintRow.Condition), "CHECK 조건" }
        };
        static readonly CodeItem[] ConstraintTypes =
        {
            new CodeItem("P", "기본 키 (PK)"), new CodeItem("U", "고유 (UK)"),
            new CodeItem("R", "외래 키 (FK)"), new CodeItem("C", "검사 (CHECK)")
        };
        readonly bool editable;
        /// <summary>DBMS별 DDL 방언(읽기 전용이면 null).</summary>
        readonly ISchemaDdl ddl;
        readonly ObservableCollection<ColumnRow> columns = new ObservableCollection<ColumnRow>();
        readonly ObservableCollection<IndexRow> indexes = new ObservableCollection<IndexRow>();
        readonly ObservableCollection<ConstraintRow> constraints = new ObservableCollection<ConstraintRow>();
        readonly IList[] lists;
        readonly DataGrid[] grids = new DataGrid[3];
        readonly TextBlock[] counts = new TextBlock[3];
        readonly TextBlock[] badges = new TextBlock[3];
        readonly TabControl tabs;
        readonly ObservableCollection<SchemaError> errors = new ObservableCollection<SchemaError>();
        readonly Border errorPanel;
        readonly TextBlock errorTitle;
        readonly TextBlock status;
        string owner, table;
        bool loading;

        /// <summary>ddl이 null이면 읽기 전용(표만 보인다).</summary>
        public TableSchemaEditor(ISchemaDdl ddl)
        {
            this.ddl = ddl;
            editable = ddl != null;
            lists = new IList[] { columns, indexes, constraints };

            tabs = new TabControl { TabStripPlacement = Dock.Left, BorderThickness = new Thickness(1), Padding = new Thickness(0) };
            tabs.SetResourceReference(Control.BackgroundProperty, Theme.PanelBackground);
            tabs.SetResourceReference(Control.BorderBrushProperty, Theme.Border);
            tabs.ItemContainerStyle = TabStyle();
            for (int section = 0; section < 3; section++) tabs.Items.Add(Tab(section));
            AutomationProperties.SetAutomationId(tabs, "schema-tabs");

            errorTitle = new TextBlock { Foreground = Theme.Danger, FontWeight = FontWeights.SemiBold };
            var list = new ItemsControl { ItemsSource = errors, ItemTemplate = ErrorTemplate(), Margin = new Thickness(0, 4, 0, 0) };
            var errorBody = new DockPanel();
            DockPanel.SetDock(errorTitle, Dock.Top); errorBody.Children.Add(errorTitle);
            errorBody.Children.Add(new ScrollViewer { Content = list, MaxHeight = 110, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
            errorPanel = new Border { Child = errorBody, BorderBrush = Theme.Danger, BorderThickness = new Thickness(1), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
            errorPanel.SetResourceReference(Border.BackgroundProperty, Theme.SurfaceBackground);
            AutomationProperties.SetAutomationId(errorPanel, "schema-errors");

            status = Theme.Secondary("");
            status.VerticalAlignment = VerticalAlignment.Center;
            var footer = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            if (editable)
            {
                var revert = new Button { Content = "↶ 모든 변경 취소", Padding = new Thickness(10, 2, 10, 2), ToolTip = "저장하지 않은 추가·수정·삭제를 모두 버립니다." };
                revert.Click += (s, e) => RevertAll(ask: true);
                DockPanel.SetDock(revert, Dock.Right); footer.Children.Add(revert);
            }
            footer.Children.Add(status);

            var view = new DockPanel();
            DockPanel.SetDock(footer, Dock.Bottom); view.Children.Add(footer);
            DockPanel.SetDock(errorPanel, Dock.Bottom); view.Children.Add(errorPanel);
            view.Children.Add(tabs);
            View = view;
        }

        public FrameworkElement View { get; }
        /// <summary>행이 추가·수정·삭제될 때(저장 단추·창 제목 갱신용).</summary>
        public event Action Changed;
        public SchemaContext Context => new SchemaContext { Owner = owner, Table = table, Ddl = ddl ?? new OracleSchemaDdl(), Columns = columns, Indexes = indexes, Constraints = constraints };
        public int ChangeCount => Context.All.Count(r => r.Dirty);
        public int SelectedSection { get => Math.Max(0, tabs.SelectedIndex); set => tabs.SelectedIndex = value; }
        public DataGrid GridOf(int section) => grids[section];

        public void Load(TableDescription info)
        {
            loading = true;
            owner = info.Owner; table = info.Name;
            foreach (var list in lists) list.Clear();
            errors.Clear();
            foreach (var row in SchemaPlan.Load(info, ddl).All) Attach(row);
            loading = false;
            Refresh();
        }

        void Attach(SchemaRow row)
        {
            if (row is ConstraintRow key && ddl != null) key.DisableRule = ddl.CanDisable;
            row.PropertyChanged += (s, e) => { if (!loading) Refresh(); };
            lists[row.Section].Add(row);
        }

        // ───────── 편집 동작 ─────────

        /// <summary>현재 탭에 빈 행을 넣고 이름 칸을 편집한다.</summary>
        public SchemaRow Add(int section)
        {
            CommitEdits();
            SchemaRow row;
            if (section == 0) row = new ColumnRow(true);
            else if (section == 1) row = new IndexRow(true, NewName(1, "IX"));
            else
            {
                var hasKey = constraints.Any(c => !c.Deleted && c.Type == "P");
                row = new ConstraintRow(true, NewName(2, hasKey ? "UK" : "PK"), hasKey ? "U" : "P");
            }
            Attach(row);
            Refresh();
            Focus(row, nameof(SchemaRow.Name), edit: true);
            return row;
        }

        /// <summary>선택 행 삭제 표시(새 행은 바로 뺀다). 모두 삭제 표시된 상태면 삭제를 취소한다.</summary>
        public void ToggleDelete(int section, IEnumerable<SchemaRow> rows)
        {
            CommitEdits();
            var selected = rows.ToArray();
            if (selected.Length == 0) return;
            bool restore = selected.All(r => r.Deleted);
            foreach (var row in selected)
            {
                if (row.IsNew) lists[section].Remove(row);
                else row.Deleted = !restore;
            }
            Refresh();
        }

        /// <summary>표에서 행을 뺀다(DROP이 끝난 행).</summary>
        public void Remove(SchemaRow row) { lists[row.Section].Remove(row); Refresh(); }

        /// <summary>선택 행을 DB 값으로 되돌린다(새 행은 뺀다).</summary>
        public void RevertRows(int section, IEnumerable<SchemaRow> rows)
        {
            grids[section].CancelEdit();
            foreach (var row in rows.ToArray())
            {
                if (row.IsNew) lists[section].Remove(row);
                else row.Revert();
            }
            Refresh();
        }

        public void RevertAll(bool ask)
        {
            var count = ChangeCount;
            if (count == 0) return;
            if (ask && !Dialogs.Confirm(Window.GetWindow(View), "변경 취소", "저장하지 않은 변경 " + count.ToString(CultureInfo.InvariantCulture) + "건을 모두 버릴까요?", "모두 버리기", danger: false))
                return;
            foreach (var grid in grids) grid.CancelEdit();
            for (int section = 0; section < 3; section++) RevertRows(section, lists[section].Cast<SchemaRow>().ToArray());
            errors.Clear();
            Refresh();
        }

        /// <summary>컬럼 탭에서 고른 컬럼으로 기본 키·고유 제약·인덱스를 만든다.</summary>
        void CreateFromColumns(string what)
        {
            CommitEdits();
            var picked = columns.Where(r => grids[0].SelectedItems.Contains(r) && !r.Deleted && !string.IsNullOrWhiteSpace(r.Name)).ToArray();
            if (picked.Length == 0) { status.Text = "이름을 입력한 컬럼을 먼저 선택하세요."; return; }
            var list = TableSchemaLogic.IndexColumns(picked.Select(r => new IndexColumnChoice { Name = r.Name.Trim() }));
            SchemaRow row;
            switch (what)
            {
                case "PK": row = new ConstraintRow(true, NewName(2, "PK"), "P", list); break;
                case "UK": row = new ConstraintRow(true, NewName(2, "UK"), "U", list); break;
                case "UX": row = new IndexRow(true, NewName(1, "UX"), true, list); break;
                default: row = new IndexRow(true, NewName(1, "IX"), false, list); break;
            }
            Attach(row);
            Refresh();
            Focus(row, nameof(SchemaRow.Name), edit: true);
        }

        string NewName(int section, string prefix)
        {
            var names = new HashSet<string>(lists[section].Cast<SchemaRow>().Select(r => r.Name.Trim()), StringComparer.Ordinal);
            var name = prefix + "_" + table;
            for (int n = 1; names.Contains(name); n++) name = prefix + "_" + table + "_" + n.ToString(CultureInfo.InvariantCulture);
            return name;
        }

        public void CommitEdits()
        {
            foreach (var grid in grids)
            {
                grid.CommitEdit(DataGridEditingUnit.Cell, true);
                grid.CommitEdit(DataGridEditingUnit.Row, true);
            }
        }

        // ───────── 저장 검증 ─────────

        /// <summary>저장 전 검증: 오류를 칸·탭·아래 목록에 표시하고 첫 오류 칸으로 간다. 오류가 없으면 true.</summary>
        public bool Validate()
        {
            CommitEdits();
            var found = SchemaPlan.Validate(Context);
            errors.Clear();
            foreach (var error in found.OrderBy(e => e.Row.Section).ThenBy(e => lists[e.Row.Section].IndexOf(e.Row))) errors.Add(error);
            Refresh();
            if (errors.Count == 0) return true;
            Focus(errors[0].Row, errors[0].Field, edit: false);
            return false;
        }

        public void ClearErrors()
        {
            foreach (var row in Context.All) row.ClearErrors();
            errors.Clear();
            Refresh();
        }

        public void SetStatus(string text) { status.Text = text; }

        /// <summary>그 행이 있는 탭으로 가서 칸을 고르고(edit면 편집 시작) 보이게 한다.</summary>
        public void Focus(SchemaRow row, string field, bool edit)
        {
            var grid = grids[row.Section];
            tabs.SelectedIndex = row.Section;
            grid.SelectedItem = row;
            // 스크롤은 표가 배치된 뒤에 한다: 처음 보이는 탭에서 미리 하면 열 폭이 0 기준으로 계산되어 모두 최소 폭이 된다.
            var column = grid.Columns.FirstOrDefault(c => c.SortMemberPath == field) ?? grid.Columns.FirstOrDefault(c => c.SortMemberPath == nameof(SchemaRow.Name));
            grid.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (column != null) grid.ScrollIntoView(row, column); else grid.ScrollIntoView(row);
                grid.Focus();
                if (column == null) return;
                grid.CurrentCell = new DataGridCellInfo(row, column);
                if (!edit || !editable || row.Deleted) return;
                if (!column.IsReadOnly) grid.BeginEdit();
                else CodeIn(grid, grid.CurrentCell)?.Open();
            }));
        }

        void Refresh()
        {
            if (loading) return;
            for (int section = 0; section < 3; section++)
            {
                var rows = lists[section].Cast<SchemaRow>().ToArray();
                counts[section].Text = rows.Length.ToString(CultureInfo.InvariantCulture);
                var changed = rows.Count(r => r.Dirty);
                var failed = rows.Count(r => r.HasErrors);
                badges[section].Text = failed > 0 ? "⚠ " + failed.ToString(CultureInfo.InvariantCulture) : changed > 0 ? "● " + changed.ToString(CultureInfo.InvariantCulture) : "";
                badges[section].Foreground = failed > 0 ? Theme.Danger : Theme.Warning;
                badges[section].ToolTip = failed > 0 ? "오류 " + failed + "건" : changed > 0 ? "변경 " + changed + "건" : null;
            }
            // 고친 칸의 오류는 목록에서 뺀다(행이 빠졌거나 칸 오류가 사라짐).
            foreach (var error in errors.Where(e => !lists[e.Row.Section].Contains(e.Row) || !e.Row.GetErrors(e.Field).Cast<object>().Any()).ToArray()) errors.Remove(error);
            errorPanel.Visibility = errors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            errorTitle.Text = "⚠ 저장하지 못했습니다 — 고칠 곳 " + errors.Count.ToString(CultureInfo.InvariantCulture) + "건 (누르면 그 칸으로 갑니다)";
            var count = ChangeCount;
            status.Text = !editable ? "읽기 전용 — 쓰기 가능한 접속(Oracle·PostgreSQL·SQL Server)의 테이블만 구조를 고칠 수 있습니다."
                : count == 0 ? "칸을 더블클릭하거나 바로 입력해 고칩니다 · Insert 행 추가 · Delete 삭제 · 우클릭 메뉴"
                : "변경 " + count.ToString(CultureInfo.InvariantCulture) + "건 — Ctrl+S로 저장합니다(검사는 저장할 때 합니다).";
            Changed?.Invoke();
        }

        // ───────── 화면 조립 ─────────

        TabItem Tab(int section)
        {
            var header = new Grid { Width = 150, Margin = new Thickness(8, 8, 8, 8) };
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(Theme.Text(Icons[section] + "  " + Titles[section]));
            badges[section] = new TextBlock { Margin = new Thickness(6, 0, 6, 0), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(badges[section], 1); header.Children.Add(badges[section]);
            counts[section] = Theme.Secondary("0");
            Grid.SetColumn(counts[section], 2); header.Children.Add(counts[section]);

            var grid = NewGrid(section);
            grids[section] = grid;
            var content = new DockPanel();
            if (editable)
            {
                var add = new Button
                {
                    Content = new TextBlock { Text = "＋  " + Titles[section] + " 추가" },
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(8, 6, 8, 6),
                    BorderThickness = new Thickness(0, 1, 0, 0),
                    Background = Brushes.Transparent,
                    Cursor = Cursors.Hand,
                    ToolTip = "맨 아래에 새 " + Titles[section] + " 행을 넣습니다 (Insert)"
                };
                add.SetResourceReference(Control.ForegroundProperty, Theme.Accent);
                add.SetResourceReference(Control.BorderBrushProperty, Theme.Border);
                AutomationProperties.SetAutomationId(add, "schema-add-" + section);
                add.Click += (s, e) => Add(section);
                DockPanel.SetDock(add, Dock.Bottom);
                content.Children.Add(add);
            }
            content.Children.Add(grid);
            return new TabItem { Header = header, Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        }

        DataGrid NewGrid(int section)
        {
            var grid = new DataGrid
            {
                AutoGenerateColumns = false, ItemsSource = lists[section], CanUserAddRows = false, CanUserDeleteRows = false,
                CanUserReorderColumns = false, CanUserSortColumns = false, IsReadOnly = !editable,
                SelectionMode = DataGridSelectionMode.Extended, SelectionUnit = DataGridSelectionUnit.FullRow,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, HeadersVisibility = DataGridHeadersVisibility.Column,
                BorderThickness = new Thickness(0), RowHeight = 28, ColumnHeaderHeight = 30, MinRowHeight = 28,
                ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader, EnableRowVirtualization = true
            };
            AutomationProperties.SetAutomationId(grid, "schema-grid-" + section);
            grid.SetResourceReference(Control.BackgroundProperty, Theme.PanelBackground);
            grid.SetResourceReference(Control.ForegroundProperty, Theme.PrimaryText);
            grid.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, Theme.Border);
            grid.CellStyle = CellStyle();
            grid.ColumnHeaderStyle = HeaderStyle();
            grid.RowStyle = RowStyle();

            grid.Columns.Add(MarkColumn());
            switch (section)
            {
                case 0:
                    grid.Columns.Add(TextColumn("이름", nameof(SchemaRow.Name), 190));
                    grid.Columns.Add(CodeColumn("자료형", nameof(ColumnRow.DataType), new DataGridLength(190), 120, () => (ddl?.DataTypes ?? Array.Empty<DataTypeChoice>()).Select(t => new CodeItem(t.Type, t.Title)), "자료형 (직접 입력 가능)", freeText: true, mono: true));
                    grid.Columns.Add(CheckColumn("NOT NULL", nameof(ColumnRow.NotNull), nameof(ColumnRow.Nullable), 78, null));
                    grid.Columns.Add(TextColumn("기본값", nameof(ColumnRow.Default), 150, mono: true));
                    grid.Columns.Add(TextColumn("주석", nameof(ColumnRow.Comment), 0));
                    grid.Columns.Add(new DataGridTextColumn { Header = "PK", Binding = new Binding(nameof(ColumnRow.PrimaryKey)) { Converter = new FlagText("🔑") }, IsReadOnly = true, Width = 40, ElementStyle = CenteredText() });
                    break;
                case 1:
                    grid.Columns.Add(TextColumn("이름", nameof(SchemaRow.Name), 220));
                    grid.Columns.Add(CheckColumn("UNIQUE", nameof(IndexRow.Unique), nameof(IndexRow.Uniqueness), 70, null));
                    grid.Columns.Add(CodeColumn("컬럼 (순서대로)", nameof(IndexRow.Columns), new DataGridLength(1, DataGridLengthUnitType.Star), 220, ColumnItems, "인덱스 컬럼 — 체크한 순서대로", multi: true, allowDescending: true));
                    grid.Columns.Add(new DataGridTextColumn { Header = "종류", Binding = new Binding(nameof(IndexRow.IndexType)), IsReadOnly = true, Width = 110, ElementStyle = SecondaryText() });
                    grid.Columns.Add(new DataGridTextColumn { Header = "상태", Binding = new Binding(nameof(IndexRow.Status)), IsReadOnly = true, Width = 80, ElementStyle = SecondaryText() });
                    break;
                default:
                    grid.Columns.Add(TextColumn("이름", nameof(SchemaRow.Name), 170));
                    grid.Columns.Add(CodeColumn("종류", nameof(ConstraintRow.Type), new DataGridLength(130), 100, () => ConstraintTypes, "제약 종류"));
                    grid.Columns.Add(CodeColumn("컬럼 (순서대로)", nameof(ConstraintRow.Columns), new DataGridLength(1.3, DataGridLengthUnitType.Star), 170, ColumnItems, "키 컬럼 — 체크한 순서대로", multi: true, enabledPath: nameof(ConstraintRow.UsesColumns)));
                    grid.Columns.Add(TextColumn("참조 (OWNER.TABLE (컬럼))", nameof(ConstraintRow.Reference), -1, mono: true, enabledPath: nameof(ConstraintRow.UsesReference)));
                    grid.Columns.Add(TextColumn("CHECK 조건", nameof(ConstraintRow.Condition), -1, mono: true, enabledPath: nameof(ConstraintRow.UsesCondition)));
                    grid.Columns.Add(CheckColumn("사용", nameof(ConstraintRow.Enabled), nameof(ConstraintRow.Status), 52, nameof(ConstraintRow.CanToggle)));
                    break;
            }
            if (editable) grid.Columns.Add(ActionColumn(section));
            // 표가 좁을 때 한 번 줄어든 고정 폭 열은 표가 넓어져도 WPF가 되돌리지 않는다 — 자리가 생기면 정한 폭으로 되돌린다.
            grid.LayoutUpdated += (s, e) => { if (grid.IsLoaded) Unsqueeze(grid); };
            if (!editable) return grid;

            // 삭제 표시 행·해당 없는 칸(외래 키가 아닌데 참조 등)은 편집하지 않는다. 편집을 시작한 칸의 오류 표시는 지운다(편집 중 검증으로 묶이지 않게).
            grid.BeginningEdit += (s, e) =>
            {
                var row = e.Row.Item as SchemaRow;
                if (row == null || row.Deleted) { e.Cancel = true; return; }
                if (row is ConstraintRow key && (e.Column.SortMemberPath == nameof(ConstraintRow.Reference) && !key.UsesReference || e.Column.SortMemberPath == nameof(ConstraintRow.Condition) && !key.UsesCondition))
                { e.Cancel = true; return; }
                row.ClearError(e.Column.SortMemberPath);
            };
            grid.PreparingCellForEdit += (s, e) => FocusEditor(e.EditingElement);
            grid.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Insert && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; Add(section); return; }
                if (IsTyping(e.OriginalSource)) return;
                var alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
                if (e.Key == Key.F4 || e.Key == Key.F2 || alt && (e.SystemKey == Key.Down || e.Key == Key.Down))
                {
                    // 코드 보기 칸이면 팝업을 연다(F2·F4·Alt+↓).
                    var code = CodeIn(grid, grid.CurrentCell);
                    if (code != null) { e.Handled = true; code.Open(); }
                    return;
                }
                if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; ToggleDelete(section, grid.SelectedItems.Cast<SchemaRow>()); }
                else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; RevertRows(section, grid.SelectedItems.Cast<SchemaRow>()); }
            };
            // 우클릭한 행이 선택에 없으면 그 행만 고른다(메뉴가 엉뚱한 행에 적용되지 않게).
            grid.PreviewMouseRightButtonDown += (s, e) =>
            {
                var item = ItemsControl.ContainerFromElement(grid, e.OriginalSource as DependencyObject) as DataGridRow;
                if (item != null && !item.IsSelected) { grid.SelectedItems.Clear(); item.IsSelected = true; }
            };
            grid.ContextMenu = Menu(section, grid);
            return grid;
        }

        static void Unsqueeze(DataGrid grid)
        {
            var squeezed = grid.Columns.Where(c => c.Width.IsAbsolute && c.ActualWidth + 0.5 < c.Width.Value).ToList();
            if (squeezed.Count == 0) return;
            var need = grid.Columns.Sum(c => c.Width.IsAbsolute ? c.Width.Value : c.MinWidth);
            if (need > grid.ActualWidth - SystemParameters.VerticalScrollBarWidth) return;
            foreach (var column in squeezed) column.Width = new DataGridLength(column.Width.Value);
            // 비율 열도 다시 나누게 한다(새 값이어야 다시 계산한다).
            foreach (var column in grid.Columns.Where(c => c.Width.IsStar)) column.Width = new DataGridLength(column.Width.Value, DataGridLengthUnitType.Star);
        }

        /// <summary>칸 안의 코드 보기 컨트롤(없으면 null).</summary>
        static CodePopup CodeIn(DataGrid grid, DataGridCellInfo cell)
        {
            if (cell.Column == null || cell.Item == null) return null;
            var content = cell.Column.GetCellContent(cell.Item);
            return content as CodePopup ?? (content == null ? null : FindChild<CodePopup>(content));
        }

        static bool IsTyping(object source)
        {
            for (var element = source as DependencyObject; element != null; element = VisualTreeHelper.GetParent(element))
                if (element is TextBox || element is ComboBox || element is Popup) return true;
                else if (element is DataGrid) return false;
            return false;
        }

        static void FocusEditor(FrameworkElement editor)
        {
            editor.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                var box = editor as TextBox ?? FindChild<TextBox>(editor);
                if (box != null) { box.Focus(); box.SelectAll(); }
                else editor.Focus();
            }));
        }

        static T FindChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T found) return found;
                var deeper = FindChild<T>(child);
                if (deeper != null) return deeper;
            }
            return null;
        }

        ContextMenu Menu(int section, DataGrid grid)
        {
            var menu = new ContextMenu();
            Func<SchemaRow[]> selected = () => grid.SelectedItems.Cast<SchemaRow>().ToArray();
            void Item(string title, string keys, Action run, bool needsSelection)
            {
                var entry = new MenuItem { Header = title, InputGestureText = keys ?? "" };
                entry.Click += (s, e) => run();
                menu.Opened += (s, e) => entry.IsEnabled = View.IsEnabled && (!needsSelection || grid.SelectedItems.Count > 0);
                menu.Items.Add(entry);
            }
            Item(Titles[section] + " 추가", "Insert", () => Add(section), false);
            Item("삭제 / 삭제 취소", "Delete", () => ToggleDelete(section, selected()), true);
            Item("선택 행 되돌리기", "Ctrl+Z", () => RevertRows(section, selected()), true);
            if (section == 0)
            {
                menu.Items.Add(new Separator());
                Item("선택 컬럼으로 기본 키 만들기", null, () => CreateFromColumns("PK"), true);
                Item("선택 컬럼으로 고유 제약 만들기", null, () => CreateFromColumns("UK"), true);
                Item("선택 컬럼으로 인덱스 만들기", null, () => CreateFromColumns("IX"), true);
                Item("선택 컬럼으로 고유 인덱스 만들기", null, () => CreateFromColumns("UX"), true);
            }
            return menu;
        }

        // ───────── 열 ─────────

        /// <summary>상태 표시 열: ✚ 추가 · ✎ 수정 · ✕ 삭제, 오류가 있으면 ⚠(툴팁에 이유).</summary>
        DataGridTemplateColumn MarkColumn()
        {
            var text = new FrameworkElementFactory(typeof(TextBlock));
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.TextProperty, new Binding(nameof(SchemaRow.Mark))));
            style.Setters.Add(new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center));
            style.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension(Theme.SecondaryText)));
            foreach (var pair in new[] { ("추가", Theme.Success, "추가 — 저장하면 만듭니다"), ("수정", Theme.Warning, "수정 — 저장하면 바꿉니다"), ("삭제", Theme.Danger, "삭제 — 저장하면 지웁니다") })
            {
                var trigger = new DataTrigger { Binding = new Binding(nameof(SchemaRow.State)), Value = pair.Item1 };
                trigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, pair.Item2));
                trigger.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, pair.Item3));
                style.Triggers.Add(trigger);
            }
            var error = new DataTrigger { Binding = new Binding(nameof(SchemaRow.HasErrors)), Value = true };
            error.Setters.Add(new Setter(TextBlock.TextProperty, "⚠"));
            error.Setters.Add(new Setter(TextBlock.ForegroundProperty, Theme.Danger));
            error.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(nameof(SchemaRow.ErrorText))));
            style.Triggers.Add(error);
            text.SetValue(FrameworkElement.StyleProperty, style);
            return new DataGridTemplateColumn { Header = "", Width = 30, IsReadOnly = true, CellTemplate = new DataTemplate { VisualTree = text } };
        }

        DataGridTextColumn TextColumn(string header, string field, double width, bool mono = false, string enabledPath = null)
        {
            var column = new DataGridTextColumn
            {
                Header = header, SortMemberPath = field,
                Binding = new Binding(field) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged, ValidatesOnNotifyDataErrors = true },
                // width > 0: 고정 폭, 0 이하: 남는 폭을 나눠 가짐(비율 -width, 0이면 1). 고정 폭 합이 표보다 크면 WPF가 고정 열을 최소 폭으로 줄이므로 넓은 칸은 비율로 둔다.
                Width = width > 0 ? new DataGridLength(width) : new DataGridLength(width < 0 ? -width : 1, DataGridLengthUnitType.Star), MinWidth = width > 0 ? 80 : 120,
                ElementStyle = ValueText(mono, enabledPath)
            };
            var editing = new Style(typeof(TextBox));
            editing.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(2, 0, 2, 0)));
            editing.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            editing.Setters.Add(new Setter(Validation.ErrorTemplateProperty, null));
            if (mono) editing.Setters.Add(new Setter(Control.FontFamilyProperty, Theme.Mono));
            column.EditingElementStyle = editing;
            return column;
        }

        /// <summary>
        /// 코드 보기 팝업 칸(CodePopup). 칸 자체가 편집기라 표 편집 모드는 쓰지 않는다(IsReadOnly) —
        /// 단일: 자료형(직접 입력 가능)·제약 종류, 다중: 인덱스·키 컬럼(체크 순서, DESC).
        /// </summary>
        DataGridTemplateColumn CodeColumn(string header, string field, DataGridLength width, double minWidth, Func<IEnumerable<CodeItem>> items,
            string caption, bool multi = false, bool freeText = false, bool mono = false, bool allowDescending = false, string enabledPath = null)
        {
            var code = new FrameworkElementFactory(typeof(CodePopup));
            code.SetValue(CodePopup.ItemsSourceProperty, items);
            code.SetValue(CodePopup.CaptionProperty, caption);
            code.SetValue(CodePopup.MultiProperty, multi);
            code.SetValue(CodePopup.AllowFreeTextProperty, freeText);
            code.SetValue(CodePopup.MonoProperty, mono);
            code.SetValue(CodePopup.AllowDescendingProperty, allowDescending);
            if (multi)
            {
                code.SetValue(CodePopup.ParseProperty, (Func<string, IEnumerable<string>, List<IndexColumnChoice>>)IndexColumnsLogic.Parse);
                code.SetValue(CodePopup.FormatProperty, (Func<IEnumerable<IndexColumnChoice>, string>)TableSchemaLogic.IndexColumns);
            }
            code.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            code.SetBinding(CodePopup.ValueProperty, new Binding(field) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged, ValidatesOnNotifyDataErrors = true });
            BindEnabled(code, enabledPath);
            code.SetValue(AutomationProperties.AutomationIdProperty, "code-" + field);
            return new DataGridTemplateColumn { Header = header, SortMemberPath = field, Width = width, MinWidth = minWidth, IsReadOnly = true, CellTemplate = new DataTemplate { VisualTree = code } };
        }

        /// <summary>컬럼 목록 팝업 항목: 저장 후 남을 컬럼(이름 · 자료형 · 주석).</summary>
        IEnumerable<CodeItem> ColumnItems()
            => columns.Where(c => !c.Deleted && !string.IsNullOrWhiteSpace(c.Name))
                .Select(c => new CodeItem(c.Name.Trim(), (c.DataType + (c.NotNull ? " NOT NULL" : "") + (string.IsNullOrWhiteSpace(c.Comment) ? "" : " · " + c.Comment)).Trim()));

        /// <summary>한 번 클릭으로 바뀌는 체크박스(편집 모드 없음). errorField는 오류를 붙일 값 이름.</summary>
        DataGridTemplateColumn CheckColumn(string header, string path, string field, double width, string enabledPath)
        {
            var box = new FrameworkElementFactory(typeof(CheckBox));
            box.SetBinding(ToggleButton.IsCheckedProperty, new Binding(path) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            box.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            box.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            box.SetValue(UIElement.FocusableProperty, false);
            BindEnabled(box, enabledPath);
            return new DataGridTemplateColumn { Header = header, SortMemberPath = field, Width = width, IsReadOnly = true, CellTemplate = new DataTemplate { VisualTree = box } };
        }

        /// <summary>행 끝 단추: [✕] 삭제 표시(새 행은 뺌), 삭제 표시된 행은 [↶] 삭제 취소.</summary>
        DataGridTemplateColumn ActionColumn(int section)
        {
            var button = new FrameworkElementFactory(typeof(Button));
            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(ContentControl.ContentProperty, "✕"));
            style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, "삭제 (Delete)"));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
            style.Setters.Add(new Setter(FrameworkElement.WidthProperty, 26.0));
            style.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
            style.Setters.Add(new Setter(UIElement.FocusableProperty, false));
            style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(Theme.SecondaryText)));
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Control.ForegroundProperty, Theme.Danger));
            style.Triggers.Add(hover);
            var removed = new DataTrigger { Binding = new Binding(nameof(SchemaRow.Deleted)), Value = true };
            removed.Setters.Add(new Setter(ContentControl.ContentProperty, "↶"));
            removed.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, "삭제 취소 (Delete)"));
            removed.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(Theme.Accent)));
            style.Triggers.Add(removed);
            button.SetValue(FrameworkElement.StyleProperty, style);
            button.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((s, e) =>
            {
                if (((FrameworkElement)s).DataContext is SchemaRow row) ToggleDelete(section, new[] { row });
            }));
            return new DataGridTemplateColumn { Header = "", Width = 34, IsReadOnly = true, CellTemplate = new DataTemplate { VisualTree = button } };
        }

        // ───────── 스타일 ─────────

        /// <summary>값 글자: 삭제 행은 취소선, 해당 없는 칸은 흐리게, 오류 칸은 빨간 바탕 + 툴팁.</summary>
        static Style ValueText(bool mono, string enabledPath)
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            style.Setters.Add(new Setter(Validation.ErrorTemplateProperty, null));
            if (mono) style.Setters.Add(new Setter(TextBlock.FontFamilyProperty, Theme.Mono));
            var removed = new DataTrigger { Binding = new Binding(nameof(SchemaRow.Deleted)), Value = true };
            removed.Setters.Add(new Setter(TextBlock.TextDecorationsProperty, TextDecorations.Strikethrough));
            style.Triggers.Add(removed);
            if (enabledPath != null)
            {
                var unused = new DataTrigger { Binding = new Binding(enabledPath), Value = false };
                unused.Setters.Add(new Setter(UIElement.OpacityProperty, 0.35));
                unused.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, "이 종류에는 쓰지 않습니다."));
                style.Triggers.Add(unused);
            }
            var error = new Trigger { Property = Validation.HasErrorProperty, Value = true };
            error.Setters.Add(new Setter(TextBlock.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x55, 0xE0, 0x6C, 0x75))));
            error.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding("(Validation.Errors)[0].ErrorContent") { RelativeSource = RelativeSource.Self }));
            style.Triggers.Add(error);
            return style;
        }

        /// <summary>
        /// 칸 안 컨트롤 켜기/끄기: 편집 가능 && 삭제 표시 아님 && (enabledPath가 있으면 그 값). Style을 덮지 않고 바인딩으로 —
        /// Style을 지정하면 Folderss 테마의 기본 ComboBox 모양이 사라져 글자가 안 보인다.
        /// </summary>
        void BindEnabled(FrameworkElementFactory element, string enabledPath)
        {
            var binding = new MultiBinding { Converter = new AllTrue(editable) };
            binding.Bindings.Add(new Binding(nameof(SchemaRow.Deleted)));
            if (enabledPath != null) binding.Bindings.Add(new Binding(enabledPath));
            element.SetBinding(UIElement.IsEnabledProperty, binding);
        }

        /// <summary>[Deleted, 쓰는 칸?…] → 편집 가능하고 삭제 표시가 아니며 나머지가 모두 true.</summary>
        sealed class AllTrue : IMultiValueConverter
        {
            readonly bool editable;
            public AllTrue(bool editable) { this.editable = editable; }
            public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
                => editable && !(values[0] is bool deleted && deleted) && values.Skip(1).All(v => v is bool flag && flag);
            public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
        }

        static Style SecondaryText()
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension(Theme.SecondaryText)));
            return style;
        }

        static Style CenteredText()
        {
            var style = SecondaryText();
            style.Setters.Add(new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center));
            return style;
        }

        static Style CellStyle()
        {
            var style = new Style(typeof(DataGridCell));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 2, 6, 2)));
            style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            var template = new ControlTemplate(typeof(DataGridCell));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            template.VisualTree = border;
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            var current = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
            current.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(Theme.Selection)));
            current.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(Theme.PrimaryText)));
            style.Triggers.Add(current);
            return style;
        }

        static Style HeaderStyle()
        {
            var style = new Style(typeof(DataGridColumnHeader));
            style.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(Theme.SurfaceBackground)));
            style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(Theme.SecondaryText)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 4, 8, 4)));
            style.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension(Theme.Border)));
            return style;
        }

        static Style RowStyle()
        {
            var style = new Style(typeof(DataGridRow));
            style.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(Theme.PanelBackground)));
            style.Setters.Add(new Setter(Validation.ErrorTemplateProperty, null));
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(Theme.RowHover)));
            style.Triggers.Add(hover);
            foreach (var pair in new[] { ("추가", Theme.InsertedRow), ("수정", Theme.ModifiedRow), ("삭제", Theme.DeletedRow) })
            {
                var trigger = new DataTrigger { Binding = new Binding(nameof(SchemaRow.State)), Value = pair.Item1 };
                trigger.Setters.Add(new Setter(Control.BackgroundProperty, pair.Item2));
                style.Triggers.Add(trigger);
            }
            var removed = new DataTrigger { Binding = new Binding(nameof(SchemaRow.Deleted)), Value = true };
            removed.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(Theme.SecondaryText)));
            style.Triggers.Add(removed);
            return style;
        }

        static Style TabStyle()
        {
            var style = new Style(typeof(TabItem));
            var template = new ControlTemplate(typeof(TabItem));
            var border = new FrameworkElementFactory(typeof(Border)) { Name = "TabBorder" };
            border.SetValue(Border.BackgroundProperty, new DynamicResourceExtension(Theme.PanelBackground));
            border.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
            border.SetValue(Border.BorderThicknessProperty, new Thickness(3, 0, 0, 0));
            border.SetValue(FrameworkElement.CursorProperty, Cursors.Hand);
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            border.AppendChild(content);
            template.VisualTree = border;
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension(Theme.RowHover), "TabBorder"));
            template.Triggers.Add(hover);
            var selected = new Trigger { Property = TabItem.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension(Theme.SurfaceBackground), "TabBorder"));
            selected.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension(Theme.Accent), "TabBorder"));
            template.Triggers.Add(selected);
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            return style;
        }

        DataTemplate ErrorTemplate()
        {
            var link = new FrameworkElementFactory(typeof(Button));
            var line = new FrameworkElementFactory(typeof(TextBlock));
            line.SetBinding(TextBlock.TextProperty, new Binding { Converter = new ErrorLine(this) });
            line.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
            link.AppendChild(line);
            link.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            link.SetValue(Control.BackgroundProperty, Brushes.Transparent);
            link.SetValue(Control.BorderThicknessProperty, new Thickness(0));
            link.SetValue(Control.PaddingProperty, new Thickness(2, 1, 2, 1));
            link.SetValue(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left);
            link.SetValue(FrameworkElement.CursorProperty, Cursors.Hand);
            link.SetResourceReference(Control.ForegroundProperty, Theme.PrimaryText);
            link.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((s, e) =>
            {
                if (((FrameworkElement)s).DataContext is SchemaError error) Focus(error.Row, error.Field, edit: true);
            }));
            return new DataTemplate { VisualTree = link };
        }

        /// <summary>오류 목록 한 줄: "• 컬럼 CODE(3행) · 자료형 — 이유".</summary>
        public string Describe(SchemaError error)
        {
            var position = lists[error.Row.Section].IndexOf(error.Row) + 1;
            var name = string.IsNullOrWhiteSpace(error.Row.Name) ? "" : " " + error.Row.Name.Trim();
            var field = FieldTitles.TryGetValue(error.Field, out var title) ? title : error.Field;
            return "• " + error.Row.Kind + name + " (" + position.ToString(CultureInfo.InvariantCulture) + "행) · " + field + " — " + error.Message;
        }

        sealed class ErrorLine : IValueConverter
        {
            readonly TableSchemaEditor editor;
            public ErrorLine(TableSchemaEditor editor) { this.editor = editor; }
            public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is SchemaError error ? editor.Describe(error) : "";
            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
        }

        sealed class FlagText : IValueConverter
        {
            readonly string text;
            public FlagText(string text) { this.text = text; }
            public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is bool flag && flag ? text : "";
            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
        }
    }
}

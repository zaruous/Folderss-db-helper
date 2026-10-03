using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace MyPlugin
{
    /// <summary>메시지 탭: "{시각} {DB 배지} {문장}" 줄, 새것이 위. 종류별 색(오류 = 위험색, 성공 = 성공색). Ctrl+C·오른쪽 메뉴로 복사.</summary>
    internal sealed class ResultMessageList
    {
        private readonly ListBox _list;
        private readonly Action<string> _copy;
        private bool _styleApplied;

        /// <param name="copy">복사할 텍스트를 받아 클립보드에 넣는 쪽(실패 안내 포함).</param>
        public ResultMessageList(Action<string> copy)
        {
            _copy = copy;
            _list = new ListBox
            {
                SelectionMode = SelectionMode.Extended,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 2, 4, 2),
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            Theme.Background(_list, Theme.PanelBackground);
            // 긴 오류 문장은 줄바꿈해 다 보인다(가로 스크롤 대신)
            ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
            AutomationProperties.SetName(_list, "메시지");
            _list.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy,
                (s, e) =>
                {
                    e.Handled = true;
                    CopySelected();
                },
                (s, e) =>
                {
                    e.CanExecute = _list.SelectedItems.Count > 0;
                    e.Handled = true;
                }));
            var menu = new ContextMenu();
            var copyItem = new MenuItem { Header = "복사", InputGestureText = "Ctrl+C" };
            copyItem.Click += (s, e) => CopySelected();
            menu.Items.Add(copyItem);
            menu.Opened += (s, e) => copyItem.IsEnabled = _list.SelectedItems.Count > 0;
            _list.ContextMenu = menu;
            _list.Loaded += (s, e) => ApplyItemStyle(_list, ref _styleApplied);
        }

        public ListBox View
        {
            get { return _list; }
        }

        public int Count
        {
            get { return _list.Items.Count; }
        }

        /// <param name="profile">배지에 쓸 접속(삭제됐으면 null → "대상 없음").</param>
        /// <param name="badge">false면 배지 없이.</param>
        public void Add(DateTime time, OracleConnectionProfile profile, bool badge, string text, MessageKind kind)
        {
            var line = new DockPanel();
            var stamp = Theme.Text(time.ToString("HH:mm:ss", CultureInfo.InvariantCulture), Theme.SecondaryText);
            stamp.FontFamily = Theme.Mono;
            stamp.FontSize = 12;
            stamp.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(stamp, Dock.Left);
            line.Children.Add(stamp);
            string dbName = null;
            if (badge)
            {
                var dbBadge = Theme.DbBadge(profile);
                dbBadge.Margin = new Thickness(0, 1, 6, 0);
                dbBadge.VerticalAlignment = VerticalAlignment.Top;
                DockPanel.SetDock(dbBadge, Dock.Left);
                line.Children.Add(dbBadge);
                dbName = profile != null ? profile.Name : "대상 없음";
            }
            var body = new TextBlock { Text = text ?? "", TextWrapping = TextWrapping.Wrap, FontFamily = Theme.Mono, FontSize = 12 };
            switch (kind)
            {
                case MessageKind.Error:
                    body.Foreground = Theme.Danger;
                    break;
                case MessageKind.Success:
                    body.Foreground = Theme.Success;
                    break;
                default:
                    Theme.Foreground(body);
                    break;
            }
            line.Children.Add(body);
            line.Tag = time.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " + (dbName != null ? "[" + dbName + "] " : "") + (text ?? "");
            _list.Items.Insert(0, line);
            while (_list.Items.Count > WorkspaceLogic.MessageLimit)
                _list.Items.RemoveAt(_list.Items.Count - 1);
        }

        private void CopySelected()
        {
            var lines = _list.SelectedItems.OfType<FrameworkElement>()
                .OrderBy(item => _list.Items.IndexOf(item))
                .Select(item => item.Tag as string)
                .Where(text => text != null)
                .ToList();
            if (lines.Count > 0)
                _copy(string.Join("\r\n", lines));
        }

        /// <summary>항목이 가로로 채워지게 테마 항목 스타일을 바탕으로 한 스타일을 준다(화면 트리에 붙은 뒤 한 번).</summary>
        internal static void ApplyItemStyle(ListBox list, ref bool applied)
        {
            if (applied)
                return;
            applied = true;
            var style = WorkspaceUi.StretchItemStyle(list, typeof(ListBoxItem));
            if (style != null)
                list.ItemContainerStyle = style;
        }
    }

    /// <summary>실행 기록 한 건(메모리에만).</summary>
    internal sealed class HistoryEntry
    {
        public DateTime Time { get; set; }
        public string DbId { get; set; }
        /// <summary>실행한 문장(SqlStatement.Text).</summary>
        public string Sql { get; set; }
        public TimeSpan Elapsed { get; set; }
    }

    /// <summary>기록 탭: 시각 · DB 배지 · SQL 첫 줄(툴팁에 전체) · 걸린 시간, 새것이 위, 최대 100건. 누르거나 Enter면 Activated.</summary>
    internal sealed class ResultHistoryList
    {
        private readonly ListBox _list;
        private bool _styleApplied;

        public ResultHistoryList()
        {
            _list = new ListBox
            {
                SelectionMode = SelectionMode.Single,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 2, 4, 2),
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            Theme.Background(_list, Theme.PanelBackground);
            ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
            AutomationProperties.SetName(_list, "실행 기록");
            // 항목이 MouseLeftButtonUp을 처리했어도 받는다(누른 줄을 편집기에 넣음)
            _list.AddHandler(UIElement.MouseLeftButtonUpEvent, new MouseButtonEventHandler(List_MouseLeftButtonUp), true);
            _list.KeyDown += List_KeyDown;
            _list.Loaded += (s, e) => ResultMessageList.ApplyItemStyle(_list, ref _styleApplied);
        }

        /// <summary>기록을 눌렀음(편집기에 넣기).</summary>
        public event Action<HistoryEntry> Activated;

        public ListBox View
        {
            get { return _list; }
        }

        public void Add(HistoryEntry entry, OracleConnectionProfile profile)
        {
            var row = new Grid { Tag = entry };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var stamp = Theme.Text(entry.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture), Theme.SecondaryText);
            stamp.Margin = new Thickness(0, 0, 8, 0);
            row.Children.Add(stamp);

            var badge = Theme.DbBadge(profile);
            badge.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(badge, 1);
            row.Children.Add(badge);

            var sql = Theme.Text(WorkspaceLogic.FirstLine(entry.Sql, 200));
            sql.FontFamily = Theme.Mono;
            sql.FontSize = 12;
            sql.TextTrimming = TextTrimming.CharacterEllipsis;
            sql.ToolTip = WorkspaceLogic.TooltipText(entry.Sql);
            Grid.SetColumn(sql, 2);
            row.Children.Add(sql);

            var elapsed = Theme.Text(WorkspaceLogic.Seconds(entry.Elapsed) + "초", Theme.SecondaryText);
            elapsed.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(elapsed, 3);
            row.Children.Add(elapsed);

            _list.Items.Insert(0, row);
            while (_list.Items.Count > WorkspaceLogic.HistoryLimit)
                _list.Items.RemoveAt(_list.Items.Count - 1);
        }

        private void List_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // 스크롤 막대 등 항목 밖을 누른 것은 무시한다
            var source = e.OriginalSource as DependencyObject;
            var container = source == null ? null : ItemsControl.ContainerFromElement(_list, source) as ListBoxItem;
            if (container == null)
                return;
            Raise(_list.ItemContainerGenerator.ItemFromContainer(container) as FrameworkElement);
        }

        private void List_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;
            e.Handled = true;
            Raise(_list.SelectedItem as FrameworkElement);
        }

        private void Raise(FrameworkElement row)
        {
            var entry = row != null ? row.Tag as HistoryEntry : null;
            if (entry != null && Activated != null)
                Activated(entry);
        }
    }
}

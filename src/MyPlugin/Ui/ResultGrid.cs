using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace MyPlugin
{
    /// <summary>결과 그리드 한 행. 값은 ValueFormatter.Format 결과(null = NULL).</summary>
    internal sealed class ResultGridRow : INotifyPropertyChanged
    {
        public ResultGridRow(int number, string[] values)
        {
            Number = number;
            Values = values ?? Array.Empty<string>();
        }

        /// <summary>1부터 세는 행 번호.</summary>
        public int Number { get; }

        public string[] Values { get; }

        // 값은 바뀌지 않는다. INotifyPropertyChanged를 구현해 두면 WPF가 PropertyDescriptor 변경 구독(행이 해제되지 않는 누수)을 쓰지 않는다.
        public event PropertyChangedEventHandler PropertyChanged
        {
            add { }
            remove { }
        }
    }

    /// <summary>
    /// 조회 결과 그리드(ListView + GridView, 가상화). 열 머리는 이름(굵게) 위·형식(작은 고정폭) 아래,
    /// NULL은 흐린 기울임꼴, 숫자 열은 오른쪽 정렬, 긴 값은 한 줄로 줄이고 툴팁으로 전체를 보인다.
    /// Ctrl+C는 선택한 행, 오른쪽 메뉴로 전체 복사. 복사(클립보드·메시지)는 만든 쪽이 copy 콜백으로 한다.
    /// 열 머리를 누르면 가져온 행을 정렬한다(오름차순 → 내림차순 → 해제, # 열은 해제). 행 번호는 원래 순서 그대로 보이고,
    /// 더 가져온 행도 정렬 위치에 들어간다(ListCollectionView.CustomSort). 복사는 화면 순서를 따른다.
    /// </summary>
    internal sealed class ResultGridView
    {
        private const int SampleRows = 50;
        private const string SortHint = "누르면 정렬: 오름차순 → 내림차순 → 해제 (가져온 행 안에서)";

        private readonly ListView _list;
        private readonly Action<bool> _copy;
        private readonly List<TextBlock> _titles = new List<TextBlock>();
        private readonly List<string> _names = new List<string>();
        private IList<ResultColumn> _columns;
        private ListCollectionView _rowsView;
        private GridViewColumn _numberColumn;
        private Style _headerStyle;
        private bool _stylesApplied;
        private int _sortColumn = -1;
        private bool _sortDescending;

        /// <param name="copy">복사 요청. true = 전체(머리글 포함), false = 선택한 행.</param>
        public ResultGridView(Action<bool> copy)
        {
            _copy = copy;
            _list = new ListView
            {
                SelectionMode = SelectionMode.Extended,
                BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            Theme.Background(_list, Theme.PanelBackground);
            VirtualizingPanel.SetIsVirtualizing(_list, true);
            VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
            ScrollViewer.SetCanContentScroll(_list, true);
            ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Auto);
            ScrollViewer.SetVerticalScrollBarVisibility(_list, ScrollBarVisibility.Auto);
            AutomationProperties.SetName(_list, "조회 결과");

            _list.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy,
                (s, e) =>
                {
                    e.Handled = true;
                    _copy(false);
                },
                (s, e) =>
                {
                    e.CanExecute = _list.SelectedItems.Count > 0;
                    e.Handled = true;
                }));

            var menu = new ContextMenu();
            var copySelected = new MenuItem { Header = "선택한 행 복사", InputGestureText = "Ctrl+C" };
            copySelected.Click += (s, e) => _copy(false);
            var copyAll = new MenuItem { Header = "모두 복사 (머리글 포함)" };
            copyAll.Click += (s, e) => _copy(true);
            menu.Items.Add(copySelected);
            menu.Items.Add(copyAll);
            menu.Opened += (s, e) =>
            {
                copySelected.IsEnabled = _list.SelectedItems.Count > 0;
                copyAll.IsEnabled = _list.Items.Count > 0;
            };
            _list.ContextMenu = menu;
            _list.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(Header_Click));
        }

        public ListView View
        {
            get { return _list; }
        }

        /// <summary>정렬한 열(결과 열 위치). 정렬 안 했으면 -1.</summary>
        public int SortColumn
        {
            get { return _sortColumn; }
        }

        public bool SortDescending
        {
            get { return _sortDescending; }
        }

        /// <summary>열 머리를 눌러 정렬을 바꿨을 때.</summary>
        public event EventHandler Sorted;

        /// <summary>새 결과로 바꾼다. 이 그리드는 화면 트리에 붙어 있어야 한다(테마 스타일을 찾으려고).</summary>
        public void Show(IList<ResultColumn> columns, ObservableCollection<ResultGridRow> rows)
        {
            ApplyThemeStyles();
            // 열을 끌어 옮기면 복사(TSV)의 열 순서와 화면이 달라진다
            var view = new GridView { AllowsColumnReorder = false };
            if (_headerStyle != null)
                view.ColumnHeaderContainerStyle = _headerStyle;
            // 새 결과는 정렬하지 않은 원래 순서로 보인다
            _sortColumn = -1;
            _sortDescending = false;
            _columns = columns;
            _titles.Clear();
            _names.Clear();
            TextBlock numberTitle;
            var numberHeader = HeaderText("#", null, out numberTitle);
            numberHeader.ToolTip = "원래 순서(행 번호). 누르면 정렬을 해제합니다.";
            _numberColumn = new GridViewColumn
            {
                Header = numberHeader,
                CellTemplate = NumberTemplate(),
                Width = WorkspaceLogic.RowNumberWidth(rows.Count)
            };
            view.Columns.Add(_numberColumn);
            for (var i = 0; i < columns.Count; i++)
            {
                var index = i;
                var column = columns[i];
                var sample = rows.Take(SampleRows).Select(r => index < r.Values.Length ? r.Values[index] : null);
                TextBlock title;
                var header = HeaderText(column.Name, column.TypeLabel, out title);
                _titles.Add(title);
                _names.Add(column.Name ?? "");
                view.Columns.Add(new GridViewColumn
                {
                    Header = header,
                    CellTemplate = CellTemplate(index, column.IsNumeric),
                    Width = WorkspaceLogic.ColumnWidth(column.Name, column.TypeLabel, sample)
                });
            }
            _list.View = view;
            _list.ItemsSource = rows;
            _rowsView = CollectionViewSource.GetDefaultView(rows) as ListCollectionView;
            if (_rowsView != null)
                _rowsView.CustomSort = null;
            // 같은 탭에서 다시 조회하면 이전 결과의 스크롤 위치에 머물지 않게 한다
            var scroller = WorkspaceUi.FindDescendant<ScrollViewer>(_list);
            if (scroller != null)
                scroller.ScrollToHome();
        }

        /// <summary>행이 늘어 번호 자릿수가 커지면 번호 열을 넓힌다.</summary>
        public void FitRowNumbers(int rowCount)
        {
            if (_numberColumn == null)
                return;
            var width = WorkspaceLogic.RowNumberWidth(rowCount);
            if (double.IsNaN(_numberColumn.Width) || width > _numberColumn.Width)
                _numberColumn.Width = width;
        }

        public void ScrollIntoView(ResultGridRow row)
        {
            if (row != null)
                _list.ScrollIntoView(row);
        }

        /// <summary>선택한 행(화면 순서 — 정렬했으면 정렬한 순서).</summary>
        public List<ResultGridRow> SelectedRows()
        {
            var selected = _list.SelectedItems.OfType<ResultGridRow>().ToList();
            if (_sortColumn < 0 || _rowsView == null)
                return selected.OrderBy(r => r.Number).ToList();
            var order = ViewOrder();
            return selected.OrderBy(r => order.TryGetValue(r, out var position) ? position : int.MaxValue).ToList();
        }

        /// <summary>모든 행(화면 순서 — 정렬했으면 정렬한 순서).</summary>
        public List<ResultGridRow> RowsInViewOrder()
        {
            if (_rowsView == null)
                return _list.Items.OfType<ResultGridRow>().ToList();
            return _rowsView.OfType<ResultGridRow>().ToList();
        }

        private Dictionary<ResultGridRow, int> ViewOrder()
        {
            var order = new Dictionary<ResultGridRow, int>();
            var position = 0;
            foreach (var row in _rowsView.OfType<ResultGridRow>())
                order[row] = position++;
            return order;
        }

        private void Header_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var header = e.OriginalSource as GridViewColumnHeader;
                var view = _list.View as GridView;
                if (header == null || header.Column == null || header.Role == GridViewColumnHeaderRole.Padding || view == null || _rowsView == null)
                    return;
                e.Handled = true;
                var clicked = view.Columns.IndexOf(header.Column) - 1;
                if (clicked < -1)
                    return;
                int column;
                bool descending;
                if (clicked < 0)
                {
                    // # 열: 원래 순서로
                    if (_sortColumn < 0)
                        return;
                    column = -1;
                    descending = false;
                }
                else
                {
                    WorkspaceLogic.NextSort(_sortColumn, _sortDescending, clicked, out column, out descending);
                }
                _sortColumn = column;
                _sortDescending = descending;
                ApplySort();
                Sorted?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception)
            {
                // 정렬하지 못해도 결과는 그대로 보인다
            }
        }

        private void ApplySort()
        {
            var numeric = _sortColumn >= 0 && _columns != null && _sortColumn < _columns.Count && _columns[_sortColumn].IsNumeric;
            _rowsView.CustomSort = _sortColumn < 0 ? null : new RowComparer(_sortColumn, numeric, _sortDescending);
            for (var i = 0; i < _titles.Count; i++)
                _titles[i].Text = _names[i] + (i == _sortColumn ? WorkspaceLogic.SortMark(_sortDescending) : "");
            // 정렬한 결과의 처음을 보인다
            var scroller = WorkspaceUi.FindDescendant<ScrollViewer>(_list);
            if (scroller != null)
                scroller.ScrollToVerticalOffset(0);
        }

        private sealed class RowComparer : System.Collections.IComparer
        {
            private readonly int _column;
            private readonly bool _numeric;
            private readonly bool _descending;

            public RowComparer(int column, bool numeric, bool descending)
            {
                _column = column;
                _numeric = numeric;
                _descending = descending;
            }

            public int Compare(object x, object y)
            {
                var a = x as ResultGridRow;
                var b = y as ResultGridRow;
                if (a == null || b == null)
                    return a == null ? (b == null ? 0 : 1) : -1;
                return WorkspaceLogic.CompareRows(a.Values, a.Number, b.Values, b.Number, _column, _numeric, _descending);
            }
        }

        private void ApplyThemeStyles()
        {
            if (_stylesApplied)
                return;
            _stylesApplied = true;
            var itemStyle = WorkspaceUi.StretchItemStyle(_list, typeof(ListViewItem));
            if (itemStyle != null)
                _list.ItemContainerStyle = itemStyle;
            var baseHeader = _list.TryFindResource(typeof(GridViewColumnHeader)) as Style;
            if (baseHeader != null)
            {
                var header = new Style(typeof(GridViewColumnHeader), baseHeader);
                header.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
                _headerStyle = header;
            }
        }

        private static FrameworkElement HeaderText(string name, string typeLabel, out TextBlock title)
        {
            var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 1, 2, 1) };
            title = Theme.Text(name ?? "");
            title.FontWeight = FontWeights.SemiBold;
            panel.Children.Add(title);
            if (!string.IsNullOrEmpty(typeLabel))
            {
                var type = Theme.Text(typeLabel, Theme.SecondaryText);
                type.FontFamily = Theme.Mono;
                type.FontSize = 10.5;
                panel.Children.Add(type);
                panel.ToolTip = (name ?? "") + " · " + typeLabel + Environment.NewLine + SortHint;
            }
            else
            {
                panel.ToolTip = SortHint;
            }
            return panel;
        }

        private static DataTemplate NumberTemplate()
        {
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, new Binding(nameof(ResultGridRow.Number)) { Mode = BindingMode.OneTime, StringFormat = "{0:N0}" });
            text.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Right);
            text.SetResourceReference(TextBlock.ForegroundProperty, Theme.SecondaryText);
            var template = new DataTemplate { VisualTree = text };
            template.Seal();
            return template;
        }

        private static DataTemplate CellTemplate(int index, bool numeric)
        {
            var text = new FrameworkElementFactory(typeof(TextBlock), "cell");
            text.SetBinding(TextBlock.TextProperty, new Binding { Converter = CellTextConverter.Instance, ConverterParameter = index, Mode = BindingMode.OneWay });
            text.SetBinding(FrameworkElement.ToolTipProperty, new Binding { Converter = CellTooltipConverter.Instance, ConverterParameter = index, Mode = BindingMode.OneWay });
            text.SetValue(FrameworkElement.TagProperty, index);
            text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap);
            if (numeric)
                text.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Right);
            text.AddHandler(FrameworkElement.ToolTipOpeningEvent, new ToolTipEventHandler(OnCellToolTipOpening));

            var template = new DataTemplate { VisualTree = text };
            var isNull = new DataTrigger
            {
                Binding = new Binding { Converter = CellIsNullConverter.Instance, ConverterParameter = index, Mode = BindingMode.OneWay },
                Value = true
            };
            isNull.Setters.Add(new Setter(TextBlock.FontStyleProperty, FontStyles.Italic, "cell"));
            isNull.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension(Theme.DisabledText), "cell"));
            template.Triggers.Add(isNull);
            template.Seal();
            return template;
        }

        // 잘리지 않은 한 줄 값에는 툴팁을 띄우지 않는다(숫자·짧은 글자 위에서 툴팁이 계속 뜨지 않게)
        private static void OnCellToolTipOpening(object sender, ToolTipEventArgs e)
        {
            try
            {
                var text = sender as TextBlock;
                var value = text == null ? null : CellValue(text.DataContext, text.Tag);
                if (value == null)
                {
                    e.Handled = true;
                    return;
                }
                // 줄바꿈이 있는 값은 칸에 한 줄로 바꿔 보이므로 언제나 원래 값을 보인다
                if (!WorkspaceLogic.HasLineBreak(value) && !IsTrimmed(text))
                    e.Handled = true;
            }
            catch (Exception)
            {
                // 툴팁이 필요한지 판단하지 못하면 띄우지 않는다
                e.Handled = true;
            }
        }

        private static bool IsTrimmed(TextBlock text)
        {
            var formatted = new FormattedText(text.Text ?? "", CultureInfo.CurrentUICulture, text.FlowDirection,
                new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, text.Foreground,
                VisualTreeHelper.GetDpi(text).PixelsPerDip);
            return formatted.WidthIncludingTrailingWhitespace > text.ActualWidth - text.Padding.Left - text.Padding.Right + 0.5;
        }

        private static string CellValue(object row, object parameter)
        {
            var gridRow = row as ResultGridRow;
            if (gridRow == null || !(parameter is int))
                return null;
            var index = (int)parameter;
            return index >= 0 && index < gridRow.Values.Length ? gridRow.Values[index] : null;
        }

        private sealed class CellTextConverter : IValueConverter
        {
            public static readonly CellTextConverter Instance = new CellTextConverter();

            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            {
                // 컨테이너를 재사용하는 동안 DataContext가 행이 아닐 수 있다(그때는 NULL로 보이지 않게 빈 칸)
                if (!(value is ResultGridRow))
                    return "";
                return WorkspaceLogic.CellText(CellValue(value, parameter));
            }

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            {
                return Binding.DoNothing;
            }
        }

        private sealed class CellIsNullConverter : IValueConverter
        {
            public static readonly CellIsNullConverter Instance = new CellIsNullConverter();

            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            {
                return value is ResultGridRow && CellValue(value, parameter) == null;
            }

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            {
                return Binding.DoNothing;
            }
        }

        private sealed class CellTooltipConverter : IValueConverter
        {
            public static readonly CellTooltipConverter Instance = new CellTooltipConverter();

            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            {
                return WorkspaceLogic.TooltipText(CellValue(value, parameter));
            }

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            {
                return Binding.DoNothing;
            }
        }
    }

    /// <summary>클립보드 쓰기. 다른 프로그램이 클립보드를 잡고 있으면 COMException이 나므로 잠시 뒤 한 번 더 한다.</summary>
    internal static class ResultClipboard
    {
        /// <summary>성공하면 null, 실패하면 사람이 읽을 이유. UI 스레드(STA)에서 호출한다.</summary>
        public static async Task<string> SetTextAsync(string text)
        {
            try
            {
                Clipboard.SetText(text ?? "");
                return null;
            }
            catch (COMException)
            {
                // 다른 프로그램이 클립보드를 열고 있음 — 잠깐 기다렸다 한 번 더
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
            await Task.Delay(150);
            try
            {
                Clipboard.SetText(text ?? "");
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
    }
}

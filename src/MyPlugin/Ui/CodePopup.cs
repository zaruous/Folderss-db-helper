using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MyPlugin
{
    /// <summary>코드 보기 팝업의 항목 하나: 값(코드)과 설명.</summary>
    internal sealed class CodeItem
    {
        public CodeItem(string code, string title = null) { Code = code ?? ""; Title = title ?? ""; }
        public string Code { get; }
        public string Title { get; }
    }

    /// <summary>
    /// 코드 보기 팝업(공통 컨트롤). 칸에는 값과 [▾]만 보이고, 누르면(또는 F4·Alt+↓) 검색 상자와 코드 목록 팝업이 열린다.
    /// - 단일 선택(Multi=false): 행을 누르거나 Enter로 고르면 바로 값이 바뀌고 닫힌다. AllowFreeText면 칸에 직접 입력할 수 있고,
    ///   목록에 없는 검색어는 "그대로 쓰기" 행으로 고를 수 있다.
    /// - 다중 선택(Multi=true): 행 어디를 눌러도 체크가 바뀌고(Space도), 체크한 순서가 값의 순서다. ↑↓로 순서, AllowDescending이면 ASC/DESC.
    ///   [확인]·Enter·바깥 클릭으로 반영, [취소]·Esc는 버린다.
    /// 팝업은 이 컨트롤의 논리 트리에 붙이지 않는다 — 표(DataGrid) 칸 안에서 써도 팝업의 클릭·키가 표로 새어 선택·편집을 건드리지 않는다.
    /// 값의 문법(따옴표·DESC 등)은 모른다: 다중 선택은 Parse·Format을 호출자가 준다.
    /// </summary>
    internal sealed class CodePopup : ContentControl
    {
        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(string), typeof(CodePopup),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((CodePopup)d).ShowValue()));
        public static readonly DependencyProperty MultiProperty = DependencyProperty.Register(nameof(Multi), typeof(bool), typeof(CodePopup), new PropertyMetadata(false, (d, e) => ((CodePopup)d).ShowValue()));
        public static readonly DependencyProperty AllowDescendingProperty = DependencyProperty.Register(nameof(AllowDescending), typeof(bool), typeof(CodePopup), new PropertyMetadata(false));
        public static readonly DependencyProperty AllowFreeTextProperty = DependencyProperty.Register(nameof(AllowFreeText), typeof(bool), typeof(CodePopup), new PropertyMetadata(false, (d, e) => ((CodePopup)d).ShowValue()));
        public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(nameof(Caption), typeof(string), typeof(CodePopup), new PropertyMetadata(""));
        public static readonly DependencyProperty MonoProperty = DependencyProperty.Register(nameof(Mono), typeof(bool), typeof(CodePopup), new PropertyMetadata(false, (d, e) => ((CodePopup)d).ShowValue()));
        public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(nameof(ItemsSource), typeof(Func<IEnumerable<CodeItem>>), typeof(CodePopup), new PropertyMetadata(null, (d, e) => ((CodePopup)d).ShowValue()));
        public static readonly DependencyProperty ParseProperty = DependencyProperty.Register(nameof(Parse), typeof(Func<string, IEnumerable<string>, List<IndexColumnChoice>>), typeof(CodePopup));
        public static readonly DependencyProperty FormatProperty = DependencyProperty.Register(nameof(Format), typeof(Func<IEnumerable<IndexColumnChoice>, string>), typeof(CodePopup));

        public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
        public bool Multi { get => (bool)GetValue(MultiProperty); set => SetValue(MultiProperty, value); }
        public bool AllowDescending { get => (bool)GetValue(AllowDescendingProperty); set => SetValue(AllowDescendingProperty, value); }
        public bool AllowFreeText { get => (bool)GetValue(AllowFreeTextProperty); set => SetValue(AllowFreeTextProperty, value); }
        public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }
        public bool Mono { get => (bool)GetValue(MonoProperty); set => SetValue(MonoProperty, value); }
        /// <summary>목록(열 때마다 다시 부른다 — 컬럼 탭에서 방금 넣은 컬럼도 보인다).</summary>
        public Func<IEnumerable<CodeItem>> ItemsSource { get => (Func<IEnumerable<CodeItem>>)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
        /// <summary>다중 선택: 값 글자 → 고른 코드(순서·DESC). 해석할 수 없으면 ArgumentException.</summary>
        public Func<string, IEnumerable<string>, List<IndexColumnChoice>> Parse { get => (Func<string, IEnumerable<string>, List<IndexColumnChoice>>)GetValue(ParseProperty); set => SetValue(ParseProperty, value); }
        /// <summary>다중 선택: 고른 코드 → 값 글자.</summary>
        public Func<IEnumerable<IndexColumnChoice>, string> Format { get => (Func<IEnumerable<IndexColumnChoice>, string>)GetValue(FormatProperty); set => SetValue(FormatProperty, value); }

        public bool IsOpen => popup.IsOpen;

        // ── 칸 ──
        readonly TextBox input = new TextBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center };
        readonly TextBlock display = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        readonly Button opener = new Button { Content = "▾", Width = 22, Padding = new Thickness(0), Focusable = false, Cursor = Cursors.Hand, ToolTip = "목록에서 고르기 (F4)" };
        bool writing;

        // ── 팝업 ──
        readonly Popup popup;
        readonly TextBox search = new TextBox { Padding = new Thickness(4, 2, 4, 2) };
        readonly StackPanel list = new StackPanel();
        readonly ScrollViewer scroller;
        readonly TextBlock caption = new TextBlock { FontWeight = FontWeights.SemiBold };
        readonly TextBlock counter = new TextBlock { FontSize = 11.5 };
        readonly TextBlock note = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Foreground = Theme.Warning, Visibility = Visibility.Collapsed };
        readonly TextBlock preview = new TextBlock { FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, FontFamily = Theme.Mono };
        readonly Button ok = new Button { Content = "확인", MinWidth = 64, Margin = new Thickness(6, 0, 0, 0), Focusable = false };
        readonly Button cancel = new Button { Content = "취소", MinWidth = 64, Margin = new Thickness(6, 0, 0, 0), Focusable = false };
        readonly Button clear = new Button { Content = "모두 해제", MinWidth = 64, Focusable = false };

        sealed class Entry
        {
            public CodeItem Item;
            public bool Free;            // "그대로 쓰기" 행
            public bool Descending;
            public Border Row;
        }
        List<Entry> entries = new List<Entry>();
        readonly List<Entry> chosen = new List<Entry>();   // 다중 선택: 체크한 순서
        List<Entry> visible = new List<Entry>();
        int highlight;
        string openedValue, openedCanonical;
        bool closingHandled;
        long closedAt = long.MinValue / 2; // 한 번도 닫힌 적 없음(빼기가 넘치지 않게 절반)

        public CodePopup()
        {
            Focusable = false;
            var cell = new DockPanel();
            DockPanel.SetDock(opener, Dock.Right);
            opener.SetResourceReference(Control.BackgroundProperty, Theme.ControlBackground);
            opener.SetResourceReference(Control.ForegroundProperty, Theme.SecondaryText);
            cell.Children.Add(opener);
            var host = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Hand };
            host.Children.Add(display);
            host.Children.Add(input);
            cell.Children.Add(host);
            Content = cell;
            // 누름으로 열지 않고 뗀 뒤에 연다: 표 칸(DataGridCell)은 처리된 누름도 받아 행 선택·끌기를 시작하며 마우스를 잡는다 —
            // 누를 때 열면 팝업(StaysOpen=false)이 마우스를 빼앗겨 바로 닫히고, [▾] 버튼의 Click도 오지 않는다.
            cell.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (AllowFreeText && input.IsAncestorOf(e.OriginalSource as DependencyObject)) return; // 입력 상자는 커서 놓기
                e.Handled = true;
                bool wasOpen = popup.IsOpen || Environment.TickCount64 - closedAt < 150;
                if (wasOpen) { Close(apply: Multi); return; }
                OpenAfterMouseUp();
            };

            input.SetResourceReference(Control.ForegroundProperty, Theme.PrimaryText);
            input.SetResourceReference(TextBoxBase.CaretBrushProperty, Theme.PrimaryText);
            input.TextChanged += (s, e) => { if (!writing) Write(input.Text); };
            PreviewKeyDown += (s, e) =>
            {
                if (popup.IsOpen) return;
                var alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
                if (e.Key == Key.F4 || alt && (e.SystemKey == Key.Down || e.Key == Key.Down)) { e.Handled = true; Open(); }
            };

            // 팝업 내용
            var border = new Border { Padding = new Thickness(8), BorderThickness = new Thickness(1), MinWidth = 320, MaxWidth = 640 };
            border.SetResourceReference(Border.BackgroundProperty, Theme.PanelBackground);
            border.SetResourceReference(Border.BorderBrushProperty, Theme.Accent);
            border.SetResourceReference(TextElement.ForegroundProperty, Theme.PrimaryText);
            border.SetResourceReference(TextElement.FontFamilyProperty, Theme.AppFont);
            TextElement.SetFontSize(border, 13);
            counter.SetResourceReference(TextBlock.ForegroundProperty, Theme.SecondaryText);
            preview.SetResourceReference(TextBlock.ForegroundProperty, Theme.SecondaryText);

            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            DockPanel.SetDock(counter, Dock.Right); head.Children.Add(counter); head.Children.Add(caption);

            search.SetResourceReference(Control.BackgroundProperty, Theme.SurfaceBackground);
            search.SetResourceReference(Control.ForegroundProperty, Theme.PrimaryText);
            search.SetResourceReference(Control.BorderBrushProperty, Theme.Border);
            search.SetResourceReference(TextBoxBase.CaretBrushProperty, Theme.PrimaryText);
            AutomationProperties.SetName(search, "검색");
            var hint = Theme.Secondary("🔍 검색 (코드·설명)  ·  ↑↓ 이동  ·  Enter 선택");
            hint.IsHitTestVisible = false; hint.Margin = new Thickness(7, 0, 0, 0); hint.VerticalAlignment = VerticalAlignment.Center;
            var searchHost = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            searchHost.Children.Add(search); searchHost.Children.Add(hint);
            search.TextChanged += (s, e) => { hint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; highlight = 0; Render(); };
            search.PreviewKeyDown += OnSearchKey;

            scroller = new ScrollViewer { Content = list, MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
            var listBorder = new Border { Child = scroller, BorderThickness = new Thickness(1) };
            listBorder.SetResourceReference(Border.BorderBrushProperty, Theme.Border);
            listBorder.SetResourceReference(Border.BackgroundProperty, Theme.SurfaceBackground);

            var foot = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            actions.Children.Add(clear); actions.Children.Add(ok); actions.Children.Add(cancel);
            DockPanel.SetDock(actions, Dock.Right); foot.Children.Add(actions);
            preview.VerticalAlignment = VerticalAlignment.Center; preview.Margin = new Thickness(0, 0, 8, 0);
            foot.Children.Add(preview);
            ok.Click += (s, e) => Close(apply: true);
            cancel.Click += (s, e) => Close(apply: false);
            clear.Click += (s, e) => { chosen.Clear(); Render(); search.Focus(); };

            var body = new DockPanel();
            DockPanel.SetDock(head, Dock.Top); body.Children.Add(head);
            DockPanel.SetDock(searchHost, Dock.Top); body.Children.Add(searchHost);
            DockPanel.SetDock(note, Dock.Top); body.Children.Add(note);
            DockPanel.SetDock(foot, Dock.Bottom); body.Children.Add(foot);
            body.Children.Add(listBorder);
            border.Child = body;

            // 논리 부모 없이 띄운다(위 설명). 바깥을 누르면 닫히고, 다중 선택은 그때 반영한다.
            popup = new Popup { Child = border, PlacementTarget = this, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.None };
            popup.Closed += (s, e) =>
            {
                if (!closingHandled && Multi) Apply();
                closingHandled = false;
                closedAt = Environment.TickCount64;
                // 포커스를 칸으로 돌려준다(표에서 키보드로 계속 움직일 수 있게).
                if (AllowFreeText) input.Focus();
                else for (var parent = VisualTreeHelper.GetParent(this); parent != null; parent = VisualTreeHelper.GetParent(parent))
                        if (parent is UIElement element && element.Focusable) { element.Focus(); break; }
            };
            Unloaded += (s, e) => { if (popup.IsOpen) Close(apply: false); };
            IsEnabledChanged += (s, e) => { if (!IsEnabled && popup.IsOpen) Close(apply: false); };
        }

        // ───────── 칸 ─────────

        void ShowValue()
        {
            var value = Value ?? "";
            input.Visibility = AllowFreeText ? Visibility.Visible : Visibility.Collapsed;
            display.Visibility = AllowFreeText ? Visibility.Collapsed : Visibility.Visible;
            if (Mono) input.FontFamily = display.FontFamily = Theme.Mono;
            else { input.ClearValue(Control.FontFamilyProperty); display.ClearValue(TextBlock.FontFamilyProperty); }
            if (input.Text != value) { writing = true; input.Text = value; writing = false; }
            var title = Multi || value.Length == 0 ? null : (ItemsSource?.Invoke() ?? Enumerable.Empty<CodeItem>()).FirstOrDefault(i => i.Code == value)?.Title;
            display.Text = string.IsNullOrEmpty(title) ? value : title;
            if (value.Length == 0 && !AllowFreeText) { display.Text = Multi ? "고르기…" : "선택…"; display.Opacity = 0.5; }
            else display.Opacity = 1;
        }

        void Write(string value)
        {
            SetCurrentValue(ValueProperty, value ?? "");
            GetBindingExpression(ValueProperty)?.UpdateSource();
        }

        // ───────── 팝업 ─────────

        /// <summary>마우스를 뗀 뒤(표가 마우스를 놓은 뒤) 팝업을 연다.</summary>
        void OpenAfterMouseUp()
        {
            var window = Window.GetWindow(this);
            if (window == null || Mouse.LeftButton != MouseButtonState.Pressed) { Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Open)); return; }
            MouseButtonEventHandler up = null;
            up = (s, e) =>
            {
                window.PreviewMouseLeftButtonUp -= up;
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Open));
            };
            window.PreviewMouseLeftButtonUp += up;
        }

        public void Toggle()
        {
            if (popup.IsOpen) Close(apply: Multi);
            // [▾]를 눌러 닫을 때: 바깥 클릭으로 이미 닫힌 직후의 Click이 다시 여는 것을 막는다.
            else if (Environment.TickCount64 - closedAt > 300) Open();
        }

        public void Open()
        {
            if (!IsEnabled || popup.IsOpen) return;
            var codes = (ItemsSource?.Invoke() ?? Enumerable.Empty<CodeItem>()).Where(i => i.Code.Length > 0).GroupBy(i => i.Code, StringComparer.Ordinal).Select(g => g.First()).ToList();
            entries = codes.Select(i => new Entry { Item = i }).ToList();
            chosen.Clear();
            note.Visibility = Visibility.Collapsed;
            openedValue = Value ?? "";
            if (Multi && openedValue.Trim().Length > 0)
            {
                try
                {
                    foreach (var choice in (Parse ?? SimpleParse)(openedValue, codes.Select(c => c.Code)))
                    {
                        var entry = entries.First(e => e.Item.Code == choice.Name);
                        entry.Descending = choice.Descending;
                        chosen.Add(entry);
                    }
                }
                catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
                {
                    chosen.Clear();
                    note.Text = "⚠ 지금 값(" + openedValue + ")을 목록으로 풀 수 없습니다. 고르면 새 값으로 바뀝니다.";
                    note.Visibility = Visibility.Visible;
                }
            }
            openedCanonical = Multi ? Text() : null;
            caption.Text = string.IsNullOrEmpty(Caption) ? (Multi ? "여러 개 고르기" : "고르기") : Caption;
            clear.Visibility = ok.Visibility = Multi ? Visibility.Visible : Visibility.Collapsed;
            cancel.Content = Multi ? "취소" : "닫기";
            search.Text = "";
            highlight = Multi ? 0 : Math.Max(0, entries.FindIndex(e => e.Item.Code == openedValue));
            Render();
            ((FrameworkElement)popup.Child).MinWidth = Math.Max(320, ActualWidth);
            popup.IsOpen = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => { search.Focus(); Keyboard.Focus(search); BringHighlightIntoView(); }));
        }

        public void Close(bool apply)
        {
            if (!popup.IsOpen) return;
            closingHandled = true;
            if (apply && Multi) Apply();
            popup.IsOpen = false;
        }

        void Apply()
        {
            if (!Multi) return;
            var text = Text();
            Write(text == openedCanonical ? openedValue : text); // 고른 것이 같으면 원래 글자를 그대로 둔다(따옴표 차이로 수정 표시되지 않게).
        }

        string Text() => (Format ?? SimpleFormat)(chosen.Select(e => new IndexColumnChoice { Name = e.Item.Code, Descending = AllowDescending && e.Descending }));

        void Choose(Entry entry)
        {
            if (Multi)
            {
                if (chosen.Contains(entry)) chosen.Remove(entry); else chosen.Add(entry);
                highlight = Math.Max(0, Visible().IndexOf(entry));
                Render();
                return;
            }
            Write(entry.Item.Code);
            Close(apply: false);
        }

        List<Entry> Visible()
        {
            var term = search.Text.Trim();
            Func<Entry, bool> match = e => term.Length == 0 || e.Item.Code.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 || e.Item.Title.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
            var result = Multi ? chosen.Where(match).Concat(entries.Where(e => !chosen.Contains(e) && match(e))).ToList() : entries.Where(match).ToList();
            if (!Multi && AllowFreeText && term.Length > 0 && !entries.Any(e => string.Equals(e.Item.Code, term, StringComparison.OrdinalIgnoreCase)))
                result.Insert(0, new Entry { Item = new CodeItem(term, "입력한 그대로 쓰기"), Free = true });
            return result;
        }

        void Render()
        {
            visible = Visible();
            highlight = Math.Min(Math.Max(0, highlight), Math.Max(0, visible.Count - 1));
            list.Children.Clear();
            if (visible.Count == 0)
            {
                var empty = Theme.Secondary(entries.Count == 0 ? "고를 항목이 없습니다." : "검색과 맞는 항목이 없습니다.");
                empty.Margin = new Thickness(8, 6, 8, 6);
                list.Children.Add(empty);
            }
            foreach (var entry in visible) list.Children.Add(Row(entry));
            Paint();
            counter.Text = Multi ? chosen.Count.ToString(CultureInfo.InvariantCulture) + "개 선택" : entries.Count.ToString(CultureInfo.InvariantCulture) + "개";
            preview.Text = Multi ? (chosen.Count == 0 ? "고른 것 없음" : Text()) : "";
            preview.ToolTip = preview.Text;
        }

        Border Row(Entry entry)
        {
            var row = new Border { Padding = new Thickness(6, 4, 6, 4), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            entry.Row = row;
            var line = new DockPanel();
            int order = chosen.IndexOf(entry);
            bool isChosen = Multi ? order >= 0 : !entry.Free && entry.Item.Code == openedValue;

            // 표시용 체크(누름은 행 전체가 받는다 — 체크박스와 행이 서로 토글을 다투지 않게)
            var mark = new TextBlock { Width = 34, VerticalAlignment = VerticalAlignment.Center, FontFamily = Theme.Mono };
            mark.Text = Multi ? (isChosen ? "☑ " + (order + 1).ToString(CultureInfo.InvariantCulture) : "☐") : (isChosen ? "✓" : entry.Free ? "✎" : "");
            mark.SetResourceReference(TextBlock.ForegroundProperty, isChosen ? Theme.Accent : Theme.SecondaryText);
            DockPanel.SetDock(mark, Dock.Left); line.Children.Add(mark);

            if (Multi && isChosen)
            {
                var tools = new StackPanel { Orientation = Orientation.Horizontal };
                if (AllowDescending)
                {
                    var direction = Small(entry.Descending ? "DESC" : "ASC", "정렬 방향 바꾸기", () => { entry.Descending = !entry.Descending; Render(); });
                    direction.Width = 44;
                    tools.Children.Add(direction);
                }
                var up = Small("↑", "앞으로", () => Move(entry, -1)); up.IsEnabled = order > 0; tools.Children.Add(up);
                var down = Small("↓", "뒤로", () => Move(entry, 1)); down.IsEnabled = order < chosen.Count - 1; tools.Children.Add(down);
                DockPanel.SetDock(tools, Dock.Right); line.Children.Add(tools);
            }

            var code = new TextBlock { Text = entry.Item.Code, VerticalAlignment = VerticalAlignment.Center, MinWidth = 150, Margin = new Thickness(0, 0, 10, 0) };
            if (Mono) code.FontFamily = Theme.Mono;
            if (isChosen) code.FontWeight = FontWeights.SemiBold;
            DockPanel.SetDock(code, Dock.Left); line.Children.Add(code);
            var title = Theme.Secondary(entry.Item.Title);
            title.VerticalAlignment = VerticalAlignment.Center; title.TextTrimming = TextTrimming.CharacterEllipsis;
            line.Children.Add(title);
            row.Child = line;
            AutomationProperties.SetName(row, entry.Item.Code);

            row.MouseEnter += (s, e) => { highlight = visible.IndexOf(entry); Paint(); };
            row.MouseLeftButtonDown += (s, e) => { e.Handled = true; Choose(entry); search.Focus(); };
            return row;
        }

        Button Small(string text, string tip, Action run)
        {
            var button = new Button { Content = text, ToolTip = tip, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(3, 0, 0, 0), Height = 22, Focusable = false, FontSize = 11 };
            button.Click += (s, e) => { run(); search.Focus(); };
            return button;
        }

        void Move(Entry entry, int offset)
        {
            int at = chosen.IndexOf(entry), to = at + offset;
            if (at < 0 || to < 0 || to >= chosen.Count) return;
            chosen.RemoveAt(at); chosen.Insert(to, entry);
            Render();
        }

        void Paint()
        {
            for (int i = 0; i < visible.Count; i++)
            {
                var row = visible[i].Row;
                if (row == null) continue;
                if (i == highlight) row.SetResourceReference(Border.BackgroundProperty, Theme.Selection);
                else row.Background = Brushes.Transparent;
            }
        }

        void BringHighlightIntoView()
        {
            if (highlight >= 0 && highlight < visible.Count) visible[highlight].Row?.BringIntoView();
        }

        void OnSearchKey(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Down: highlight = Math.Min(visible.Count - 1, highlight + 1); Paint(); BringHighlightIntoView(); e.Handled = true; break;
                case Key.Up: highlight = Math.Max(0, highlight - 1); Paint(); BringHighlightIntoView(); e.Handled = true; break;
                case Key.PageDown: highlight = Math.Min(visible.Count - 1, highlight + 8); Paint(); BringHighlightIntoView(); e.Handled = true; break;
                case Key.PageUp: highlight = Math.Max(0, highlight - 8); Paint(); BringHighlightIntoView(); e.Handled = true; break;
                case Key.Space:
                    if (Multi && highlight < visible.Count) { Choose(visible[highlight]); e.Handled = true; }
                    break;
                case Key.Enter:
                    e.Handled = true;
                    if (Multi) Close(apply: true);
                    else if (highlight < visible.Count) Choose(visible[highlight]);
                    break;
                case Key.Escape: e.Handled = true; Close(apply: false); break;
                case Key.Tab: e.Handled = true; Close(apply: Multi); break;
            }
        }

        static List<IndexColumnChoice> SimpleParse(string text, IEnumerable<string> available)
        {
            var names = new HashSet<string>(available, StringComparer.Ordinal);
            var result = new List<IndexColumnChoice>();
            foreach (var part in (text ?? "").Split(',').Select(p => p.Trim()).Where(p => p.Length > 0))
            {
                var desc = part.EndsWith(" DESC", StringComparison.OrdinalIgnoreCase);
                var name = desc ? part.Substring(0, part.Length - 5).Trim() : part;
                if (!names.Contains(name)) throw new ArgumentException("목록에 없는 값입니다: " + name);
                result.Add(new IndexColumnChoice { Name = name, Descending = desc });
            }
            return result;
        }

        static string SimpleFormat(IEnumerable<IndexColumnChoice> choices) => string.Join(", ", choices.Select(c => c.Name + (c.Descending ? " DESC" : "")));
    }
}

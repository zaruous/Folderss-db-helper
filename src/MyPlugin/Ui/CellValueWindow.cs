using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MyPlugin
{
    /// <summary>
    /// 결과 그리드 오른쪽 메뉴 [팝업으로 보기]: 칸 값 하나를 줄바꿈·스크롤로 보이는 창(모달 아님, Esc로 닫음).
    /// 그리드 값은 조회 때 잘린 것(글 4,000자·이진 2,000바이트)이라, 잘린 값이면 [전체 보기]로 DB에서 그 칸을 다시 읽는다(상한 ValueFormatter.MaxFullLength).
    /// 다시 읽을 수 없으면(직접 쓴 SQL 결과 등) 버튼을 끄고 이유를 보인다. 창을 닫으면 읽기를 취소한다.
    /// </summary>
    internal static class CellValueWindow
    {
        /// <param name="load">[전체 보기]로 DB에서 다시 읽기(null이면 못 읽음). 결과 null = NULL. 실패는 예외(메시지를 보임).</param>
        /// <param name="blockReason">load가 null일 때 보일 이유.</param>
        public static Window Show(Window owner, string columnName, string typeLabel, int rowNumber, string value,
            Func<CancellationToken, Task<string>> load, string blockReason)
        {
            var window = new Window
            {
                Title = "값 — " + columnName + " (" + rowNumber.ToString("N0", CultureInfo.CurrentCulture) + "행)",
                Width = 720,
                Height = 480,
                MinWidth = 400,
                MinHeight = 240,
                ShowInTaskbar = false,
                ResizeMode = ResizeMode.CanResizeWithGrip
            };
            Theme.ApplyWindow(window);
            DialogKit.SetOwner(window, owner);

            var root = new DockPanel { Margin = new Thickness(14, 12, 14, 12) };
            var header = new WrapPanel();
            var name = Theme.Text(columnName ?? "");
            name.FontSize = 16;
            name.FontWeight = FontWeights.SemiBold;
            name.Margin = new Thickness(0, 0, 8, 0);
            header.Children.Add(name);
            if (!string.IsNullOrEmpty(typeLabel))
            {
                var type = Theme.Text(typeLabel, Theme.SecondaryText);
                type.FontFamily = Theme.Mono;
                type.VerticalAlignment = VerticalAlignment.Bottom;
                type.Margin = new Thickness(0, 0, 0, 2);
                header.Children.Add(type);
            }
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var note = DialogKit.Hint("", 4);
            DockPanel.SetDock(note, Dock.Top);
            root.Children.Add(note);

            var text = new TextBox
            {
                IsReadOnly = true,
                IsReadOnlyCaretVisible = true,
                AcceptsReturn = true,
                AcceptsTab = true,
                // 긴 한 줄 값(JSON·16진수)이 많아 줄을 바꿔 보인다
                TextWrapping = TextWrapping.Wrap,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                // Folderss의 암시적 TextBox 스타일은 한 줄 입력칸용(가운데 정렬)이다
                VerticalContentAlignment = VerticalAlignment.Top,
                FontFamily = Theme.Mono,
                Margin = new Thickness(0, 8, 0, 0)
            };
            SpellCheck.SetIsEnabled(text, false);
            Theme.Background(text, Theme.PanelBackground);
            Theme.Foreground(text);
            System.Windows.Automation.AutomationProperties.SetName(text, "값");

            var truncated = RowEditLogic.IsTruncated(value);
            var full = new Button
            {
                Content = "전체 보기",
                Padding = new Thickness(12, 3, 12, 3),
                Margin = new Thickness(0, 0, 6, 0),
                Visibility = truncated ? Visibility.Visible : Visibility.Collapsed,
                IsEnabled = load != null,
                ToolTip = load != null ? "DB에서 이 칸을 다시 읽습니다(최대 " + ValueFormatter.MaxFullLength.ToString("N0", CultureInfo.CurrentCulture) + "자·바이트)" : blockReason
            };
            ToolTipService.SetShowOnDisabled(full, true);
            var close = new Button { Content = "닫기", Padding = new Thickness(16, 3, 16, 3), IsCancel = true };
            close.Click += (s, e) => window.Close();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            buttons.Children.Add(full);
            buttons.Children.Add(close);
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);
            root.Children.Add(text);
            window.Content = root;

            SetValue(text, note, value, truncated
                ? "그리드에 있는 앞부분만 보입니다." + (load != null ? " [전체 보기]로 DB에서 다시 읽을 수 있습니다." : " " + blockReason)
                : null);

            var cancel = new CancellationTokenSource();
            full.Click += async (s, e) =>
            {
                full.IsEnabled = false;
                note.Text = "DB에서 읽는 중…";
                try
                {
                    var loaded = await load(cancel.Token);
                    if (cancel.IsCancellationRequested)
                        return;
                    var stillTruncated = RowEditLogic.IsTruncated(loaded);
                    SetValue(text, note, loaded, "DB에서 다시 읽은 값입니다(조회한 뒤 바뀌었을 수 있음)."
                        + (stillTruncated ? " 상한(" + ValueFormatter.MaxFullLength.ToString("N0", CultureInfo.CurrentCulture) + "자·바이트)까지만 보입니다." : ""));
                    full.Visibility = Visibility.Collapsed;
                }
                catch (Exception ex)
                {
                    if (cancel.IsCancellationRequested)
                        return;
                    note.Text = ex is OperationCanceledException ? "읽기를 취소했습니다." : "읽지 못했습니다: " + ex.Message;
                    full.IsEnabled = true;
                }
            };
            window.Closed += (s, e) => cancel.Cancel();
            window.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    window.Close();
                }
            };
            window.Loaded += (s, e) =>
            {
                text.Focus();
                text.CaretIndex = 0;
            };
            window.Show();
            return window;
        }

        // NULL은 빈 글 + 안내
        private static void SetValue(TextBox text, TextBlock note, string value, string message)
        {
            text.Text = value ?? "";
            note.Text = value == null ? "NULL" + (message != null ? " · " + message : "") : message ?? "";
            note.Visibility = string.IsNullOrEmpty(note.Text) ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}

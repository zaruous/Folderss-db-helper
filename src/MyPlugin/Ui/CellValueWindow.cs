using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MyPlugin
{
    /// <summary>
    /// 결과 그리드 오른쪽 메뉴 [팝업으로 보기]: 칸 값 하나를 줄바꿈·스크롤로 보이는 창(모달 아님, Esc로 닫음).
    /// 그리드가 가진 값 그대로 보인다(DB에 다시 가지 않음) — 조회 때 잘린 값(글 4,000자·이진 2,000바이트)은 "…(전체 n자)" 꼬리까지만.
    /// </summary>
    internal static class CellValueWindow
    {
        public static Window Show(Window owner, string columnName, string typeLabel, int rowNumber, string value)
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

            var message = value == null ? "NULL"
                : RowEditLogic.IsTruncated(value) ? "조회할 때 잘린 값입니다(글 4,000자·이진 2,000바이트까지). 끝의 \"…(전체 n자)\"가 전체 길이입니다."
                : null;
            if (message != null)
            {
                var note = DialogKit.Hint(message, 4);
                DockPanel.SetDock(note, Dock.Top);
                root.Children.Add(note);
            }

            var close = new Button { Content = "닫기", Padding = new Thickness(16, 3, 16, 3), IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            close.Click += (s, e) => window.Close();
            DockPanel.SetDock(close, Dock.Bottom);
            root.Children.Add(close);

            var text = new TextBox
            {
                Text = value ?? "",
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
            root.Children.Add(text);
            window.Content = root;

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
    }
}

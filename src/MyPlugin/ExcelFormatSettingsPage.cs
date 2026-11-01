using Folderss.Plugins;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace MyPlugin
{
    /// <summary>
    /// 설정 창 "엑셀 서식": 결과 편집의 [엑셀 내보내기]·[엑셀 임포트]에 쓸 서식(열 순서·셀 서식을 정한 .xlsx)을 등록·관리한다.
    /// 처음 등록 항목은 "기본 서식"(자동 — 결과 열 그대로, 지울 수 없음). [파일 추가…]는 파일을 관리 폴더에 복사해 등록하고,
    /// 이름·대상 테이블(비우면 모든 결과)을 고친다. [엑셀로 열기]로 서식을 고친다(규칙: 데이터 시트 첫 행 = DB 컬럼명).
    /// 결과 그리드의 "이 결과로 서식 만들기"도 여기에 등록된다. [저장]하면 목록이 설정 값에 남는다(파일 삭제는 저장할 때).
    /// </summary>
    public sealed class ExcelFormatSettingsPage : IPluginSettingsPage
    {
        private readonly IPluginManager _manager;
        private List<ExcelFormatEntry> _working;
        private readonly List<string> _removedFiles = new List<string>();
        private ListBox _list;
        private TextBox _name;
        private TextBox _table;
        private TextBlock _detail;
        private Button _open;
        private Button _delete;
        private bool _filling;

        public ExcelFormatSettingsPage(IPluginManager manager)
        {
            _manager = manager;
            ExcelFormats.DataDirectory = manager.DataDirectory;
            ExcelFormats.Set(ExcelFormats.Parse(manager.GetSetting(ExcelFormats.SettingKey)));
            ExcelFormats.Persist = json => _manager.SetSetting(ExcelFormats.SettingKey, json);
        }

        public string Title { get { return "엑셀 서식"; } }

        public FrameworkElement CreateView()
        {
            _working = ExcelFormats.Registered.Select(e => e.Copy()).ToList();
            _removedFiles.Clear();
            var root = new DockPanel { Margin = new Thickness(8) };

            var intro = Theme.Text("결과 편집의 [엑셀 내보내기]·[엑셀 임포트]에 쓸 서식입니다. 서식 파일의 데이터 시트 첫 행은 DB 컬럼명이고, 열 순서·너비·색·셀 서식은 자유롭게 바꿔도 됩니다.", Theme.SecondaryText);
            intro.TextWrapping = TextWrapping.Wrap;
            intro.Margin = new Thickness(0, 0, 0, 8);
            DockPanel.SetDock(intro, Dock.Top);
            root.Children.Add(intro);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            var add = new Button { Content = "파일 추가…", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 6, 0) };
            add.Click += (s, e) => AddFile();
            _open = new Button { Content = "엑셀로 열기", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 6, 0) };
            _open.Click += (s, e) => OpenFile();
            _delete = new Button { Content = "삭제", Padding = new Thickness(10, 2, 10, 2) };
            _delete.Click += (s, e) => Delete();
            AutomationProperties.SetAutomationId(add, "excel-format-add");
            AutomationProperties.SetAutomationId(_open, "excel-format-open");
            AutomationProperties.SetAutomationId(_delete, "excel-format-delete");
            buttons.Children.Add(add); buttons.Children.Add(_open); buttons.Children.Add(_delete);
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);

            var form = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
            form.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < 3; i++) form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _name = new TextBox { Margin = new Thickness(0, 0, 0, 4) };
            AutomationProperties.SetName(_name, "서식 이름");
            _name.TextChanged += (s, e) => Edit(entry => entry.Name = _name.Text.Trim());
            _table = new TextBox { Margin = new Thickness(0, 0, 0, 4), ToolTip = "OWNER.TABLE 또는 TABLE. 비우면 모든 결과에 보입니다." };
            AutomationProperties.SetName(_table, "대상 테이블");
            _table.TextChanged += (s, e) => Edit(entry => entry.Table = _table.Text.Trim());
            _detail = Theme.Text("", Theme.SecondaryText);
            _detail.TextWrapping = TextWrapping.Wrap;
            Place(form, Theme.Text("이름"), 0, 0); Place(form, _name, 0, 1);
            Place(form, Theme.Text("대상 테이블"), 1, 0); Place(form, _table, 1, 1);
            Place(form, _detail, 2, 1);
            DockPanel.SetDock(form, Dock.Bottom);
            root.Children.Add(form);

            _list = new ListBox { MinHeight = 140 };
            AutomationProperties.SetName(_list, "등록한 엑셀 서식");
            _list.SelectionChanged += (s, e) => Fill();
            root.Children.Add(_list);
            Render(null);
            return root;
        }

        static void Place(Grid grid, UIElement element, int row, int column)
        {
            Grid.SetRow(element, row); Grid.SetColumn(element, column);
            if (element is FrameworkElement fe && column == 0) fe.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(element);
        }

        ExcelFormatEntry Selected => (_list?.SelectedItem as ListBoxItem)?.Tag as ExcelFormatEntry;

        void Render(ExcelFormatEntry select)
        {
            _list.Items.Clear();
            foreach (var entry in new[] { ExcelFormats.Default }.Concat(_working))
            {
                var item = new ListBoxItem { Content = Label(entry), Tag = entry };
                AutomationProperties.SetName(item, entry.Name);
                _list.Items.Add(item);
            }
            _list.SelectedIndex = select == null ? 0 : Math.Max(0, _list.Items.Cast<ListBoxItem>().ToList().FindIndex(i => i.Tag == select));
            Fill();
        }

        static string Label(ExcelFormatEntry entry)
            => entry.BuiltIn ? entry.Name : entry.Name + "   ·   " + (string.IsNullOrWhiteSpace(entry.Table) ? "모든 결과" : entry.Table);

        void Fill()
        {
            var entry = Selected;
            _filling = true;
            _name.Text = entry?.Name ?? "";
            _table.Text = entry?.Table ?? "";
            _filling = false;
            var builtIn = entry == null || entry.BuiltIn;
            _name.IsEnabled = _table.IsEnabled = _delete.IsEnabled = _open.IsEnabled = !builtIn;
            if (builtIn)
                _detail.Text = "처음 등록 항목 — 내보낼 때 결과 열 그대로 새 파일을 만듭니다(문자 열은 텍스트, 숫자·날짜 열은 엑셀 숫자·날짜). 지울 수 없습니다.";
            else
            {
                var path = ExcelFormats.PathOf(entry);
                _detail.Text = "파일: " + path + (File.Exists(path) ? "" : "  (없음 — 다시 추가하세요)");
            }
        }

        void Edit(Action<ExcelFormatEntry> change)
        {
            if (_filling) return;
            var entry = Selected;
            if (entry == null || entry.BuiltIn) return;
            change(entry);
            if (_list.SelectedItem is ListBoxItem item) item.Content = Label(entry);
        }

        void AddFile()
        {
            var open = new Microsoft.Win32.OpenFileDialog { Title = "엑셀 서식 파일 추가", Filter = "Excel 통합 문서 (*.xlsx)|*.xlsx" };
            if (open.ShowDialog() != true) return;
            try
            {
                var entry = ExcelFormats.Import(open.FileName, "");
                _working.Add(entry);
                Render(entry);
            }
            catch (Exception ex)
            {
                Dialogs.Show(Window.GetWindow(_list), "엑셀 서식", "서식으로 등록할 수 없습니다: " + ex.Message, true);
            }
        }

        void OpenFile()
        {
            var path = ExcelFormats.PathOf(Selected);
            if (path == null || !File.Exists(path)) return;
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { Dialogs.Show(Window.GetWindow(_list), "엑셀 서식", "파일을 열 수 없습니다: " + ex.Message, true); }
        }

        void Delete()
        {
            var entry = Selected;
            if (entry == null || entry.BuiltIn) return;
            _working.Remove(entry);
            _removedFiles.Add(entry.File);
            Render(null);
        }

        public void Save()
        {
            // 설정 창을 한 번도 열지 않았으면 바꿀 값이 없다
            if (_working == null)
                return;
            if (_working.Any(e => string.IsNullOrWhiteSpace(e.Name)))
                throw new InvalidOperationException("엑셀 서식 이름을 입력하세요.");
            ExcelFormats.Set(_working);
            _manager.SetSetting(ExcelFormats.SettingKey, ExcelFormats.Serialize(_working));
            foreach (var file in _removedFiles.Where(f => _working.All(e => e.File != f)))
            {
                try { File.Delete(Path.Combine(ExcelFormats.Folder, file)); }
                catch (Exception) { /* 엑셀이 열고 있으면 남긴다 — 목록에서는 빠졌다 */ }
            }
            _removedFiles.Clear();
        }
    }
}

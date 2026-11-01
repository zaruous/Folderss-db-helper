using Folderss.Plugins;
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace MyPlugin
{
    /// <summary>
    /// 설정 창 "데이터 조회" 쪽: 트리 F1 빠른 조회 행 수, Oracle FetchSize(바이트).
    /// 플러그인을 불러올 때 저장된 값을 QueryOptions에 넣고, [저장]하면 다음 조회부터 바로 쓴다.
    /// </summary>
    public sealed class QuerySettingsPage : IPluginSettingsPage
    {
        public const string RowsSettingKey = "query.rows";
        public const string FetchBytesSettingKey = "query.fetchBytes";
        public const int DefaultRows = 100;
        public const int MaxRows = 10000;
        public const int DefaultFetchBytes = 131072;
        public const int MinFetchBytes = 8192;
        public const int MaxFetchBytes = 67108864;

        private readonly IPluginManager _manager;
        private TextBox _rows;
        private TextBox _bytes;

        public QuerySettingsPage(IPluginManager manager)
        {
            _manager = manager;
            QueryOptions.QuickRows = Read(RowsSettingKey, DefaultRows, 1, MaxRows);
            QueryOptions.FetchBytes = Read(FetchBytesSettingKey, DefaultFetchBytes, MinFetchBytes, MaxFetchBytes);
        }

        public string Title { get { return "데이터 조회"; } }

        public FrameworkElement CreateView()
        {
            var root = new StackPanel { Margin = new Thickness(8) };
            _rows = Field(root, "F1 빠른 조회 행 수", QueryOptions.QuickRows, 1, MaxRows, DefaultRows, "query-settings-rows");
            _bytes = Field(root, "Oracle FetchSize (바이트)", QueryOptions.FetchBytes, MinFetchBytes, MaxFetchBytes, DefaultFetchBytes, "query-settings-fetch-bytes");
            return root;
        }

        public void Save()
        {
            // 설정 창을 한 번도 열지 않았으면 바꿀 값이 없다
            if (_rows == null || _bytes == null)
                return;
            int rows, bytes;
            if (!TryParse(_rows.Text, 1, MaxRows, out rows))
                throw new InvalidOperationException("F1 빠른 조회 행 수는 1~" + MaxRows.ToString("N0", CultureInfo.InvariantCulture) + " 사이로 입력하세요.");
            if (!TryParse(_bytes.Text, MinFetchBytes, MaxFetchBytes, out bytes))
                throw new InvalidOperationException("Oracle FetchSize는 " + MinFetchBytes.ToString("N0", CultureInfo.InvariantCulture) + "~"
                    + MaxFetchBytes.ToString("N0", CultureInfo.InvariantCulture) + " 바이트 사이로 입력하세요.");
            _manager.SetSetting(RowsSettingKey, rows.ToString(CultureInfo.InvariantCulture));
            _manager.SetSetting(FetchBytesSettingKey, bytes.ToString(CultureInfo.InvariantCulture));
            QueryOptions.QuickRows = rows;
            QueryOptions.FetchBytes = bytes;
        }

        private static TextBox Field(Panel root, string label, int value, int min, int max, int fallback, string automationId)
        {
            var head = Theme.Text(label);
            head.Margin = new Thickness(0, root.Children.Count == 0 ? 0 : 12, 0, 4);
            root.Children.Add(head);
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            var box = new TextBox { Width = 110, Text = value.ToString(CultureInfo.InvariantCulture), HorizontalContentAlignment = HorizontalAlignment.Right };
            AutomationProperties.SetName(box, label);
            AutomationProperties.SetAutomationId(box, automationId);
            line.Children.Add(box);
            var range = Theme.Text("(" + min.ToString("N0", CultureInfo.InvariantCulture) + "~" + max.ToString("N0", CultureInfo.InvariantCulture)
                + ", 기본 " + fallback.ToString("N0", CultureInfo.InvariantCulture) + ")", Theme.SecondaryText);
            range.VerticalAlignment = VerticalAlignment.Center;
            range.Margin = new Thickness(8, 0, 0, 0);
            line.Children.Add(range);
            root.Children.Add(line);
            return box;
        }

        private int Read(string key, int fallback, int min, int max)
        {
            int value;
            return TryParse(_manager.GetSetting(key), min, max, out value) ? value : fallback;
        }

        private static bool TryParse(string text, int min, int max, out int value)
        {
            return int.TryParse((text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= min && value <= max;
        }
    }
}

using Folderss.Plugins;
using System.Windows;
using System.Windows.Controls;

namespace MyPlugin
{
    /// <summary>
    /// 플러그인 진입점. plugin.json의 "type"(네임스페이스.클래스)과 이름이 같아야 한다.
    /// public이고 인수 없는 public 생성자가 있어야 한다.
    /// </summary>
    public sealed class MyPlugin : IFolderssPlugin
    {
        private IPluginManager _manager;

        /// <summary>처음 실행할 때 한 번 호출된다.</summary>
        public void Initialize(IPluginManager manager)
        {
            _manager = manager;

            manager.AddSettingsPage(new ConnectionSettingsPage(manager));
        }

        /// <summary>
        /// ⋯ 메뉴 > 플러그인에서 실행할 때마다 호출된다. 돌려준 요소가 팝업 창의 내용이 된다.
        /// 매번 새 요소를 만들어야 한다 (이미 다른 창에 붙은 요소를 다시 돌려주면 WPF 예외).
        /// </summary>
        public FrameworkElement CreateView()
        {
            var root = new DockPanel { Margin = new Thickness(16) };

            var title = new TextBlock
            {
                Text = "My Plugin",
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 12)
            };
            DockPanel.SetDock(title, Dock.Top);
            root.Children.Add(title);

            var body = new TextBlock
            {
                Text = "여기에 화면을 만드세요.\n플러그인 ID: " + _manager.PluginId,
                TextWrapping = TextWrapping.Wrap
            };
            // 색은 고정값 대신 Folderss 테마 리소스 키에 연결한다 (테마를 바꾸면 함께 바뀜).
            body.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
            root.Children.Add(body);

            return root;
        }
    }
}

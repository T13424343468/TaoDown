// ==== NavigationView / Frame 桩: 与 XAML 声明的成员对齐 ====
namespace Microsoft.UI.Xaml.Controls
{
    public class NavigationViewSelectionChangedEventArgs : global::System.EventArgs
    {
        public object? SelectedItem { get; set; }
        public bool IsSettingsSelected { get; set; }
    }

    public class NavigationView : FrameworkElement
    {
        public Microsoft.UI.Xaml.Media.Brush? Background { get; set; }
        public global::System.Collections.Generic.IList<object> MenuItems { get; } =
            new global::System.Collections.Generic.List<object>();
        public object? SelectedItem { get; set; }
        public NavigationViewPaneDisplayMode PaneDisplayMode { get; set; }
        public NavigationViewBackButtonVisible IsBackButtonVisible { get; set; }
        public bool IsPaneToggleButtonVisible { get; set; }
        public bool IsSettingsVisible { get; set; }
        public double OpenPaneLength { get; set; }
        public event global::System.EventHandler<NavigationViewSelectionChangedEventArgs> SelectionChanged { add { } remove { } }
    }

    public enum NavigationViewPaneDisplayMode { Auto, Left, Top, LeftMinimal, LeftCompact }

    // v2.51: NavigationView 模板内含 RootSplitView；PinSplitViewPane 直涂其 PaneBackground
    public class SplitView : global::Microsoft.UI.Xaml.DependencyObject
    {
        public Microsoft.UI.Xaml.Media.Brush? PaneBackground { get; set; }
    }

    public enum NavigationViewBackButtonVisible { Auto, Collapsed, Visible }

    public class NavigationViewItem : UIElement
    {
        public object? Content { get; set; }
        public object? Tag { get; set; }
        public NavigationViewItemIcon? Icon { get; set; }
    }

    public class NavigationViewItemIcon { }

    public class FontIcon : NavigationViewItemIcon
    {
        public string Glyph { get; set; } = "";
    }

    public class Frame : Control
    {
        public object? Content { get; }
        public void Navigate(global::System.Type pageType) { }
        public void Navigate(global::System.Type pageType, object? parameter) { }
        public bool CanGoBack { get; }
        public void GoBack() { }
    }
}

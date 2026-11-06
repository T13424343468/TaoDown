// ==== WinUI3 类型桩: 仅用于语法/类型检查, 不随产品分发 ====
namespace Microsoft.UI.Xaml
{
    public class Window
    {
        private ContentHolder _content = new();
        public Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; } = new();
        public UIElement Content { get => _content; set => _content = (ContentHolder)value; }
        public bool ExtendsContentIntoTitleBar { get; set; }
        public Microsoft.UI.Windowing.AppWindow AppWindow { get; } = new();
        public string Title { get; set; } = "";
        public void Activate() { }
        protected void InitializeComponent() { }
        public void Close() { }
        public event global::System.EventHandler<object> Closed { add { } remove { } }
    }

    public class ContentHolder : UIElement
    {
        public Microsoft.UI.Xaml.XamlRoot XamlRoot { get; } = new();
    }

    public class RoutedEventArgs { }

    public class UIElement : DependencyObject
    {
        public Microsoft.UI.Xaml.Visibility Visibility { get; set; }
        public Microsoft.UI.Xaml.XamlRoot? XamlRoot { get; set; }
        public double MinWidth { get; set; }
        public double Height { get; set; }
        public double MinHeight { get; set; }
        public event global::System.EventHandler<object> DoubleTapped { add { } remove { } }
    }

    public class DependencyObject { }

    public class FrameworkElement : UIElement
    {
        public Microsoft.UI.Xaml.XamlRoot XamlRoot { get; set; } = new();
        public Microsoft.UI.Xaml.HorizontalAlignment HorizontalAlignment { get; set; }
        public Microsoft.UI.Xaml.Thickness Margin { get; set; }
        public double MaxWidth { get; set; }
        public Microsoft.UI.Xaml.ElementTheme RequestedTheme { get; set; }
        public Microsoft.UI.Xaml.ElementTheme ActualTheme { get; }
        public Microsoft.UI.Xaml.ResourceDictionary Resources { get; } = new();
        public object? DataContext { get; set; }
        public object? Tag { get; set; }
        // v2.51: MainWindow 在 Nav.Loaded 补钉窗格底色（真实 API: FrameworkElement.Loaded）
        public event global::System.EventHandler<object> Loaded { add { } remove { } }
    }

    public class XamlRoot { }

    public struct Thickness
    {
        public double Left, Top, Right, Bottom;
        // v2.59: 补真实 API 的两个构造（统一边距 / 四向边距），AskWindow 等新代码依赖
        public Thickness(double uniform) { Left = Top = Right = Bottom = uniform; }
        public Thickness(double left, double top, double right, double bottom) { Left = left; Top = top; Right = right; Bottom = bottom; }
    }

    public enum HorizontalAlignment { Left, Center, Right, Stretch }

    public class TextWrapping { public static TextWrapping Wrap = new(); }

    public enum Visibility { Visible, Collapsed }

    public class TextTrimming { }

    public enum VerticalAlignment { Top, Center, Bottom, Stretch }

    public struct GridLength { public static GridLength Auto => default; }

    public class Application
    {
        public static Application Current { get; } = new();
        public Microsoft.UI.Xaml.ResourceDictionary Resources { get; } = new();
        public Microsoft.UI.Xaml.Window? Window { get; set; }
        protected void InitializeComponent() { }
    }

    public class ResourceDictionary
    {
        public object? this[object key] { get => null; set { } }
        public Uri? Source { get; set; }
        // 真实 WinUI ResourceDictionary 实现 IDictionary<object,object>，Remove 可摘除键值；
        // v2.49 钉色兜底（取不到键时移除旧钉值）会调用，桩体必须同步该 API。
        public void Remove(object key) { }
        public global::System.Collections.Generic.IList<ResourceDictionary> MergedDictionaries { get; } =
            new global::System.Collections.Generic.List<ResourceDictionary>();
    }

    public enum ElementTheme { Default, Light, Dark }

    public class LaunchActivatedEventArgs { }
}

namespace Microsoft.UI.Dispatching
{
    public class DispatcherQueue
    {
        public bool HasThreadAccess { get; } = true;
        public bool TryEnqueue(global::System.Action callback) { callback(); return true; }
        public static DispatcherQueue GetForCurrentThread() => new();

        public class Timer
        {
            public global::System.TimeSpan Interval { get; set; }
            public event global::System.EventHandler<object> Tick { add { } remove { } }
            public void Start() { }
            public void Stop() { }
        }

        public DispatcherQueueTimer CreateTimer() => new();
    }

    /// <summary>真实 WinUI 中 DispatcherQueueTimer 是独立类型（CreateTimer 返回值），非嵌套类。</summary>
    public class DispatcherQueueTimer
    {
        public global::System.TimeSpan Interval { get; set; }
        public bool IsRepeating { get; set; }
        public event global::System.EventHandler<object> Tick { add { } remove { } }
        public void Start() { }
        public void Stop() { }
    }
}

namespace Microsoft.UI.Text
{
    public static class FontWeights
    {
        public static readonly FontWeight SemiBold = default;
        public static readonly FontWeight Normal = default;
        public static readonly FontWeight Bold = default;
    }

    public struct FontWeight { }
}

namespace Microsoft.UI.Windowing
{
    public class AppWindow
    {
        public string Title { get; set; } = "";
        public Microsoft.UI.Windowing.AppWindowTitleBar? TitleBar { get; } = new();
        public void Resize(Windows.Graphics.SizeInt32 size) { }
        public void Move(Windows.Graphics.PointInt32 position) { }
        public void SetIcon(string iconPath) { }
        public void Show() { }
        public void Hide() { }
        public bool IsAlwaysOnTop { get; set; }
        public Microsoft.UI.WindowId Id { get; }
        public event Microsoft.UI.Windowing.AppWindowClosingTypedEventHandler Closing { add { } remove { } }
    }

    public class AppWindowTitleBar
    {
        public Windows.UI.Color BackgroundColor { get; set; }
        public Windows.UI.Color ForegroundColor { get; set; }
        public Windows.UI.Color ButtonBackgroundColor { get; set; }
        public Windows.UI.Color ButtonForegroundColor { get; set; }
        public Windows.UI.Color ButtonHoverBackgroundColor { get; set; }
        public Windows.UI.Color ButtonHoverForegroundColor { get; set; }
        public Windows.UI.Color ButtonPressedBackgroundColor { get; set; }
        public Windows.UI.Color ButtonPressedForegroundColor { get; set; }
    }

    public class AppWindowClosingEventArgs { public bool Cancel { get; set; } }

    public delegate void AppWindowClosingTypedEventHandler(AppWindow sender, AppWindowClosingEventArgs args);

    public enum GetAncestorAreaOptions { Primary, Nearest }

    public class DisplayArea
    {
        public static DisplayArea GetFromWindowId(Microsoft.UI.WindowId windowId, GetAncestorAreaOptions options) => new();
        public static DisplayArea Primary { get; } = new();
        public Windows.Graphics.RectInt32 WorkArea { get; }
        public Windows.Graphics.RectInt32 OuterBounds { get; }
    }
}

namespace Microsoft.UI.Xaml.Controls
{
    public class Control : FrameworkElement
    {
        public Microsoft.UI.Xaml.Media.Brush? Background { get; set; }
        public Microsoft.UI.Xaml.Media.Brush? Foreground { get; set; }
    }

    public class RadioButtons : Control
    {
        public int SelectedIndex { get; set; }
        public event global::System.EventHandler<SelectionChangedEventArgs> SelectionChanged { add { } remove { } }
    }

    public class Page : UIElement
    {
        protected void InitializeComponent() { }
        protected virtual void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e) { }
        protected virtual void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e) { }
    }

    public class Panel : FrameworkElement
    {
        public global::System.Collections.Generic.IList<UIElement> Children { get; } =
            new global::System.Collections.Generic.List<UIElement>();
    }

    public class StackPanel : Panel
    {
        public Microsoft.UI.Xaml.Media.Brush? Background { get; set; }
        public double Spacing { get; set; }
        public global::Microsoft.UI.Xaml.Controls.Orientation Orientation { get; set; }
        public Microsoft.UI.Xaml.VerticalAlignment VerticalAlignment { get; set; }
    }

    public enum Orientation { Horizontal, Vertical }

    public class Grid : Panel
    {
        public Microsoft.UI.Xaml.Media.Brush? Background { get; set; }
        public double RowSpacing { get; set; }
        public double ColumnSpacing { get; set; }
        public global::System.Collections.Generic.IList<RowDefinition> RowDefinitions { get; } =
            new global::System.Collections.Generic.List<RowDefinition>();
        public global::System.Collections.Generic.IList<ColumnDefinition> ColumnDefinitions { get; } =
            new global::System.Collections.Generic.List<ColumnDefinition>();
    }

    public class RowDefinition { public Microsoft.UI.Xaml.GridLength Height { get; set; } }
    public class ColumnDefinition { public Microsoft.UI.Xaml.GridLength Width { get; set; } }

    public class TextBlock : UIElement
    {
        public string Text { get; set; } = "";
        public double FontSize { get; set; }
        public double Opacity { get; set; }
        public Microsoft.UI.Xaml.Thickness Margin { get; set; }
        public Microsoft.UI.Xaml.Thickness Padding { get; set; }
        public Microsoft.UI.Xaml.TextWrapping TextWrapping { get; set; }
        public Microsoft.UI.Xaml.TextTrimming TextTrimming { get; set; }
        public Microsoft.UI.Xaml.VerticalAlignment VerticalAlignment { get; set; }
        public int MaxLines { get; set; }
        public Microsoft.UI.Text.FontWeight FontWeight { get; set; }
        public object? Style { get; set; }
    }

    public class TextBox : UIElement
    {
        public string Text { get; set; } = "";
        public string? PlaceholderText { get; set; }
        public string? Header { get; set; }
        public Microsoft.UI.Xaml.VerticalAlignment VerticalAlignment { get; set; }
        public event global::System.EventHandler<Microsoft.UI.Xaml.Input.KeyRoutedEventArgs> KeyDown { add { } remove { } }
    }

    public class PasswordBox : UIElement
    {
        public string Password { get; set; } = "";
        public string? PlaceholderText { get; set; }
        public string? Header { get; set; }
    }

    public class Button : UIElement
    {
        public object? Content { get; set; }
        public Microsoft.UI.Xaml.Thickness Padding { get; set; }
        public event global::System.EventHandler<Microsoft.UI.Xaml.RoutedEventArgs> Click { add { } remove { } }
    }

    public class CheckBox : UIElement
    {
        public object? Content { get; set; }
        public bool? IsChecked { get; set; }
    }

    public class ToggleSwitch : UIElement
    {
        public bool IsOn { get; set; }
        public string? Header { get; set; }
        public object? OnContent { get; set; }
        public object? OffContent { get; set; }
        public bool IsEnabled { get; set; }
    }

    public class NumberBox : UIElement
    {
        public double Value { get; set; }
        public double Minimum { get; set; }
        public double Maximum { get; set; }
        public string? Header { get; set; }
        public int SpinButtonPlacementMode { get; set; }
    }

    public class ComboBox : Selector
    {
        public string? Header { get; set; }
        public Microsoft.UI.Xaml.VerticalAlignment VerticalAlignment { get; set; }
    }

    public class ComboBoxItem : UIElement
    {
        public object? Content { get; set; }
        public object? Tag { get; set; }
    }

    public class Selector : UIElement
    {
        public int SelectedIndex { get; set; }
        public object? SelectedItem { get; set; }
        public event global::System.EventHandler<SelectionChangedEventArgs> SelectionChanged { add { } remove { } }
    }

    public class SelectionChangedEventArgs : global::System.EventArgs { }

    public class ListView : Selector
    {
        public object? ItemsSource { get; set; }
        public global::System.Collections.Generic.IList<object> Items { get; } =
            new global::System.Collections.Generic.List<object>();
        public Microsoft.UI.Xaml.Controls.SelectionMode SelectionMode { get; set; }
        public global::System.Collections.Generic.IList<object> SelectedItems { get; } =
            new global::System.Collections.Generic.List<object>();
        public event global::System.EventHandler<global::System.EventArgs> DoubleTapped { add { } remove { } }
    }

    public enum SelectionMode { Single, Multiple, Extended }

    public class ProgressBar : UIElement
    {
        public double Minimum { get; set; }
        public double Maximum { get; set; }
        public double Value { get; set; }
        public Microsoft.UI.Xaml.VerticalAlignment VerticalAlignment { get; set; }
    }

    public class ContentDialog
    {
        public object? Title { get; set; }
        public object? Content { get; set; }
        public string PrimaryButtonText { get; set; } = "";
        public string SecondaryButtonText { get; set; } = "";
        public string CloseButtonText { get; set; } = "";
        public Microsoft.UI.Xaml.XamlRoot XamlRoot { get; set; } = new();
        public ContentDialogButton DefaultButton { get; set; }
        public global::System.Threading.Tasks.Task<ContentDialogResult> ShowAsync()
            => global::System.Threading.Tasks.Task.FromResult(ContentDialogResult.Primary);
    }

    public enum ContentDialogResult { None, Primary, Secondary }

    public enum ContentDialogButton { None, Primary, Secondary, Close }

    public class ScrollViewer : Control
    {
        public object? Content { get; set; }
        public Microsoft.UI.Xaml.Thickness Padding { get; set; }
    }
}

namespace Microsoft.UI.Xaml.Navigation
{
    public class NavigationEventArgs : global::System.EventArgs { }
}

namespace Microsoft.UI.Xaml.Media
{
    public class Brush { }
    // v2.51: PinSplitViewPane 视觉树遍历（真实 API: VisualTreeHelper）
    public static class VisualTreeHelper
    {
        public static int GetChildrenCount(DependencyObject reference) => 0;
        public static DependencyObject GetChild(DependencyObject reference, int childIndex) =>
            throw new global::System.InvalidOperationException();
    }
    public class SolidColorBrush : Brush
    {
        public SolidColorBrush(Windows.UI.Color c) { Color = c; }
        public Windows.UI.Color Color { get; set; }
    }
}

namespace Microsoft.UI.Xaml.Input
{
    public class DoubleTappedRoutedEventArgs { }
    public class RightTappedRoutedEventArgs { }

    public class DragEventArgs
    {
        public Windows.ApplicationModel.DataTransfer.DataPackageOperation AcceptedOperation { get; set; }
        public Windows.ApplicationModel.DataTransfer.DataPackageView DataView { get; } = new();
        public bool Handled { get; set; }
    }

    public class KeyRoutedEventArgs
    {
        public Windows.System.VirtualKey Key { get; set; }
    }
}

namespace Microsoft.UI
{
    public struct WindowId { public ulong Value; }

    public static class Colors
    {
        public static readonly Windows.UI.Color IndianRed = default;
        public static readonly Windows.UI.Color SeaGreen = default;
        public static readonly Windows.UI.Color RoyalBlue = default;
        public static readonly Windows.UI.Color Gray = default;
        public static readonly Windows.UI.Color Crimson = default;
        public static readonly Windows.UI.Color DarkOrange = default;
    }
}

namespace Windows.UI
{
    public struct Color
    {
        public byte A, R, G, B;
        public static Color FromArgb(byte a, byte r, byte g, byte b) => new Color { A = a, R = r, G = g, B = b };
    }
}

namespace Windows.System
{
    public enum VirtualKey { Enter, Escape }
}

namespace Windows.Graphics
{
    public struct SizeInt32 { public SizeInt32(int width, int height) { Width = width; Height = height; } public int Width; public int Height; }
    public struct PointInt32 { public PointInt32(int x, int y) { X = x; Y = y; } public int X; public int Y; }
    public struct RectInt32 { public int X; public int Y; public int Width; public int Height; }
}

namespace Windows.ApplicationModel.DataTransfer
{
    public enum DataPackageOperation { Copy }

    public class DataPackageView
    {
        public bool Contains(string format) => true;
        public global::System.Threading.Tasks.Task<string> GetTextAsync()
            => global::System.Threading.Tasks.Task.FromResult("");
        public global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<Windows.Storage.StorageItem>> GetStorageItemsAsync()
            => global::System.Threading.Tasks.Task.FromResult<global::System.Collections.Generic.IReadOnlyList<Windows.Storage.StorageItem>>(
                new global::System.Collections.Generic.List<Windows.Storage.StorageItem>());
    }

    public class DataPackage { public DataPackageView GetView() => new(); }

    public class Clipboard
    {
        public static DataPackageView GetContent() => new();
        public static event global::System.EventHandler<object> ContentChanged { add { } remove { } }
    }

    public static class StandardDataFormats
    {
        public static string Text = "Text";
        public static string StorageItems = "StorageItems";
        public static bool Contains(string f) => true;
    }
}

namespace Windows.Storage
{
    public class StorageItem { public string Path { get; } = ""; public string Name { get; } = ""; }
    public class StorageFile : StorageItem { }
    public class StorageFolder : StorageItem { }
}

namespace Windows.Storage.Pickers
{
    public enum PickerLocationId { Downloads, Desktop, Documents }

    public class FileOpenPicker
    {
        public PickerLocationId SuggestedStartLocation { get; set; }
        public global::System.Collections.Generic.IList<string> FileTypeFilter { get; } =
            new global::System.Collections.Generic.List<string>();
        public global::System.Threading.Tasks.Task<StorageFile?> PickSingleFileAsync()
            => global::System.Threading.Tasks.Task.FromResult<StorageFile?>(new StorageFile());
    }

    public class FolderPicker
    {
        public PickerLocationId SuggestedStartLocation { get; set; }
        public global::System.Collections.Generic.IList<string> FileTypeFilter { get; } =
            new global::System.Collections.Generic.List<string>();
        public global::System.Threading.Tasks.Task<StorageFolder?> PickSingleFolderAsync()
            => global::System.Threading.Tasks.Task.FromResult<StorageFolder?>(new StorageFolder());
    }

    public class FileSavePicker
    {
        public string SuggestedFileName { get; set; } = "";
        public PickerLocationId SuggestedStartLocation { get; set; }
        public global::System.Collections.Generic.IDictionary<string, global::System.Collections.Generic.IList<string>> FileTypeChoices { get; } =
            new global::System.Collections.Generic.Dictionary<string, global::System.Collections.Generic.IList<string>>();
        public global::System.Threading.Tasks.Task<StorageFile?> PickSaveFileAsync()
            => global::System.Threading.Tasks.Task.FromResult<StorageFile?>(new StorageFile());
    }
}

namespace Microsoft.UI.Xaml.Controls.Primitives
{
    public class FlyoutBase { }
}

namespace WinRT.Interop
{
    public static class WindowNative
    {
        public static global::System.IntPtr GetWindowHandle(object w) => global::System.IntPtr.Zero;
    }

    public static class InitializeWithWindow
    {
        public static void Initialize(object o, global::System.IntPtr h) { }
    }
}

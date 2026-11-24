using Microsoft.UI.Windowing;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using TV.Services;
using TV.Controls;
using TV.Models;
using TV.ViewModels;
using TV.Views;
using TV.Services.Library;
using TV.Services.Persistence;
using TV.Services.Updates;

namespace TV;

public sealed partial class MainWindow : Window
{
    private string _currentTag = string.Empty;
    private PageTransitionKind _pendingTransition = PageTransitionKind.TopLevel;
    private bool _hasAnimatedNavIndicator;
    private bool _fullScreen;
    private bool _compact;
    private bool _leavingPlayer;
    private Task? _leavePlayerTask;
    private readonly WindowTray? _tray;
    private MouseBackNavigation? _mouseBack;
    private WindowCloseBehavior _closeBehavior;
    private bool _titleInputQueued;
    private RectInt32[]? _titleInputRegions;
    private sealed record NavigationEntry(string Tag, VodCardViewModel? Detail = null, PlaybackRequest? Playback = null);
    private readonly Stack<NavigationEntry> _back = new();

    public MainWindow()
    {
        InitializeComponent();
        if (App.PlayerOnly)
        {
            ShellRoot.Children.Remove(NavPane);
            PaneColumn.Width = new GridLength(0);
        }
        else
        {
            // 影视仓式布局：隐藏左侧栏，导航统一走顶部全局导航条。
            NavPane.Visibility = Visibility.Collapsed;
            PaneColumn.Width = new GridLength(0);
            try
            {
                if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported())
                    SystemBackdrop = new MicaBackdrop();
            }
            catch (Exception ex) { Diagnostics.Log($"Mica 背景不可用，已降级纯色背景: {ex.GetType().Name}"); }
        }
        ExtendsContentIntoTitleBar = true;
        try
        {
            if (AppWindowTitleBar.IsCustomizationSupported())
                AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        }
        catch (Exception ex) { Diagnostics.Log($"标题栏高度定制不可用: {ex.GetType().Name}"); }
        RestorePlacement();
        AppWindow.Changed += (_, _) => CapturePlacement();
        Title = "PCbox";
        Diagnostics.Log("MainWindow initialized");
        if (!App.PlayerOnly) App.Services.Sniffer.Attach(ShellRoot);
        ViewModel = new MainViewModel(App.Services, DispatcherQueue);
        Diagnostics.Log("ViewModel initialized");
        ShellRoot.DataContext = ViewModel;
        ContentFrame.Navigated += (_, args) =>
        {
            if (args.Content is FrameworkElement page)
            {
                page.DataContext = _currentTag switch
                {
                    "live" => ViewModel.Live, "library" => ViewModel.Library, "settings" => ViewModel.Settings,
                    "player" => ViewModel.Player, "search" => ViewModel.Search, "cloud" => ViewModel.CloudDrive, _ => ViewModel.Vod
                };
                MotionService.EnterPage(page, _pendingTransition);
            }
        };
        ViewModel.NavigationRequested += OnNavigationRequested;
        ViewModel.Player.FullScreenRequested += OnFullScreenRequested;
        ViewModel.Player.CompactOverlayRequested += OnCompactOverlayRequested;
        ShellRoot.ActualThemeChanged += (_, _) => UpdateCaptionTheme();
        ShellRoot.SizeChanged += (_, _) => UpdatePaneLayout();
        var backShortcut = new KeyboardAccelerator
        {
            Key = Windows.System.VirtualKey.Left,
            Modifiers = Windows.System.VirtualKeyModifiers.Menu
        };
        backShortcut.Invoked += (_, args) => { GoBack(); args.Handled = true; };
        ShellRoot.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        ShellRoot.KeyboardAccelerators.Add(backShortcut);
        // PCbox：键盘 Backspace 也可返回（输入框聚焦时由系统快捷键自然让位）。
        var backspaceShortcut = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Back };
        backspaceShortcut.Invoked += (_, args) => { GoBack(); args.Handled = true; };
        ShellRoot.KeyboardAccelerators.Add(backspaceShortcut);
        // PCbox：鼠标侧键（XButton1）返回，blocking 与托盘图标使用不同 subclass id。
        _mouseBack = new MouseBackNavigation(WinRT.Interop.WindowNative.GetWindowHandle(this), DispatcherQueue, GoBack);
        if (!App.PlayerOnly)
        {
            var searchShortcut = new KeyboardAccelerator
            {
                Key = Windows.System.VirtualKey.F,
                Modifiers = Windows.System.VirtualKeyModifiers.Control
            };
            searchShortcut.Invoked += (_, args) => { OpenSearch(); args.Handled = true; };
            ShellRoot.KeyboardAccelerators.Add(searchShortcut);
            _tray = new WindowTray(WinRT.Interop.WindowNative.GetWindowHandle(this), DispatcherQueue, Title, Activate, () => _ = ShutdownAsync());
        }
        AppWindow.Closing += OnClosing;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_closing) return;
            if (App.PlayerOnly && App.StartupVideoPath is { } path)
            {
                Navigate("player");
                try { _ = ViewModel.Player.LoadAsync(LocalMediaService.CreateRequest(path)); }
                catch (Exception ex) { ViewModel.Player.StatusMessage = ex.Message; }
            }
            else Navigate("vod");
            _ = LoadPreferencesAsync();
            // PCbox：不再在启动时自动检测新版本，避免弹出升级提示（仍可在设置里手动检查）。
        });
    }

    public void SetTheme(ElementTheme theme)
    {
        if (Content is FrameworkElement root) root.RequestedTheme = theme;
    }

    private async Task LoadPreferencesAsync()
    {
        try
        {
            var settings = App.Services.Database;
            var theme = await settings.GetSettingAsync("ui.theme");
            if (_closing) return;
            SetTheme(theme switch { "1" => ElementTheme.Light, "2" => ElementTheme.Default, _ => ElementTheme.Dark });
            MotionService.Enabled = await settings.GetSettingAsync("ui.animations") != "false";
            if (_tray is not null) _tray.Enabled = await settings.GetSettingAsync("ui.minimizeToTray") != "false";
            if (int.TryParse(await settings.GetSettingAsync("ui.closeBehavior"), out var closeBehavior) &&
                Enum.IsDefined((WindowCloseBehavior)closeBehavior))
                _closeBehavior = (WindowCloseBehavior)closeBehavior;
        }
        catch (Exception ex) { Diagnostics.Log($"窗口设置读取失败: {ex.Message}"); }
    }

    internal void SetMinimizeToTray(bool enabled) { if (_tray is not null) _tray.Enabled = enabled; }
    internal void SetCloseBehavior(WindowCloseBehavior behavior) => _closeBehavior = behavior;
    internal void RestoreWindow()
    {
        if (_closing) return;
        if (_tray is not null) _tray.Restore();
        else Activate();
    }

    public MainViewModel ViewModel { get; }

    public void Navigate(string tag)
    {
        if (App.PlayerOnly && tag != "player") return;
        if (_currentTag == tag || _leavingPlayer || _closing) return;
        _ = NavigateAsync(tag, false);
    }

    public void GoBack()
    {
        if (App.PlayerOnly) { _ = ShutdownAsync(); return; }
        if (_leavingPlayer || _closing) return;
        if (_fullScreen || _compact)
        {
            SetPlayerPresenter(AppWindowPresenterKind.Overlapped);
            return;
        }
        if (_back.TryPop(out var previous)) _ = NavigateAsync(previous.Tag, true, previous);
    }

    private async Task NavigateAsync(string tag, bool goingBack, NavigationEntry? restoring = null)
    {
        var previous = _currentTag;
        var resume = previous == "player" && tag == "search" ? ViewModel.Player.GetResumeRequest() : null;
        if (previous == "player")
        {
            _leavingPlayer = true;
            try { await (_leavePlayerTask = ViewModel.Player.PrepareLeaveAsync()); }
            finally { _leavingPlayer = false; }
            if (_closing) return;
            SetPlayerPresenter(AppWindowPresenterKind.Overlapped);
        }
        if (previous == "search" && tag != "detail") ViewModel.Search.CancelPending();
        if (!goingBack && previous.Length > 0)
        {
            if (tag is "player" or "detail" or "search")
                _back.Push(new NavigationEntry(previous, previous == "detail" ? ViewModel.Vod.SelectedCard : null, resume));
            else
            {
                while (previous is "player" or "detail" or "search" && _back.TryPop(out var parent))
                {
                    if (previous == "search") ViewModel.Search.CancelPending();
                    previous = parent.Tag;
                }
                if (previous != tag && previous is not ("player" or "detail" or "search")) _back.Push(new NavigationEntry(previous));
                ViewModel.Vod.SelectedCard = null;
            }
        }
        if (_currentTag == "detail" && tag != "player") ViewModel.Vod.SelectedCard = null;
        NavigateCore(tag, goingBack);
        if (tag == "detail" && restoring?.Detail is { } detail) ViewModel.Vod.SelectedCard = detail;
        if (tag == "player" && restoring?.Playback is { } playback) _ = ViewModel.Player.LoadAsync(playback);
        if (tag != "player" && _pendingUpdate is { } update)
        {
            _pendingUpdate = null;
            DispatcherQueue.TryEnqueue(async () => await ShowUpdateAsync(update));
        }
    }

    private void NavigateCore(string tag, bool goingBack)
    {
        Diagnostics.Log($"NavigateCore tag={tag} prev={_currentTag}");
        var previous = _currentTag;
        _currentTag = tag;
        _pendingTransition = tag == "player"
            ? PageTransitionKind.Immersive
            : goingBack
                ? PageTransitionKind.DrillOut
                : tag is "detail" or "search"
                    ? PageTransitionKind.DrillIn
                    : PageTransitionKind.TopLevel;
        Type? pageType = tag switch
        {
            "live" => typeof(LivePage),
            "cast" => typeof(CastPage),
            "library" => typeof(LibraryPage),
            "settings" => typeof(SettingsPage),
            "cloud" => typeof(CloudDrivePage),
            "player" => typeof(PlayerPage),
            "detail" => typeof(DetailPage),
            "search" => typeof(SearchPage),
            _ => typeof(VodPage)
        };
        try
        {
            // 使用 SuppressNavigationTransitionInfo 将页面转场完全交给 MotionService 物理引擎，支持随时零卡顿打断。
            ContentFrame.Navigate(pageType, null, new SuppressNavigationTransitionInfo());
            UpdatePaneLayout();
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"页面导航失败 ({tag}): {ex}");
        }
        ContentFrame.BackStack.Clear();
        SyncNavSelection(tag);
        UpdateBackButton();
    }

    private bool _syncingNav;
    private static readonly string[] PrimaryTags = { "vod", "live", "library", "cloud", "cast" };
    private static readonly SolidColorBrush TransparentBrush = new(Windows.UI.Color.FromArgb(0, 0, 0, 0));
    private static readonly Windows.UI.ViewManagement.AccessibilitySettings Accessibility = new();
    private readonly Dictionary<ListViewItem, (UIElement? Plate, UIElement? Indicator)> _navVisualCache = [];
    private double _cachedWidePaneWidth;

    private void SyncNavSelection(string tag)
    {
        if (App.PlayerOnly || NavPane.Visibility == Visibility.Collapsed) return;
        SettingsButton.Background = tag == "settings" ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SelectionBrush"] : TransparentBrush;
        if (tag == "settings")
        {
            _syncingNav = true;
            try { NavList.SelectedItem = null; }
            finally { _syncingNav = false; }
            UpdateNavVisuals(tag);
            return;
        }
        if (!PrimaryTags.Contains(tag)) return;
        _syncingNav = true;
        try
        {
            NavList.SelectedItem = NavList.Items.OfType<ListViewItem>().FirstOrDefault(item => item.Tag as string == tag);
        }
        finally { _syncingNav = false; }
        UpdateNavVisuals(tag);
    }

    // 校正每项选中底板，并将全局弹簧指示条平滑滑行到当前激活项（含设置按钮）。
    private void UpdateNavVisuals(string tag)
    {
        FrameworkElement? activeTarget = tag == "settings" ? SettingsButton : null;
        foreach (var item in NavList.Items.OfType<ListViewItem>())
        {
            var active = item.Tag as string == tag;
            if (active) activeTarget = item;
            if (!_navVisualCache.TryGetValue(item, out var cached) || cached.Plate is null)
            {
                cached = (FindTemplateChild(item, "SelectionPlate"), FindTemplateChild(item, "Indicator"));
                if (cached.Plate is not null) _navVisualCache[item] = cached;
            }
            if (cached.Plate is { } plate) plate.Opacity = active ? 1 : 0;
            // 侧栏使用全局弹簧 FloatingNavIndicator，隐藏各子项静态指示条避免双条重影
            if (cached.Indicator is { } indicator) indicator.Opacity = 0;
        }
        ScheduleIndicatorGlide(activeTarget);
    }

    private void ScheduleIndicatorGlide(FrameworkElement? target)
    {
        if (App.PlayerOnly || target is null) return;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
        {
            if (_closing || target.XamlRoot is null || NavPaneContent.XamlRoot is null) return;
            try
            {
                var pt = target.TransformToVisual(NavPaneContent).TransformPoint(new Windows.Foundation.Point(0, 0));
                var centerY = pt.Y + (target.ActualHeight - FloatingNavIndicator.Height) * 0.5;
                if (centerY > 0)
                {
                    FloatingNavIndicator.Opacity = 1;
                    MotionService.GlideVerticalIndicator(FloatingNavIndicator, centerY, animate: _hasAnimatedNavIndicator);
                    _hasAnimatedNavIndicator = true;
                }
            }
            catch { }
        });
    }

    private static FrameworkElement? FindTemplateChild(DependencyObject root, string name)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement element && element.Name == name) return element;
            if (FindTemplateChild(child, name) is { } found) return found;
        }
        return null;
    }

    private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingNav) return;
        if (NavList.SelectedItem is ListViewItem { Tag: string tag })
        {
            Diagnostics.Log($"Nav selection changed tag={tag}");
            ViewModel.NavigateCommand.Execute(tag);
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => ViewModel.NavigateCommand.Execute("settings");
    private void OnSearchNavClick(object sender, RoutedEventArgs e) => OpenSearch();

    // ===== 影视仓式顶部全局导航 =====
    // PCbox 品牌按钮：点击返回主页（点播首页）。
    private void OnBrandHomeClick(object sender, RoutedEventArgs e) => Navigate("vod");
    private void OnTopHistoryClick(object sender, RoutedEventArgs e) => NavigateLibraryTab(0);
    private void OnTopLiveClick(object sender, RoutedEventArgs e) => ViewModel.NavigateCommand.Execute("live");
    private void OnTopSearchClick(object sender, RoutedEventArgs e) => OpenSearch();
    private void OnTopConfigClick(object sender, RoutedEventArgs e) => ViewModel.NavigateCommand.Execute("settings");
    private void OnTopLineClick(object sender, RoutedEventArgs e) => ViewModel.NavigateCommand.Execute("settings");
    private void OnTopLocalClick(object sender, RoutedEventArgs e) => NavigateLibraryTab(3);
    private void OnTopFavoriteClick(object sender, RoutedEventArgs e) => NavigateLibraryTab(1);
    private void OnTopPushClick(object sender, RoutedEventArgs e) => OpenSearch();

    /// <summary>打开媒体库并切到指定标签页：0=历史 1=收藏 2=下载 3=本地。</summary>
    private void NavigateLibraryTab(int tab)
    {
        ViewModel.Library.SelectedTab = tab;
        ViewModel.NavigateCommand.Execute("library");
    }

    private bool _narrowPane;
    private void UpdatePaneLayout()
    {
        if (ShellRoot.XamlRoot is null) return;
        // 影视仓式布局：侧栏永久隐藏，顶部导航条负责所有一级导航。
        // 播放器/全屏/画中画时额外隐藏顶部导航条，保持沉浸。
        TopNavBar.Visibility = (_currentTag == "player" || _fullScreen || _compact)
            ? Visibility.Collapsed : Visibility.Visible;
        NavPane.Visibility = Visibility.Collapsed;
        PaneColumn.Width = new GridLength(0);
        // 侧栏已永久隐藏，无需旧版宽度/标签/指示条计算。
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => GoBack();

    internal void OpenLocal(string path)
    {
        try
        {
            var request = LocalMediaService.CreateRequest(path);
            if (_currentTag != "player") Navigate("player");
            _ = ViewModel.Player.LoadAsync(request);
            RestoreWindow();
        }
        catch (Exception ex) { Diagnostics.Log($"打开本地视频失败: {ex.GetType().Name} {ex.Message}"); }
    }

    internal void OpenSearch()
    {
        if (App.PlayerOnly) return;
        if (_currentTag == "search" && ContentFrame.Content is SearchPage page)
        {
            page.FocusQuery();
        }
        else if (_back.TryPeek(out var previous) && previous.Tag == "search") GoBack();
        else Navigate("search");
    }

    internal void OpenAiSettings()
    {
        if (App.PlayerOnly) return;
        if (_currentTag == "settings" && ContentFrame.Content is SettingsPage current)
        {
            current.SelectAiSection();
            return;
        }
        _ = NavigateToAiSettingsAsync();
    }

    private async Task NavigateToAiSettingsAsync()
    {
        if (_leavingPlayer || _closing) return;
        await NavigateAsync("settings", false);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
        {
            if (ContentFrame.Content is SettingsPage page) page.SelectAiSection();
        });
    }

    internal bool IsPlayerFullScreen => _fullScreen;

    private void UpdateBackButton()
    {
        // 主导航页（点播/直播/媒体库/云盘/投屏）与自带返回键的播放器页不显示标题栏返回键。
        var canGoBack = _currentTag is not ("vod" or "live" or "library" or "cloud" or "cast" or "player") && _back.Count > 0;
        MotionService.SetFloatingVisible(BackButton, canGoBack, offsetY: 0, offsetX: -8, hiddenScale: 0.90f);
        ScheduleTitleBarInput();
    }

    private void OnTitleBarLoaded(object sender, RoutedEventArgs e)
    {
        AppTitleBar.XamlRoot.Changed += (_, _) => ScheduleTitleBarInput();
        UpdateCaptionTheme();
        ScheduleTitleBarInput();
    }

    private void UpdateCaptionTheme()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;
        Windows.UI.Color? foreground = null;
        Windows.UI.Color? inactiveForeground = null;
        if (!Accessibility.HighContrast)
        {
            // 与 Styles/Tokens.xaml 的 PrimaryTextBrush / SecondaryTextBrush 保持一致。
            if (ShellRoot.ActualTheme == ElementTheme.Light)
            {
                foreground = Windows.UI.Color.FromArgb(255, 0x17, 0x19, 0x1E);
                inactiveForeground = Windows.UI.Color.FromArgb(255, 0x56, 0x5E, 0x6B);
            }
            else
            {
                foreground = Windows.UI.Color.FromArgb(255, 0xF3, 0xF0, 0xE9);
                inactiveForeground = Windows.UI.Color.FromArgb(255, 0xA6, 0xAE, 0xBC);
            }
        }
        var caption = AppWindow.TitleBar;
        caption.ButtonForegroundColor = foreground;
        caption.ButtonInactiveForegroundColor = inactiveForeground;
    }

    private void OnTitleBarSizeChanged(object sender, SizeChangedEventArgs e) => ScheduleTitleBarInput();
    private void OnTitleControlsLayoutUpdated(object? sender, object e) => ScheduleTitleBarInput();

    private void ScheduleTitleBarInput()
    {
        if (_titleInputQueued) return;
        _titleInputQueued = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _titleInputQueued = false;
            if (_closing || AppTitleBar.XamlRoot is not { } root) return;
            var rects = new List<RectInt32>();
            var scale = root.RasterizationScale;
            // PCbox 品牌按钮：标题栏左侧，需要 passthrough 才能点击返回主页。
            if (AppTitleBar.Visibility == Visibility.Visible && BrandHomeButton.Visibility == Visibility.Visible && BrandHomeButton.IsHitTestVisible)
            {
                var bounds = BrandHomeButton.TransformToVisual(null).TransformBounds(
                    new Windows.Foundation.Rect(0, 0, BrandHomeButton.ActualWidth, BrandHomeButton.ActualHeight));
                rects.Add(new RectInt32((int)Math.Round(bounds.X * scale), (int)Math.Round(bounds.Y * scale),
                    (int)Math.Round(bounds.Width * scale), (int)Math.Round(bounds.Height * scale)));
            }
            if (AppTitleBar.Visibility == Visibility.Visible && BackButton.Visibility == Visibility.Visible && BackButton.IsHitTestVisible)
            {
                var bounds = BackButton.TransformToVisual(null).TransformBounds(
                    new Windows.Foundation.Rect(0, 0, BackButton.ActualWidth, BackButton.ActualHeight));
                rects.Add(new RectInt32((int)Math.Round(bounds.X * scale), (int)Math.Round(bounds.Y * scale),
                    (int)Math.Round(bounds.Width * scale), (int)Math.Round(bounds.Height * scale)));
            }
            var regions = rects.ToArray();
            if (_titleInputRegions is not null && _titleInputRegions.SequenceEqual(regions)) return;
            try
            {
                InputNonClientPointerSource.GetForWindowId(AppWindow.Id).SetRegionRects(NonClientRegionKind.Passthrough, regions);
                _titleInputRegions = regions;
            }
            catch (Exception ex) { Diagnostics.Log($"标题栏输入区域不可用 ({ex.GetType().Name} 0x{ex.HResult:X8})"); }
        });
    }

    private void OnNavigationRequested(object? sender, string tag) => Navigate(tag);

    private void OnFullScreenRequested(object? sender, EventArgs e) =>
        SetPlayerPresenter(_fullScreen ? AppWindowPresenterKind.Overlapped : AppWindowPresenterKind.FullScreen);

    private void OnCompactOverlayRequested(object? sender, EventArgs e) =>
        SetPlayerPresenter(_compact ? AppWindowPresenterKind.Overlapped : AppWindowPresenterKind.CompactOverlay);

    private void SetPlayerPresenter(AppWindowPresenterKind kind)
    {
        if (AppWindow.Presenter.Kind != kind) AppWindow.SetPresenter(kind);
        _fullScreen = kind == AppWindowPresenterKind.FullScreen;
        _compact = kind == AppWindowPresenterKind.CompactOverlay;
        AppTitleBar.Visibility = _fullScreen || _compact ? Visibility.Collapsed : Visibility.Visible;
        UpdatePaneLayout();
    }

    private bool _closing;
    private bool _allowClose;
    private bool _closePromptOpen;
    private bool _updateInstallPending;
    private Flyout? _closeFlyout;
    private ContentDialog? _updateDialog;
    private AppUpdateRelease? _pendingUpdate;
    internal bool IsClosing => _closing || _updateInstallPending;

    internal async Task ShowUpdateAsync(AppUpdateRelease release)
    {
        if (IsClosing || App.PlayerOnly) return;
        if (_currentTag == "player")
        {
            _pendingUpdate = release;
            return;
        }
        if (_updateDialog is not null || ContentFrame.XamlRoot is not { } root) return;
        var dialog = new UpdateDialog(release, App.Services.Updates) { XamlRoot = root };
        _updateDialog = dialog;
        try { await dialog.ShowAsync(); }
        catch (Exception ex)
        {
            _pendingUpdate = release;
            Diagnostics.Log($"更新弹窗暂不可用: {ex.GetType().Name}");
        }
        finally { _updateDialog = null; }
    }

    internal void BeginUpdateInstallation(string installerPath)
    {
        if (IsClosing) return;
        if (!File.Exists(installerPath)) throw new FileNotFoundException("更新安装包不存在，请重新下载。", installerPath);
        _updateInstallPending = true;
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo
            {
                FileName = installerPath,
                UseShellExecute = true,
                WorkingDirectory = AppPaths.Root
            };
            foreach (var argument in new[] { "/SILENT", "/NORESTART", "/CLOSEAPPLICATIONS" }) start.ArgumentList.Add(argument);
            start.ArgumentList.Add("/DIR=" + AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            start.ArgumentList.Add("/LOG=" + Path.Combine(AppPaths.Root, "update-install.log"));
            if (System.Diagnostics.Process.Start(start) is null) throw new InvalidOperationException("无法启动安装程序。");
            _ = ShutdownAsync();
        }
        catch
        {
            _updateInstallPending = false;
            throw;
        }
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        if (_updateInstallPending)
        {
            if (!_closing) _ = ShutdownAsync();
            return;
        }
        if (App.PlayerOnly)
        {
            if (!_closing) _ = ShutdownAsync();
            return;
        }
        if (_tray?.IsEndingSession == true) _ = ShutdownAsync();
        else if (!_closing && !_closePromptOpen) _ = ApplyCloseChoiceAsync();
    }

    private async Task ApplyCloseChoiceAsync()
    {
        _closePromptOpen = true;
        try
        {
            var behavior = _closeBehavior;
            if (behavior == WindowCloseBehavior.Ask)
            {
                var (choice, remember) = await ShowCloseChoiceAsync();
                if (choice is null || _closing) return;
                behavior = choice.Value;
                if (remember) await ViewModel.Settings.SaveCloseBehaviorAsync(behavior);
            }
            if (behavior == WindowCloseBehavior.MinimizeToTray)
            {
                if (_tray?.TryMinimizeFromClose() != true && AppWindow.Presenter is OverlappedPresenter presenter)
                    presenter.Minimize();
            }
            else await ShutdownAsync();
        }
        catch (Exception ex) { Diagnostics.Log($"关闭窗口操作失败: {ex.Message}"); }
        finally { _closePromptOpen = false; }
    }

    private async Task<(WindowCloseBehavior? Choice, bool Remember)> ShowCloseChoiceAsync()
    {
        var result = new TaskCompletionSource<WindowCloseBehavior?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var remember = new CheckBox { Content = "记住此选择" };
        var tray = new Button { Content = "最小化到托盘" };
        var exit = new Button { Content = "退出" };
        var cancel = new Button { Content = "取消" };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Children = { tray, exit, cancel }
        };
        var flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
            Content = new StackPanel { Spacing = 12, Children = { remember, actions } } };
        _closeFlyout = flyout;
        tray.Click += (_, _) => { result.TrySetResult(WindowCloseBehavior.MinimizeToTray); flyout.Hide(); };
        exit.Click += (_, _) => { result.TrySetResult(WindowCloseBehavior.Exit); flyout.Hide(); };
        cancel.Click += (_, _) => flyout.Hide();
        flyout.Closed += (_, _) => result.TrySetResult(null);
        flyout.ShowAt(ClosePromptAnchor);
        try { return (await result.Task, remember.IsChecked == true); }
        finally { _closeFlyout = null; }
    }

    private async Task ShutdownAsync()
    {
        if (_closing) return;
        CapturePlacement();
        SavePlacement();
        _closing = true;
        _closeFlyout?.Hide();
        _updateDialog?.Hide();
        Diagnostics.Log("Shutdown requested");
        _tray?.Dispose();
        _mouseBack?.Dispose();
        try
        {
            if (!App.PlayerOnly)
            {
                ViewModel.Search.CancelPending();
                if (ViewModel.Search.SubmitCommand.ExecutionTask is { IsCompleted: false } pendingSearch)
                    await pendingSearch.WaitAsync(TimeSpan.FromSeconds(2))
                        .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
                if (ContentFrame.Content is SearchPage search) search.CloseTransientUI(true);
                var download = ViewModel.Vod.CancelPendingWork();
                ViewModel.Settings.CancelCloudLoginCommand.Execute(null);
                if (_currentTag == "player")
                    await (_leavingPlayer && _leavePlayerTask is not null ? _leavePlayerTask : ViewModel.Player.PrepareLeaveAsync());
                if (ContentFrame.Content is CastPage cast) await cast.StopAsync();
                if (download is { IsCompleted: false })
                    await download.WaitAsync(TimeSpan.FromMilliseconds(750));
                await ViewModel.Settings.FlushAsync();
            }
            else if (_currentTag == "player")
                await (_leavingPlayer && _leavePlayerTask is not null ? _leavePlayerTask : ViewModel.Player.PrepareLeaveAsync());
        }
        catch (Exception ex) { Diagnostics.Log($"结束当前任务失败: {ex.Message}"); }
        try
        {
            if (!App.PlayerOnly) App.Services.Sniffer.Detach();
            await Task.Run(App.Services.Dispose);
        }
        catch (Exception ex) { Diagnostics.Log($"释放应用资源失败: {ex.Message}"); }
        finally
        {
            Diagnostics.Log("Shutdown completed");
            Diagnostics.Shutdown();
            ContentFrame.Content = null;
            _allowClose = true;
            Close();
        }
    }
}

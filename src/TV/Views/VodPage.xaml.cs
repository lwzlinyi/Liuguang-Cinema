using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using TV.Controls;
using TV.Models;
using TV.Services;
using TV.Services.Vod.Native;
using TV.ViewModels;

namespace TV.Views;

public sealed partial class VodPage : Page
{

    public VodPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
    }

    private VodViewModel ViewModel => (VodViewModel)DataContext;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Diagnostics.Log("VodPage loaded");
        try { await ViewModel.InitializeAsync(); Diagnostics.Log("VodPage initialized"); }
        catch (Exception ex) { ViewModel.StatusMessage = "加载失败：" + ex.Message; }
    }

    private ScrollViewer? _postersScroll;
    private bool _browseLayoutReady;
    private bool _scrolledCompact;
    private void OnSearchClick(object sender, RoutedEventArgs e) => App.Window.OpenSearch();

    // PCbox：影视仓式「请选择首页数据源」弹窗——两列站点网格，当前选中高亮带对勾。
    private async void OnHomeSourceClick(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is null) return;
        var sites = ViewModel.Sites
            .Where(s => s.IsSupported && !MyDriveVodProvider.Supports(s))
            .ToArray();
        if (sites.Length == 0) return;

        const int columns = 2;
        var rows = (sites.Length + columns - 1) / columns;
        var grid = new Grid { ColumnSpacing = 10, RowSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var r = 0; r < rows; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var selectedBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x21, 0x96, 0xF3));
        var idleBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x2A, 0x2B, 0x31));
        var selectedInk = new SolidColorBrush(Microsoft.UI.Colors.White);
        var idleInk = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0xF2, 0xF3, 0xF5));

        ContentDialog? dialog = null;
        for (var i = 0; i < sites.Length; i++)
        {
            var site = sites[i];
            var isSelected = ReferenceEquals(site, ViewModel.SelectedSite);
            var button = new Button
            {
                Content = isSelected ? "✓  " + site.Name : site.Name,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MinHeight = 44,
                Padding = new Thickness(10, 8, 10, 8),
                CornerRadius = new CornerRadius(8),
                Background = isSelected ? selectedBrush : idleBrush,
                Foreground = isSelected ? selectedInk : idleInk
            };
            ToolTipService.SetToolTip(button, site.DisplayName);
            button.Click += (_, _) =>
            {
                // 触发 OnSelectedSiteChanged：保存 homeSite 并重新加载该站点首页。
                ViewModel.SelectedSite = site;
                dialog?.Hide();
            };
            Grid.SetRow(button, i / columns);
            Grid.SetColumn(button, i % columns);
            grid.Children.Add(button);
        }

        var scroll = new ScrollViewer
        {
            Content = grid,
            MaxHeight = 480,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "请选择首页数据源",
            Content = scroll,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        await dialog.ShowAsync();
    }

    private async void OnVerifySiteClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.PendingVerification is not { } challenge || XamlRoot is null) return;
        try
        {
            var dialog = new SiteVerificationDialog(App.Services, challenge, XamlRoot);
            if (await dialog.ShowAsync() && IsLoaded)
            {
                ViewModel.PendingVerification = null;
                ViewModel.ReloadCommand.Execute(null);
            }
        }
        catch (Exception ex) { ViewModel.StatusMessage = "验证面板打开失败：" + ex.Message; }
    }
    private void OnPostersLoaded(object sender, RoutedEventArgs e)
    {
        _browseLayoutReady = false;
        _scrolledCompact = false;
        BrowseHeader.Visibility = GalleryHost.Visibility = Visibility.Visible;
        BackToTop.Visibility = Visibility.Collapsed;
        _postersScroll = SearchPage.FindScrollViewer(Posters);
        if (_postersScroll is not null)
        {
            _postersScroll.ViewChanged += OnBrowseScroll;
            _postersScroll.ChangeView(null, 0, null, true);
        }
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _browseLayoutReady = true);
    }
    private void OnPostersUnloaded(object sender, RoutedEventArgs e)
    {
        _browseLayoutReady = false;
        if (_postersScroll is not null) _postersScroll.ViewChanged -= OnBrowseScroll;
        _postersScroll = null;
    }
    private void OnTopClick(object sender, RoutedEventArgs e) => _postersScroll?.ChangeView(null, 0, null);
    private void OnBrowseScroll(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!_browseLayoutReady) return;
        var offset = _postersScroll?.VerticalOffset ?? 0;
        if (offset > 0 && offset < 40) return;
        var compact = offset >= 40;
        if (compact == _scrolledCompact) return;
        _scrolledCompact = compact;
        BrowseHeader.Visibility = GalleryHost.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        MotionService.SetFloatingVisible(BackToTop, compact, offsetY: 16, hiddenScale: 0.84f);
    }

    private async void OnCategoryPillClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: VodCategory category } button)
        {
            if (ViewModel.SelectedSite is { } site && BilibiliVodProvider.Supports(site) && BilibiliVodProvider.IsLoginCategory(category.Id))
            {
                button.IsChecked = false;
                if (XamlRoot is null) return;
                using var dialog = new BilibiliLoginDialog(App.Services.BilibiliAccount, XamlRoot);
                try { await dialog.ShowAsync(); }
                catch (Exception) { ViewModel.StatusMessage = "B 站账号面板无法打开，请重试。"; }
                return;
            }
            ViewModel.SelectedCategory = category;
            button.IsChecked = true;
        }
    }

    private void OnPosterContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is VodCardViewModel card) _ = card.LoadVisualAsync();
    }

    private void OnPosterViewportChanged(object sender, SizeChangedEventArgs args)
    {
        if (sender is not GridView { ItemsPanelRoot: ItemsWrapGrid panel }) return;
        // 海报列宽按令牌自适应：最少一张 188 宽，列间距 18。
        const double posterWidth = 188, columnGap = 18;
        var width = Math.Max(1, args.NewSize.Width);
        var columns = Math.Max(1, (int)((width + columnGap) / (posterWidth + columnGap)));
        // ItemWidth 是列间距（pitch），模板里的 18 右边距由 item 内部 Button.Margin 承担，
        // 所以这里不能用 (width - gap*(n-1))/n，否则最后会空出一整列宽度的空白。
        panel.ItemWidth = Math.Floor(width / columns);
        // 行高 = 封面 268 + 间距 8 + 标题 22 + 间距 8 + 元数据 18 + 上下 Margin 22 = 346。
        panel.ItemHeight = 346;
    }

    private void OnFeaturedLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: VodCardViewModel card }) _ = card.LoadVisualAsync();
    }

    private void OnCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: VodCardViewModel card }) ViewModel.SelectedCard = card;
    }

    private void OnFeaturedIn(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element) MotionService.SetCardHover(element, null, null, true);
    }

    private void OnFeaturedOut(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element) MotionService.SetCardHover(element, null, null, false);
    }

    private sealed record CardParts(FrameworkElement? Overlay, FrameworkElement? Badge);
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, CardParts> _cardParts = new();

    private CardParts GetCardParts(FrameworkElement card)
    {
        if (_cardParts.TryGetValue(card, out var parts) && parts.Overlay is not null) return parts;
        parts = new CardParts(FindDescendant(card, "PlayOverlay"), FindDescendant(card, "PlayBadge"));
        if (parts.Overlay is not null) _cardParts.AddOrUpdate(card, parts);
        return parts;
    }

    private void OnCardIn(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        var parts = GetCardParts(element);
        MotionService.SetCardHover(element, parts.Overlay, parts.Badge, true);
    }

    private void OnCardOut(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        var parts = GetCardParts(element);
        MotionService.SetCardHover(element, parts.Overlay, parts.Badge, false);
    }

    private void OnCardPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element) MotionService.SetCardPressed(element, true);
    }

    private void OnCardReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element) MotionService.SetCardPressed(element, false);
    }

    private static FrameworkElement? FindDescendant(DependencyObject root, string name)
    {
        if (root is FrameworkElement element && element.Name == name) return element;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            if (FindDescendant(VisualTreeHelper.GetChild(root, i), name) is { } found) return found;
        }
        return null;
    }

}

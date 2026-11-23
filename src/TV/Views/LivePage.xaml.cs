using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TV.ViewModels;

namespace TV.Views;

public sealed partial class LivePage : Page
{
    private bool _narrow;
    private bool _ready;
    private int _stage;

    public LivePage() => InitializeComponent();

    private LiveViewModel ViewModel => (LiveViewModel)DataContext;

    private async void OnLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        ViewModel.Groups.CollectionChanged -= OnGroupsChanged;
        ViewModel.Groups.CollectionChanged += OnGroupsChanged;
        UpdateGroupSection();
        try { await ViewModel.ReloadCommand.ExecuteAsync(null); }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = "加载失败：" + ex.Message;
            ViewModel.HasError = true;
        }
        _ready = true;
        UpdateGroupSection();
        ApplyLayout(ActualWidth);
    }

    private void OnUnloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (DataContext is LiveViewModel model) model.Groups.CollectionChanged -= OnGroupsChanged;
    }

    /// <summary>弹出对话框添加自定义直播源（名称 + m3u/m3u8 地址）。</summary>
    private async void OnAddSourceClick(object sender, RoutedEventArgs e)
    {
        var nameBox = new TextBox { Header = "直播源名称", PlaceholderText = "例如：我的 IPTV" };
        var urlBox = new TextBox { Header = "直播源地址", PlaceholderText = "https://example.com/live.m3u8", AcceptsReturn = false };
        var stack = new StackPanel { Spacing = 12 };
        stack.Children.Add(nameBox);
        stack.Children.Add(urlBox);
        var dialog = new ContentDialog
        {
            Title = "添加直播源",
            Content = stack,
            PrimaryButtonText = "添加",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;
        var error = await ViewModel.AddCustomSourceAsync(nameBox.Text, urlBox.Text);
        if (error is not null)
        {
            var tip = new ContentDialog { Title = "无法添加", Content = error, CloseButtonText = "知道了", XamlRoot = XamlRoot };
            await tip.ShowAsync();
        }
    }

    /// <summary>删除当前选中的自定义直播源（内置源不可删）。</summary>
    private async void OnRemoveSourceClick(object sender, RoutedEventArgs e)
    {
        var source = ViewModel.SelectedRepository;
        if (source is null)
        {
            var tip = new ContentDialog { Title = "提示", Content = "请先选择一个直播源", CloseButtonText = "知道了", XamlRoot = XamlRoot };
            await tip.ShowAsync();
            return;
        }
        if (source.IsBuiltIn)
        {
            var tip = new ContentDialog { Title = "提示", Content = "内置直播源不能删除", CloseButtonText = "知道了", XamlRoot = XamlRoot };
            await tip.ShowAsync();
            return;
        }
        var confirm = new ContentDialog
        {
            Title = "删除直播源",
            Content = $"确定删除「{source.Name}」吗？",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await confirm.ShowAsync() == ContentDialogResult.Primary)
            await ViewModel.RemoveCustomSourceAsync(source);
    }

    private void OnGroupsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateGroupSection();

    /// <summary>分组列表为空（未加载 / 加载失败）时隐藏整块「分组」，避免只留标题、下方空一片。</summary>
    private void UpdateGroupSection() =>
        GroupSection.Visibility = ViewModel.Groups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e) => ApplyLayout(e.NewSize.Width);

    /// <summary>宽窗三栏；窄窗退化为「源/分组 → 频道 → 详情」单任务切换。</summary>
    private void ApplyLayout(double width)
    {
        _narrow = width > 0 && width < 940;
        if (_narrow)
        {
            SourcesColumn.Width = new GridLength(1, GridUnitType.Star);
            ChannelsColumn.Width = new GridLength(0);
            DetailColumn.Width = new GridLength(0);
            BodyGrid.ColumnSpacing = 0;
            Grid.SetColumn(LeftPanel, 0);
            Grid.SetColumn(ChannelsPanel, 0);
            Grid.SetColumn(DetailPanel, 0);
            NarrowNav.Visibility = Visibility.Visible;
            ShowStage(_stage);
            return;
        }

        SourcesColumn.Width = new GridLength(280);
        ChannelsColumn.Width = new GridLength(1, GridUnitType.Star);
        DetailColumn.Width = new GridLength(360);
        BodyGrid.ColumnSpacing = 24; // 与 XAML 的 SpaceLarge 一致
        Grid.SetColumn(LeftPanel, 0);
        Grid.SetColumn(ChannelsPanel, 1);
        Grid.SetColumn(DetailPanel, 2);
        LeftPanel.Visibility = Visibility.Visible;
        ChannelsPanel.Visibility = Visibility.Visible;
        DetailPanel.Visibility = Visibility.Visible;
        NarrowNav.Visibility = Visibility.Collapsed;
        _stage = 0;
    }

    private void ShowStage(int stage)
    {
        _stage = Math.Clamp(stage, 0, 2);
        if (!_narrow)
        {
            LeftPanel.Visibility = Visibility.Visible;
            ChannelsPanel.Visibility = Visibility.Visible;
            DetailPanel.Visibility = Visibility.Visible;
            return;
        }

        LeftPanel.Visibility = _stage == 0 ? Visibility.Visible : Visibility.Collapsed;
        ChannelsPanel.Visibility = _stage == 1 ? Visibility.Visible : Visibility.Collapsed;
        DetailPanel.Visibility = _stage == 2 ? Visibility.Visible : Visibility.Collapsed;
        NarrowBackButton.Visibility = _stage > 0 ? Visibility.Visible : Visibility.Collapsed;
        NarrowStageText.Text = _stage switch
        {
            0 => "直播源与分组",
            1 => "频道",
            _ => "频道详情"
        };
    }

    private void OnNarrowBackClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_narrow && _stage > 0) ShowStage(_stage - 1);
    }

    private void OnGroupSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || !_narrow || _stage != 0 || e.AddedItems.Count == 0) return;
        ShowStage(1);
    }

    private void OnChannelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_narrow || _stage != 1 || e.AddedItems.Count == 0) return;
        ShowStage(2);
    }

    private void OnChannelDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is { } item) ViewModel.PlayChannelCommand.Execute(item.Primary.Channel);
        else ViewModel.PlayChannelCommand.Execute(ViewModel.SelectedChannel);
    }

    private void OnLineClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LiveChannelLine line) ViewModel.PlayChannelCommand.Execute(line.Channel);
    }
}

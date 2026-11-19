using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace FloatBrowser;
public partial class BrowserWindow : Window
{
    private readonly BrowserManager manager;
    public VisibilityPolicy Policy { get; } = new();
    internal TaskCompletionSource<bool> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool applying = true;
    private bool initialized, closed, pointerWasInside, pointerArmed, modeChanging;
    private Task mediaTransition = Task.CompletedTask;
    private readonly string mediaKey = "__float_media_" + Guid.NewGuid().ToString("N");
    private int documentVersion;
    private DateTime revealTime, leaveTime;
    private Native.RECT hiddenBounds;
    private int visibilityEpoch;
    private Rect normalBounds;
    private bool changingPlacement;
    private bool? backdropApplied;
    private Task backdropTransition = Task.CompletedTask;
    internal bool IsBackdropActive => manager.Settings.ClearWhiteBackground && (compactLayout || Overlay.Visibility != Visibility.Visible);
    private bool compactLayout, layoutChanging, nativeReady;
    internal bool IsInteractiveResize { get; private set; }
    internal int ChromeLayoutPasses { get; private set; }
    internal int NativeOpacityUpdates { get; private set; }
    internal int SharedPlacementApplications { get; private set; }
    private (bool Compact,bool Expanded,bool Ad,bool Overlay,bool Visible,WindowState State,bool Ready)? layoutState;
    private (double Opacity,bool Active)? nativeAppearance;
    private bool? surfaceAppearance;
    private bool surfaceSyncQueued;
    internal bool UsesCompactTools => compactLayout;
    internal BackdropInput TransparentInput { get; }
    private readonly TaskbarGuard taskbarGuard;
    internal bool CanSharePlacement => !changingPlacement && !IsAd && IsLoaded && IsVisible && WindowState == WindowState.Normal;
    private bool browsingPage, toolsExpanded;
    internal bool ToolsExpanded => toolsExpanded;
    internal bool IsAd { get; private set; }
    internal bool IsMobile { get; private set; }
    private string defaultAgent = "";
    private readonly string initialUrl;
    internal string PageTitle = "起始页";
    internal BrowserWindow(BrowserManager manager, string? url)
    {
        this.manager = manager; initialUrl = url ?? "https://float.internal/home.html";
        InitializeComponent();
        TransparentInput = new BackdropInput(this);
        taskbarGuard = new TaskbarGuard(this);
        CompactTools.Opened+=(_,_)=>RaiseCompactTools();
        Web.SizeChanged += (_, _) => { if (!closed) { TransparentInput.Sync(); QueueSurfaceSync(); } };
        IsVisibleChanged += (_, _) => { ApplyBrowserLayout(); TransparentInput.Sync(); QueueSurfaceSync(); };
        StateChanged += (_, _) => { ApplyBrowserLayout(); TransparentInput.Sync(); QueueSurfaceSync(); UpdateMediaPolicy(); };
        Activated+=(_,_)=>UpdateMediaPolicy();
        Deactivated+=(_,_)=>UpdateMediaPolicy();
        LocationChanged += (_, _) => { TransparentInput.Sync(); taskbarGuard.Sync(); if(CompactTools.IsOpen && !IsInteractiveResize) { CompactTools.HorizontalOffset+=.01;CompactTools.HorizontalOffset-=.01; } };
        DpiChanged+=(_,_)=> { layoutState=null;ApplyBrowserLayout();QueueSurfaceSync(); };
        applying = false;
        if (manager.Settings.WindowPlacement is { } placement) ApplySharedPlacement(placement);
        SizeChanged += (_, _) => { manager.RememberPlacement(this); ApplyBrowserLayout(); };
        LocationChanged += (_, _) => manager.RememberPlacement(this);
        Loaded += (_, _) => manager.RememberPlacement(this);
        SourceInitialized += (_, _) =>
        {
            var source=HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source?.AddHook(Native.LayeredStyleHook);
            source?.AddHook(ResizeHook);
            nativeReady=true;
            ApplyBrowserLayout();
            ApplySettings();
        };
        Overlay.IsVisibleChanged += (_, _) =>
        {
            if(layoutChanging || closed)return;
            ApplyBrowserLayout();
        };
        Loaded += Initialize;
        Closed += (_, _) => manager.Remove(this);
        Closing += (_, e) =>
        {
            closed = true; ++visibilityEpoch;
            CompactTools.IsOpen=false;
            // Release capture/controller while the host HWND is still alive.
            taskbarGuard.Dispose();
            TransparentInput.Dispose();
            Web.Dispose();
            Ready.TrySetCanceled();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.OriginalSource is TextBox { Tag: "HotkeyCapture" }) return;
            if (e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.Control) { if (IsAd) ToggleAd(); SetToolsExpanded(true); Address.Focus(); Address.SelectAll(); e.Handled = true; }
            if (e.Key == Key.Escape && Overlay.Visibility == Visibility.Visible) { Overlay.Visibility = Visibility.Collapsed; e.Handled = true; }
        };
        ApplySettings();
        ApplyBrowserLayout();
    }
    private async void Initialize(object sender, RoutedEventArgs e)
    {
        if (initialized) return; initialized = true;
        try
        {
            var env = await manager.Environment;
            if (closed) return;
            await Web.EnsureCoreWebView2Async(env);
            if (closed) return;
            Web.CoreWebView2.SetVirtualHostNameToFolderMapping("float.internal", Path.Combine(AppContext.BaseDirectory, "Assets"), CoreWebView2HostResourceAccessKind.DenyCors);
            defaultAgent = Web.CoreWebView2.Settings.UserAgent;
            await Web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(MediaPlayback.Install(mediaKey));
            await Web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(LinkRouting.Install(mediaKey));
            Web.CoreWebView2.FrameCreated+=(_,a)=>RegisterMediaFrame(a.Frame);
            Web.CoreWebView2.WebMessageReceived+=ReceiveLinkIntent;
            UpdateMediaPolicy(true);
            await mediaTransition;
            if (closed) return;
            Web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Web.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = true;
            Web.CoreWebView2.DOMContentLoaded += (_, _) => { backdropApplied = null; ApplySettings(); UpdateMediaPolicy(true); };
            Web.CoreWebView2.NavigationStarting += (_, a) => { documentVersion++; plainLink=null; PlayingMedia=false; TransparentInput.ResetPointer(); Address.Text = a.Uri.StartsWith("https://float.internal/") ? "" : a.Uri; SetStatus("正在加载…"); };
            Web.CoreWebView2.SourceChanged += (_, _) => { UpdateAddress(); UpdateFavorite(); UpdatePageMode(); };
            Web.CoreWebView2.HistoryChanged += (_, _) => UpdateNavigation();
            Web.CoreWebView2.DocumentTitleChanged += (_, _) =>
            {
                PageTitle = Web.CoreWebView2.DocumentTitle; Title = PageTitle + " · 浮光"; manager.RefreshWindows();
                UpdateTagline();
            };
            Web.CoreWebView2.NavigationCompleted += (_, a) =>
            {
                Loading.Visibility = Visibility.Collapsed; UpdateAddress(); UpdateNavigation(); UpdateFavorite();
                backdropApplied = null; ApplySettings();
                SetStatus(a.IsSuccess ? "就绪 · 登录与浏览数据保存在本机" : "加载失败：" + a.WebErrorStatus + "，可刷新重试");
                if (a.IsSuccess) try { manager.Store.Visit(Web.CoreWebView2.DocumentTitle, Web.CoreWebView2.Source); } catch (Exception ex) { SetStatus("历史保存失败：" + ex.Message); }
            };
            Web.CoreWebView2.NewWindowRequested += async (_, a) =>
            {
                var deferral = a.GetDeferral(); a.Handled = true;
                try
                {
                    if (!Navigation.IsWebUrl(a.Uri) && a.Uri != "about:blank") { a.Handled=false;return; }
                    if(OpenLinkHere(a)) { Web.CoreWebView2.Navigate(a.Uri);return; }
                    var child = manager.CreateWindow("about:blank");
                    if (child is null) { MessageBox.Show(this,"最多保留 5 个窗口，请关闭一个窗口后重试。","无法新建窗口");return; }
                    await child.Ready.Task; a.NewWindow = child.Web.CoreWebView2;
                }
                catch (Exception ex) { SetStatus("新窗口打开失败：" + ex.Message); }
                finally { deferral.Complete(); }
            };
            Web.CoreWebView2.WindowCloseRequested += (_, _) => Dispatcher.BeginInvoke(() => { if (!closed) Close(); });
            Web.CoreWebView2.ProcessFailed += (_, a) => SetStatus("网页进程异常：" + a.ProcessFailedKind + "。请刷新；若无效，请关闭并重开窗口。");
            Web.ZoomFactor = manager.Settings.Zoom;
            Web.ZoomFactorChanged += (_, _) => { if (!applying) { manager.Settings.Zoom = Math.Clamp(Web.ZoomFactor, .3, 3); manager.SettingsChanged(); } };
            Web.CoreWebView2.Navigate(initialUrl);
            Ready.TrySetResult(true);
            if (manager.HotkeyError is not null || manager.Store.LoadWarning is not null) ShowSettings();
        }
        catch (Exception ex)
        {
            Web.Visibility = Visibility.Hidden;
            Loading.Visibility = Visibility.Visible;
            LoadingText.Text = "浏览器初始化失败：" + ex.Message + "\n\n请安装 Microsoft Edge WebView2 Runtime 后重新打开。";
            Ready.TrySetException(ex);
        }
    }
    internal void SetStatus(string text) { if (!closed) Status.Text = text; }
    internal void ApplySettings()
    {
        if (OpacitySlider is null) return;
        applying = true;
        try
        {
            Topmost = manager.Settings.Topmost;
            ApplyBackdropAppearance();
            OpacitySlider.Value = manager.Settings.Opacity * 100; OpacityText.Text = $"{manager.Settings.Opacity * 100:0}%";
            PinButton.Content = Topmost ? "● 已置顶" : "○ 置顶";
            if (Web.CoreWebView2 is not null) Web.ZoomFactor = manager.Settings.Zoom;
            ZoomButton.Content = $"{manager.Settings.Zoom * 100:0}% ▾";
            BossHint.Text = manager.Settings.Boss + "  隐藏 / 召回";
            ResumeMediaButton.Content = manager.Settings.ResumeMediaOnShow ? "回到前台自动续播：开" : "回到前台自动续播：关";
            ResumeMediaButton.IsEnabled=!manager.Settings.BackgroundPlayback;
            BackgroundMediaButton.Content=manager.Settings.BackgroundPlayback?"后台继续播放：开":"后台继续播放：关";
            if(backgroundCheck is not null)backgroundCheck.IsChecked=manager.Settings.BackgroundPlayback;
            if(resumeCheck is not null){resumeCheck.IsChecked=manager.Settings.ResumeMediaOnShow;resumeCheck.IsEnabled=!manager.Settings.BackgroundPlayback;}
            UpdateMediaPolicy();
        }
        finally { applying = false; }
    }
    private void ApplyBackdropAppearance()
    {
        if (Web is null) return;
        bool active=IsBackdropActive;
        // Keep the chroma-key surface flush with device pixels. A rounded frame
        // or fractional inset can blend the near-white key into a visible rim.
        if(surfaceAppearance!=active)
        {
            surfaceAppearance=active;
            WindowFrame.BorderThickness=active?new Thickness(0):new Thickness(1);
            WindowFrame.CornerRadius=active?new CornerRadius(0):new CornerRadius(12);
            WindowLayout.Margin=active?new Thickness(0):new Thickness(1);
            Web.DefaultBackgroundColor = active ? System.Drawing.Color.Transparent : System.Drawing.Color.FromArgb(16,26,32);
            BrowserHost.Background = active ? Brush("#FEFDFE") : Brush("#101A20");
        }
        var appearance=(manager.Settings.Opacity,active);
        if(nativeReady && nativeAppearance!=appearance)
        {
            Native.SetOpacity(new WindowInteropHelper(this).Handle, manager.Settings.Opacity, active);
            nativeAppearance=appearance;NativeOpacityUpdates++;
        }
        BackdropButton.Content = manager.Settings.ClearWhiteBackground ? "白底透明：开" : "白底透明：关";
        TransparentInput?.Sync();
        if (Web.CoreWebView2 is not null && backdropApplied != active)
        {
            backdropApplied=active; backdropTransition=UpdateBackdrop(backdropTransition,active);
        }
    }
    private async Task UpdateBackdrop(Task previous, bool enabled)
    {
        try
        {
            await previous;
            if (closed || Web.CoreWebView2 is null) return;
            await Web.CoreWebView2.ExecuteScriptAsync(PageBackdrop.Script(enabled));
        }
        catch (Exception ex) { SetStatus("白底穿透未能应用：" + ex.Message); }
    }
    internal void ApplySharedPlacement(WindowPlacement placement)
    {
        if (IsAd || !placement.IsValid) return;
        if(Width==placement.Width && Height==placement.Height && Left==placement.Left && Top==placement.Top)return;
        SharedPlacementApplications++;
        changingPlacement = true;
        try
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Width = placement.Width; Height = placement.Height; Left = placement.Left; Top = placement.Top;
        }
        finally { changingPlacement = false; }
    }
    internal void UpdateWindowList()
    {
        WindowList.Children.Clear(); WindowCount.Text = $"{manager.Windows.Count} / 5"; NewButton.IsEnabled = manager.Windows.Count < 5;
        for (int i = 0; i < manager.Windows.Count; i++)
        {
            var target = manager.Windows[i];
            var row=new Grid { Tag=target, Margin=new Thickness(0,2,0,2) };
            row.ColumnDefinitions.Add(new ColumnDefinition());row.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(30) });
            var label=new TextBlock { Text=$"{(target.PlayingMedia?"▶ ":"")}{i+1:00}  {target.PageTitle}",TextTrimming=TextTrimming.CharacterEllipsis };
            var button=new Button { Tag="ActivateWindow",Content=label,ToolTip=(target.PlayingMedia?"正在播放 · ":"")+target.PageTitle,Padding=new Thickness(7,8,7,8),Margin=new Thickness(0),HorizontalContentAlignment=HorizontalAlignment.Stretch,Background=Brush(target==this?"#293D40":"#142027") };
            // The shared Button template centers its content: constrain this text to
            // its star-column allocation so long titles cannot push Close off-screen.
            button.SizeChanged+=(_,_)=>label.MaxWidth=Math.Max(0,button.ActualWidth-14);
            var close=new Button { Tag="CloseWindow",Content="×",ToolTip="关闭此网页",FontSize=17,Padding=new Thickness(0),Margin=new Thickness(2,0,0,0),Background=Brush("#24353C") };
            System.Windows.Automation.AutomationProperties.SetName(close,"关闭网页："+target.PageTitle);
            Grid.SetColumn(close,1);
            button.Click+=(_,_)=>manager.Activate(target);
            close.Click+=(_,e)=> { e.Handled=true;if(!target.closed)target.Close(); };
            row.Children.Add(button);row.Children.Add(close);WindowList.Children.Add(row);
        }
    }
    internal void Conceal(HiddenReason reason)
    {
        if (closed) return;
        if (!IsVisible) { Policy.Hide(reason); return; }
        if (IsVisible) Native.GetWindowRect(new WindowInteropHelper(this).Handle, out hiddenBounds);
        Policy.Hide(reason); ++visibilityEpoch; pointerArmed = false; pointerWasInside = false;
        Hide(); QueueMediaTransition(false, visibilityEpoch);
    }
    private void QueueMediaTransition(bool resume, int epoch) => UpdateMediaPolicy();
    internal void Reveal()
    {
        if (closed) return;
        bool returning = !IsVisible;
        ++visibilityEpoch; Policy.Show(); pointerArmed = false; pointerWasInside = false; revealTime = DateTime.UtcNow;
        Web.CoreWebView2?.Resume(); Show(); WindowState = WindowState.Normal; Activate();
        if (manager.Settings.WindowPlacement is { } placement) ApplySharedPlacement(placement);
        ApplySettings();
        if (returning) QueueMediaTransition(true, visibilityEpoch);
    }
    internal void PollPointer()
    {
        if (closed || modeChanging || IsInteractiveResize) return;
        var cursor = manager.PointerReader();
        if (cursor is null) return;
        var point = cursor.Value;
        if (!IsVisible)
        {
            bool inside = hiddenBounds.Contains(point);
            if (Policy.CanMouseRestore(manager.Settings.RestoreOnHover, inside && !pointerWasInside)) Reveal();
            pointerWasInside = inside; return;
        }
        if (!Native.GetWindowRect(new WindowInteropHelper(this).Handle, out var rect)) return;
        bool within = rect.Contains(point);
        if(CompactTools.IsOpen && CompactToolsRoot.IsVisible)
        {
            var origin=CompactToolsRoot.PointToScreen(new Point());
            var corner=CompactToolsRoot.PointToScreen(new Point(CompactToolsRoot.ActualWidth,CompactToolsRoot.ActualHeight));
            within |= point.X>=origin.X && point.X<corner.X && point.Y>=origin.Y && point.Y<corner.Y;
        }
        if (within) { pointerArmed = true; leaveTime = DateTime.MinValue; }
        // Native popup menus can extend beyond the window; settings pages are ordinary content.
        if (menuOpen) return;
        if (manager.Settings.HideOnLeave && pointerArmed && !within && DateTime.UtcNow - revealTime > TimeSpan.FromMilliseconds(120))
        {
            if (leaveTime == DateTime.MinValue) leaveTime = DateTime.UtcNow;
            if (DateTime.UtcNow - leaveTime > TimeSpan.FromMilliseconds(40)) Conceal(HiddenReason.MouseLeave);
        }
    }
    private void UpdatePageMode()
    {
        string source = Web.CoreWebView2.Source;
        browsingPage = Navigation.IsWebUrl(source) && !source.Equals("https://float.internal/home.html", StringComparison.OrdinalIgnoreCase);
        toolsExpanded = false;
        ApplyBrowserLayout();
    }
    private void UpdateCompactTitle()
    {
        bool small=ActualWidth<420;
        Brand.Visibility=small?Visibility.Collapsed:Visibility.Visible;
        Tagline.Visibility=ActualWidth<640||IsAd?Visibility.Collapsed:Visibility.Visible;
        HideButton.Visibility=ActualWidth<240?Visibility.Collapsed:Visibility.Visible;
        ToolsToggle.Content=small?(toolsExpanded?"‹":"☰"):(toolsExpanded?"‹ 收起":"☰ 工具");
        RestoreAd.Content=small?"↗":"恢复窗口 ↗";
    }
    private void UpdateTagline() => Tagline.Text = browsingPage ? " / " + PageTitle : " /  FLOAT BROWSER";
    internal void SetToolsExpanded(bool expanded)
    {
        toolsExpanded = expanded;
        ApplyBrowserLayout();
    }
    private void ToolsClick(object s, RoutedEventArgs e)
    {
        bool wasExpanded = toolsExpanded || Overlay.Visibility == Visibility.Visible;
        if (Overlay.Visibility == Visibility.Visible) Overlay.Visibility = Visibility.Collapsed;
        SetToolsExpanded(!wasExpanded);
    }
    private void ApplyBrowserLayout()
    {
        if (Sidebar is null || layoutChanging || closed) return;
        if(IsInteractiveResize)return;
        bool compact=Overlay.Visibility==Visibility.Visible || (ActualWidth>0?ActualWidth:Width)<780 || (ActualHeight>0?ActualHeight:Height)<460;
        bool expanded = !IsAd && (toolsExpanded || Overlay.Visibility == Visibility.Visible);
        var next=(compact,expanded,IsAd,Overlay.Visibility==Visibility.Visible,IsVisible,WindowState,nativeReady);
        if(layoutState==next){UpdateCompactTitle();return;}
        layoutState=next;ChromeLayoutPasses++;
        bool webWasHidden=Web.Visibility!=Visibility.Visible;
        layoutChanging=true;
        try
        {
        if(compact!=compactLayout)
        {
            CompactTools.IsOpen=false;
            foreach(var item in new FrameworkElement[]{Toolbar,Footer,Sidebar,Overlay}) ((Panel)item.Parent).Children.Remove(item);
            compactLayout=compact;
            if(compact)
            {
                CompactToolsStack.Children.Add(Toolbar);CompactToolsStack.Children.Add(Footer);CompactToolsStack.Children.Add(Sidebar);
                CompactToolsContent.Children.Add(Overlay);
            }
            else
            {
                ShellBody.Children.Add(Sidebar);MainContent.Children.Add(Toolbar);MainContent.Children.Add(Footer);MainContent.Children.Add(Overlay);
            }
            Grid.SetRowSpan(Overlay,compact?1:3);
            Grid.SetRow(NavigationButtons,compact?1:0);Grid.SetRow(ToolbarActions,compact?1:0);
            Grid.SetColumn(Address,compact?0:1);Grid.SetColumnSpan(Address,compact?3:1);
            SidebarInfo.Visibility=FooterStatus.Visibility=compact?Visibility.Collapsed:Visibility.Visible;
            Sidebar.Padding=compact?new Thickness(8):new Thickness(12,18,12,14);
            Overlay.Padding=compact?new Thickness(12):new Thickness(30,24,30,24);
            OverlayTitle.FontSize=compact?17:23;
        }
        Sidebar.Visibility = Toolbar.Visibility = Footer.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        SidebarColumn.Width = new GridLength(expanded && !compact ? 180 : 0);
        ToolbarRow.Height = new GridLength(expanded && !compact ? 62 : 0);
        FooterRow.Height = expanded && !compact ? GridLength.Auto : new GridLength(0);
        if(compact && expanded && IsVisible && nativeReady && WindowState!=WindowState.Minimized)
        {
            var area=System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).WorkingArea;
            var dpi=VisualTreeHelper.GetDpi(this);
            CompactToolsRoot.Width=Math.Min(420,area.Width/dpi.DpiScaleX-16);
            CompactToolsRoot.Height=Math.Min(600,area.Height/dpi.DpiScaleY-16);
            CompactTools.IsOpen=true;
        }
        else CompactTools.IsOpen=false;
        Web.Visibility=Overlay.Visibility==Visibility.Visible && !compact ? Visibility.Hidden:Visibility.Visible;
        ToolsToggle.Visibility = !IsAd ? Visibility.Visible : Visibility.Collapsed;
        ToolsToggle.Content = expanded ? "‹ 收起" : "☰ 工具";
        TitleRow.Height = new GridLength(IsAd ? 36 : 32);
        Tagline.Visibility = IsAd ? Visibility.Collapsed : Visibility.Visible;
        UpdateTagline();
        UpdateCompactTitle();
        ApplyBackdropAppearance();
        }
        finally { layoutChanging=false; }
        QueueSurfaceSync();
        if(webWasHidden && IsVisible && Web.Visibility==Visibility.Visible && Web.CoreWebView2 is not null)
            QueueMediaTransition(true,visibilityEpoch);
    }
    private void CloseCompactTools(object sender,RoutedEventArgs e) { Overlay.Visibility=Visibility.Collapsed;SetToolsExpanded(false); }
    private IntPtr ResizeHook(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled)
    {
        if(message==0x0231)
        {
            IsInteractiveResize=true;CompactTools.IsOpen=false;TransparentInput.Sync();
        }
        else if(message==0x0232 && IsInteractiveResize)
        {
            IsInteractiveResize=false;layoutState=null;
            manager.RememberPlacement(this);ApplyBrowserLayout();TransparentInput.Sync();taskbarGuard.Sync();
            QueueSurfaceSync();
            revealTime=DateTime.UtcNow;pointerArmed=false;
        }
        return IntPtr.Zero;
    }
    private void QueueSurfaceSync()
    {
        if(closed || !nativeReady || IsInteractiveResize || surfaceSyncQueued)return;
        surfaceSyncQueued=true;
        // SizeChanged/WM_EXITSIZEMOVE can precede the final arrange pass. Only
        // reconcile the HWND viewport and input surface after WPF has arranged
        // the compact/expanded layout, without reloading or altering page zoom.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(()=>
        {
            surfaceSyncQueued=false;
            if(closed || !IsVisible || IsInteractiveResize || WindowState==WindowState.Minimized)return;
            UpdateLayout();
            if(Web.CoreWebView2 is not null && Web.IsVisible) Web.UpdateWindowPos();
            TransparentInput.Sync(); taskbarGuard.Sync();
            Native.RedrawSurface(new WindowInteropHelper(this).Handle);
        }));
    }
    internal void RaiseCompactTools()
    {
        if(CompactTools.IsOpen && PresentationSource.FromVisual(CompactToolsRoot) is HwndSource source)
            Native.RaisePopup(source.Handle);
    }
    private void ScrollCompactTools(object sender,MouseWheelEventArgs e)
    {
        // The normal sidebar has its own ScrollViewer. In the compact panel,
        // scroll the shared viewport even when the pointer is over that sidebar.
        CompactToolsScroll.ScrollToVerticalOffset(CompactToolsScroll.VerticalOffset-e.Delta/120.0*48);
        e.Handled=true;
    }
    internal void Navigate(string input)
    {
        Overlay.Visibility = Visibility.Collapsed;
        if (Web.CoreWebView2 is null) { SetStatus("浏览器仍在初始化。"); return; }
        try { Web.CoreWebView2.Navigate(Navigation.Resolve(input)); Web.Focus(); }
        catch (Exception ex) { SetStatus("无法打开：" + ex.Message); }
    }
    private void UpdateAddress() => Address.Text = Web.CoreWebView2.Source.StartsWith("https://float.internal/") ? "" : Web.CoreWebView2.Source;
    private void UpdateNavigation() { BackButton.IsEnabled = Web.CoreWebView2.CanGoBack; ForwardButton.IsEnabled = Web.CoreWebView2.CanGoForward; }
    private void UpdateFavorite() => FavoriteButton.Content = manager.Store.Data.Favorites.Any(x => x.Url == Web.CoreWebView2.Source) ? "★" : "☆";
    private void AddressKey(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { Navigate(Address.Text); e.Handled = true; } }
    private void BackClick(object s, RoutedEventArgs e) { if (Web.CoreWebView2?.CanGoBack == true) Web.CoreWebView2.GoBack(); }
    private void ForwardClick(object s, RoutedEventArgs e) { if (Web.CoreWebView2?.CanGoForward == true) Web.CoreWebView2.GoForward(); }
    private void ReloadClick(object s, RoutedEventArgs e) => Web.CoreWebView2?.Reload();
    private void HomeClick(object s, RoutedEventArgs e) => Navigate("");
    private void NewClick(object s, RoutedEventArgs e) => manager.CreateWindow();
    private void HideClick(object s, RoutedEventArgs e) => manager.ToggleBoss();
    private void CloseClick(object s, RoutedEventArgs e) => Close();
    private void DragTitle(object s, MouseButtonEventArgs e) { if (e.ChangedButton == MouseButton.Left) DragMove(); }
    private void PinClick(object s, RoutedEventArgs e) { manager.Settings.Topmost = !Topmost; manager.SettingsChanged(); }
    private CheckBox? backgroundCheck, resumeCheck;
    private void BackgroundMediaClick(object s,RoutedEventArgs e) { manager.Settings.BackgroundPlayback=!manager.Settings.BackgroundPlayback;manager.SettingsChanged(); }
    private void ResumeMediaClick(object s, RoutedEventArgs e) { manager.Settings.ResumeMediaOnShow = !manager.Settings.ResumeMediaOnShow; manager.SettingsChanged(); }
    private void BackdropClick(object s, RoutedEventArgs e) { manager.Settings.ClearWhiteBackground = !manager.Settings.ClearWhiteBackground; manager.SettingsChanged(); }
    private void OpacityChanged(object s, RoutedPropertyChangedEventArgs<double> e) { if (applying || manager is null) return; manager.Settings.Opacity = e.NewValue / 100; manager.SettingsChanged(); }
    private void AdClick(object s, RoutedEventArgs e) => ToggleAd();
    internal void ToggleAd()
    {
        if (!IsAd) manager.RememberPlacement(this);
        changingPlacement = true;
        try
        {
        if (!IsAd)
        {
            normalBounds = new Rect(Left, Top, ActualWidth, ActualHeight);
            Overlay.Visibility = Visibility.Collapsed;
            MinWidth = 160; MinHeight = 90; Width = 380; Height = 270;
            var screen = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).WorkingArea;
            var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var corner = transform.Transform(new Point(screen.Right, screen.Bottom));
            Left = corner.X - Width - 16; Top = corner.Y - Height - 16;
            Brand.Text = "精选推荐"; Tagline.Visibility = Visibility.Collapsed; RestoreAd.Visibility = Visibility.Visible;
            IsAd = true;
        }
        else
        {
            var shared = manager.Settings.WindowPlacement;
            MinWidth = 160; MinHeight = 90; Width = shared?.Width ?? normalBounds.Width; Height = shared?.Height ?? normalBounds.Height; Left = shared?.Left ?? normalBounds.Left; Top = shared?.Top ?? normalBounds.Top;
            Brand.Text = "浮光"; Tagline.Visibility = Visibility.Visible; RestoreAd.Visibility = Visibility.Collapsed;
            IsAd = false;
        }
        ApplyBrowserLayout();
        revealTime = DateTime.UtcNow; pointerArmed = false;
        }
        finally { changingPlacement = false; }
    }
    private bool menuOpen;
    private void SetAutoHide(bool enabled)
    {
        manager.Settings.HideOnLeave = enabled;
        if (enabled) { pointerArmed = true; leaveTime = DateTime.MinValue; }
        manager.SettingsChanged();
    }
    private void OpenMenu(Button target, ContextMenu menu)
    {
        menu.PlacementTarget = target; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.Opened += (_, _) => menuOpen = true;
        menu.Closed += (_, _) => { menuOpen = false; revealTime = DateTime.UtcNow; };
        menu.IsOpen = true;
    }
    private void MoreClick(object s, RoutedEventArgs e)
        => OpenMenu((Button)s, CreateMoreMenu());
    internal ContextMenu CreateMoreMenu()
    {
        var menu = new ContextMenu();
        var hide = new MenuItem { Header = "鼠标离开自动隐藏", IsCheckable = true, IsChecked = manager.Settings.HideOnLeave };
        hide.Click += (_, _) => SetAutoHide(hide.IsChecked); menu.Items.Add(hide);
        var restore = new MenuItem { Header = "鼠标回到原区域时自动恢复", IsCheckable = true, IsChecked = manager.Settings.RestoreOnHover };
        restore.Click += (_, _) => { manager.Settings.RestoreOnHover = restore.IsChecked; manager.SettingsChanged(); }; menu.Items.Add(restore);
        var background=new MenuItem { Header="后台继续播放（含老板键隐藏）",IsCheckable=true,IsChecked=manager.Settings.BackgroundPlayback };
        background.Click+=(_,_)=>{manager.Settings.BackgroundPlayback=background.IsChecked;manager.SettingsChanged();};menu.Items.Add(background);
        var pauseAll=new MenuItem { Header="全部暂停" };pauseAll.Click+=async (_,_)=>await manager.PauseAllMedia();menu.Items.Add(pauseAll);
        var playback = new MenuItem { Header = "回到前台后自动续播", IsCheckable = true, IsChecked = manager.Settings.ResumeMediaOnShow, IsEnabled=!manager.Settings.BackgroundPlayback };
        playback.Click += (_, _) => { manager.Settings.ResumeMediaOnShow = playback.IsChecked; manager.SettingsChanged(); }; menu.Items.Add(playback);
        var backdrop = new MenuItem { Header = "网页白底透明（实验性）", IsCheckable = true, IsChecked = manager.Settings.ClearWhiteBackground };
        backdrop.Click += (_, _) => { manager.Settings.ClearWhiteBackground = backdrop.IsChecked; manager.SettingsChanged(); }; menu.Items.Add(backdrop);
        menu.Items.Add(new Separator());
        var settings = new MenuItem { Header = "快捷键与设置" }; settings.Click += (_, _) => ShowSettings(); menu.Items.Add(settings);
        var close = new MenuItem { Header = "关闭当前窗口" }; close.Click += (_, _) => Close(); menu.Items.Add(close);
        var quit = new MenuItem { Header = "退出浮光" }; quit.Click += (_, _) => ((App)Application.Current).Quit(); menu.Items.Add(quit);
        return menu;
    }
    private void ZoomClick(object s, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var panel = new StackPanel { Width = 240, Margin = new Thickness(14) };
        var text = new TextBlock { Text = $"网页缩放 {manager.Settings.Zoom * 100:0}%", Foreground = Brush("#F3F7F5") };
        var slider = new Slider { Minimum = 30, Maximum = 300, Value = manager.Settings.Zoom * 100, TickFrequency = 10, Margin = new Thickness(0,15,0,10), IsSnapToTickEnabled = true };
        slider.ValueChanged += (_, a) => { manager.Settings.Zoom = a.NewValue / 100; manager.SettingsChanged(); text.Text = $"网页缩放 {a.NewValue:0}%"; };
        panel.Children.Add(text); panel.Children.Add(slider);
        var reset = new Button { Content = "重置为 100%" }; reset.Click += (_, _) => slider.Value = 100; panel.Children.Add(reset);
        menu.Items.Add(new MenuItem { Header = panel, StaysOpenOnClick = true }); OpenMenu((Button)s, menu);
    }
    private async void DeviceClick(object s, RoutedEventArgs e)
    {
        try { await SetMobile(!IsMobile); }
        catch (Exception ex) { SetStatus("设备模式切换失败：" + ex.Message); }
    }
    internal async Task SetMobile(bool mobile)
    {
        if (Web.CoreWebView2 is null || modeChanging) return;
        modeChanging = true; DeviceButton.IsEnabled = false;
        try
        {
            if (mobile)
            {
                await Web.CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride", JsonSerializer.Serialize(new { width = 390, height = 844, deviceScaleFactor = 1, mobile = true }));
                await Web.CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.setTouchEmulationEnabled", "{\"enabled\":true,\"maxTouchPoints\":5}");
                Web.CoreWebView2.Settings.UserAgent = defaultAgent.Replace("Windows NT 10.0; Win64; x64", "Linux; Android 13; Pixel 7").Replace("Safari/537.36", "Mobile Safari/537.36");
            }
            else
            {
                await Web.CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.clearDeviceMetricsOverride", "{}");
                await Web.CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.setTouchEmulationEnabled", "{\"enabled\":false}");
                Web.CoreWebView2.Settings.UserAgent = defaultAgent;
            }
            IsMobile = mobile; DeviceButton.Content = mobile ? "▯  手机" : "▣  电脑";
            SetStatus(mobile ? "手机视口 390 × 844；刷新页面可请求手机版内容" : "已恢复电脑模式；刷新可更新网站布局");
        }
        catch
        {
            // Return to a consistent desktop mode if only part of emulation succeeded.
            try { await Web.CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.clearDeviceMetricsOverride", "{}"); await Web.CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.setTouchEmulationEnabled", "{\"enabled\":false}"); } catch { }
            Web.CoreWebView2.Settings.UserAgent = defaultAgent; IsMobile = false; DeviceButton.Content = "▣  电脑"; throw;
        }
        finally { modeChanging = false; DeviceButton.IsEnabled = true; }
    }
    private void FavoriteClick(object s, RoutedEventArgs e)
    {
        if (Web.CoreWebView2 is null) return;
        var url = Web.CoreWebView2.Source;
        if (!Navigation.IsWebUrl(url) || url.StartsWith("https://float.internal/")) { SetStatus("请打开一个网站后再收藏。"); return; }
        var list = manager.Store.Data.Favorites;
        if (list.Any(x => x.Url == url)) list.RemoveAll(x => x.Url == url);
        else list.Add(new(Web.CoreWebView2.DocumentTitle, url, DateTimeOffset.Now));
        manager.SaveNow(); UpdateFavorite();
    }
    private void FavoritesClick(object s, RoutedEventArgs e) => ShowLibrary(true);
    private void HistoryClick(object s, RoutedEventArgs e) => ShowLibrary(false);
    private void DismissOverlay(object s, RoutedEventArgs e) { Overlay.Visibility = Visibility.Collapsed; revealTime = DateTime.UtcNow; }
    private void BeginOverlay(string title) { OverlayTitle.Text = title; OverlayContent.Children.Clear(); Overlay.Visibility = Visibility.Visible; ApplyBrowserLayout(); }
    private void ShowLibrary(bool favorites)
    {
        BeginOverlay(favorites ? "我的收藏" : "最近访问");
        AddDescription(favorites ? "喜欢的网页，随时回来。收藏为扁平列表，点击星标即可添加。" : "只保留最近 50 个访问地址，全部存储在这台电脑上。");
        var entries = favorites ? manager.Store.Data.Favorites : manager.Store.Data.History;
        if (entries.Count == 0) AddDescription(favorites ? "还没有收藏。打开网页，点地址栏右侧的 ☆。" : "还没有浏览记录。输入网址，开始浏览吧。");
        foreach (var entry in entries.ToArray())
        {
            var row = new DockPanel { Margin = new Thickness(0,5,0,5) };
            var remove = new Button { Content = "移除", VerticalAlignment = VerticalAlignment.Center }; DockPanel.SetDock(remove, Dock.Right);
            remove.Click += (_, _) => { entries.Remove(entry); manager.SaveNow(); ShowLibrary(favorites); if (Web.CoreWebView2 is not null) UpdateFavorite(); }; row.Children.Add(remove);
            var label = new StackPanel(); label.Children.Add(new TextBlock { Text = entry.Title, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 520 }); label.Children.Add(new TextBlock { Text = entry.Url, Foreground = Brush("#8BA29F"), FontSize = 11, MaxWidth = 520, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0,5,0,0) });
            var visit = new Button { Content = label, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(14) }; visit.Click += (_, _) => Navigate(entry.Url); row.Children.Add(visit); OverlayContent.Children.Add(row);
        }
    }
    private void SettingsClick(object s, RoutedEventArgs e) => ShowSettings();
    internal void ShowSettings()
    {
        if (IsAd) ToggleAd(); BeginOverlay("快捷键与设置");
        AddDescription("把窗口留在需要的地方，其余交给快捷键。常规设置即时保存；快捷键需点击保存后生效。");
        if (manager.HotkeyError is not null) AddDescription("⚠ 快捷键未生效：" + manager.HotkeyError);
        if (manager.Store.LoadWarning is not null) AddDescription(manager.Store.LoadWarning);
        AddHeading("全局快捷键");
        AddDescription("支持单键或组合键，无需 Ctrl / Alt。单键设为全局快捷键后，在其他应用按这个键也会触发；编辑输入框时会暂停快捷键，避免误隐藏。");
        var keys = new[] { manager.Settings.Boss, manager.Settings.Previous, manager.Settings.Next };
        var labels = new[] { "老板键 · 隐藏 / 召回", "上一个窗口", "下一个窗口" };
        for (int i = 0; i < 3; i++)
        {
            int index = i;
            var grid = new Grid { Margin = new Thickness(0,5,0,5) }; grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4,GridUnitType.Star),MaxWidth=230 });
            grid.Children.Add(new TextBlock { Text = labels[i], VerticalAlignment = VerticalAlignment.Center });
            var box = new TextBox { Text = keys[i].ToString(), IsReadOnly = true, Tag = "HotkeyCapture", ToolTip = "点击后按一个键或组合键，无需 Ctrl / Alt" }; Grid.SetColumn(box, 1);
            box.GotKeyboardFocus += (_, _) => manager.SuspendHotkeys();
            box.LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                if (Keyboard.FocusedElement is not TextBox { Tag: "HotkeyCapture" }) manager.ResumeHotkeys();
            });
            box.PreviewKeyDown += (_, a) =>
            {
                a.Handled = true; var key = a.Key == Key.System ? a.SystemKey : a.Key; var mods = Keyboard.Modifiers;
                if (key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
                keys[index] = Hotkey.FromKey(key, mods); box.Text = keys[index].ToString();
            };
            grid.Children.Add(box); OverlayContent.Children.Add(grid);
        }
        var feedback = new TextBlock { Foreground = Brush("#B4DCCB"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,8,0,0) };
        var save = new Button { Content = "保存快捷键", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0,12,0,0), Background = Brush("#285A4C") };
        save.Click += (_, _) => feedback.Text = manager.ChangeHotkeys(keys, out var error) ? "快捷键已生效。" : error;
        OverlayContent.Children.Add(save); OverlayContent.Children.Add(feedback);
        AddHeading("鼠标行为");
        var hide = new CheckBox { Content = "鼠标离开窗口时自动隐藏", IsChecked = manager.Settings.HideOnLeave };
        hide.Click += (_, _) => SetAutoHide(hide.IsChecked == true); OverlayContent.Children.Add(hide);
        var restore = new CheckBox { Content = "鼠标回到原区域时自动恢复", IsChecked = manager.Settings.RestoreOnHover };
        restore.Click += (_, _) => { manager.Settings.RestoreOnHover = restore.IsChecked == true; manager.SettingsChanged(); }; OverlayContent.Children.Add(restore);
        AddDescription("两个开关独立生效，默认关闭。自动恢复开启后，鼠标重新进入隐藏前的窗口区域时恢复；老板键主动隐藏不会被鼠标唤回。");
        AddHeading("播放行为");
        backgroundCheck=new CheckBox { Content="后台继续播放（包含老板键隐藏）",IsChecked=manager.Settings.BackgroundPlayback };
        backgroundCheck.Click+=(_,_)=>{manager.Settings.BackgroundPlayback=backgroundCheck.IsChecked==true;manager.SettingsChanged();};OverlayContent.Children.Add(backgroundCheck);
        resumeCheck=new CheckBox { Content="回到前台后自动续播",IsChecked=manager.Settings.ResumeMediaOnShow,IsEnabled=!manager.Settings.BackgroundPlayback };
        resumeCheck.Click+=(_,_)=>{manager.Settings.ResumeMediaOnShow=resumeCheck.IsChecked==true;manager.SettingsChanged();};OverlayContent.Children.Add(resumeCheck);
        AddDescription("后台播放开启后，老板键隐藏、最小化及切换浮光窗口均继续播放，多个窗口可以同时出声。关闭后隐藏才暂停；自动续播只恢复被浮光暂停的内容。打开工具面板不打断播放。托盘和更多菜单可全部暂停。");
        AddHeading("本机数据");
        AddDescription("收藏、最近 50 条历史及 WebView2 网站登录状态保存在当前 Windows 用户目录，与程序安装位置无关。覆盖安装不主动清除。网站到期或服务端撤销的登录与授权仍需重新验证。");
        var location = new TextBox { Text = manager.Store.Root, IsReadOnly = true, Margin = new Thickness(0,4,0,12) }; OverlayContent.Children.Add(location);
        AddDescription("播放行为由全局开关控制；部分受保护播放器仍可能限制后台播放。置顶适用于大多数普通窗口，独占全屏应用可能限制显示。右上角 × 关闭当前窗口，最后一个窗口关闭时退出程序；— 隐藏到托盘，可用老板键召回。");
    }
    private void AddHeading(string text) => OverlayContent.Children.Add(new TextBlock { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,24,0,10) });
    private void AddDescription(string text) => OverlayContent.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = Brush("#91A5AC"), TextWrapping = TextWrapping.Wrap, LineHeight = 22, Margin = new Thickness(0,0,0,10) });
    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
}





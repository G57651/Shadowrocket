using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shadowrocket.Core;
using System.Windows.Forms;
using Shadowrocket.ViewModels;
using Shadowrocket.Views;

namespace Shadowrocket;

/// <summary>外壳：胶囊 Tab 导航在四个页面间切换（ui-spec §3）。</summary>
public partial class MainWindow : Window, INotifyPropertyChanged
{
    private static readonly string[] TabItems = ["首页", "配置", "数据", "设置"];

    private int _selectedTabIndex;
    private System.Windows.Forms.NotifyIcon? _tray;
    private bool _forceClose;

    /// <summary>子页面请求切换主 Tab（如首页"去下载"跳设置）。</summary>
    public static event Action<int>? NavigateRequested;

    public MainWindow()
    {
        InitializeComponent();
        Tabs = TabItems;
        DataContext = this;

        var rt = App.Runtime;
        HomePage.DataContext = new HomeViewModel(rt);
        ConfigPage.DataContext = new ConfigViewModel(rt);
        DataPage.DataContext = new DataViewModel(rt);
        SettingsPage.DataContext = new SettingsViewModel(rt);

        SelectedTabIndex = 0;
        rt.NodesChanged += () => Dispatcher.BeginInvoke(async () =>
        {
            if (SelectedTabIndex == 1)
            {
                await ((ConfigViewModel)ConfigPage.DataContext).RefreshAsync();
            }
        });
        Loaded += async (_, _) =>
        {
            await ((HomeViewModel)HomePage.DataContext).RefreshAsync();
            await ((ConfigViewModel)ConfigPage.DataContext).RefreshAsync();
        };
        NavigateRequested += i => Dispatcher.Invoke(() => SelectedTabIndex = i);
        InitTray();
    }

    /// <summary>供子页面跳转主 Tab。</summary>
    public static void RequestNavigate(int index) => NavigateRequested?.Invoke(index);

    private void InitTray()
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Text = "Shadowrocket",
            Visible = true,
        };
        _tray.Icon = LoadAppIcon();
        var menu = new System.Windows.Forms.ContextMenuStrip();
        var host = new TrayHost
        {
            InvokeOnUi = a => Dispatcher.Invoke(a),
            ShowMainWindow = ShowFromTray,
            ExitApp = ExitApp,
        };
        // 每次展开都重建：模式、路由、节点都要反映内核的实时状态
        menu.Opening += (_, _) => TrayMenu.Build(menu, App.Runtime, host);
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowFromTray();
        TrayMenu.Build(menu, App.Runtime, host);
        // 注意：同时引用了 System.Windows.Forms 与 System.Windows，Application 全部写全名
    }

    /// <summary>托盘气泡提示（scheme 订阅导入结果反馈用）。</summary>
    public void ShowTrayTip(string title, string text)
    {
        _tray?.ShowBalloonTip(4000, title, text, System.Windows.Forms.ToolTipIcon.Info);
    }

    /// <summary>把主窗口恢复显示并置于前台（托盘双击与第二实例唤起共用）。</summary>
    public void ShowFromTray()
    {
        System.Windows.Application.Current.MainWindow?.Show();
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApp()
    {
        _forceClose = true;
        _tray?.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>加载嵌入的应用图标（托盘用，与 exe / 窗口图标同源）。</summary>
    private static System.Drawing.Icon LoadAppIcon()
    {
        var sri = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"));
        return new System.Drawing.Icon(sri.Stream);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_forceClose && App.Runtime.Settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _tray?.Dispose();
        base.OnClosing(e);
    }

    public IReadOnlyList<string> Tabs { get; }

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            _selectedTabIndex = Math.Clamp(value, 0, TabItems.Length - 1);
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentTabTitle));
            ShowPage(_selectedTabIndex);
            _ = RefreshCurrentPageAsync();
        }
    }

    public string CurrentTabTitle => Tabs[_selectedTabIndex];

    /// <summary>各页「添加订阅」按钮共用的入口。</summary>
    public static void RequestAddSubscription()
    {
        var dlg = new AddSubscriptionDialog { Owner = System.Windows.Application.Current.MainWindow };
        dlg.ShowDialog();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void ShowPage(int index)
    {
        HomePage.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        ConfigPage.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        DataPage.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RefreshCurrentPageAsync()
    {
        switch (SelectedTabIndex)
        {
            case 0:
                await ((HomeViewModel)HomePage.DataContext).RefreshAsync();
                break;
            case 1:
                await ((ConfigViewModel)ConfigPage.DataContext).RefreshAsync();
                break;
            case 2:
                await ((DataViewModel)DataPage.DataContext).RefreshAsync();
                break;
            case 3:
                ((SettingsViewModel)SettingsPage.DataContext).RefreshAll();
                break;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));

    // 快捷键：Ctrl+1..4 切换 Tab
    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyboardDevice.Modifiers == ModifierKeys.Control && e.Key is >= Key.D1 and <= Key.D4)
        {
            SelectedTabIndex = e.Key - Key.D1;
            e.Handled = true;
        }
    }
}

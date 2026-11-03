using System.Windows;
using System.Windows.Controls;

namespace Shadowrocket.Views;

public partial class ConfigView : UserControl
{
    public ConfigView()
    {
        InitializeComponent();
    }

    private void ProfileCard_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.FrameworkElement)?.Tag is ViewModels.ProfileRow row)
        {
            new SubscriptionDetailDialog(App.Runtime, row).ShowDialog();
            (DataContext as ViewModels.ConfigViewModel)?.RefreshCommand.Execute(null);
        }
    }

    private void GroupRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.FrameworkElement)?.Tag is ViewModels.GroupRow g)
        {
            new GroupDetailDialog(App.Runtime, g.Name).ShowDialog();
            (DataContext as ViewModels.ConfigViewModel)?.RefreshCommand.Execute(null);
        }
    }

    private void RulesRow_Click(object sender, RoutedEventArgs e)
    {
        new RulesDialog(App.Runtime).ShowDialog();
    }

    private void RouteRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu })
        {
            menu.PlacementTarget = sender as Button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }
}

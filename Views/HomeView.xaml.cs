using System.Windows;
using System.Windows.Controls;

namespace Shadowrocket.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
    }

    private void RouteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { ContextMenu: { } menu })
        {
            menu.PlacementTarget = sender as System.Windows.Controls.Button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private void GoSettings_Click(object sender, RoutedEventArgs e) => MainWindow.RequestNavigate(3);
}

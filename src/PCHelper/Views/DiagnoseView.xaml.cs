using System.Windows.Controls;
using System.Windows.Navigation;
using PCHelper.Core;

namespace PCHelper.Views;

public partial class DiagnoseView : UserControl
{
    public DiagnoseView() => InitializeComponent();

    private void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        Shell.Open(e.Uri.AbsoluteUri);
        e.Handled = true;
    }
}

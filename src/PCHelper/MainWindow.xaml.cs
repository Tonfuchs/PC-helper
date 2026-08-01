using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using PCHelper.Core;

namespace PCHelper;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Native.EnableDarkTitleBar(new WindowInteropHelper(this).Handle);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Beim Schliessen soll die Ueberwachung normalerweise weiterlaufen -
        // das Fenster wandert dann in den Infobereich der Taskleiste.
        if (System.Windows.Application.Current is App app && app.HideInsteadOfClose())
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);

        if (System.Windows.Application.Current is App a) a.ExitApplication();
    }
}

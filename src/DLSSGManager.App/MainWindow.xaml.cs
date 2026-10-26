using DLSSGManager.ViewModels;

namespace DLSSGManager;

public partial class MainWindow
{
    public MainWindow(MainViewModel model)
    {
        InitializeComponent();
        DataContext = model;
    }
}

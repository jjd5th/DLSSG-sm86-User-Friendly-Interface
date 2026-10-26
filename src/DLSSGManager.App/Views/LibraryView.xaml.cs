using System.Windows.Input;

namespace DLSSGManager.Views;

public partial class LibraryView
{
    public LibraryView() => InitializeComponent();

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ViewModels.LibraryViewModel model && model.OpenSelectedCommand.CanExecute(null))
            model.OpenSelectedCommand.Execute(null);
    }
}

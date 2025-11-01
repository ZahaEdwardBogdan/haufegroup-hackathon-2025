using System.Reactive.Linq;
using Avalonia.Controls;
using HaufeApp.ViewModels;
using ReactiveUI;

namespace HaufeApp.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        
        if (StorageProvider is not { } provider)
        {
            return;
        }

        _viewModel = new MainWindowViewModel(provider);
        this.DataContext = _viewModel;
        
        this.WhenAnyValue(x => x.DataContext)!
            .OfType<MainWindowViewModel>() 
            .Select(vm => vm.WhenAnyValue(x => x.WindowTitle))
            .Switch()
            .BindTo(this, x => x.Title);
    }
}
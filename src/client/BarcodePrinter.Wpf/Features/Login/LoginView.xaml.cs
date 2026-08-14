using System.ComponentModel;
using System.Windows;

namespace BarcodePrinter.Wpf.Features.Login;

public partial class LoginView : Window
{
    private readonly LoginViewModel _viewModel;

    public LoginView(LoginViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += (_, _) => UsernameBox.Focus();   // keyboard-first (§12)
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LoginViewModel.IsChangePasswordMode) &&
            _viewModel.IsChangePasswordMode)
        {
            SignInPanel.Visibility = Visibility.Collapsed;
            ChangePanel.Visibility = Visibility.Visible;
            CurrentBox.Focus();
        }
    }

    private void OnChangePasswordClick(object sender, RoutedEventArgs e) =>
        _viewModel.ChangePasswordCommand.Execute(new object[] { CurrentBox, NewBox, ConfirmBox });
}

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace BarcodePrinter.Wpf.Shell;

public partial class ShellView : Window
{
    private readonly ShellViewModel _viewModel;
    private readonly Func<string, object?> _pageResolver;
    private readonly Dictionary<string, object> _pageCache = [];

    public ShellView(ShellViewModel viewModel, Func<string, object?> pageResolver)
    {
        _viewModel = viewModel;
        _pageResolver = pageResolver;
        DataContext = viewModel;
        InitializeComponent();

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += (_, _) => CheckInitialNavItem();
    }

    /// <summary>240 ↔ 56 px icon rail (§12). Only this column changes —
    /// rows 0/2/3 span the full width and never move.</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.IsSidebarCollapsed))
        {
            SidebarColumn.Width = new GridLength(_viewModel.IsSidebarCollapsed ? 56 : 240);
        }
        else if (e.PropertyName == nameof(ShellViewModel.SelectedItem))
        {
            ShowPage(_viewModel.SelectedItem);
        }
    }

    /// <summary>Pages are resolved once and cached — navigating back keeps
    /// grid position, search text and drawer state.</summary>
    private void ShowPage(NavItem? item)
    {
        object? page = null;
        if (item is not null)
        {
            if (!_pageCache.TryGetValue(item.Key, out page))
            {
                page = _pageResolver(item.Key);
                if (page is not null)
                {
                    _pageCache[item.Key] = page;
                    // Dashboard alerts and quick actions drive shell navigation.
                    if (page is System.Windows.FrameworkElement
                        { DataContext: Features.Dashboard.DashboardViewModel dashboard })
                    {
                        dashboard.NavigationRequested += (_, key) => NavigateTo(key);
                    }
                }
            }
        }
        PageHost.Content = page;
        PageHost.Visibility = page is null ? Visibility.Collapsed : Visibility.Visible;
        Placeholder.Visibility = page is null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Selects a nav item by key, so other screens can navigate
    /// without knowing about views.</summary>
    public void NavigateTo(string key)
    {
        var target = _viewModel.NavItems.FirstOrDefault(i => i.Key == key);
        if (target is null)
        {
            return;   // the user's role does not have that page
        }
        foreach (var container in NavList.Items)
        {
            if (NavList.ItemContainerGenerator.ContainerFromItem(container) is ContentPresenter presenter &&
                System.Windows.Media.VisualTreeHelper.GetChildrenCount(presenter) > 0 &&
                System.Windows.Media.VisualTreeHelper.GetChild(presenter, 0) is RadioButton
                { Tag: NavItem item } radio && item.Key == key)
            {
                radio.IsChecked = true;
                return;
            }
        }
        _viewModel.SelectedItem = target;
    }

    private void OnNavItemChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: NavItem item })
        {
            _viewModel.SelectedItem = item;
        }
    }

    private void CheckInitialNavItem()
    {
        if (NavList.ItemContainerGenerator.ContainerFromIndex(0) is ContentPresenter presenter &&
            System.Windows.Media.VisualTreeHelper.GetChildrenCount(presenter) > 0 &&
            System.Windows.Media.VisualTreeHelper.GetChild(presenter, 0) is RadioButton first)
        {
            first.IsChecked = true;
        }
        // The ViewModel already selected the first item, so checking the radio
        // raises no PropertyChanged — the landing page must be shown explicitly.
        ShowPage(_viewModel.SelectedItem);
    }
}

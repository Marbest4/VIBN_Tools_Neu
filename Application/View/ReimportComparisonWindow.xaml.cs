using System.Windows;

namespace VIBN_Tools.Application.View;

public partial class ReimportComparisonWindow : Window
{
    public ReimportComparisonWindow()
    {
        InitializeComponent();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

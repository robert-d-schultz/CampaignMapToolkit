using System.Windows;

namespace CAIME.Views.Windows
{
    /// <summary>
    /// Interaction logic for UpscaleWindow.xaml
    /// </summary>
    public partial class UpscaleWindow : Window
    {
        private readonly UpscaleViewModel upscaleVM;

        public UpscaleWindow(ProjectManager ProjectManager)
        {
            InitializeComponent();
            upscaleVM = new UpscaleViewModel(ProjectManager);
            DataContext = upscaleVM;
        }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            if (upscaleVM.UpscaleProject())
            {
                Close();
                Owner.Focus();
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
            Owner.Focus();
        }
    }
}

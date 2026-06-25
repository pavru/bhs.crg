using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace BHS.CustomXmlParts.Dialogs
{
    /// <summary>
    /// Interaction logic for SelectXmlPartDialog.xaml
    /// </summary>
    public partial class SelectXmlPartDialog : Window
    {
        public SelectXmlPartDialog(List<string> namespaces)
        {
            InitializeComponent();
            foreach (var ns in namespaces)
            {
                NameSpaceList.Items.Add(ns);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            this.Close();
        }

        private void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            this.Close();
        }
    }
}

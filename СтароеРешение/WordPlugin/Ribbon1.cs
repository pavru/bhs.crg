using Microsoft.Office.Tools.Ribbon;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BHS.CustomXmlParts;
using System.Windows;

namespace BHS
{
    public partial class Ribbon1
    {
        private void Ribbon1_Load(object sender, RibbonUIEventArgs e)
        {

        }

        private void OnClickReload(object sender, RibbonControlEventArgs e)
        {
            using (var command = new ReloadXmlPartCommand(Globals.ThisAddIn))
            {
                command.Execute(false);
            }
        }

        private void OnClickRemove(object sender, RibbonControlEventArgs e)
        {
            using (var command = new RemoveXmlPartCommand())
            {
                command.Execute(Globals.ThisAddIn);
            }
        }

        private void OnClickReloadFrom(object sender, RibbonControlEventArgs e)
        {
            using (var command = new ReloadXmlPartCommand(Globals.ThisAddIn))
            {
                command.Execute(true);
            }
        }

        private void OnClickCurrentMapping(object sender, RibbonControlEventArgs e)
        {
            var a = Globals.ThisAddIn.Application.Selection.ParentContentControl;
            if (a != null)
            {
                if (a.XMLMapping.IsMapped)
                {
                    MessageBox.Show(a.XMLMapping.XPath);
                } else
                {
                    MessageBox.Show("Content Control is not mapped");
                }
            }
        }
    }
}

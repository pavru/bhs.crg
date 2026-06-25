using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using BHS.CustomXmlParts.Dialogs;
using Microsoft.Office.Core;

namespace BHS.CustomXmlParts
{
    internal class RemoveXmlPartCommand: IDisposable
    {
        private void ReleaseUnmanagedResources()
        {
            // TODO release unmanaged resources here
        }

        public void Execute(ThisAddIn thisAddIn)
        {
            var namespces = new List<string>();
            foreach (CustomXMLPart part in thisAddIn.Application.ActiveDocument.CustomXMLParts)
            {
                namespces.Add(part.NamespaceURI);
            }
            var dialog = new SelectXmlPartDialog(namespces);
            if (dialog.ShowDialog() == true)
            {
                var ns = dialog.NameSpaceList.SelectedValue as string;
                foreach (CustomXMLPart part in thisAddIn.Application.ActiveDocument.CustomXMLParts)
                {
                    if (part.NamespaceURI == ns)
                    {
                        part.Delete();
                    }
                }
            }

        }

        public void Dispose()
        {
            ReleaseUnmanagedResources();
            GC.SuppressFinalize(this);
        }

        ~RemoveXmlPartCommand()
        {
            ReleaseUnmanagedResources();
        }
    }
}

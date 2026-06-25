using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Office.Core;
using Microsoft.Office.Interop.Word;

namespace BHS.CustomXmlParts
{
    internal class ReloadXmlPartCommand : IDisposable
    {
        private const string DataSourcePropName = "BimHouseXmlDataSource";
        private readonly ThisAddIn _thisAddIn;

        public ReloadXmlPartCommand(ThisAddIn thisAddIn)
        {
            _thisAddIn = thisAddIn;
        }

        private void ReleaseUnmanagedResources()
        {
        }

        public void Execute(bool reloadFrom = true)
        {
            string file = null;
            if (reloadFrom)
            {
                var dlg = _thisAddIn.Application.FileDialog[MsoFileDialogType.msoFileDialogFilePicker];
                dlg.Filters.Clear();
                dlg.Filters.Add("XML data file", "*.xml");
                dlg.Filters.Add("All files", "*.*");
                dlg.AllowMultiSelect = false;
                if (dlg.Show() == -1)
                {
                    FileDialogSelectedItems selectedItems = dlg.SelectedItems;
                    file = selectedItems.Item(1);
                }
            }
            else
            {
                file = ReadDataSourceProperty();
            }

            if (file == null)
            {
                throw new Exception("Source file not selected");
            }
            try
            {
                using (var xmlPartsFile = new XmlPartsFile(file))
                {
                    xmlPartsFile.LoadFile();
                    if (xmlPartsFile.HasStyleSheet)
                    {
                        xmlPartsFile.TransformDocument();
                    }

                    foreach (CustomXMLPart v in _thisAddIn.Application.ActiveDocument.CustomXMLParts)
                    {
                        if (v.NamespaceURI == xmlPartsFile.DefaultXmlNs)
                        {
                            v.Delete();
                        }
                    }


                    _thisAddIn.Application.ActiveDocument.CustomXMLParts.Add(xmlPartsFile.XML);
                    WriteDataSourceProperty(file);

                }
            }
            catch (Exception e)
            {
                MessageBox.Show(e.ToString(), @"File can not be loaded");
            }
        }



        private string ReadDataSourceProperty()
        {
            var properties = (DocumentProperties)_thisAddIn.Application.ActiveDocument.CustomDocumentProperties;
            foreach (DocumentProperty property in properties)
            {
                if (property.Name == DataSourcePropName)
                {
                    return property.Value.ToString();
                }
            }
            return null;
        }

        private void WriteDataSourceProperty(string sourceFile)
        {
            var properties = (DocumentProperties)_thisAddIn.Application.ActiveDocument.CustomDocumentProperties;
            if (ReadDataSourceProperty() != null)
            {
                properties[DataSourcePropName].Delete();
            }

            properties.Add(DataSourcePropName, false, Microsoft.Office.Core.MsoDocProperties.msoPropertyTypeString, sourceFile);

        }
        public void Dispose()
        {
            ReleaseUnmanagedResources();
            GC.SuppressFinalize(this);
        }

        ~ReloadXmlPartCommand()
        {
            ReleaseUnmanagedResources();
        }
    }
}

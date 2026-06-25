using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Xsl;
using BHS.Properties;
using Microsoft.Office.Interop.Word;
using Saxon.Api;

namespace BHS.CustomXmlParts
{
    internal class XmlPartsFile : IDisposable
    {
        private readonly string _fileName;
        private readonly XmlDocument _document;

        public XmlPartsFile(string fielName)
        {
            _fileName = fielName;
            _document = new XmlDocument();
            _document.PreserveWhitespace = true;
        }

        public void LoadFile()
        {
            _document.Load(_fileName);
        }

        public void TransformDocument()
        {
            var transform = new XslCompiledTransform();
            var settings = new XsltSettings(true, true);
            transform.Load(StyleSheetUri, settings, new XmlUrlResolver());
            using (StringWriter writer = new StringWriter())
            {
                var processor = new Processor();
                var input = processor.NewDocumentBuilder().Build(new Uri(_fileName));
                var transformer = processor.NewXsltCompiler().Compile(new Uri(StyleSheetUri)).Load30();
                transformer.GlobalContextItem = input;
                var serializer = processor.NewSerializer();
                serializer.SetOutputWriter(writer);
                transformer.ApplyTemplates(input, serializer);
                // transform.Transform(_document.CreateNavigator(), null, writer);
                _document.LoadXml(writer.ToString());
            }
        }


        public string XML
        {
            get
            {
                using (StringWriter writer = new StringWriter())
                {
                    _document.Save(writer);
                    return writer.ToString();
                }
            }
        }
        public string DefaultXmlNs => _document.DocumentElement?.NamespaceURI ?? string.Empty;

        public bool HasStyleSheet => _document.SelectSingleNode("//processing-instruction(\"xml-stylesheet\")") != null;

        public string StyleSheetUri
        {
            get
            {
                var re = new Regex(".*href=\"(.*)\"");
                var uri= re.Match((_document.SelectSingleNode("//processing-instruction(\"xml-stylesheet\")") as XmlProcessingInstruction)?.Data ?? "").Groups[1].Value;
                return uri;
            }
        }


        private void ReleaseUnmanagedResources()
        {
            // TODO release unmanaged resources here
        }

        public void Dispose()
        {
            ReleaseUnmanagedResources();
            GC.SuppressFinalize(this);
        }

        ~XmlPartsFile()
        {
            ReleaseUnmanagedResources();
        }
    }
}
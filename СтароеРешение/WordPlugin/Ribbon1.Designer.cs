namespace BHS
{
    partial class Ribbon1 : Microsoft.Office.Tools.Ribbon.RibbonBase
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        public Ribbon1()
            : base(Globals.Factory.GetRibbonFactory())
        {
            InitializeComponent();
        }

        /// <summary> 
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Component Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(Ribbon1));
            this.BhsTab = this.Factory.CreateRibbonTab();
            this.XmlPartsGroup = this.Factory.CreateRibbonGroup();
            this.XmlPartsReloadButton = this.Factory.CreateRibbonButton();
            this.XmlXPartsReloadFromButton = this.Factory.CreateRibbonButton();
            this.XmlPartRemoveButton = this.Factory.CreateRibbonButton();
            this.CurrentMappingButton = this.Factory.CreateRibbonButton();
            this.BhsTab.SuspendLayout();
            this.XmlPartsGroup.SuspendLayout();
            this.SuspendLayout();
            // 
            // BhsTab
            // 
            this.BhsTab.Groups.Add(this.XmlPartsGroup);
            this.BhsTab.Label = "BHS";
            this.BhsTab.Name = "BhsTab";
            // 
            // XmlPartsGroup
            // 
            this.XmlPartsGroup.Items.Add(this.XmlPartsReloadButton);
            this.XmlPartsGroup.Items.Add(this.XmlXPartsReloadFromButton);
            this.XmlPartsGroup.Items.Add(this.XmlPartRemoveButton);
            this.XmlPartsGroup.Items.Add(this.CurrentMappingButton);
            this.XmlPartsGroup.Label = "XML Parts";
            this.XmlPartsGroup.Name = "XmlPartsGroup";
            // 
            // XmlPartsReloadButton
            // 
            this.XmlPartsReloadButton.ControlSize = Microsoft.Office.Core.RibbonControlSize.RibbonControlSizeLarge;
            this.XmlPartsReloadButton.Image = ((System.Drawing.Image)(resources.GetObject("XmlPartsReloadButton.Image")));
            this.XmlPartsReloadButton.Label = "Reload";
            this.XmlPartsReloadButton.Name = "XmlPartsReloadButton";
            this.XmlPartsReloadButton.ShowImage = true;
            this.XmlPartsReloadButton.Click += new Microsoft.Office.Tools.Ribbon.RibbonControlEventHandler(this.OnClickReload);
            // 
            // XmlXPartsReloadFromButton
            // 
            this.XmlXPartsReloadFromButton.ControlSize = Microsoft.Office.Core.RibbonControlSize.RibbonControlSizeLarge;
            this.XmlXPartsReloadFromButton.Image = ((System.Drawing.Image)(resources.GetObject("XmlXPartsReloadFromButton.Image")));
            this.XmlXPartsReloadFromButton.Label = "Reload From";
            this.XmlXPartsReloadFromButton.Name = "XmlXPartsReloadFromButton";
            this.XmlXPartsReloadFromButton.ShowImage = true;
            this.XmlXPartsReloadFromButton.Click += new Microsoft.Office.Tools.Ribbon.RibbonControlEventHandler(this.OnClickReloadFrom);
            // 
            // XmlPartRemoveButton
            // 
            this.XmlPartRemoveButton.ControlSize = Microsoft.Office.Core.RibbonControlSize.RibbonControlSizeLarge;
            this.XmlPartRemoveButton.Image = ((System.Drawing.Image)(resources.GetObject("XmlPartRemoveButton.Image")));
            this.XmlPartRemoveButton.Label = "Remove";
            this.XmlPartRemoveButton.Name = "XmlPartRemoveButton";
            this.XmlPartRemoveButton.ShowImage = true;
            this.XmlPartRemoveButton.Click += new Microsoft.Office.Tools.Ribbon.RibbonControlEventHandler(this.OnClickRemove);
            // 
            // CurrentMappingButton
            // 
            this.CurrentMappingButton.ControlSize = Microsoft.Office.Core.RibbonControlSize.RibbonControlSizeLarge;
            this.CurrentMappingButton.Image = ((System.Drawing.Image)(resources.GetObject("CurrentMappingButton.Image")));
            this.CurrentMappingButton.Label = "Current Mapping";
            this.CurrentMappingButton.Name = "CurrentMappingButton";
            this.CurrentMappingButton.ShowImage = true;
            this.CurrentMappingButton.Click += new Microsoft.Office.Tools.Ribbon.RibbonControlEventHandler(this.OnClickCurrentMapping);
            // 
            // Ribbon1
            // 
            this.Name = "Ribbon1";
            this.RibbonType = "Microsoft.Word.Document";
            this.Tabs.Add(this.BhsTab);
            this.Load += new Microsoft.Office.Tools.Ribbon.RibbonUIEventHandler(this.Ribbon1_Load);
            this.BhsTab.ResumeLayout(false);
            this.BhsTab.PerformLayout();
            this.XmlPartsGroup.ResumeLayout(false);
            this.XmlPartsGroup.PerformLayout();
            this.ResumeLayout(false);

        }

        #endregion

        internal Microsoft.Office.Tools.Ribbon.RibbonTab BhsTab;
        internal Microsoft.Office.Tools.Ribbon.RibbonGroup XmlPartsGroup;
        internal Microsoft.Office.Tools.Ribbon.RibbonButton XmlPartsReloadButton;
        internal Microsoft.Office.Tools.Ribbon.RibbonButton XmlPartRemoveButton;
        internal Microsoft.Office.Tools.Ribbon.RibbonButton XmlXPartsReloadFromButton;
        internal Microsoft.Office.Tools.Ribbon.RibbonButton CurrentMappingButton;
    }

    partial class ThisRibbonCollection
    {
        internal Ribbon1 Ribbon1
        {
            get { return this.GetRibbon<Ribbon1>(); }
        }
    }
}

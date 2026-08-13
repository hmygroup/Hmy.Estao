using System.Collections;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Windows.Forms;

namespace ZarpaSuite.Controls.Design
{
    public sealed class ZarpaThemeManagerDesigner : ComponentDesigner
    {
        private DesignerActionListCollection actionLists;

        public override void InitializeNewComponent(IDictionary defaultValues)
        {
            base.InitializeNewComponent(defaultValues);
            IDesignerHost host = GetService(typeof(IDesignerHost)) as IDesignerHost;
            Control root = host == null ? null : host.RootComponent as Control;
            PropertyDescriptor property = TypeDescriptor.GetProperties(Component)["RootControl"];
            if (root != null && property != null) property.SetValue(Component, root);
        }

        public override DesignerActionListCollection ActionLists
        {
            get
            {
                if (actionLists == null)
                {
                    actionLists = new DesignerActionListCollection();
                    actionLists.Add(new ZarpaThemeManagerActionList(Component));
                }
                return actionLists;
            }
        }
    }

    internal sealed class ZarpaThemeManagerActionList : DesignerActionList
    {
        private readonly ZarpaThemeManager manager;

        internal ZarpaThemeManagerActionList(IComponent component) : base(component)
        {
            manager = (ZarpaThemeManager)component;
        }

        public ZarpaThemePreset Preset
        {
            get { return manager.Preset; }
            set { SetProperty("Preset", value); }
        }

        public ZarpaDensity Density
        {
            get { return manager.Density; }
            set { SetProperty("Density", value); }
        }

        public bool ApplyThemeFontToNativeControls
        {
            get { return manager.ApplyThemeFontToNativeControls; }
            set { SetProperty("ApplyThemeFontToNativeControls", value); }
        }

        public void ApplyTheme()
        {
            manager.Apply();
        }

        public override DesignerActionItemCollection GetSortedActionItems()
        {
            return new DesignerActionItemCollection
            {
                new DesignerActionHeaderItem("Apariencia global"),
                new DesignerActionPropertyItem("Preset", "Tema"),
                new DesignerActionPropertyItem("Density", "Densidad"),
                new DesignerActionPropertyItem("ApplyThemeFontToNativeControls",
                    "Aplicar fuente a controles nativos"),
                new DesignerActionMethodItem(this, "ApplyTheme", "Aplicar ahora")
            };
        }

        private void SetProperty(string name, object value)
        {
            PropertyDescriptor property = TypeDescriptor.GetProperties(manager)[name];
            if (property != null) property.SetValue(manager, value);
        }
    }
}

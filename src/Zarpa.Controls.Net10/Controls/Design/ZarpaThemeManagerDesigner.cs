using System.Collections;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Windows.Forms;

namespace ZarpaSuite.Controls.Design
{
    public sealed class ZarpaThemeManagerDesigner : ComponentDesigner
    {
        public override void InitializeNewComponent(IDictionary defaultValues)
        {
            base.InitializeNewComponent(defaultValues);
            IDesignerHost host = GetService(typeof(IDesignerHost)) as IDesignerHost;
            Control root = host == null ? null : host.RootComponent as Control;
            PropertyDescriptor property = TypeDescriptor.GetProperties(Component)["RootControl"];
            if (root != null && property != null) property.SetValue(Component, root);
        }
    }
}

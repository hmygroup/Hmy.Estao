using System;
using System.Collections;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Drawing;
using System.Windows.Forms;
using System.Windows.Forms.Design;

namespace ZarpaSuite.Controls.Design
{
    public sealed class ZarpaNavigationViewDesigner : ControlDesigner
    {
        private DesignerVerbCollection verbs;
        private DesignerActionListCollection actionLists;
        private ISelectionService selectionService;
        private IComponentChangeService changeService;
        private ContextMenuStrip contextMenu;

        private ZarpaNavigationView Navigation { get { return (ZarpaNavigationView)Component; } }

        public override DesignerVerbCollection Verbs
        {
            get
            {
                if (verbs == null)
                    verbs = new DesignerVerbCollection
                    {
                        new DesignerVerb("Añadir página", delegate { SelectComponent(AddItem(ZarpaNavigationItemKind.Item)); }),
                        new DesignerVerb("Añadir cabecera", delegate { SelectComponent(AddItem(ZarpaNavigationItemKind.Header)); }),
                        new DesignerVerb("Añadir separador", delegate { SelectComponent(AddItem(ZarpaNavigationItemKind.Separator)); })
                    };
                return verbs;
            }
        }

        public override DesignerActionListCollection ActionLists
        {
            get
            {
                if (actionLists == null)
                    actionLists = new DesignerActionListCollection { new ZarpaNavigationActionList(this) };
                return actionLists;
            }
        }

        public override void Initialize(IComponent component)
        {
            base.Initialize(component);
            Navigation.MouseUp += NavigationMouseUp;
            selectionService = GetService(typeof(ISelectionService)) as ISelectionService;
            changeService = GetService(typeof(IComponentChangeService)) as IComponentChangeService;
            if (selectionService != null) selectionService.SelectionChanged += SelectionChanged;
            if (changeService != null) changeService.ComponentChanged += ComponentChanged;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Navigation.MouseUp -= NavigationMouseUp;
                if (selectionService != null) selectionService.SelectionChanged -= SelectionChanged;
                if (changeService != null) changeService.ComponentChanged -= ComponentChanged;
                if (contextMenu != null) contextMenu.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override bool GetHitTest(Point point)
        {
            return Navigation.RectangleToScreen(Navigation.ClientRectangle).Contains(point);
        }

        internal ZarpaNavigationItem AddItem(ZarpaNavigationItemKind kind)
        {
            ZarpaNavigationItem result = null;
            Mutate("Añadir elemento de navegación", delegate(IDesignerHost host)
            {
                result = (ZarpaNavigationItem)host.CreateComponent(typeof(ZarpaNavigationItem));
                result.Kind = kind;
                if (kind == ZarpaNavigationItemKind.Item)
                {
                    result.Text = "Nueva página";
                    ZarpaNavigationPage page = (ZarpaNavigationPage)host.CreateComponent(typeof(ZarpaNavigationPage));
                    if (Navigation.Parent != null) Navigation.Parent.Controls.Add(page);
                    result.Page = page;
                }
                else if (kind == ZarpaNavigationItemKind.Header)
                    result.Text = "Nueva sección";
                Navigation.Items.Add(result);
                Navigation.ActivateDesignItem(result);
            });
            return result;
        }

        internal void Remove(ZarpaNavigationItem item)
        {
            if (item == null) return;
            ZarpaNavigationPage page = item.Page;
            Mutate("Eliminar elemento de navegación", delegate(IDesignerHost host)
            {
                Navigation.Items.Remove(item);
                if (item.Site != null) host.DestroyComponent(item);
                if (page != null && page.Site != null && !IsPageUsed(page)) host.DestroyComponent(page);
            });
            SelectComponent(Navigation);
        }

        internal void Move(ZarpaNavigationItem item, int offset)
        {
            if (item == null) return;
            int index = Navigation.Items.IndexOf(item);
            int next = index + offset;
            if (index < 0 || next < 0 || next >= Navigation.Items.Count) return;
            Mutate("Mover elemento de navegación", delegate(IDesignerHost host)
            {
                Navigation.Items.RemoveAt(index);
                Navigation.Items.Insert(next, item);
            });
            SelectComponent(item);
        }

        internal void SetCompact(bool value)
        {
            PropertyDescriptor property = TypeDescriptor.GetProperties(Navigation)["Compact"];
            if (property != null) property.SetValue(Navigation, value);
        }

        private void NavigationMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                int index = Navigation.DesignHitTest(e.Location);
                if (index >= 0)
                {
                    ZarpaNavigationItem item = Navigation.Items[index];
                    Navigation.ActivateDesignItem(item);
                    SelectComponent(item);
                }
                else if (e.Y < 48 && e.X >= Navigation.Width - 48)
                    SetCompact(!Navigation.Compact);
            }
            else if (e.Button == MouseButtons.Right)
            {
                int index = Navigation.DesignHitTest(e.Location);
                ShowContextMenu(e.Location, index < 0 ? null : Navigation.Items[index]);
            }
        }

        private void SelectionChanged(object sender, EventArgs e)
        {
            object selected = selectionService == null ? null : selectionService.PrimarySelection;
            ZarpaNavigationItem item = selected as ZarpaNavigationItem;
            if (item == null)
            {
                ZarpaNavigationPage page = selected as ZarpaNavigationPage;
                if (page != null) item = FindItem(page);
            }
            Navigation.DesignSelectedItem = item != null && Navigation.Items.Contains(item) ? item : null;
            if (Navigation.DesignSelectedItem != null) Navigation.ActivateDesignItem(Navigation.DesignSelectedItem);
        }

        private void ComponentChanged(object sender, ComponentChangedEventArgs e)
        {
            object changed = e == null ? null : e.Component;
            if (changed == Navigation || changed is ZarpaNavigationItem || changed is ZarpaNavigationPage)
                Navigation.RefreshItems();
        }

        private void ShowContextMenu(Point location, ZarpaNavigationItem item)
        {
            if (contextMenu != null) contextMenu.Dispose();
            contextMenu = new ContextMenuStrip();
            if (item != null)
            {
                AddMenuItem("Mover arriba", delegate { Move(item, -1); });
                AddMenuItem("Mover abajo", delegate { Move(item, 1); });
                contextMenu.Items.Add(new ToolStripSeparator());
                AddMenuItem("Eliminar", delegate { Remove(item); });
                contextMenu.Items.Add(new ToolStripSeparator());
            }
            AddMenuItem("Añadir página", delegate { SelectComponent(AddItem(ZarpaNavigationItemKind.Item)); });
            AddMenuItem("Añadir cabecera", delegate { SelectComponent(AddItem(ZarpaNavigationItemKind.Header)); });
            AddMenuItem("Añadir separador", delegate { SelectComponent(AddItem(ZarpaNavigationItemKind.Separator)); });
            contextMenu.Show(Navigation, location);
        }

        private void AddMenuItem(string text, EventHandler handler)
        {
            ToolStripMenuItem menuItem = new ToolStripMenuItem(text);
            menuItem.Click += handler;
            contextMenu.Items.Add(menuItem);
        }

        private void SelectComponent(object value)
        {
            if (value == null || selectionService == null) return;
            IComponent component = value as IComponent;
            if (component != null && component != Navigation && component.Site == null) return;
            selectionService.SetSelectedComponents(new[] { value }, SelectionTypes.Replace);
            Navigation.DesignSelectedItem = value as ZarpaNavigationItem;
        }

        private ZarpaNavigationItem FindItem(ZarpaNavigationPage page)
        {
            foreach (ZarpaNavigationItem item in Navigation.Items)
                if (item.Page == page) return item;
            return null;
        }

        private bool IsPageUsed(ZarpaNavigationPage page)
        {
            foreach (ZarpaNavigationItem item in Navigation.Items)
                if (item.Page == page) return true;
            return false;
        }

        private void Mutate(string description, Action<IDesignerHost> mutation)
        {
            IDesignerHost host = GetService(typeof(IDesignerHost)) as IDesignerHost;
            if (host == null) return;
            PropertyDescriptor property = TypeDescriptor.GetProperties(Navigation)["Items"];
            using (DesignerTransaction transaction = host.CreateTransaction(description))
            {
                if (changeService != null) changeService.OnComponentChanging(Navigation, property);
                mutation(host);
                Navigation.RefreshItems();
                if (changeService != null) changeService.OnComponentChanged(Navigation, property, null, null);
                transaction.Commit();
            }
        }
    }

    internal sealed class ZarpaNavigationActionList : DesignerActionList
    {
        private readonly ZarpaNavigationViewDesigner designer;
        internal ZarpaNavigationActionList(ZarpaNavigationViewDesigner owner) : base(owner.Component) { designer = owner; }

        public bool Compact
        {
            get { return ((ZarpaNavigationView)Component).Compact; }
            set { designer.SetCompact(value); }
        }

        public void AddPage() { designer.AddItem(ZarpaNavigationItemKind.Item); }
        public void AddHeader() { designer.AddItem(ZarpaNavigationItemKind.Header); }
        public void AddSeparator() { designer.AddItem(ZarpaNavigationItemKind.Separator); }

        public override DesignerActionItemCollection GetSortedActionItems()
        {
            return new DesignerActionItemCollection
            {
                new DesignerActionHeaderItem("Estructura"),
                new DesignerActionMethodItem(this, "AddPage", "Añadir página", "Estructura", true),
                new DesignerActionMethodItem(this, "AddHeader", "Añadir cabecera", "Estructura", true),
                new DesignerActionMethodItem(this, "AddSeparator", "Añadir separador", "Estructura", true),
                new DesignerActionHeaderItem("Vista"),
                new DesignerActionPropertyItem("Compact", "Compacta", "Vista")
            };
        }
    }
}

using System;
using System.Collections;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.ComponentModel.Design.Serialization;
using System.Drawing;
using System.Drawing.Design;
using System.Windows.Forms;
using System.Windows.Forms.Design;

namespace ZarpaSuite.Controls.Design
{
    public abstract class ZarpaCollectionControlDesigner : ControlDesigner
    {
        private DesignerVerbCollection verbs;
        private DesignerActionListCollection actionLists;
        private ISelectionService selectionService;
        private IComponentChangeService changeService;
        private ContextMenuStrip contextMenu;

        protected abstract string CollectionPropertyName { get; }
        protected abstract IList Items { get; }
        protected abstract DesignerVerbCollection CreateVerbs();
        protected abstract DesignerActionList CreateActionList();
        protected abstract void AddContextMenuItems(ContextMenuStrip menu);

        protected virtual int HitTestItem(Point location) { return -1; }
        protected virtual void ActivateItem(object item) { }
        protected virtual object GetItemForSelection(object selected) { return selected != null && Items.Contains(selected) ? selected : null; }
        protected virtual void DestroyOwnedComponents(IDesignerHost host, IComponent item) { }
        protected virtual void AddItemContextMenuItems(ContextMenuStrip menu, IComponent item) { }
        protected virtual void RefreshOwner() { ComponentControl.Invalidate(); }

        protected Control ComponentControl { get { return (Control)Component; } }

        public override DesignerVerbCollection Verbs
        {
            get
            {
                if (verbs == null) verbs = CreateVerbs();
                return verbs;
            }
        }

        public override DesignerActionListCollection ActionLists
        {
            get
            {
                if (actionLists == null)
                    actionLists = new DesignerActionListCollection { CreateActionList() };
                return actionLists;
            }
        }

        public override void Initialize(IComponent component)
        {
            base.Initialize(component);
            ComponentControl.MouseUp += ComponentMouseUp;
            selectionService = GetService(typeof(ISelectionService)) as ISelectionService;
            changeService = GetService(typeof(IComponentChangeService)) as IComponentChangeService;
            if (selectionService != null) selectionService.SelectionChanged += SelectionChanged;
            if (changeService != null) changeService.ComponentChanged += ComponentChanged;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ComponentControl.MouseUp -= ComponentMouseUp;
                if (selectionService != null) selectionService.SelectionChanged -= SelectionChanged;
                if (changeService != null) changeService.ComponentChanged -= ComponentChanged;
                if (contextMenu != null) contextMenu.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override bool GetHitTest(Point point)
        {
            return ComponentControl.RectangleToScreen(ComponentControl.ClientRectangle).Contains(point);
        }

        internal IComponent AddItem(Type itemType, Action<IComponent> initialize)
        {
            IComponent result = null;
            Mutate("Añadir elemento", delegate(IDesignerHost host)
            {
                result = host.CreateComponent(itemType);
                if (initialize != null) initialize(result);
                Items.Add(result);
            });
            SelectComponent(result);
            return result;
        }

        internal void RemoveItem(IComponent item)
        {
            if (item == null || !Items.Contains(item)) return;
            Mutate("Eliminar elemento", delegate(IDesignerHost host)
            {
                Items.Remove(item);
                DestroyOwnedComponents(host, item);
                if (item.Site != null) host.DestroyComponent(item);
            });
            SelectComponent(Component);
        }

        internal void MoveItem(IComponent item, int offset)
        {
            if (item == null) return;
            int index = Items.IndexOf(item);
            int next = index + offset;
            if (index < 0 || next < 0 || next >= Items.Count) return;
            Mutate("Reordenar elementos", delegate(IDesignerHost host)
            {
                Items.RemoveAt(index);
                Items.Insert(next, item);
            });
            SelectComponent(item);
        }

        internal void EditCollection()
        {
            PropertyDescriptor property = TypeDescriptor.GetProperties(Component)[CollectionPropertyName];
            UITypeEditor editor = property == null ? null : property.GetEditor(typeof(UITypeEditor)) as UITypeEditor;
            if (editor == null) return;
            ZarpaDesignerTypeContext context = new ZarpaDesignerTypeContext(this, Component, property);
            editor.EditValue(context, new ZarpaDesignerServiceProvider(this), property.GetValue(Component));
            RefreshOwner();
        }

        internal void SetProperty(string propertyName, object value)
        {
            PropertyDescriptor property = TypeDescriptor.GetProperties(Component)[propertyName];
            if (property != null) property.SetValue(Component, value);
        }

        internal object GetDesignerService(Type serviceType)
        {
            return GetService(serviceType);
        }

        internal void NotifyCollectionChanged()
        {
            PropertyDescriptor property = TypeDescriptor.GetProperties(Component)[CollectionPropertyName];
            if (changeService != null)
                changeService.OnComponentChanged(Component, property, null, null);
            RefreshOwner();
        }

        private void ComponentMouseUp(object sender, MouseEventArgs e)
        {
            int index = HitTestItem(e.Location);
            IComponent item = index >= 0 && index < Items.Count ? Items[index] as IComponent : null;
            if (e.Button == MouseButtons.Left && item != null)
            {
                ActivateItem(item);
                SelectComponent(item);
            }
            else if (e.Button == MouseButtons.Right)
            {
                ActivateItem(item);
                SelectComponent(item ?? Component);
                ShowContextMenu(e.Location, item);
            }
        }

        private void SelectionChanged(object sender, EventArgs e)
        {
            object selected = selectionService == null ? null : selectionService.PrimarySelection;
            object item = GetItemForSelection(selected);
            if (item != null) ActivateItem(item);
            else ActivateItem(null);
        }

        private void ComponentChanged(object sender, ComponentChangedEventArgs e)
        {
            object changed = e == null ? null : e.Component;
            if (changed == Component || (changed != null && Items.Contains(changed))) RefreshOwner();
        }

        private void ShowContextMenu(Point location, IComponent item)
        {
            if (contextMenu != null) contextMenu.Dispose();
            contextMenu = new ContextMenuStrip();
            if (item != null)
            {
                AddItemContextMenuItems(contextMenu, item);
                if (contextMenu.Items.Count != 0) contextMenu.Items.Add(new ToolStripSeparator());
                int index = Items.IndexOf(item);
                ToolStripMenuItem moveLeft = AddMenuItem(contextMenu, "Mover a la izquierda", delegate { MoveItem(item, -1); });
                ToolStripMenuItem moveRight = AddMenuItem(contextMenu, "Mover a la derecha", delegate { MoveItem(item, 1); });
                moveLeft.Enabled = index > 0;
                moveRight.Enabled = index >= 0 && index < Items.Count - 1;
                contextMenu.Items.Add(new ToolStripSeparator());
                AddMenuItem(contextMenu, "Eliminar", delegate { RemoveItem(item); });
                contextMenu.Items.Add(new ToolStripSeparator());
            }
            AddContextMenuItems(contextMenu);
            contextMenu.Show(ComponentControl, location);
        }

        protected ToolStripMenuItem AddMenuItem(ContextMenuStrip menu, string text, EventHandler handler)
        {
            ToolStripMenuItem menuItem = new ToolStripMenuItem(text);
            menuItem.Click += handler;
            menu.Items.Add(menuItem);
            return menuItem;
        }

        protected ToolStripMenuItem AddCheckMenuItem(ContextMenuStrip menu, string text, bool isChecked, EventHandler handler)
        {
            ToolStripMenuItem menuItem = AddMenuItem(menu, text, handler);
            menuItem.Checked = isChecked;
            return menuItem;
        }

        protected void SetItemProperty(IComponent item, string propertyName, object value, string description)
        {
            if (item == null) return;
            PropertyDescriptor property = TypeDescriptor.GetProperties(item)[propertyName];
            if (property == null || property.IsReadOnly) return;
            IDesignerHost host = GetService(typeof(IDesignerHost)) as IDesignerHost;
            using (DesignerTransaction transaction = host == null ? null : host.CreateTransaction(description))
            {
                if (changeService != null) changeService.OnComponentChanging(item, property);
                property.SetValue(item, value);
                if (changeService != null) changeService.OnComponentChanged(item, property, null, value);
                if (transaction != null) transaction.Commit();
            }
            ActivateItem(item);
            SelectComponent(item);
            RefreshOwner();
        }

        protected void ChooseIcon(IComponent item)
        {
            if (item == null) return;
            PropertyDescriptor property = TypeDescriptor.GetProperties(item)["IconKey"];
            if (property == null) return;
            string current = property.GetValue(item) as string;
            using (FluentIconPickerForm picker = new FluentIconPickerForm(current))
            {
                if (picker.ShowDialog(ComponentControl) == DialogResult.OK)
                    SetItemProperty(item, "IconKey", picker.SelectedKey, "Cambiar icono");
            }
        }

        protected void SelectComponent(object value)
        {
            if (value == null || selectionService == null) return;
            IComponent component = value as IComponent;
            if (component != null && component != Component && !EnsureDesignSite(component)) return;
            selectionService.SetSelectedComponents(new[] { value }, SelectionTypes.Replace);
        }

        private bool EnsureDesignSite(IComponent component)
        {
            if (component.Site != null) return true;
            IDesignerHost host = GetService(typeof(IDesignerHost)) as IDesignerHost;
            if (host == null || host.Container == null) return false;
            INameCreationService names = GetService(typeof(INameCreationService)) as INameCreationService;
            string name = names == null ? CreateFallbackName(host.Container, component.GetType()) :
                names.CreateName(host.Container, component.GetType());
            try
            {
                if (string.IsNullOrEmpty(name)) host.Container.Add(component);
                else host.Container.Add(component, name);
            }
            catch (ArgumentException) { return component.Site != null; }
            catch (InvalidOperationException) { return component.Site != null; }
            return component.Site != null;
        }

        private static string CreateFallbackName(IContainer container, Type componentType)
        {
            string typeName = componentType.Name;
            string prefix = char.ToLowerInvariant(typeName[0]) + typeName.Substring(1);
            int suffix = 1;
            while (container.Components[prefix + suffix] != null) suffix++;
            return prefix + suffix;
        }

        protected void Mutate(string description, Action<IDesignerHost> mutation)
        {
            IDesignerHost host = GetService(typeof(IDesignerHost)) as IDesignerHost;
            if (host == null) return;
            PropertyDescriptor property = TypeDescriptor.GetProperties(Component)[CollectionPropertyName];
            using (DesignerTransaction transaction = host.CreateTransaction(description))
            {
                if (changeService != null) changeService.OnComponentChanging(Component, property);
                mutation(host);
                RefreshOwner();
                if (changeService != null)
                    changeService.OnComponentChanged(Component, property, null, null);
                transaction.Commit();
            }
        }
    }

    public sealed class ZarpaCommandBarDesigner : ZarpaCollectionControlDesigner
    {
        private ZarpaCommandBar CommandBar { get { return (ZarpaCommandBar)Component; } }
        protected override string CollectionPropertyName { get { return "Items"; } }
        protected override IList Items { get { return (IList)CommandBar.Items; } }
        protected override int HitTestItem(Point location) { return CommandBar.DesignHitTest(location); }
        protected override void ActivateItem(object item) { CommandBar.ActivateDesignItem(item as ZarpaCommandItem); }

        protected override DesignerVerbCollection CreateVerbs()
        {
            return new DesignerVerbCollection
            {
                new DesignerVerb("Añadir botón", delegate { AddButton(); }),
                new DesignerVerb("Añadir botón toggle", delegate { AddToggle(); }),
                new DesignerVerb("Añadir separador", delegate { AddSeparator(); }),
                new DesignerVerb("Editar elementos...", delegate { EditCollection(); })
            };
        }

        protected override DesignerActionList CreateActionList()
        {
            return new ZarpaCommandBarActionList(this);
        }

        protected override void AddContextMenuItems(ContextMenuStrip menu)
        {
            AddMenuItem(menu, "Añadir botón", delegate { AddButton(); });
            AddMenuItem(menu, "Añadir botón toggle", delegate { AddToggle(); });
            AddMenuItem(menu, "Añadir separador", delegate { AddSeparator(); });
            menu.Items.Add(new ToolStripSeparator());
            AddCheckMenuItem(menu, "Mostrar texto", CommandBar.ShowText, delegate { SetProperty("ShowText", !CommandBar.ShowText); });
            AddMenuItem(menu, "Editar elementos...", delegate { EditCollection(); });
        }

        protected override void AddItemContextMenuItems(ContextMenuStrip menu, IComponent component)
        {
            ZarpaCommandItem item = component as ZarpaCommandItem;
            if (item == null) return;
            if (item.Kind != ZarpaCommandItemKind.Separator)
                AddMenuItem(menu, "Elegir icono...", delegate { ChooseIcon(item); });

            ToolStripMenuItem kindMenu = new ToolStripMenuItem("Tipo");
            AddKindItem(kindMenu, "Botón", item, ZarpaCommandItemKind.Button);
            AddKindItem(kindMenu, "Botón toggle", item, ZarpaCommandItemKind.Toggle);
            AddKindItem(kindMenu, "Separador", item, ZarpaCommandItemKind.Separator);
            menu.Items.Add(kindMenu);
            if (item.Kind == ZarpaCommandItemKind.Toggle)
                AddCheckMenuItem(menu, "Marcado", item.Checked, delegate { SetItemProperty(item, "Checked", !item.Checked, "Cambiar estado del comando"); });
            AddCheckMenuItem(menu, "Habilitado", item.Enabled, delegate { SetItemProperty(item, "Enabled", !item.Enabled, "Cambiar disponibilidad del comando"); });
            AddCheckMenuItem(menu, "Visible", item.Visible, delegate { SetItemProperty(item, "Visible", !item.Visible, "Cambiar visibilidad del comando"); });
        }

        private void AddKindItem(ToolStripMenuItem menu, string text, ZarpaCommandItem item, ZarpaCommandItemKind kind)
        {
            ToolStripMenuItem option = new ToolStripMenuItem(text) { Checked = item.Kind == kind };
            option.Click += delegate { SetItemProperty(item, "Kind", kind, "Cambiar tipo de comando"); };
            menu.DropDownItems.Add(option);
        }

        internal ZarpaCommandItem AddButton()
        {
            return (ZarpaCommandItem)AddItem(typeof(ZarpaCommandItem), delegate(IComponent component)
            {
                ((ZarpaCommandItem)component).Text = "Nuevo comando";
            });
        }

        internal ZarpaCommandItem AddToggle()
        {
            return (ZarpaCommandItem)AddItem(typeof(ZarpaCommandItem), delegate(IComponent component)
            {
                ZarpaCommandItem item = (ZarpaCommandItem)component;
                item.Text = "Nuevo toggle";
                item.Kind = ZarpaCommandItemKind.Toggle;
            });
        }

        internal ZarpaCommandItem AddSeparator()
        {
            return (ZarpaCommandItem)AddItem(typeof(ZarpaCommandItem), delegate(IComponent component)
            {
                ((ZarpaCommandItem)component).Kind = ZarpaCommandItemKind.Separator;
            });
        }
    }

    public sealed class ZarpaBreadcrumbDesigner : ZarpaCollectionControlDesigner
    {
        private ZarpaBreadcrumb Breadcrumb { get { return (ZarpaBreadcrumb)Component; } }
        protected override string CollectionPropertyName { get { return "Items"; } }
        protected override IList Items { get { return (IList)Breadcrumb.Items; } }
        protected override int HitTestItem(Point location) { return Breadcrumb.DesignHitTest(location); }

        protected override DesignerVerbCollection CreateVerbs()
        {
            return new DesignerVerbCollection
            {
                new DesignerVerb("Añadir nivel", delegate { AddLevel(); }),
                new DesignerVerb("Editar niveles...", delegate { EditCollection(); })
            };
        }

        protected override DesignerActionList CreateActionList()
        {
            return new ZarpaBreadcrumbActionList(this);
        }

        protected override void AddContextMenuItems(ContextMenuStrip menu)
        {
            AddMenuItem(menu, "Añadir nivel", delegate { AddLevel(); });
            AddMenuItem(menu, "Editar niveles...", delegate { EditCollection(); });
        }

        internal ZarpaBreadcrumbItem AddLevel()
        {
            return (ZarpaBreadcrumbItem)AddItem(typeof(ZarpaBreadcrumbItem), delegate(IComponent component)
            {
                ((ZarpaBreadcrumbItem)component).Text = "Nuevo nivel";
            });
        }
    }

    public sealed class ZarpaDocumentTabsDesigner : ZarpaCollectionControlDesigner
    {
        private ZarpaDocumentTabs DocumentTabs { get { return (ZarpaDocumentTabs)Component; } }
        protected override string CollectionPropertyName { get { return "Tabs"; } }
        protected override IList Items { get { return (IList)DocumentTabs.Tabs; } }
        protected override int HitTestItem(Point location) { return DocumentTabs.DesignHitTest(location); }
        protected override void ActivateItem(object item) { DocumentTabs.ActivateDesignTab(item as ZarpaDocumentTab); }
        protected override bool GetHitTest(Point point) { return DocumentTabs.DesignHeaderContains(DocumentTabs.PointToClient(point)); }
        protected override object GetItemForSelection(object selected)
        {
            object item = base.GetItemForSelection(selected);
            if (item != null) return item;
            return DocumentTabs.FindTabForControl(selected as Control);
        }

        protected override DesignerVerbCollection CreateVerbs()
        {
            return new DesignerVerbCollection
            {
                new DesignerVerb("Añadir documento", delegate { AddDocument(); }),
                new DesignerVerb("Editar pestañas...", delegate { EditCollection(); })
            };
        }

        protected override DesignerActionList CreateActionList()
        {
            return new ZarpaDocumentTabsActionList(this);
        }

        protected override void AddContextMenuItems(ContextMenuStrip menu)
        {
            AddMenuItem(menu, "Añadir documento", delegate { AddDocument(); });
            AddMenuItem(menu, "Editar pestañas...", delegate { EditCollection(); });
        }

        protected override void AddItemContextMenuItems(ContextMenuStrip menu, IComponent component)
        {
            ZarpaDocumentTab tab = component as ZarpaDocumentTab;
            if (tab == null) return;
            AddMenuItem(menu, "Elegir icono...", delegate { ChooseIcon(tab); });
            AddMenuItem(menu, "Seleccionar área de contenido", delegate { ActivateItem(tab); SelectComponent(tab); });
            AddCheckMenuItem(menu, "Se puede cerrar", tab.CanClose, delegate { SetItemProperty(tab, "CanClose", !tab.CanClose, "Cambiar cierre de pestaña"); });
            AddCheckMenuItem(menu, "Marcar como modificado", tab.IsDirty, delegate { SetItemProperty(tab, "IsDirty", !tab.IsDirty, "Cambiar estado de pestaña"); });
        }

        internal ZarpaDocumentTab AddDocument()
        {
            ZarpaDocumentTab result = null;
            Mutate("Añadir pestaña de documento", delegate(IDesignerHost host)
            {
                result = (ZarpaDocumentTab)host.CreateComponent(typeof(ZarpaDocumentTab));
                result.Text = "Nuevo documento";
                DocumentTabs.Tabs.Add(result);
            });
            ActivateItem(result);
            SelectComponent(result);
            return result;
        }
    }

    internal sealed class ZarpaCommandBarActionList : DesignerActionList
    {
        private readonly ZarpaCommandBarDesigner designer;
        internal ZarpaCommandBarActionList(ZarpaCommandBarDesigner owner) : base(owner.Component) { designer = owner; }
        public bool ShowText { get { return ((ZarpaCommandBar)Component).ShowText; } set { designer.SetProperty("ShowText", value); } }
        public void AddButton() { designer.AddButton(); }
        public void AddToggle() { designer.AddToggle(); }
        public void AddSeparator() { designer.AddSeparator(); }
        public void EditItems() { designer.EditCollection(); }
        public override DesignerActionItemCollection GetSortedActionItems()
        {
            return new DesignerActionItemCollection
            {
                new DesignerActionHeaderItem("Estructura"),
                new DesignerActionMethodItem(this, "AddButton", "Añadir botón", "Estructura", true),
                new DesignerActionMethodItem(this, "AddToggle", "Añadir botón toggle", "Estructura", true),
                new DesignerActionMethodItem(this, "AddSeparator", "Añadir separador", "Estructura", true),
                new DesignerActionMethodItem(this, "EditItems", "Editar elementos...", "Estructura", true),
                new DesignerActionHeaderItem("Vista"),
                new DesignerActionPropertyItem("ShowText", "Mostrar texto", "Vista")
            };
        }
    }

    internal sealed class ZarpaBreadcrumbActionList : DesignerActionList
    {
        private readonly ZarpaBreadcrumbDesigner designer;
        internal ZarpaBreadcrumbActionList(ZarpaBreadcrumbDesigner owner) : base(owner.Component) { designer = owner; }
        public void AddLevel() { designer.AddLevel(); }
        public void EditItems() { designer.EditCollection(); }
        public override DesignerActionItemCollection GetSortedActionItems()
        {
            return new DesignerActionItemCollection
            {
                new DesignerActionHeaderItem("Estructura"),
                new DesignerActionMethodItem(this, "AddLevel", "Añadir nivel", "Estructura", true),
                new DesignerActionMethodItem(this, "EditItems", "Editar niveles...", "Estructura", true)
            };
        }
    }

    internal sealed class ZarpaDocumentTabsActionList : DesignerActionList
    {
        private readonly ZarpaDocumentTabsDesigner designer;
        internal ZarpaDocumentTabsActionList(ZarpaDocumentTabsDesigner owner) : base(owner.Component) { designer = owner; }
        public void AddDocument() { designer.AddDocument(); }
        public void EditTabs() { designer.EditCollection(); }
        public override DesignerActionItemCollection GetSortedActionItems()
        {
            return new DesignerActionItemCollection
            {
                new DesignerActionHeaderItem("Estructura"),
                new DesignerActionMethodItem(this, "AddDocument", "Añadir documento", "Estructura", true),
                new DesignerActionMethodItem(this, "EditTabs", "Editar pestañas...", "Estructura", true)
            };
        }
    }

    internal sealed class ZarpaDesignerServiceProvider : IServiceProvider
    {
        private readonly ZarpaCollectionControlDesigner designer;
        internal ZarpaDesignerServiceProvider(ZarpaCollectionControlDesigner owner) { designer = owner; }
        public object GetService(Type serviceType) { return designer.GetDesignerService(serviceType); }
    }

    internal sealed class ZarpaDesignerTypeContext : ITypeDescriptorContext
    {
        private readonly ZarpaCollectionControlDesigner designer;
        private readonly object instance;
        private readonly PropertyDescriptor property;
        internal ZarpaDesignerTypeContext(ZarpaCollectionControlDesigner owner, object value, PropertyDescriptor descriptor)
        {
            designer = owner;
            instance = value;
            property = descriptor;
        }
        public IContainer Container { get { IComponent component = instance as IComponent; return component == null || component.Site == null ? null : component.Site.Container; } }
        public object Instance { get { return instance; } }
        public PropertyDescriptor PropertyDescriptor { get { return property; } }
        public object GetService(Type serviceType) { return designer.GetDesignerService(serviceType); }
        public void OnComponentChanged() { designer.NotifyCollectionChanged(); }
        public bool OnComponentChanging()
        {
            IComponentChangeService change = GetService(typeof(IComponentChangeService)) as IComponentChangeService;
            if (change == null) return true;
            try { change.OnComponentChanging(instance, property); return true; }
            catch (CheckoutException) { return false; }
        }
    }
}

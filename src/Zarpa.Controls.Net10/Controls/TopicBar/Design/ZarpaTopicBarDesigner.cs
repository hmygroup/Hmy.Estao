using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Drawing;
using System.Windows.Forms;
using System.Windows.Forms.Design;

namespace ZarpaSuite.Controls.Design
{
    public sealed class ZarpaTopicBarDesigner : ControlDesigner
    {
        private DesignerVerbCollection verbs;
        private DesignerActionListCollection actionLists;
        private ISelectionService selectionService;
        private IComponentChangeService changeService;
        private ContextMenuStrip contextMenu;

        private ZarpaTopicBar TopicBar { get { return (ZarpaTopicBar)Component; } }

        public override DesignerVerbCollection Verbs
        {
            get
            {
                if (verbs == null)
                    verbs = new DesignerVerbCollection
                    {
                        new DesignerVerb("Editor de estructura...", delegate { ShowStructureEditor(); }),
                        new DesignerVerb("Añadir página", delegate { SelectComponent(AddPage()); }),
                        new DesignerVerb("Añadir enlace", delegate { SelectComponent(AddLink(null, ZarpaTopicLinkKind.Link)); }),
                        new DesignerVerb("Añadir separador", delegate { SelectComponent(AddLink(null, ZarpaTopicLinkKind.Separator)); }),
                        new DesignerVerb("Expandir todo", delegate { Change("Expandir páginas", TopicBar.ExpandAll); }),
                        new DesignerVerb("Contraer todo", delegate { Change("Contraer páginas", TopicBar.CollapseAll); })
                    };
                return verbs;
            }
        }

        public override DesignerActionListCollection ActionLists
        {
            get
            {
                if (actionLists == null)
                    actionLists = new DesignerActionListCollection { new ZarpaTopicBarActionList(this) };
                return actionLists;
            }
        }

        public override void Initialize(IComponent component)
        {
            base.Initialize(component);
            TopicBar.MouseUp += TopicBarMouseUp;
            selectionService = GetService(typeof(ISelectionService)) as ISelectionService;
            changeService = GetService(typeof(IComponentChangeService)) as IComponentChangeService;
            if (selectionService != null) selectionService.SelectionChanged += SelectionChanged;
            if (changeService != null) changeService.ComponentChanged += ComponentChanged;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                TopicBar.MouseUp -= TopicBarMouseUp;
                if (selectionService != null) selectionService.SelectionChanged -= SelectionChanged;
                if (changeService != null) changeService.ComponentChanged -= ComponentChanged;
                if (contextMenu != null) contextMenu.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override bool GetHitTest(Point point)
        {
            return TopicBar.RectangleToScreen(TopicBar.ClientRectangle).Contains(point);
        }

        internal void ShowStructureEditor()
        {
            using (ZarpaTopicBarEditorDialog dialog = new ZarpaTopicBarEditorDialog(this, TopicBar))
                dialog.ShowDialog();
        }

        internal ZarpaTopicPage AddPage()
        {
            ZarpaTopicPage result = null;
            Mutate("Añadir página de temas", delegate(IDesignerHost host)
            {
                result = (ZarpaTopicPage)host.CreateComponent(typeof(ZarpaTopicPage));
                result.Text = "Nueva página";
                TopicBar.Pages.Add(result);
                TopicBar.DesignSelectedObject = result;
            });
            return result;
        }

        internal ZarpaTopicLink AddLink(ZarpaTopicPage page, ZarpaTopicLinkKind kind)
        {
            ZarpaTopicLink result = null;
            Mutate(kind == ZarpaTopicLinkKind.Separator ? "Añadir separador" : "Añadir enlace",
                delegate(IDesignerHost host)
                {
                    ZarpaTopicPage target = page ?? CurrentPage;
                    if (target == null)
                    {
                        target = (ZarpaTopicPage)host.CreateComponent(typeof(ZarpaTopicPage));
                        target.Text = "Nueva página";
                        TopicBar.Pages.Add(target);
                    }
                    target.Collapsed = false;
                    result = (ZarpaTopicLink)host.CreateComponent(typeof(ZarpaTopicLink));
                    result.Kind = kind;
                    result.Text = kind == ZarpaTopicLinkKind.Separator ? string.Empty : "Nuevo enlace";
                    target.Links.Add(result);
                    TopicBar.DesignSelectedObject = result;
                });
            return result;
        }

        internal void Remove(object value)
        {
            if (value == null) return;
            Mutate("Eliminar elemento del TopicBar", delegate(IDesignerHost host)
            {
                ZarpaTopicLink link = value as ZarpaTopicLink;
                ZarpaTopicPage page = value as ZarpaTopicPage;
                if (link != null)
                {
                    ZarpaTopicPage owner = link.OwnerPage;
                    if (owner != null) owner.Links.Remove(link);
                    if (link.Site != null) host.DestroyComponent(link);
                }
                else if (page != null)
                {
                    List<ZarpaTopicLink> links = new List<ZarpaTopicLink>();
                    foreach (ZarpaTopicLink child in page.Links) links.Add(child);
                    TopicBar.Pages.Remove(page);
                    foreach (ZarpaTopicLink child in links)
                        if (child.Site != null) host.DestroyComponent(child);
                    if (page.Site != null) host.DestroyComponent(page);
                }
                TopicBar.DesignSelectedObject = null;
            });
        }

        internal void Move(object value, int offset)
        {
            if (value == null) return;
            Mutate("Reordenar TopicBar", delegate(IDesignerHost host)
            {
                ZarpaTopicPage page = value as ZarpaTopicPage;
                ZarpaTopicLink link = value as ZarpaTopicLink;
                if (page != null)
                {
                    int index = TopicBar.Pages.IndexOf(page);
                    int next = index + offset;
                    if (index >= 0 && next >= 0 && next < TopicBar.Pages.Count)
                    {
                        TopicBar.Pages.RemoveAt(index);
                        TopicBar.Pages.Insert(next, page);
                    }
                }
                else if (link != null && link.OwnerPage != null)
                {
                    ZarpaTopicLinkCollection links = link.OwnerPage.Links;
                    int index = links.IndexOf(link);
                    int next = index + offset;
                    if (index >= 0 && next >= 0 && next < links.Count)
                    {
                        links.RemoveAt(index);
                        links.Insert(next, link);
                    }
                }
            });
            TopicBar.DesignSelectedObject = value;
        }

        internal void NotifyChanged()
        {
            if (changeService != null)
                changeService.OnComponentChanged(TopicBar, TypeDescriptor.GetProperties(TopicBar)["Pages"], null, null);
            TopicBar.RefreshPages();
        }

        internal void Change(string description, Action action)
        {
            IDesignerHost host = GetService(typeof(IDesignerHost)) as IDesignerHost;
            using (DesignerTransaction transaction = host == null ? null : host.CreateTransaction(description))
            {
                if (changeService != null) changeService.OnComponentChanging(TopicBar, null);
                action();
                if (changeService != null) changeService.OnComponentChanged(TopicBar, null, null, null);
                if (transaction != null) transaction.Commit();
            }
        }

        internal void SetProperty(string propertyName, object value)
        {
            PropertyDescriptor property = TypeDescriptor.GetProperties(TopicBar)[propertyName];
            if (property != null) property.SetValue(TopicBar, value);
        }

        internal void SelectComponent(object value)
        {
            if (value == null || selectionService == null) return;
            IComponent component = value as IComponent;
            if (component != TopicBar && (component == null || component.Site == null)) return;
            selectionService.SetSelectedComponents(new[] { value }, SelectionTypes.Replace);
            TopicBar.DesignSelectedObject = value == TopicBar ? null : value;
        }

        private ZarpaTopicPage CurrentPage
        {
            get
            {
                ZarpaTopicPage page = TopicBar.DesignSelectedObject as ZarpaTopicPage;
                ZarpaTopicLink link = TopicBar.DesignSelectedObject as ZarpaTopicLink;
                if (page == null && link != null) page = link.OwnerPage;
                if (page == null && TopicBar.Pages.Count != 0) page = TopicBar.Pages[TopicBar.Pages.Count - 1];
                return page;
            }
        }

        private void TopicBarMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (TopicBar.HitTestDesignAddPage(e.Location))
                {
                    SelectComponent(AddPage());
                    return;
                }
                ZarpaTopicPage addLinkPage = TopicBar.HitTestDesignAddLink(e.Location);
                if (addLinkPage != null)
                {
                    SelectComponent(AddLink(addLinkPage, ZarpaTopicLinkKind.Link));
                    return;
                }
                object target = TopicBar.HitTestDesignElement(e.Location);
                SelectComponent(target ?? (object)TopicBar);
            }
            else if (e.Button == MouseButtons.Right)
                ShowContextMenu(e.Location, TopicBar.HitTestDesignElement(e.Location));
        }

        private void SelectionChanged(object sender, EventArgs e)
        {
            object selected = selectionService == null ? null : selectionService.PrimarySelection;
            TopicBar.DesignSelectedObject = selected is ZarpaTopicPage || selected is ZarpaTopicLink ? selected : null;
        }

        private void ComponentChanged(object sender, ComponentChangedEventArgs e)
        {
            object changed = e == null ? null : e.Component;
            if (changed == TopicBar || changed is ZarpaTopicPage || changed is ZarpaTopicLink)
                TopicBar.RefreshPages();
        }

        private void ShowContextMenu(Point location, object target)
        {
            if (contextMenu != null) contextMenu.Dispose();
            contextMenu = new ContextMenuStrip();
            ZarpaTopicPage page = target as ZarpaTopicPage;
            ZarpaTopicLink link = target as ZarpaTopicLink;
            if (page != null)
            {
                AddMenuItem("Añadir enlace", delegate { SelectComponent(AddLink(page, ZarpaTopicLinkKind.Link)); });
                AddMenuItem("Añadir separador", delegate { SelectComponent(AddLink(page, ZarpaTopicLinkKind.Separator)); });
                AddMenuItem("Elegir icono...", delegate { ChooseIcon(page); });
                AddMenuItem(page.Collapsed ? "Expandir" : "Contraer", delegate
                {
                    Change("Cambiar página", delegate { page.Collapsed = !page.Collapsed; });
                    SelectComponent(page);
                });
            }
            else if (link != null)
            {
                if (link.Kind == ZarpaTopicLinkKind.Link)
                    AddMenuItem("Elegir icono...", delegate { ChooseIcon(link); });
            }
            else
            {
                AddMenuItem("Añadir página", delegate { SelectComponent(AddPage()); });
                ToolStripMenuItem densityMenu = new ToolStripMenuItem("Densidad");
                foreach (ZarpaTopicBarDensity value in Enum.GetValues(typeof(ZarpaTopicBarDensity)))
                {
                    if (value == ZarpaTopicBarDensity.Custom) continue;
                    ZarpaTopicBarDensity current = value;
                    ToolStripMenuItem option = new ToolStripMenuItem(value.ToString())
                    {
                        Checked = TopicBar.Density == value
                    };
                    option.Click += delegate { SetProperty("Density", current); };
                    densityMenu.DropDownItems.Add(option);
                }
                contextMenu.Items.Add(densityMenu);
            }

            if (target != null)
            {
                contextMenu.Items.Add(new ToolStripSeparator());
                AddMenuItem("Mover arriba", delegate { Move(target, -1); SelectComponent(target); });
                AddMenuItem("Mover abajo", delegate { Move(target, 1); SelectComponent(target); });
                AddMenuItem("Eliminar", delegate { Remove(target); SelectComponent(TopicBar); });
            }
            contextMenu.Items.Add(new ToolStripSeparator());
            AddMenuItem("Editor de estructura...", delegate { ShowStructureEditor(); });
            contextMenu.Show(TopicBar, location);
        }

        private void ChooseIcon(object value)
        {
            ZarpaTopicPage page = value as ZarpaTopicPage;
            ZarpaTopicLink link = value as ZarpaTopicLink;
            string current = page != null ? page.IconKey : link == null ? string.Empty : link.IconKey;
            using (FluentIconPickerForm picker = new FluentIconPickerForm(current))
            {
                if (picker.ShowDialog() != DialogResult.OK) return;
                Change("Cambiar icono", delegate
                {
                    if (page != null) page.IconKey = picker.SelectedKey;
                    else if (link != null) link.IconKey = picker.SelectedKey;
                });
            }
            SelectComponent(value);
        }

        private void AddMenuItem(string text, EventHandler handler)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Click += handler;
            contextMenu.Items.Add(item);
        }

        private void Mutate(string description, Action<IDesignerHost> mutation)
        {
            IDesignerHost host = GetService(typeof(IDesignerHost)) as IDesignerHost;
            if (host == null) return;
            PropertyDescriptor property = TypeDescriptor.GetProperties(TopicBar)["Pages"];
            using (DesignerTransaction transaction = host.CreateTransaction(description))
            {
                if (changeService != null) changeService.OnComponentChanging(TopicBar, property);
                mutation(host);
                TopicBar.RefreshPages();
                if (changeService != null) changeService.OnComponentChanged(TopicBar, property, null, null);
                transaction.Commit();
            }
        }
    }

    internal sealed class ZarpaTopicBarActionList : DesignerActionList
    {
        private readonly ZarpaTopicBarDesigner designer;

        internal ZarpaTopicBarActionList(ZarpaTopicBarDesigner owner) : base(owner.Component)
        {
            designer = owner;
        }

        public ZarpaThemePreset ThemePreset
        {
            get { return ((ZarpaTopicBar)Component).ThemePreset; }
            set { designer.SetProperty("ThemePreset", value); }
        }

        public ZarpaTopicBarDensity Density
        {
            get { return ((ZarpaTopicBar)Component).Density; }
            set { designer.SetProperty("Density", value); }
        }

        public bool AllowMultipleExpanded
        {
            get { return ((ZarpaTopicBar)Component).AllowMultipleExpanded; }
            set { designer.SetProperty("AllowMultipleExpanded", value); }
        }

        public bool ShowToolTips
        {
            get { return ((ZarpaTopicBar)Component).ShowToolTips; }
            set { designer.SetProperty("ShowToolTips", value); }
        }

        public void EditStructure() { designer.ShowStructureEditor(); }
        public void AddPage() { designer.SelectComponent(designer.AddPage()); }
        public void AddLink() { designer.SelectComponent(designer.AddLink(null, ZarpaTopicLinkKind.Link)); }
        public void AddSeparator() { designer.SelectComponent(designer.AddLink(null, ZarpaTopicLinkKind.Separator)); }
        public void ExpandAll() { designer.Change("Expandir páginas", ((ZarpaTopicBar)Component).ExpandAll); }
        public void CollapseAll() { designer.Change("Contraer páginas", ((ZarpaTopicBar)Component).CollapseAll); }

        public override DesignerActionItemCollection GetSortedActionItems()
        {
            return new DesignerActionItemCollection
            {
                new DesignerActionHeaderItem("Editor del TopicBar"),
                new DesignerActionMethodItem(this, "EditStructure", "Editor de estructura...", true),
                new DesignerActionHeaderItem("Añadir"),
                new DesignerActionMethodItem(this, "AddPage", "Página"),
                new DesignerActionMethodItem(this, "AddLink", "Enlace"),
                new DesignerActionMethodItem(this, "AddSeparator", "Separador"),
                new DesignerActionHeaderItem("Apariencia y comportamiento"),
                new DesignerActionPropertyItem("ThemePreset", "Tema"),
                new DesignerActionPropertyItem("Density", "Densidad"),
                new DesignerActionPropertyItem("AllowMultipleExpanded", "Varias páginas abiertas"),
                new DesignerActionPropertyItem("ShowToolTips", "Mostrar tooltips"),
                new DesignerActionMethodItem(this, "ExpandAll", "Expandir todo"),
                new DesignerActionMethodItem(this, "CollapseAll", "Contraer todo")
            };
        }
    }
}

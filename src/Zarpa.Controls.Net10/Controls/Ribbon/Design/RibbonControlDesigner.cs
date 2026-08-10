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
    public sealed class RibbonControlDesigner : ControlDesigner
    {
        private DesignerVerbCollection verbs;
        private DesignerActionListCollection actionLists;
        private ISelectionService selectionService;
        private IComponentChangeService componentChangeService;
        private ContextMenuStrip contextMenu;
        private RibbonItem dragItem;
        private RibbonGroup dragGroup;
        private Point dragStart;
        private Point dragOriginalLocation;
        private Size dragOriginalSize;
        private bool dragResize;
        private bool dragChanged;
        private DesignerTransaction dragTransaction;

        private RibbonControl Ribbon
        {
            get { return (RibbonControl)Component; }
        }

        public override DesignerVerbCollection Verbs
        {
            get
            {
                if (verbs == null)
                {
                    verbs = new DesignerVerbCollection
                    {
                        new DesignerVerb("Editor de estructura...", delegate { ShowRibbonEditor(); }),
                        new DesignerVerb("Añadir pestaña", delegate { AddTab(); }),
                        new DesignerVerb("Añadir grupo", delegate { AddGroup(); }),
                        new DesignerVerb("Añadir botón", delegate { AddItem(typeof(RibbonButton)); }),
                        new DesignerVerb("Añadir botón desplegable", delegate { AddItem(typeof(RibbonDropDownButton)); }),
                        new DesignerVerb("Añadir botón split", delegate { AddItem(typeof(RibbonSplitButton)); }),
                        new DesignerVerb("Añadir botón toggle", delegate { AddItem(typeof(RibbonToggleButton)); }),
                        new DesignerVerb("Añadir separador", delegate { AddItem(typeof(RibbonSeparator)); }),
                        new DesignerVerb("Añadir TextBox", delegate { AddItem(typeof(RibbonTextBox)); }),
                        new DesignerVerb("Añadir ComboBox", delegate { AddItem(typeof(RibbonComboBox)); }),
                        new DesignerVerb("Añadir selector de fecha", delegate { AddItem(typeof(RibbonDatePicker)); }),
                        new DesignerVerb("Añadir CheckBox", delegate { AddItem(typeof(RibbonCheckBox)); }),
                        new DesignerVerb("Añadir control numérico", delegate { AddItem(typeof(RibbonNumericUpDown)); }),
                        new DesignerVerb("Añadir etiqueta", delegate { AddItem(typeof(RibbonLabel)); })
                    };
                }
                return verbs;
            }
        }

        public override void Initialize(IComponent component)
        {
            base.Initialize(component);
            Ribbon.MouseUp += RibbonMouseUp;
            Ribbon.MouseDown += RibbonMouseDown;
            Ribbon.MouseMove += RibbonMouseMove;
            selectionService = GetService(typeof(ISelectionService)) as ISelectionService;
            if (selectionService != null)
                selectionService.SelectionChanged += SelectionServiceSelectionChanged;
            componentChangeService = GetService(typeof(IComponentChangeService)) as IComponentChangeService;
            if (componentChangeService != null)
                componentChangeService.ComponentChanged += ComponentChangeServiceComponentChanged;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Ribbon.MouseUp -= RibbonMouseUp;
                Ribbon.MouseDown -= RibbonMouseDown;
                Ribbon.MouseMove -= RibbonMouseMove;
                if (selectionService != null)
                    selectionService.SelectionChanged -= SelectionServiceSelectionChanged;
                if (componentChangeService != null)
                    componentChangeService.ComponentChanged -= ComponentChangeServiceComponentChanged;
                if (contextMenu != null)
                    contextMenu.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override bool GetHitTest(Point point)
        {
            return Ribbon.RectangleToScreen(Ribbon.ClientRectangle).Contains(point);
        }

        public override DesignerActionListCollection ActionLists
        {
            get
            {
                if (actionLists == null)
                {
                    actionLists = new DesignerActionListCollection
                    {
                        new RibbonDesignerActionList(this)
                    };
                }
                return actionLists;
            }
        }

        public override void DoDefaultAction()
        {
            Ribbon.Invalidate();
        }

        internal void ShowRibbonEditor()
        {
            using (RibbonEditorDialog dialog = new RibbonEditorDialog(this, Ribbon))
                dialog.ShowDialog();
        }

        internal RibbonTab AddTab()
        {
            RibbonTab result = null;
            Mutate("Añadir pestaña", delegate(IDesignerHost host)
            {
                result = (RibbonTab)host.CreateComponent(typeof(RibbonTab));
                result.Text = "Nueva pestaña";
                Ribbon.Tabs.Add(result);
                Ribbon.SelectedTabIndex = Ribbon.Tabs.Count - 1;
            });
            return result;
        }

        internal RibbonGroup AddGroup()
        {
            return AddGroup(null);
        }

        internal RibbonGroup AddGroup(RibbonTab targetTab)
        {
            RibbonGroup result = null;
            Mutate("Añadir grupo", delegate(IDesignerHost host)
            {
                RibbonTab tab = targetTab ?? CurrentTab;
                if (tab == null)
                {
                    tab = (RibbonTab)host.CreateComponent(typeof(RibbonTab));
                    tab.Text = "Nueva pestaña";
                    Ribbon.Tabs.Add(tab);
                    Ribbon.SelectedTabIndex = Ribbon.Tabs.Count - 1;
                }

                result = (RibbonGroup)host.CreateComponent(typeof(RibbonGroup));
                result.Text = "Nuevo grupo";
                tab.Groups.Add(result);
            });
            return result;
        }

        internal RibbonItem AddItem(Type itemType)
        {
            return AddItem(itemType, null);
        }

        internal RibbonItem AddItem(Type itemType, RibbonGroup targetGroup)
        {
            RibbonItem result = null;
            Mutate("Añadir elemento", delegate(IDesignerHost host)
            {
                RibbonTab tab = EnsureTab(host);
                RibbonGroup group = targetGroup ?? (tab.Groups.Count == 0 ? null : tab.Groups[tab.Groups.Count - 1]);
                if (group == null)
                {
                    group = (RibbonGroup)host.CreateComponent(typeof(RibbonGroup));
                    group.Text = "Nuevo grupo";
                    tab.Groups.Add(group);
                }

                result = (RibbonItem)host.CreateComponent(itemType);
                if (result is RibbonSeparator)
                    result.Text = string.Empty;
                else if (itemType == typeof(RibbonToggleButton))
                    result.Text = "Nuevo toggle";
                else if (itemType == typeof(RibbonButton))
                    result.Text = "Nuevo botón";
                group.Items.Add(result);
            });
            return result;
        }

        internal void Remove(object value)
        {
            if (value == null)
                return;

            Mutate("Eliminar elemento", delegate(IDesignerHost host)
            {
                RibbonTab tab = value as RibbonTab;
                RibbonGroup group = value as RibbonGroup;
                RibbonItem item = value as RibbonItem;

                if (item != null)
                {
                    RibbonGroup owner = FindGroup(item);
                    if (owner != null)
                        owner.Items.Remove(item);
                }
                else if (group != null)
                {
                    RibbonTab owner = FindTab(group);
                    if (owner != null)
                        owner.Groups.Remove(group);
                }
                else if (tab != null)
                {
                    Ribbon.Tabs.Remove(tab);
                }

                IComponent component = value as IComponent;
                if (component != null && component.Site != null)
                    host.DestroyComponent(component);
            });
        }

        internal void Move(object value, int offset)
        {
            Mutate("Reordenar Ribbon", delegate
            {
                RibbonTab tab = value as RibbonTab;
                RibbonGroup group = value as RibbonGroup;
                RibbonItem item = value as RibbonItem;
                if (tab != null)
                    MoveInCollection(Ribbon.Tabs, tab, offset);
                else if (group != null)
                {
                    RibbonTab owner = FindTab(group);
                    if (owner != null)
                        MoveInCollection(owner.Groups, group, offset);
                }
                else if (item != null)
                {
                    RibbonGroup owner = FindGroup(item);
                    if (owner != null)
                        MoveInCollection(owner.Items, item, offset);
                }
            });
        }

        internal void NotifyChanged()
        {
            IComponentChangeService change = GetService(typeof(IComponentChangeService)) as IComponentChangeService;
            PropertyDescriptor property = TypeDescriptor.GetProperties(Ribbon)["Tabs"];
            if (change != null)
                change.OnComponentChanged(Ribbon, property, null, null);
            Ribbon.AttachItems();
        }

        internal void Change(string description, Action changeAction)
        {
            Mutate(description, delegate { changeAction(); });
        }

        internal void SetTheme(RibbonThemePreset preset)
        {
            Change("Cambiar tema del Ribbon", delegate { Ribbon.Appearance.Preset = preset; });
        }

        internal void SetTabAnimation(RibbonTabAnimation animation)
        {
            Change("Cambiar animación de pestañas", delegate { Ribbon.Appearance.TabAnimation = animation; });
        }

        internal void SetResponsive(bool enabled)
        {
            Change("Cambiar comportamiento responsive", delegate { Ribbon.ResponsiveEnabled = enabled; });
        }

        internal void EditMenuItems(RibbonDropDownButton button)
        {
            if (button == null)
                return;
            PropertyDescriptor property = TypeDescriptor.GetProperties(button)["Items"];
            UITypeEditor editor = property == null ? null : property.GetEditor(typeof(UITypeEditor)) as UITypeEditor;
            if (editor == null)
                return;
            RibbonDesignerTypeContext context = new RibbonDesignerTypeContext(this, button, property);
            editor.EditValue(context, new RibbonDesignerServiceProvider(this), property.GetValue(button));
            NotifyChanged();
            SelectComponent(button);
        }

        internal object GetDesignerService(Type serviceType)
        {
            return GetService(serviceType);
        }

        private void RibbonMouseUp(object sender, MouseEventArgs e)
        {
            if (dragItem != null)
            {
                bool changed = dragChanged;
                EndDrag();
                if (changed)
                    return;
            }

            if (e.Button == MouseButtons.Left)
            {
                object moreTarget = Ribbon.HitTestDesignMore(e.Location);
                if (moreTarget != null)
                {
                    SelectComponent(moreTarget);
                    ShowContextMenu(e.Location, moreTarget);
                    return;
                }
                object moveLeftTarget = Ribbon.HitTestDesignMoveLeft(e.Location);
                if (moveLeftTarget != null)
                {
                    Move(moveLeftTarget, -1);
                    SelectComponent(moveLeftTarget);
                    return;
                }
                object moveRightTarget = Ribbon.HitTestDesignMoveRight(e.Location);
                if (moveRightTarget != null)
                {
                    Move(moveRightTarget, 1);
                    SelectComponent(moveRightTarget);
                    return;
                }
                object deleteTarget = Ribbon.HitTestDesignDelete(e.Location);
                if (deleteTarget != null)
                {
                    if (MessageBox.Show(Ribbon, "¿Eliminar el elemento seleccionado?", "Ribbon",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    {
                        Remove(deleteTarget);
                        SelectComponent(Ribbon);
                    }
                    return;
                }

                if (Ribbon.HitTestAddTab(e.Location))
                {
                    SelectComponent(AddTab());
                    return;
                }
                if (Ribbon.HitTestAddGroup(e.Location))
                {
                    SelectComponent(AddGroup());
                    return;
                }

                RibbonGroup insertGroup = Ribbon.HitTestAddItem(e.Location);
                if (insertGroup != null)
                {
                    ShowAddItemMenu(e.Location, insertGroup);
                    return;
                }

                object target = Ribbon.HitTestDesignElement(e.Location);
                SelectContainingTab(target);
                SelectComponent(target ?? (object)Ribbon);
            }
            else if (e.Button == MouseButtons.Right)
            {
                ShowContextMenu(e.Location, Ribbon.HitTestDesignElement(e.Location));
            }
        }

        private void RibbonMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || Ribbon.HitTestDesignDelete(e.Location) != null ||
                Ribbon.HitTestDesignMore(e.Location) != null ||
                Ribbon.HitTestDesignMoveLeft(e.Location) != null ||
                Ribbon.HitTestDesignMoveRight(e.Location) != null ||
                Ribbon.HitTestAddTab(e.Location) || Ribbon.HitTestAddGroup(e.Location) ||
                Ribbon.HitTestAddItem(e.Location) != null)
                return;

            RibbonItem resizeItem = Ribbon.HitTestDesignResize(e.Location);
            RibbonItem item = resizeItem ?? Ribbon.HitTestDesignElement(e.Location) as RibbonItem;
            if (item == null)
                return;

            dragItem = item;
            dragGroup = FindGroup(item);
            dragStart = e.Location;
            dragResize = resizeItem != null;
            dragChanged = false;
            if (item.UseCustomBounds)
            {
                dragOriginalLocation = item.CustomLocation;
                dragOriginalSize = item.CustomSize;
            }
            else
            {
                Point origin = dragGroup == null ? Point.Empty : new Point(dragGroup.Bounds.Left + 5, dragGroup.Bounds.Top + 1);
                dragOriginalLocation = new Point(Math.Max(0, item.Bounds.Left - origin.X), Math.Max(0, item.Bounds.Top - origin.Y));
                dragOriginalSize = item.Bounds.Size;
            }
            Ribbon.Capture = true;
            Ribbon.SetDesignGrid(dragGroup, true);
            SelectComponent(item);
        }

        private void RibbonMouseMove(object sender, MouseEventArgs e)
        {
            if (dragItem == null || e.Button != MouseButtons.Left)
                return;
            int dx = e.X - dragStart.X;
            int dy = e.Y - dragStart.Y;
            if (!dragChanged && Math.Abs(dx) < 3 && Math.Abs(dy) < 3)
                return;

            if (!dragChanged)
            {
                IDesignerHost host = GetService(typeof(IDesignerHost)) as IDesignerHost;
                IComponentChangeService change = GetService(typeof(IComponentChangeService)) as IComponentChangeService;
                dragTransaction = host == null ? null : host.CreateTransaction(dragResize ? "Redimensionar elemento Ribbon" : "Mover elemento Ribbon");
                if (change != null)
                    change.OnComponentChanging(Ribbon, TypeDescriptor.GetProperties(Ribbon)["Tabs"]);
                dragChanged = true;
            }

            if (!dragItem.UseCustomBounds)
            {
                dragItem.CustomLocation = dragOriginalLocation;
                dragItem.CustomSize = dragOriginalSize;
                dragItem.UseCustomBounds = true;
            }
            if (dragResize)
            {
                int width = dx == 0 ? dragOriginalSize.Width : Snap(Math.Max(30, dragOriginalSize.Width + dx));
                int height = dy == 0 ? dragOriginalSize.Height : Snap(Math.Max(24, dragOriginalSize.Height + dy));
                dragItem.CustomSize = new Size(width, height);
            }
            else
            {
                int maxY = dragGroup == null ? 100 : Math.Max(0, dragGroup.Bounds.Height - 27 - dragOriginalSize.Height);
                dragItem.CustomLocation = new Point(Snap(Math.Max(0, dragOriginalLocation.X + dx)),
                    Snap(Math.Max(0, Math.Min(maxY, dragOriginalLocation.Y + dy))));
            }
            Ribbon.Invalidate();
        }

        private void EndDrag()
        {
            if (dragChanged)
            {
                IComponentChangeService change = GetService(typeof(IComponentChangeService)) as IComponentChangeService;
                if (change != null)
                    change.OnComponentChanged(Ribbon, TypeDescriptor.GetProperties(Ribbon)["Tabs"], null, null);
                if (dragTransaction != null)
                    dragTransaction.Commit();
            }
            dragTransaction = null;
            dragItem = null;
            dragGroup = null;
            dragChanged = false;
            Ribbon.SetDesignGrid(null, false);
            Ribbon.Capture = false;
            Ribbon.Cursor = Cursors.Default;
        }

        private static int Snap(int value)
        {
            const int grid = 4;
            return (int)Math.Round(value / (double)grid) * grid;
        }

        private void SelectionServiceSelectionChanged(object sender, EventArgs e)
        {
            object selected = selectionService == null ? null : selectionService.PrimarySelection;
            if (selected is RibbonTab || selected is RibbonGroup || selected is RibbonItem)
                Ribbon.DesignSelectedObject = selected;
            else
                Ribbon.DesignSelectedObject = null;
        }

        private void ComponentChangeServiceComponentChanged(object sender, ComponentChangedEventArgs e)
        {
            object changed = e == null ? null : e.Component;
            if (changed != Ribbon && !(changed is RibbonTab) && !(changed is RibbonGroup) &&
                !(changed is RibbonItem) && !(changed is RibbonMenuItem))
                return;
            RefreshDesignLayout();
        }

        private void RefreshDesignLayout()
        {
            Ribbon.AttachItems();
            Ribbon.PerformLayout();
            Ribbon.Invalidate(true);
            if (Ribbon.IsHandleCreated)
                Ribbon.Update();
        }

        private void SelectComponent(object component)
        {
            if (component == null || selectionService == null)
                return;

            IComponent designComponent = component as IComponent;
            if (designComponent != null && designComponent != Ribbon && !EnsureDesignSite(designComponent))
            {
                // Selecting an unsited Component makes the WinForms designer's
                // extender providers (Name, Modifiers, etc.) fail. Keep the
                // visual selection, but do not pass an invalid object to VS.
                Ribbon.DesignSelectedObject = component;
                Ribbon.Invalidate();
                return;
            }

            selectionService.SetSelectedComponents(new[] { component }, SelectionTypes.Replace);
            Ribbon.DesignSelectedObject = component == Ribbon ? null : component;
        }

        private bool EnsureDesignSite(IComponent component)
        {
            if (component.Site != null)
                return true;

            IDesignerHost host = GetService(typeof(IDesignerHost)) as IDesignerHost;
            if (host == null || host.Container == null)
                return false;

            string name = null;
            IReferenceService references = GetService(typeof(IReferenceService)) as IReferenceService;
            if (references != null)
                name = references.GetName(component);

            INameCreationService names = GetService(typeof(INameCreationService)) as INameCreationService;
            if (!string.IsNullOrEmpty(name) && names != null && !names.IsValidName(name))
                name = null;

            if (string.IsNullOrEmpty(name) || host.Container.Components[name] != null)
            {
                name = names == null
                    ? CreateFallbackName(host.Container, component.GetType())
                    : names.CreateName(host.Container, component.GetType());
            }

            try
            {
                if (string.IsNullOrEmpty(name))
                    host.Container.Add(component);
                else
                    host.Container.Add(component, name);
            }
            catch (ArgumentException)
            {
                return component.Site != null;
            }
            catch (InvalidOperationException)
            {
                return component.Site != null;
            }

            return component.Site != null;
        }

        private static string CreateFallbackName(IContainer container, Type componentType)
        {
            string typeName = componentType.Name;
            string prefix = char.ToLowerInvariant(typeName[0]) + typeName.Substring(1);
            int suffix = 1;
            while (container.Components[prefix + suffix] != null)
                suffix++;
            return prefix + suffix;
        }

        private void SelectContainingTab(object value)
        {
            RibbonTab tab = value as RibbonTab;
            RibbonGroup group = value as RibbonGroup;
            RibbonItem item = value as RibbonItem;
            if (group != null)
                tab = FindTab(group);
            else if (item != null)
            {
                RibbonGroup owner = FindGroup(item);
                if (owner != null)
                    tab = FindTab(owner);
            }
            if (tab != null)
                Ribbon.SelectedTabIndex = Ribbon.Tabs.IndexOf(tab);
        }

        private void ShowContextMenu(Point location, object target)
        {
            if (contextMenu != null)
                contextMenu.Dispose();
            contextMenu = new ContextMenuStrip();

            RibbonTab tab = target as RibbonTab;
            RibbonGroup group = target as RibbonGroup;
            RibbonItem item = target as RibbonItem;
            if (tab != null)
            {
                AddMenuItem(contextMenu, "Añadir grupo", delegate { SelectComponent(AddGroup(tab)); });
            }
            else if (group != null)
            {
                AddItemCommands(contextMenu, group);
                contextMenu.Items.Add(new ToolStripSeparator());
                AddMenuItem(contextMenu, group.LayoutMode == RibbonGroupLayout.CompactStack
                    ? "Usar layout horizontal" : "Activar apilado compacto", delegate
                {
                    Change("Cambiar layout del grupo", delegate
                    {
                        group.LayoutMode = group.LayoutMode == RibbonGroupLayout.CompactStack
                            ? RibbonGroupLayout.Horizontal : RibbonGroupLayout.CompactStack;
                    });
                    SelectComponent(group);
                });
            }
            else if (item != null)
            {
                if (item is RibbonButton)
                    AddMenuItem(contextMenu, "Elegir icono...", delegate { ChooseIcon(item); });
                RibbonDropDownButton dropDown = item as RibbonDropDownButton;
                if (dropDown != null)
                    AddMenuItem(contextMenu, "Editar opciones del menú...", delegate { EditMenuItems(dropDown); });

                RibbonButton ribbonButton = item as RibbonButton;
                if (ribbonButton != null)
                {
                    ToolStripMenuItem sizeMenu = new ToolStripMenuItem("Tamaño del botón");
                    AddButtonSizeItem(sizeMenu, "Grande", ribbonButton, RibbonItemSize.Large);
                    AddButtonSizeItem(sizeMenu, "Pequeño / apilable", ribbonButton, RibbonItemSize.Small);
                    contextMenu.Items.Add(sizeMenu);
                }

                ToolStripMenuItem toneMenu = new ToolStripMenuItem("Tono visual");
                foreach (RibbonItemTone tone in Enum.GetValues(typeof(RibbonItemTone)))
                    AddToneItem(toneMenu, tone, item);
                contextMenu.Items.Add(toneMenu);
                AddMenuItem(contextMenu, "Mover a la izquierda", delegate { Move(item, -1); });
                AddMenuItem(contextMenu, "Mover a la derecha", delegate { Move(item, 1); });
                if (item.UseCustomBounds)
                    AddMenuItem(contextMenu, "Restablecer layout automático", delegate
                    {
                        Change("Restablecer layout automático", delegate { item.UseCustomBounds = false; });
                        SelectComponent(item);
                    });
                RibbonHostedItem hostedItem = item as RibbonHostedItem;
                if (hostedItem != null && !(hostedItem is RibbonCheckBox))
                {
                    ToolStripMenuItem labelMenu = new ToolStripMenuItem("Posición de etiqueta");
                    AddLabelPositionItem(labelMenu, "Arriba", hostedItem, RibbonFieldLabelPosition.Top);
                    AddLabelPositionItem(labelMenu, "Izquierda", hostedItem, RibbonFieldLabelPosition.Left);
                    AddLabelPositionItem(labelMenu, "Oculta", hostedItem, RibbonFieldLabelPosition.Hidden);
                    contextMenu.Items.Add(labelMenu);
                }
            }
            else
            {
                AddMenuItem(contextMenu, "Añadir pestaña", delegate { SelectComponent(AddTab()); });
                AddMenuItem(contextMenu, "Añadir grupo", delegate { SelectComponent(AddGroup()); });
            }

            if (target == null || target == Ribbon)
            {
                contextMenu.Items.Add(new ToolStripSeparator());
                contextMenu.Items.Add(CreateAppearanceMenu());
                ToolStripMenuItem responsive = new ToolStripMenuItem("Diseño responsive")
                {
                    Checked = Ribbon.ResponsiveEnabled,
                    CheckOnClick = true
                };
                responsive.Click += delegate { SetResponsive(responsive.Checked); };
                contextMenu.Items.Add(responsive);
            }

            if (target != null)
            {
                contextMenu.Items.Add(new ToolStripSeparator());
                AddMenuItem(contextMenu, "Eliminar", delegate
                {
                    Remove(target);
                    SelectComponent(Ribbon);
                });
            }

            contextMenu.Items.Add(new ToolStripSeparator());
            AddMenuItem(contextMenu, "Editor de estructura...", delegate { ShowRibbonEditor(); });
            contextMenu.Show(Ribbon, location);
        }

        private void AddItemCommands(ContextMenuStrip menu, RibbonGroup group)
        {
            AddMenuItem(menu, "Añadir botón", delegate { SelectComponent(AddItem(typeof(RibbonButton), group)); });
            AddMenuItem(menu, "Añadir toggle", delegate { SelectComponent(AddItem(typeof(RibbonToggleButton), group)); });
            AddMenuItem(menu, "Añadir botón desplegable", delegate { SelectComponent(AddItem(typeof(RibbonDropDownButton), group)); });
            AddMenuItem(menu, "Añadir botón split", delegate { SelectComponent(AddItem(typeof(RibbonSplitButton), group)); });
            AddMenuItem(menu, "Añadir separador", delegate { SelectComponent(AddItem(typeof(RibbonSeparator), group)); });
            menu.Items.Add(new ToolStripSeparator());
            AddMenuItem(menu, "Añadir TextBox", delegate { SelectComponent(AddItem(typeof(RibbonTextBox), group)); });
            AddMenuItem(menu, "Añadir ComboBox", delegate { SelectComponent(AddItem(typeof(RibbonComboBox), group)); });
            AddMenuItem(menu, "Añadir selector de fecha", delegate { SelectComponent(AddItem(typeof(RibbonDatePicker), group)); });
            AddMenuItem(menu, "Añadir CheckBox", delegate { SelectComponent(AddItem(typeof(RibbonCheckBox), group)); });
            AddMenuItem(menu, "Añadir control numérico", delegate { SelectComponent(AddItem(typeof(RibbonNumericUpDown), group)); });
            AddMenuItem(menu, "Añadir etiqueta", delegate { SelectComponent(AddItem(typeof(RibbonLabel), group)); });
        }

        private void ShowAddItemMenu(Point location, RibbonGroup group)
        {
            if (contextMenu != null)
                contextMenu.Dispose();
            contextMenu = new ContextMenuStrip();
            AddItemCommands(contextMenu, group);
            contextMenu.Show(Ribbon, location);
        }

        private void ChooseIcon(RibbonItem item)
        {
            using (FluentIconPickerForm picker = new FluentIconPickerForm(item.IconKey))
            {
                if (picker.ShowDialog() != DialogResult.OK)
                    return;
                Change("Cambiar icono", delegate { item.IconKey = picker.SelectedKey; });
                SelectComponent(item);
            }
        }

        private static void AddMenuItem(ContextMenuStrip menu, string text, EventHandler click)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Click += click;
            menu.Items.Add(item);
        }

        private void AddLabelPositionItem(ToolStripMenuItem menu, string text,
            RibbonHostedItem item, RibbonFieldLabelPosition position)
        {
            ToolStripMenuItem option = new ToolStripMenuItem(text) { Checked = item.LabelPosition == position };
            option.Click += delegate
            {
                Change("Cambiar posición de etiqueta", delegate { item.LabelPosition = position; });
                SelectComponent(item);
            };
            menu.DropDownItems.Add(option);
        }

        private void AddButtonSizeItem(ToolStripMenuItem menu, string text,
            RibbonButton button, RibbonItemSize size)
        {
            ToolStripMenuItem option = new ToolStripMenuItem(text) { Checked = button.ItemSize == size };
            option.Click += delegate
            {
                Change("Cambiar tamaño del botón", delegate { button.ItemSize = size; });
                SelectComponent(button);
            };
            menu.DropDownItems.Add(option);
        }

        private void AddToneItem(ToolStripMenuItem menu, RibbonItemTone tone, RibbonItem item)
        {
            ToolStripMenuItem option = new ToolStripMenuItem(tone.ToString()) { Checked = item.Tone == tone };
            option.Click += delegate
            {
                Change("Cambiar tono visual", delegate { item.Tone = tone; });
                SelectComponent(item);
            };
            menu.DropDownItems.Add(option);
        }

        private ToolStripMenuItem CreateAppearanceMenu()
        {
            ToolStripMenuItem root = new ToolStripMenuItem("Apariencia y movimiento");
            ToolStripMenuItem themes = new ToolStripMenuItem("Tema");
            foreach (RibbonThemePreset preset in Enum.GetValues(typeof(RibbonThemePreset)))
            {
                if (preset == RibbonThemePreset.Custom)
                    continue;
                RibbonThemePreset current = preset;
                ToolStripMenuItem option = new ToolStripMenuItem(preset.ToString())
                {
                    Checked = Ribbon.Appearance.Preset == preset
                };
                option.Click += delegate { SetTheme(current); };
                themes.DropDownItems.Add(option);
            }
            root.DropDownItems.Add(themes);

            ToolStripMenuItem animations = new ToolStripMenuItem("Animación de pestañas");
            foreach (RibbonTabAnimation animation in Enum.GetValues(typeof(RibbonTabAnimation)))
            {
                RibbonTabAnimation current = animation;
                ToolStripMenuItem option = new ToolStripMenuItem(animation.ToString())
                {
                    Checked = Ribbon.Appearance.TabAnimation == animation
                };
                option.Click += delegate { SetTabAnimation(current); };
                animations.DropDownItems.Add(option);
            }
            root.DropDownItems.Add(animations);
            return root;
        }

        private RibbonTab CurrentTab
        {
            get
            {
                if (Ribbon.Tabs.Count == 0)
                    return null;
                int index = Math.Max(0, Math.Min(Ribbon.SelectedTabIndex, Ribbon.Tabs.Count - 1));
                return Ribbon.Tabs[index];
            }
        }

        private RibbonTab EnsureTab(IDesignerHost host)
        {
            RibbonTab tab = CurrentTab;
            if (tab != null)
                return tab;
            tab = (RibbonTab)host.CreateComponent(typeof(RibbonTab));
            tab.Text = "Nueva pestaña";
            Ribbon.Tabs.Add(tab);
            Ribbon.SelectedTabIndex = 0;
            return tab;
        }

        private RibbonTab FindTab(RibbonGroup group)
        {
            foreach (RibbonTab tab in Ribbon.Tabs)
                if (tab.Groups.Contains(group))
                    return tab;
            return null;
        }

        private RibbonGroup FindGroup(RibbonItem item)
        {
            foreach (RibbonTab tab in Ribbon.Tabs)
                foreach (RibbonGroup group in tab.Groups)
                    if (group.Items.Contains(item))
                        return group;
            return null;
        }

        private void Mutate(string description, Action<IDesignerHost> mutation)
        {
            IDesignerHost host = GetService(typeof(IDesignerHost)) as IDesignerHost;
            IComponentChangeService change = GetService(typeof(IComponentChangeService)) as IComponentChangeService;
            if (host == null)
                return;

            PropertyDescriptor property = TypeDescriptor.GetProperties(Ribbon)["Tabs"];
            using (DesignerTransaction transaction = host.CreateTransaction(description))
            {
                if (change != null)
                    change.OnComponentChanging(Ribbon, property);
                mutation(host);
                Ribbon.AttachItems();
                if (change != null)
                    change.OnComponentChanged(Ribbon, property, null, null);
                transaction.Commit();
            }
        }

        private static void MoveInCollection<T>(RibbonCollection<T> collection, T item, int offset)
        {
            int index = collection.IndexOf(item);
            int next = index + offset;
            if (index < 0 || next < 0 || next >= collection.Count)
                return;
            collection.RemoveAt(index);
            collection.Insert(next, item);
        }
    }

    internal sealed class RibbonDesignerActionList : DesignerActionList
    {
        private readonly RibbonControlDesigner designer;

        internal RibbonDesignerActionList(RibbonControlDesigner designer) : base(designer.Component)
        {
            this.designer = designer;
        }

        public void EditRibbon() { designer.ShowRibbonEditor(); }
        public void AddTab() { designer.AddTab(); }
        public void AddGroup() { designer.AddGroup(); }
        public void AddButton() { designer.AddItem(typeof(RibbonButton)); }
        public void AddToggle() { designer.AddItem(typeof(RibbonToggleButton)); }
        public void AddDropDown() { designer.AddItem(typeof(RibbonDropDownButton)); }
        public void AddSplit() { designer.AddItem(typeof(RibbonSplitButton)); }
        public void AddSeparator() { designer.AddItem(typeof(RibbonSeparator)); }
        public void AddTextBox() { designer.AddItem(typeof(RibbonTextBox)); }
        public void AddComboBox() { designer.AddItem(typeof(RibbonComboBox)); }
        public void AddDatePicker() { designer.AddItem(typeof(RibbonDatePicker)); }
        public void AddCheckBox() { designer.AddItem(typeof(RibbonCheckBox)); }
        public void AddNumeric() { designer.AddItem(typeof(RibbonNumericUpDown)); }
        public void AddLabel() { designer.AddItem(typeof(RibbonLabel)); }

        public RibbonThemePreset Theme
        {
            get { return ((RibbonControl)Component).Appearance.Preset; }
            set { designer.SetTheme(value); }
        }

        public RibbonTabAnimation TabAnimation
        {
            get { return ((RibbonControl)Component).Appearance.TabAnimation; }
            set { designer.SetTabAnimation(value); }
        }

        public int AnimationDuration
        {
            get { return ((RibbonControl)Component).Appearance.TabAnimationDuration; }
            set { designer.Change("Cambiar duración de animación", delegate { ((RibbonControl)Component).Appearance.TabAnimationDuration = value; }); }
        }

        public bool Responsive
        {
            get { return ((RibbonControl)Component).ResponsiveEnabled; }
            set { designer.SetResponsive(value); }
        }

        public override DesignerActionItemCollection GetSortedActionItems()
        {
            return new DesignerActionItemCollection
            {
                new DesignerActionHeaderItem("Editor del Ribbon"),
                new DesignerActionMethodItem(this, "EditRibbon", "Editor de estructura...", true),
                new DesignerActionHeaderItem("Apariencia y comportamiento"),
                new DesignerActionPropertyItem("Theme", "Tema"),
                new DesignerActionPropertyItem("TabAnimation", "Animación de pestañas"),
                new DesignerActionPropertyItem("AnimationDuration", "Duración (ms)"),
                new DesignerActionPropertyItem("Responsive", "Responsive"),
                new DesignerActionHeaderItem("Añadir"),
                new DesignerActionMethodItem(this, "AddTab", "Pestaña"),
                new DesignerActionMethodItem(this, "AddGroup", "Grupo en la pestaña actual"),
                new DesignerActionMethodItem(this, "AddButton", "Botón"),
                new DesignerActionMethodItem(this, "AddToggle", "Botón toggle"),
                new DesignerActionMethodItem(this, "AddDropDown", "Botón desplegable"),
                new DesignerActionMethodItem(this, "AddSplit", "Botón split"),
                new DesignerActionMethodItem(this, "AddSeparator", "Separador"),
                new DesignerActionMethodItem(this, "AddTextBox", "TextBox"),
                new DesignerActionMethodItem(this, "AddComboBox", "ComboBox"),
                new DesignerActionMethodItem(this, "AddDatePicker", "Selector de fecha"),
                new DesignerActionMethodItem(this, "AddCheckBox", "CheckBox"),
                new DesignerActionMethodItem(this, "AddNumeric", "Control numérico"),
                new DesignerActionMethodItem(this, "AddLabel", "Etiqueta")
            };
        }
    }

    internal sealed class RibbonDesignerServiceProvider : IServiceProvider
    {
        private readonly RibbonControlDesigner designer;
        internal RibbonDesignerServiceProvider(RibbonControlDesigner designer) { this.designer = designer; }
        public object GetService(Type serviceType) { return designer.GetDesignerService(serviceType); }
    }

    internal sealed class RibbonDesignerTypeContext : ITypeDescriptorContext
    {
        private readonly RibbonControlDesigner designer;
        private readonly object instance;
        private readonly PropertyDescriptor property;

        internal RibbonDesignerTypeContext(RibbonControlDesigner designer, object instance, PropertyDescriptor property)
        {
            this.designer = designer;
            this.instance = instance;
            this.property = property;
        }

        public IContainer Container
        {
            get { IComponent component = instance as IComponent; return component == null || component.Site == null ? null : component.Site.Container; }
        }
        public object Instance { get { return instance; } }
        public PropertyDescriptor PropertyDescriptor { get { return property; } }
        public object GetService(Type serviceType) { return designer.GetDesignerService(serviceType); }
        public void OnComponentChanged() { designer.NotifyChanged(); }
        public bool OnComponentChanging()
        {
            IComponentChangeService change = GetService(typeof(IComponentChangeService)) as IComponentChangeService;
            if (change == null) return true;
            try { change.OnComponentChanging(instance, property); return true; }
            catch (CheckoutException) { return false; }
        }
    }
}

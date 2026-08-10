using System;
using System.Drawing;
using System.Windows.Forms;

namespace ZarpaSuite.Controls.Design
{
    internal sealed class RibbonEditorDialog : Form
    {
        private readonly RibbonControlDesigner designer;
        private readonly RibbonControl ribbon;
        private readonly TreeView structureTree;
        private readonly PropertyGrid propertyGrid;

        internal RibbonEditorDialog(RibbonControlDesigner designer, RibbonControl ribbon)
        {
            this.designer = designer;
            this.ribbon = ribbon;

            Text = "Editor del Ribbon";
            Font = new Font("Segoe UI", 9F);
            BackColor = Color.FromArgb(246, 246, 248);
            ClientSize = new Size(940, 620);
            MinimumSize = new Size(720, 480);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            ShowInTaskbar = false;

            ToolStrip toolbar = CreateToolbar();
            Panel footer = CreateFooter();
            SplitContainer split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Size = new Size(940, 532),
                SplitterDistance = 370,
                SplitterWidth = 5,
                BackColor = Color.FromArgb(224, 224, 229),
                Panel1MinSize = 260,
                Panel2MinSize = 300
            };

            structureTree = new TreeView
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                BackColor = Color.White,
                HideSelection = false,
                FullRowSelect = true,
                ShowLines = true,
                ShowRootLines = false,
                ItemHeight = 25
            };

            propertyGrid = new PropertyGrid
            {
                Dock = DockStyle.Fill,
                HelpVisible = true,
                ToolbarVisible = true,
                PropertySort = PropertySort.Categorized
            };

            split.Panel1.Padding = new Padding(1);
            split.Panel2.Padding = new Padding(1);
            split.Panel1.Controls.Add(structureTree);
            split.Panel2.Controls.Add(propertyGrid);

            Controls.Add(split);
            Controls.Add(footer);
            Controls.Add(toolbar);

            structureTree.AfterSelect += StructureTreeAfterSelect;
            propertyGrid.PropertyValueChanged += PropertyGridValueChanged;
            RefreshTree(null);
        }

        private ToolStrip CreateToolbar()
        {
            ToolStrip strip = new ToolStrip
            {
                Dock = DockStyle.Top,
                GripStyle = ToolStripGripStyle.Hidden,
                BackColor = Color.White,
                Padding = new Padding(8, 5, 8, 5),
                Height = 40,
                RenderMode = ToolStripRenderMode.System
            };

            strip.Items.Add(CreateToolButton("+ Pestaña", delegate { SelectAfter(designer.AddTab()); }));
            strip.Items.Add(CreateToolButton("+ Grupo", delegate { AddGroup(); }));
            strip.Items.Add(new ToolStripSeparator());
            strip.Items.Add(CreateToolButton("+ Botón", delegate { AddItem(typeof(RibbonButton)); }));
            strip.Items.Add(CreateToolButton("+ Toggle", delegate { AddItem(typeof(RibbonToggleButton)); }));
            ToolStripDropDownButton more = new ToolStripDropDownButton("+ Controles");
            AddControlMenuItem(more, "Botón desplegable", typeof(RibbonDropDownButton));
            AddControlMenuItem(more, "Botón split", typeof(RibbonSplitButton));
            more.DropDownItems.Add(new ToolStripSeparator());
            AddControlMenuItem(more, "TextBox", typeof(RibbonTextBox));
            AddControlMenuItem(more, "ComboBox", typeof(RibbonComboBox));
            AddControlMenuItem(more, "Selector de fecha", typeof(RibbonDatePicker));
            AddControlMenuItem(more, "CheckBox", typeof(RibbonCheckBox));
            AddControlMenuItem(more, "Control numérico", typeof(RibbonNumericUpDown));
            AddControlMenuItem(more, "Etiqueta", typeof(RibbonLabel));
            strip.Items.Add(more);
            strip.Items.Add(CreateToolButton("+ Separador", delegate { AddItem(typeof(RibbonSeparator)); }));
            strip.Items.Add(new ToolStripSeparator());
            strip.Items.Add(CreateToolButton("Eliminar", delegate { RemoveSelected(); }));
            strip.Items.Add(CreateToolButton("Subir", delegate { MoveSelected(-1); }));
            strip.Items.Add(CreateToolButton("Bajar", delegate { MoveSelected(1); }));
            return strip;
        }

        private void AddControlMenuItem(ToolStripDropDownButton menu, string text, Type type)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Click += delegate { AddItem(type); };
            menu.DropDownItems.Add(item);
        }

        private Panel CreateFooter()
        {
            Panel footer = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 48,
                BackColor = Color.White,
                Padding = new Padding(12, 8, 12, 8)
            };

            Label help = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(110, 110, 119),
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "Selecciona un elemento para editar sus propiedades. IconKey abre el catálogo visual Fluent."
            };

            Button close = new Button
            {
                Dock = DockStyle.Right,
                Width = 92,
                Text = "Cerrar",
                DialogResult = DialogResult.OK
            };
            footer.Controls.Add(help);
            footer.Controls.Add(close);
            AcceptButton = close;
            return footer;
        }

        private static ToolStripButton CreateToolButton(string text, EventHandler click)
        {
            ToolStripButton button = new ToolStripButton(text)
            {
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                AutoSize = true,
                Margin = new Padding(2, 1, 2, 1)
            };
            button.Click += click;
            return button;
        }

        private void RefreshTree(object selectedObject)
        {
            structureTree.BeginUpdate();
            structureTree.Nodes.Clear();
            TreeNode ribbonNode = new TreeNode("Ribbon  ·  Apariencia y comportamiento") { Tag = ribbon };
            structureTree.Nodes.Add(ribbonNode);
            foreach (RibbonTab tab in ribbon.Tabs)
            {
                TreeNode tabNode = new TreeNode(DisplayName(tab)) { Tag = tab };
                ribbonNode.Nodes.Add(tabNode);
                foreach (RibbonGroup group in tab.Groups)
                {
                    TreeNode groupNode = new TreeNode(DisplayName(group)) { Tag = group };
                    tabNode.Nodes.Add(groupNode);
                    foreach (RibbonItem item in group.Items)
                        groupNode.Nodes.Add(new TreeNode(DisplayName(item)) { Tag = item });
                }
            }
            structureTree.ExpandAll();

            TreeNode selected = FindNode(structureTree.Nodes, selectedObject);
            if (selected == null && ribbonNode.Nodes.Count > 0)
                selected = ribbonNode.Nodes[Math.Max(0, Math.Min(ribbon.SelectedTabIndex, ribbonNode.Nodes.Count - 1))];
            if (selected == null)
                selected = ribbonNode;
            if (selected != null)
                structureTree.SelectedNode = selected;
            structureTree.EndUpdate();
        }

        private static TreeNode FindNode(TreeNodeCollection nodes, object value)
        {
            if (value == null)
                return null;
            foreach (TreeNode node in nodes)
            {
                if (ReferenceEquals(node.Tag, value))
                    return node;
                TreeNode child = FindNode(node.Nodes, value);
                if (child != null)
                    return child;
            }
            return null;
        }

        private void StructureTreeAfterSelect(object sender, TreeViewEventArgs e)
        {
            propertyGrid.SelectedObject = e.Node == null ? null : e.Node.Tag;
            RibbonTab tab = GetTab(e.Node);
            if (tab != null)
            {
                int index = ribbon.Tabs.IndexOf(tab);
                if (index >= 0)
                    ribbon.SelectedTabIndex = index;
            }
        }

        private void PropertyGridValueChanged(object sender, PropertyValueChangedEventArgs e)
        {
            object selected = propertyGrid.SelectedObject;
            designer.NotifyChanged();
            RefreshTree(selected);
        }

        private void AddGroup()
        {
            RibbonTab tab = GetTab(structureTree.SelectedNode);
            SelectAfter(designer.AddGroup(tab));
        }

        private void AddItem(Type type)
        {
            RibbonGroup group = GetGroup(structureTree.SelectedNode);
            SelectAfter(designer.AddItem(type, group));
        }

        private void RemoveSelected()
        {
            object selected = structureTree.SelectedNode == null ? null : structureTree.SelectedNode.Tag;
            if (selected == null)
                return;
            if (MessageBox.Show(this, "¿Eliminar el elemento seleccionado?", "Editor del Ribbon",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            TreeNode parent = structureTree.SelectedNode.Parent;
            object nextSelection = parent == null ? null : parent.Tag;
            designer.Remove(selected);
            RefreshTree(nextSelection);
        }

        private void MoveSelected(int offset)
        {
            object selected = structureTree.SelectedNode == null ? null : structureTree.SelectedNode.Tag;
            if (selected == null)
                return;
            designer.Move(selected, offset);
            RefreshTree(selected);
        }

        private void SelectAfter(object value)
        {
            RefreshTree(value);
            propertyGrid.SelectedObject = value;
        }

        private static RibbonTab GetTab(TreeNode node)
        {
            while (node != null)
            {
                RibbonTab tab = node.Tag as RibbonTab;
                if (tab != null)
                    return tab;
                node = node.Parent;
            }
            return null;
        }

        private static RibbonGroup GetGroup(TreeNode node)
        {
            while (node != null)
            {
                RibbonGroup group = node.Tag as RibbonGroup;
                if (group != null)
                    return group;
                node = node.Parent;
            }
            return null;
        }

        private static string DisplayName(object value)
        {
            RibbonTab tab = value as RibbonTab;
            if (tab != null)
                return "Pestaña  ·  " + tab.Text;
            RibbonGroup group = value as RibbonGroup;
            if (group != null)
                return "Grupo  ·  " + group.Text;
            if (value is RibbonSeparator)
                return "Separador";
            if (value is RibbonSplitButton)
                return "Split  ·  " + ((RibbonSplitButton)value).Text;
            if (value is RibbonDropDownButton)
                return "Desplegable  ·  " + ((RibbonDropDownButton)value).Text;
            RibbonToggleButton toggle = value as RibbonToggleButton;
            if (toggle != null)
                return "Toggle  ·  " + toggle.Text;
            RibbonButton button = value as RibbonButton;
            if (button != null)
                return "Botón  ·  " + button.Text;
            if (value is RibbonTextBox) return "TextBox  ·  " + ((RibbonTextBox)value).Text;
            if (value is RibbonComboBox) return "ComboBox  ·  " + ((RibbonComboBox)value).Text;
            if (value is RibbonDatePicker) return "Fecha  ·  " + ((RibbonDatePicker)value).Text;
            if (value is RibbonCheckBox) return "CheckBox  ·  " + ((RibbonCheckBox)value).Text;
            if (value is RibbonNumericUpDown) return "Número  ·  " + ((RibbonNumericUpDown)value).Text;
            if (value is RibbonLabel) return "Etiqueta  ·  " + ((RibbonLabel)value).Text;
            return value == null ? string.Empty : value.ToString();
        }
    }
}

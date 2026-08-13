using System;
using System.Drawing;
using System.Windows.Forms;

namespace ZarpaSuite.Controls.Design
{
    internal sealed class ZarpaTopicBarEditorDialog : Form
    {
        private readonly ZarpaTopicBarDesigner designer;
        private readonly ZarpaTopicBar topicBar;
        private readonly TreeView structureTree;
        private readonly PropertyGrid propertyGrid;

        internal ZarpaTopicBarEditorDialog(ZarpaTopicBarDesigner owner, ZarpaTopicBar control)
        {
            designer = owner;
            topicBar = control;
            Text = "Editor del TopicBar";
            Font = new Font("Segoe UI", 9F);
            BackColor = Color.FromArgb(246, 246, 248);
            ClientSize = new Size(820, 560);
            MinimumSize = new Size(680, 440);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            ShowInTaskbar = false;

            ToolStrip toolbar = CreateToolbar();
            Panel footer = CreateFooter();
            SplitContainer split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                SplitterDistance = 320,
                SplitterWidth = 5,
                Panel1MinSize = 240,
                Panel2MinSize = 280,
                BackColor = Color.FromArgb(224, 224, 229)
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
            RefreshTree(topicBar.DesignSelectedObject);
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
            strip.Items.Add(CreateButton("+ Página", delegate { SelectAfter(designer.AddPage()); }));
            strip.Items.Add(CreateButton("+ Enlace", delegate { AddLink(ZarpaTopicLinkKind.Link); }));
            strip.Items.Add(CreateButton("+ Separador", delegate { AddLink(ZarpaTopicLinkKind.Separator); }));
            strip.Items.Add(new ToolStripSeparator());
            strip.Items.Add(CreateButton("Eliminar", delegate { RemoveSelected(); }));
            strip.Items.Add(CreateButton("Subir", delegate { MoveSelected(-1); }));
            strip.Items.Add(CreateButton("Bajar", delegate { MoveSelected(1); }));
            return strip;
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
                Text = "Selecciona una página o enlace para editarlo; IconKey abre el catálogo Fluent."
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

        private static ToolStripButton CreateButton(string text, EventHandler handler)
        {
            ToolStripButton button = new ToolStripButton(text)
            {
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                AutoSize = true,
                Margin = new Padding(2, 1, 2, 1)
            };
            button.Click += handler;
            return button;
        }

        private void RefreshTree(object selection)
        {
            structureTree.BeginUpdate();
            structureTree.Nodes.Clear();
            TreeNode root = new TreeNode("TopicBar  ·  Apariencia y comportamiento") { Tag = topicBar };
            structureTree.Nodes.Add(root);
            foreach (ZarpaTopicPage page in topicBar.Pages)
            {
                TreeNode pageNode = new TreeNode(DisplayName(page)) { Tag = page };
                root.Nodes.Add(pageNode);
                foreach (ZarpaTopicLink link in page.Links)
                    pageNode.Nodes.Add(new TreeNode(DisplayName(link)) { Tag = link });
            }
            structureTree.ExpandAll();
            TreeNode selected = FindNode(structureTree.Nodes, selection) ?? root;
            structureTree.SelectedNode = selected;
            structureTree.EndUpdate();
        }

        private void StructureTreeAfterSelect(object sender, TreeViewEventArgs e)
        {
            object value = e.Node == null ? null : e.Node.Tag;
            propertyGrid.SelectedObject = value;
            designer.SelectComponent(value);
        }

        private void PropertyGridValueChanged(object sender, PropertyValueChangedEventArgs e)
        {
            object value = propertyGrid.SelectedObject;
            designer.NotifyChanged();
            RefreshTree(value);
        }

        private void AddLink(ZarpaTopicLinkKind kind)
        {
            SelectAfter(designer.AddLink(GetPage(structureTree.SelectedNode), kind));
        }

        private void RemoveSelected()
        {
            object value = structureTree.SelectedNode == null ? null : structureTree.SelectedNode.Tag;
            if (value == null || value == topicBar) return;
            if (MessageBox.Show(this, "¿Eliminar el elemento seleccionado?", "Editor del TopicBar",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            TreeNode parent = structureTree.SelectedNode.Parent;
            object next = parent == null ? topicBar : parent.Tag;
            designer.Remove(value);
            RefreshTree(next);
        }

        private void MoveSelected(int offset)
        {
            object value = structureTree.SelectedNode == null ? null : structureTree.SelectedNode.Tag;
            if (value == null || value == topicBar) return;
            designer.Move(value, offset);
            RefreshTree(value);
        }

        private void SelectAfter(object value)
        {
            RefreshTree(value);
            propertyGrid.SelectedObject = value;
            designer.SelectComponent(value);
        }

        private static ZarpaTopicPage GetPage(TreeNode node)
        {
            while (node != null)
            {
                ZarpaTopicPage page = node.Tag as ZarpaTopicPage;
                if (page != null) return page;
                ZarpaTopicLink link = node.Tag as ZarpaTopicLink;
                if (link != null) return link.OwnerPage;
                node = node.Parent;
            }
            return null;
        }

        private static TreeNode FindNode(TreeNodeCollection nodes, object value)
        {
            if (value == null) return null;
            foreach (TreeNode node in nodes)
            {
                if (ReferenceEquals(node.Tag, value)) return node;
                TreeNode child = FindNode(node.Nodes, value);
                if (child != null) return child;
            }
            return null;
        }

        private static string DisplayName(object value)
        {
            ZarpaTopicPage page = value as ZarpaTopicPage;
            if (page != null) return "Página  ·  " + page.Text;
            ZarpaTopicLink link = value as ZarpaTopicLink;
            if (link != null) return link.Kind == ZarpaTopicLinkKind.Separator ? "Separador" : "Enlace  ·  " + link.Text;
            return value == null ? string.Empty : value.ToString();
        }
    }
}

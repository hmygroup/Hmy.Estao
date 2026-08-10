using System;
using System.Collections;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Windows.Forms;

namespace ZarpaSuite.Controls.Design
{
    public sealed class ZarpaDataGridDesigner : ZarpaCollectionControlDesigner
    {
        private ZarpaDataGrid DataGrid { get { return (ZarpaDataGrid)Component; } }
        protected override string CollectionPropertyName { get { return "Columns"; } }
        protected override IList Items { get { return (IList)DataGrid.Columns; } }

        protected override DesignerVerbCollection CreateVerbs()
        {
            return new DesignerVerbCollection
            {
                new DesignerVerb("Añadir columna de texto", delegate { AddTextColumn(); }),
                new DesignerVerb("Añadir columna de selección", delegate { AddCheckBoxColumn(); }),
                new DesignerVerb("Añadir columna de acción", delegate { AddActionColumn(); }),
                new DesignerVerb("Editar columnas...", delegate { EditCollection(); })
            };
        }

        protected override DesignerActionList CreateActionList()
        {
            return new ZarpaDataGridActionList(this);
        }

        protected override void AddContextMenuItems(ContextMenuStrip menu)
        {
            AddMenuItem(menu, "Añadir columna de texto", delegate { AddTextColumn(); });
            AddMenuItem(menu, "Añadir columna de selección", delegate { AddCheckBoxColumn(); });
            AddMenuItem(menu, "Añadir columna de acción", delegate { AddActionColumn(); });
            menu.Items.Add(new ToolStripSeparator());
            AddMenuItem(menu, "Editar columnas...", delegate { EditCollection(); });
        }

        internal DataGridViewTextBoxColumn AddTextColumn()
        {
            return (DataGridViewTextBoxColumn)AddItem(typeof(DataGridViewTextBoxColumn), delegate(IComponent component)
            {
                DataGridViewColumn column = (DataGridViewColumn)component;
                column.HeaderText = "Nueva columna";
                column.Name = ComponentName(component, "columnaTexto");
            });
        }

        internal DataGridViewCheckBoxColumn AddCheckBoxColumn()
        {
            return (DataGridViewCheckBoxColumn)AddItem(typeof(DataGridViewCheckBoxColumn), delegate(IComponent component)
            {
                DataGridViewColumn column = (DataGridViewColumn)component;
                column.HeaderText = "Selección";
                column.Name = ComponentName(component, "columnaSeleccion");
            });
        }

        internal ZarpaDataGridActionColumn AddActionColumn()
        {
            return (ZarpaDataGridActionColumn)AddItem(typeof(ZarpaDataGridActionColumn), delegate(IComponent component)
            {
                ZarpaDataGridActionColumn column = (ZarpaDataGridActionColumn)component;
                column.HeaderText = "Acción";
                column.Name = ComponentName(component, "columnaAccion");
                column.ActionKey = "abrir";
            });
        }

        private static string ComponentName(IComponent component, string fallback)
        {
            return component.Site == null || string.IsNullOrEmpty(component.Site.Name)
                ? fallback : component.Site.Name;
        }
    }

    internal sealed class ZarpaDataGridActionList : DesignerActionList
    {
        private readonly ZarpaDataGridDesigner designer;
        internal ZarpaDataGridActionList(ZarpaDataGridDesigner owner) : base(owner.Component) { designer = owner; }

        public bool AutoGenerateColumns
        {
            get { return ((ZarpaDataGrid)Component).AutoGenerateColumns; }
            set { designer.SetProperty("AutoGenerateColumns", value); }
        }

        public bool ReadOnly
        {
            get { return ((ZarpaDataGrid)Component).ReadOnly; }
            set { designer.SetProperty("ReadOnly", value); }
        }

        public bool MultiSelect
        {
            get { return ((ZarpaDataGrid)Component).MultiSelect; }
            set { designer.SetProperty("MultiSelect", value); }
        }

        public int PageSize
        {
            get { return ((ZarpaDataGrid)Component).PageSize; }
            set { designer.SetProperty("PageSize", value); }
        }

        public void AddTextColumn() { designer.AddTextColumn(); }
        public void AddCheckBoxColumn() { designer.AddCheckBoxColumn(); }
        public void AddActionColumn() { designer.AddActionColumn(); }
        public void EditColumns() { designer.EditCollection(); }

        public override DesignerActionItemCollection GetSortedActionItems()
        {
            return new DesignerActionItemCollection
            {
                new DesignerActionHeaderItem("Columnas"),
                new DesignerActionMethodItem(this, "AddTextColumn", "Añadir columna de texto", "Columnas", true),
                new DesignerActionMethodItem(this, "AddCheckBoxColumn", "Añadir columna de selección", "Columnas", true),
                new DesignerActionMethodItem(this, "AddActionColumn", "Añadir columna de acción", "Columnas", true),
                new DesignerActionMethodItem(this, "EditColumns", "Editar columnas...", "Columnas", true),
                new DesignerActionHeaderItem("Datos"),
                new DesignerActionPropertyItem("AutoGenerateColumns", "Generar columnas automáticamente", "Datos"),
                new DesignerActionPropertyItem("ReadOnly", "Solo lectura", "Datos"),
                new DesignerActionPropertyItem("MultiSelect", "Selección múltiple", "Datos"),
                new DesignerActionPropertyItem("PageSize", "Filas por página", "Datos")
            };
        }
    }

    public sealed class ZarpaDataGridColumnCollectionEditor : CollectionEditor
    {
        public ZarpaDataGridColumnCollectionEditor(Type type) : base(type) { }

        protected override Type[] CreateNewItemTypes()
        {
            return new[]
            {
                typeof(DataGridViewTextBoxColumn),
                typeof(DataGridViewCheckBoxColumn),
                typeof(DataGridViewComboBoxColumn),
                typeof(DataGridViewImageColumn),
                typeof(DataGridViewLinkColumn),
                typeof(ZarpaDataGridActionColumn)
            };
        }
    }
}

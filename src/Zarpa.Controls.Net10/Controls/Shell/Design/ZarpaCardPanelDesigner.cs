using System;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Windows.Forms;
using System.Windows.Forms.Design;

namespace ZarpaSuite.Controls.Design
{
    public sealed class ZarpaCardPanelDesigner : ParentControlDesigner
    {
        private DesignerVerbCollection verbs;
        private DesignerActionListCollection actionLists;

        private ZarpaCardPanel Card { get { return (ZarpaCardPanel)Component; } }

        public override DesignerVerbCollection Verbs
        {
            get
            {
                if (verbs == null)
                    verbs = new DesignerVerbCollection
                    {
                        new DesignerVerb("Crear contenido de 1 columna", delegate { CreateContentGrid(1); }),
                        new DesignerVerb("Crear contenido de 2 columnas", delegate { CreateContentGrid(2); }),
                        new DesignerVerb("Crear contenido de 3 columnas", delegate { CreateContentGrid(3); })
                    };
                return verbs;
            }
        }

        public override DesignerActionListCollection ActionLists
        {
            get
            {
                if (actionLists == null)
                    actionLists = new DesignerActionListCollection { new ZarpaCardPanelActionList(this) };
                return actionLists;
            }
        }

        internal void SetProperty(string propertyName, object value)
        {
            PropertyDescriptor property = TypeDescriptor.GetProperties(Card)[propertyName];
            if (property != null) property.SetValue(Card, value);
        }

        internal void CreateContentGrid(int columns)
        {
            columns = Math.Max(1, Math.Min(3, columns));
            IDesignerHost host = GetService(typeof(IDesignerHost)) as IDesignerHost;
            if (host == null) return;
            IComponentChangeService changeService = GetService(typeof(IComponentChangeService)) as IComponentChangeService;
            PropertyDescriptor controlsProperty = TypeDescriptor.GetProperties(Card)["Controls"];
            TableLayoutPanel layout = FindContentGrid();
            object changedComponent = layout == null ? (object)Card : layout;
            PropertyDescriptor changedProperty = layout == null ? controlsProperty :
                TypeDescriptor.GetProperties(layout)["ColumnStyles"];
            string description = layout == null ? "Crear contenido de la tarjeta" : "Cambiar columnas de la tarjeta";

            using (DesignerTransaction transaction = host.CreateTransaction(description))
            {
                if (changeService != null) changeService.OnComponentChanging(changedComponent, changedProperty);
                if (layout == null)
                {
                    layout = (TableLayoutPanel)host.CreateComponent(typeof(TableLayoutPanel));
                    layout.Dock = DockStyle.Fill;
                    layout.GrowStyle = TableLayoutPanelGrowStyle.AddRows;
                    layout.Margin = Padding.Empty;
                    layout.Padding = Padding.Empty;
                    Card.Controls.Add(layout);
                    layout.BringToFront();
                }
                ConfigureColumns(layout, columns);
                if (changeService != null)
                    changeService.OnComponentChanged(changedComponent, changedProperty, null, null);
                transaction.Commit();
            }

            ISelectionService selection = GetService(typeof(ISelectionService)) as ISelectionService;
            if (selection != null)
                selection.SetSelectedComponents(new object[] { layout }, SelectionTypes.Replace);
        }

        private TableLayoutPanel FindContentGrid()
        {
            foreach (Control control in Card.Controls)
            {
                TableLayoutPanel layout = control as TableLayoutPanel;
                if (layout != null && layout.Dock == DockStyle.Fill) return layout;
            }
            return null;
        }

        private static void ConfigureColumns(TableLayoutPanel layout, int columns)
        {
            layout.SuspendLayout();
            try
            {
                layout.ColumnStyles.Clear();
                layout.ColumnCount = columns;
                float width = 100F / columns;
                for (int index = 0; index < columns; index++)
                    layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, width));
                if (layout.RowCount == 0)
                {
                    layout.RowCount = 1;
                    layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
                }
            }
            finally { layout.ResumeLayout(true); }
        }
    }

    internal sealed class ZarpaCardPanelActionList : DesignerActionList
    {
        private readonly ZarpaCardPanelDesigner designer;

        internal ZarpaCardPanelActionList(ZarpaCardPanelDesigner owner) : base(owner.Component)
        {
            designer = owner;
        }

        public bool Compact
        {
            get { return ((ZarpaCardPanel)Component).Compact; }
            set { designer.SetProperty("Compact", value); }
        }

        public bool RoundContentCorners
        {
            get { return ((ZarpaCardPanel)Component).RoundContentCorners; }
            set { designer.SetProperty("RoundContentCorners", value); }
        }

        public void CreateOneColumn() { designer.CreateContentGrid(1); }
        public void CreateTwoColumns() { designer.CreateContentGrid(2); }
        public void CreateThreeColumns() { designer.CreateContentGrid(3); }

        public override DesignerActionItemCollection GetSortedActionItems()
        {
            return new DesignerActionItemCollection
            {
                new DesignerActionHeaderItem("Contenido"),
                new DesignerActionTextItem("Arrastre controles directamente o cree una rejilla inicial.", "Contenido"),
                new DesignerActionMethodItem(this, "CreateOneColumn", "Crear 1 columna", "Contenido", true),
                new DesignerActionMethodItem(this, "CreateTwoColumns", "Crear 2 columnas", "Contenido", true),
                new DesignerActionMethodItem(this, "CreateThreeColumns", "Crear 3 columnas", "Contenido", true),
                new DesignerActionHeaderItem("Vista"),
                new DesignerActionPropertyItem("Compact", "Compacta", "Vista"),
                new DesignerActionPropertyItem("RoundContentCorners", "Redondear contenido", "Vista")
            };
        }
    }
}

using System;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Drawing;
using System.Windows.Forms;
using System.Windows.Forms.Design;

namespace ZarpaSuite.Controls.Design
{
    public sealed class ZarpaWizardStepperDesigner : ControlDesigner
    {
        private DesignerVerbCollection verbs;
        private DesignerActionListCollection actionLists;
        private ISelectionService selectionService;
        private IComponentChangeService changeService;
        private ContextMenuStrip contextMenu;

        private ZarpaWizardStepper Wizard { get { return (ZarpaWizardStepper)Component; } }

        public override DesignerVerbCollection Verbs
        {
            get
            {
                if (verbs == null)
                    verbs = new DesignerVerbCollection { new DesignerVerb("Añadir paso", delegate { SelectComponent(AddStep()); }) };
                return verbs;
            }
        }

        public override DesignerActionListCollection ActionLists
        {
            get
            {
                if (actionLists == null)
                    actionLists = new DesignerActionListCollection { new ZarpaWizardStepperActionList(this) };
                return actionLists;
            }
        }

        public override void Initialize(IComponent component)
        {
            base.Initialize(component);
            Wizard.MouseUp += WizardMouseUp;
            selectionService = GetService(typeof(ISelectionService)) as ISelectionService;
            changeService = GetService(typeof(IComponentChangeService)) as IComponentChangeService;
            if (selectionService != null) selectionService.SelectionChanged += SelectionChanged;
            if (changeService != null) changeService.ComponentChanged += ComponentChanged;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Wizard.MouseUp -= WizardMouseUp;
                if (selectionService != null) selectionService.SelectionChanged -= SelectionChanged;
                if (changeService != null) changeService.ComponentChanged -= ComponentChanged;
                if (contextMenu != null) contextMenu.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override bool GetHitTest(Point point)
        {
            return Wizard.RectangleToScreen(Wizard.ClientRectangle).Contains(point);
        }

        internal ZarpaWizardStep AddStep()
        {
            ZarpaWizardStep result = null;
            Mutate("Añadir paso del asistente", delegate(IDesignerHost host)
            {
                result = (ZarpaWizardStep)host.CreateComponent(typeof(ZarpaWizardStep));
                result.Text = "Nuevo paso";
                result.Description = "Configure este paso";
                ZarpaNavigationPage page = (ZarpaNavigationPage)host.CreateComponent(typeof(ZarpaNavigationPage));
                Control pageHost = Wizard.PageHost ?? Wizard.Parent;
                if (pageHost != null) pageHost.Controls.Add(page);
                result.Page = page;
                Wizard.Steps.Add(result);
                Wizard.ActivateDesignStep(result);
            });
            return result;
        }

        internal void Remove(ZarpaWizardStep step)
        {
            if (step == null) return;
            ZarpaNavigationPage page = step.Page;
            Mutate("Eliminar paso del asistente", delegate(IDesignerHost host)
            {
                Wizard.Steps.Remove(step);
                if (step.Site != null) host.DestroyComponent(step);
                if (page != null && page.Site != null && !IsPageUsed(page)) host.DestroyComponent(page);
            });
            SelectComponent(Wizard);
        }

        internal void Move(ZarpaWizardStep step, int offset)
        {
            if (step == null) return;
            int index = Wizard.Steps.IndexOf(step);
            int next = index + offset;
            if (index < 0 || next < 0 || next >= Wizard.Steps.Count) return;
            Mutate("Mover paso del asistente", delegate(IDesignerHost host)
            {
                Wizard.Steps.RemoveAt(index);
                Wizard.Steps.Insert(next, step);
                Wizard.ActivateDesignStep(step);
            });
            SelectComponent(step);
        }

        private void WizardMouseUp(object sender, MouseEventArgs e)
        {
            int index = Wizard.DesignHitTest(e.Location);
            ZarpaWizardStep step = index < 0 ? null : Wizard.Steps[index];
            if (e.Button == MouseButtons.Left && step != null)
            {
                Wizard.ActivateDesignStep(step);
                SelectComponent(step);
            }
            else if (e.Button == MouseButtons.Right) ShowContextMenu(e.Location, step);
        }

        private void SelectionChanged(object sender, EventArgs e)
        {
            object selected = selectionService == null ? null : selectionService.PrimarySelection;
            ZarpaWizardStep step = selected as ZarpaWizardStep;
            if (step == null)
            {
                ZarpaNavigationPage page = selected as ZarpaNavigationPage;
                if (page != null) step = FindStep(page);
            }
            Wizard.DesignSelectedStep = step != null && Wizard.Steps.Contains(step) ? step : null;
            if (Wizard.DesignSelectedStep != null) Wizard.ActivateDesignStep(Wizard.DesignSelectedStep);
        }

        private void ComponentChanged(object sender, ComponentChangedEventArgs e)
        {
            object changed = e == null ? null : e.Component;
            if (changed == Wizard || changed is ZarpaWizardStep || changed is ZarpaNavigationPage)
                Wizard.RefreshSteps();
        }

        private void ShowContextMenu(Point location, ZarpaWizardStep step)
        {
            if (contextMenu != null) contextMenu.Dispose();
            contextMenu = new ContextMenuStrip();
            if (step != null)
            {
                AddMenuItem("Mover a la izquierda", delegate { Move(step, -1); });
                AddMenuItem("Mover a la derecha", delegate { Move(step, 1); });
                contextMenu.Items.Add(new ToolStripSeparator());
                AddMenuItem("Eliminar paso y página", delegate { Remove(step); });
                contextMenu.Items.Add(new ToolStripSeparator());
            }
            AddMenuItem("Añadir paso", delegate { SelectComponent(AddStep()); });
            contextMenu.Show(Wizard, location);
        }

        private void AddMenuItem(string text, EventHandler handler)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Click += handler;
            contextMenu.Items.Add(item);
        }

        private void SelectComponent(object value)
        {
            if (value == null || selectionService == null) return;
            IComponent component = value as IComponent;
            if (component != null && component != Wizard && component.Site == null) return;
            selectionService.SetSelectedComponents(new[] { value }, SelectionTypes.Replace);
            Wizard.DesignSelectedStep = value as ZarpaWizardStep;
        }

        private ZarpaWizardStep FindStep(ZarpaNavigationPage page)
        {
            foreach (ZarpaWizardStep step in Wizard.Steps)
                if (step.Page == page) return step;
            return null;
        }

        private bool IsPageUsed(ZarpaNavigationPage page)
        {
            foreach (ZarpaWizardStep step in Wizard.Steps)
                if (step.Page == page) return true;
            return false;
        }

        private void Mutate(string description, Action<IDesignerHost> mutation)
        {
            IDesignerHost host = GetService(typeof(IDesignerHost)) as IDesignerHost;
            if (host == null) return;
            PropertyDescriptor property = TypeDescriptor.GetProperties(Wizard)["Steps"];
            using (DesignerTransaction transaction = host.CreateTransaction(description))
            {
                if (changeService != null) changeService.OnComponentChanging(Wizard, property);
                mutation(host);
                Wizard.RefreshSteps();
                if (changeService != null) changeService.OnComponentChanged(Wizard, property, null, null);
                transaction.Commit();
            }
        }
    }

    internal sealed class ZarpaWizardStepperActionList : DesignerActionList
    {
        private readonly ZarpaWizardStepperDesigner designer;
        internal ZarpaWizardStepperActionList(ZarpaWizardStepperDesigner owner) : base(owner.Component) { designer = owner; }
        public void AddStep() { designer.AddStep(); }

        public override DesignerActionItemCollection GetSortedActionItems()
        {
            return new DesignerActionItemCollection
            {
                new DesignerActionHeaderItem("Estructura"),
                new DesignerActionMethodItem(this, "AddStep", "Añadir paso y página", "Estructura", true),
                new DesignerActionTextItem("Seleccione un paso en la barra para editar sus propiedades y su página.", "Estructura")
            };
        }
    }
}

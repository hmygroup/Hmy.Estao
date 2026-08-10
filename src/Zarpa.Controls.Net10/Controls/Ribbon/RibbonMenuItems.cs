using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing.Design;

namespace ZarpaSuite.Controls
{
    [TypeConverter(typeof(ExpandableObjectConverter))]
    [DesignTimeVisible(false)]
    public sealed class RibbonMenuItem : Component
    {
        private string text = "Opción";
        private string iconKey = string.Empty;
        private bool enabled = true;
        private bool isChecked;
        private bool isSeparator;
        private string shortcutText = string.Empty;

        [DefaultValue("Opción")]
        public string Text { get { return text; } set { text = value ?? string.Empty; NotifyChanged(); } }

        [Category("Icono"), DefaultValue("")]
        [Editor("ZarpaSuite.Controls.Design.FluentIconPickerEditor, Zarpa.Controls", typeof(UITypeEditor))]
        public string IconKey { get { return iconKey; } set { iconKey = value ?? string.Empty; NotifyChanged(); } }

        [DefaultValue(true)]
        public bool Enabled { get { return enabled; } set { enabled = value; NotifyChanged(); } }

        [DefaultValue(false)]
        public bool Checked { get { return isChecked; } set { isChecked = value; NotifyChanged(); } }

        [DefaultValue(false)]
        public bool IsSeparator { get { return isSeparator; } set { isSeparator = value; NotifyChanged(); } }

        [DefaultValue("")]
        public string ShortcutText { get { return shortcutText; } set { shortcutText = value ?? string.Empty; NotifyChanged(); } }

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal RibbonDropDownButton Owner { get; set; }

        public event EventHandler Click;

        internal void PerformClick()
        {
            if (Enabled && !IsSeparator && Click != null)
                Click(this, EventArgs.Empty);
        }

        private void NotifyChanged()
        {
            if (Owner != null)
                Owner.NotifyChanged();
        }

        public override string ToString()
        {
            return IsSeparator ? "— Separador —" : Text;
        }
    }

    public sealed class RibbonMenuItemCollection : Collection<RibbonMenuItem>
    {
        private readonly RibbonDropDownButton owner;
        internal RibbonMenuItemCollection(RibbonDropDownButton owner) { this.owner = owner; }

        public void AddRange(RibbonMenuItem[] items)
        {
            if (items == null) throw new ArgumentNullException("items");
            foreach (RibbonMenuItem item in items) Add(item);
        }

        protected override void InsertItem(int index, RibbonMenuItem item)
        {
            if (item == null) throw new ArgumentNullException("item");
            base.InsertItem(index, item);
            item.Owner = owner;
            owner.NotifyChanged();
        }

        protected override void SetItem(int index, RibbonMenuItem item)
        {
            if (item == null) throw new ArgumentNullException("item");
            this[index].Owner = null;
            base.SetItem(index, item);
            item.Owner = owner;
            owner.NotifyChanged();
        }

        protected override void RemoveItem(int index)
        {
            this[index].Owner = null;
            base.RemoveItem(index);
            owner.NotifyChanged();
        }

        protected override void ClearItems()
        {
            foreach (RibbonMenuItem item in this) item.Owner = null;
            base.ClearItems();
            owner.NotifyChanged();
        }
    }

    public class RibbonDropDownButton : RibbonButton
    {
        private readonly RibbonMenuItemCollection items;

        public RibbonDropDownButton()
        {
            Text = "Desplegable";
            items = new RibbonMenuItemCollection(this);
        }

        [Category("Menú")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Content)]
        [Editor("ZarpaSuite.Controls.Design.RibbonMenuItemCollectionEditor, Zarpa.Controls", typeof(UITypeEditor))]
        public RibbonMenuItemCollection Items { get { return items; } }

        public event EventHandler DropDownOpening;

        internal void OnDropDownOpening()
        {
            if (DropDownOpening != null) DropDownOpening(this, EventArgs.Empty);
        }
    }

    public sealed class RibbonSplitButton : RibbonDropDownButton
    {
        public RibbonSplitButton() { Text = "Botón split"; }
    }
}

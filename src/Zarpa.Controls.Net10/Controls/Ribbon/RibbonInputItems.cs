using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Drawing.Design;
using System.Windows.Forms;

namespace ZarpaSuite.Controls
{
    public enum RibbonFieldLabelPosition
    {
        Top,
        Left,
        Hidden
    }

    public abstract class RibbonHostedItem : RibbonItem
    {
        private int controlWidth = 140;
        private int labelWidth = 70;
        private RibbonFieldLabelPosition labelPosition;

        [Category("Diseño"), DefaultValue(140)]
        public int ControlWidth
        {
            get { return controlWidth; }
            set { controlWidth = Math.Max(60, Math.Min(400, value)); NotifyChanged(); }
        }

        [Category("Diseño"), DefaultValue(70)]
        public int LabelWidth
        {
            get { return labelWidth; }
            set { labelWidth = Math.Max(30, Math.Min(180, value)); NotifyChanged(); }
        }

        [Category("Diseño"), DefaultValue(RibbonFieldLabelPosition.Top)]
        public RibbonFieldLabelPosition LabelPosition
        {
            get { return labelPosition; }
            set { labelPosition = value; NotifyChanged(); }
        }
    }

    public sealed class RibbonLabel : RibbonItem
    {
        public RibbonLabel() { Text = "Etiqueta"; }
    }

    public sealed class RibbonTextBox : RibbonHostedItem
    {
        private string value = string.Empty;
        private bool readOnly;
        private int maxLength = 32767;

        public RibbonTextBox() { Text = "Texto"; }

        [Category("Datos"), DefaultValue("")]
        public string Value
        {
            get { return value; }
            set { string next = value ?? string.Empty; if (this.value == next) return; this.value = next; NotifyChanged(); OnValueChanged(); }
        }

        [Category("Comportamiento"), DefaultValue(false)]
        public bool ReadOnly { get { return readOnly; } set { readOnly = value; NotifyChanged(); } }

        [Category("Comportamiento"), DefaultValue(32767)]
        public int MaxLength { get { return maxLength; } set { maxLength = Math.Max(0, value); NotifyChanged(); } }

        public event EventHandler ValueChanged;
        private void OnValueChanged() { if (ValueChanged != null) ValueChanged(this, EventArgs.Empty); }
    }

    public sealed class RibbonComboBox : RibbonHostedItem
    {
        private readonly StringCollection items = new StringCollection();
        private int selectedIndex = -1;
        private ComboBoxStyle dropDownStyle = ComboBoxStyle.DropDownList;

        public RibbonComboBox() { Text = "Lista"; }

        [Category("Datos"), DesignerSerializationVisibility(DesignerSerializationVisibility.Content)]
        [Editor("System.Windows.Forms.Design.StringCollectionEditor, System.Design", typeof(UITypeEditor))]
        public StringCollection Items { get { return items; } }

        [Category("Datos"), DefaultValue(-1)]
        public int SelectedIndex
        {
            get { return selectedIndex; }
            set { int next = Math.Max(-1, Math.Min(value, items.Count - 1)); if (selectedIndex == next) return; selectedIndex = next; NotifyChanged(); OnSelectedIndexChanged(); }
        }

        [Browsable(false)]
        public string SelectedText { get { return selectedIndex >= 0 && selectedIndex < items.Count ? items[selectedIndex] : string.Empty; } }

        [Category("Comportamiento"), DefaultValue(ComboBoxStyle.DropDownList)]
        public ComboBoxStyle DropDownStyle { get { return dropDownStyle; } set { dropDownStyle = value; NotifyChanged(); } }

        public event EventHandler SelectedIndexChanged;
        private void OnSelectedIndexChanged() { if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty); }
    }

    public sealed class RibbonDatePicker : RibbonHostedItem
    {
        private DateTime value = DateTime.Today;
        private DateTimePickerFormat format = DateTimePickerFormat.Short;
        private string customFormat = string.Empty;
        private bool showCheckBox;

        public RibbonDatePicker() { Text = "Fecha"; ControlWidth = 125; }

        [Category("Datos")]
        public DateTime Value
        {
            get { return value; }
            set { if (this.value == value) return; this.value = value; NotifyChanged(); OnValueChanged(); }
        }

        [Category("Apariencia"), DefaultValue(DateTimePickerFormat.Short)]
        public DateTimePickerFormat Format { get { return format; } set { format = value; NotifyChanged(); } }

        [Category("Apariencia"), DefaultValue("")]
        public string CustomFormat { get { return customFormat; } set { customFormat = value ?? string.Empty; NotifyChanged(); } }

        [Category("Comportamiento"), DefaultValue(false)]
        public bool ShowCheckBox { get { return showCheckBox; } set { showCheckBox = value; NotifyChanged(); } }

        public event EventHandler ValueChanged;
        private void OnValueChanged() { if (ValueChanged != null) ValueChanged(this, EventArgs.Empty); }
    }

    public sealed class RibbonCheckBox : RibbonHostedItem
    {
        private bool isChecked;
        private bool threeState;

        public RibbonCheckBox() { Text = "Opción"; ControlWidth = 110; }

        [Category("Datos"), DefaultValue(false)]
        public bool Checked
        {
            get { return isChecked; }
            set { if (isChecked == value) return; isChecked = value; NotifyChanged(); OnCheckedChanged(); }
        }

        [Category("Comportamiento"), DefaultValue(false)]
        public bool ThreeState { get { return threeState; } set { threeState = value; NotifyChanged(); } }

        public event EventHandler CheckedChanged;
        private void OnCheckedChanged() { if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty); }
    }

    public sealed class RibbonNumericUpDown : RibbonHostedItem
    {
        private decimal value;
        private decimal minimum;
        private decimal maximum = 100M;
        private decimal increment = 1M;
        private int decimalPlaces;

        public RibbonNumericUpDown() { Text = "Número"; ControlWidth = 100; }

        [Category("Datos"), DefaultValue(typeof(decimal), "0")]
        public decimal Value
        {
            get { return value; }
            set { decimal next = Math.Max(minimum, Math.Min(maximum, value)); if (this.value == next) return; this.value = next; NotifyChanged(); OnValueChanged(); }
        }

        [Category("Datos"), DefaultValue(typeof(decimal), "0")]
        public decimal Minimum { get { return minimum; } set { minimum = value; if (maximum < value) maximum = value; Value = this.value; NotifyChanged(); } }

        [Category("Datos"), DefaultValue(typeof(decimal), "100")]
        public decimal Maximum { get { return maximum; } set { maximum = value; if (minimum > value) minimum = value; Value = this.value; NotifyChanged(); } }

        [Category("Datos"), DefaultValue(typeof(decimal), "1")]
        public decimal Increment { get { return increment; } set { increment = value <= 0 ? 1 : value; NotifyChanged(); } }

        [Category("Apariencia"), DefaultValue(0)]
        public int DecimalPlaces { get { return decimalPlaces; } set { decimalPlaces = Math.Max(0, Math.Min(8, value)); NotifyChanged(); } }

        public event EventHandler ValueChanged;
        private void OnValueChanged() { if (ValueChanged != null) ValueChanged(this, EventArgs.Empty); }
    }
}

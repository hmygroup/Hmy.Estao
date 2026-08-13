using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ZarpaSuite.Controls
{
    [ToolboxItem(true)]
    [ToolboxBitmap(typeof(TextBox))]
    [DefaultProperty("Value")]
    public class ZarpaTextBox : ZarpaFieldBase
    {
        private readonly TextBox textBox;
        private string placeholder = string.Empty;

        public ZarpaTextBox()
        {
            textBox = new TextBox { BorderStyle = BorderStyle.None };
            textBox.TextChanged += delegate { OnValueChanged(); };
            textBox.DragEnter += delegate(object sender, DragEventArgs e) { OnDragEnter(e); };
            textBox.DragDrop += delegate(object sender, DragEventArgs e) { OnDragDrop(e); };
            InitializeEditor(textBox);
        }

        protected TextBox TextEditor { get { return textBox; } }

        [Category("Contenido"), DefaultValue("")]
        public string Value { get { return textBox.Text; } set { textBox.Text = value ?? string.Empty; } }

        [Category("Contenido"), DefaultValue("")]
        public string Placeholder { get { return placeholder; } set { placeholder = value ?? string.Empty; UpdateCueBanner(); } }

        [Category("Comportamiento"), DefaultValue(false)]
        public bool ReadOnly { get { return textBox.ReadOnly; } set { textBox.ReadOnly = value; } }

        [Category("Comportamiento"), DefaultValue(32767)]
        public int MaxLength { get { return textBox.MaxLength; } set { textBox.MaxLength = Math.Max(0, value); } }

        [Category("Comportamiento"), DefaultValue('\0')]
        public char PasswordChar { get { return textBox.PasswordChar; } set { textBox.PasswordChar = value; } }

        [Category("Comportamiento"), DefaultValue(false)]
        public bool Multiline
        {
            get { return textBox.Multiline; }
            set { textBox.Multiline = value; textBox.AcceptsReturn = value; PerformLayout(); }
        }

        [Category("Comportamiento"), DefaultValue(ScrollBars.None)]
        public ScrollBars ScrollBars { get { return textBox.ScrollBars; } set { textBox.ScrollBars = value; } }

        [Browsable(false)]
        public string[] Lines { get { return textBox.Lines; } set { textBox.Lines = value ?? new string[0]; } }

        [Category("Comportamiento"), DefaultValue(false)]
        public new bool AllowDrop
        {
            get { return base.AllowDrop; }
            set { base.AllowDrop = value; textBox.AllowDrop = value; }
        }

        public void Clear() { Value = string.Empty; }

        [Browsable(false)] public override object UntypedValue { get { return Value; } }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); UpdateCueBanner(); }

        private void UpdateCueBanner()
        {
            if (textBox.IsHandleCreated)
                SendMessage(textBox.Handle, 0x1501, new IntPtr(1), placeholder);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
    }

    [ToolboxItem(true)]
    [ToolboxBitmap(typeof(ComboBox))]
    [DefaultProperty("SelectedIndex")]
    public class ZarpaComboBox : ZarpaFieldBase
    {
        private readonly ZarpaComboEditor comboBox;
        public ZarpaComboBox()
        {
            comboBox = new ZarpaComboEditor();
            comboBox.DropDownStyle = ComboBoxStyle.DropDown;
            comboBox.SelectedIndexChanged += delegate { OnValueChanged(); };
            comboBox.TextChanged += delegate { OnValueChanged(); };
            InitializeEditor(comboBox);
        }

        [Category("Datos")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Content)]
        [Editor("System.Windows.Forms.Design.ListControlStringCollectionEditor, System.Design", typeof(System.Drawing.Design.UITypeEditor))]
        public ComboBox.ObjectCollection Items { get { return comboBox.Items; } }

        [Category("Datos"), DefaultValue(null)]
        public object DataSource { get { return comboBox.DataSource; } set { comboBox.DataSource = value; } }

        [Category("Datos"), DefaultValue("")]
        public string DisplayMember { get { return comboBox.DisplayMember; } set { comboBox.DisplayMember = value ?? string.Empty; } }

        [Category("Datos"), DefaultValue("")]
        public string ValueMember { get { return comboBox.ValueMember; } set { comboBox.ValueMember = value ?? string.Empty; } }

        [Category("Datos"), DefaultValue(-1)]
        public int SelectedIndex { get { return comboBox.SelectedIndex; } set { comboBox.SelectedIndex = value; } }

        [Browsable(false)] public object SelectedItem { get { return comboBox.SelectedItem; } }
        [Browsable(false)] public object SelectedValue { get { return comboBox.SelectedValue; } }

        public event EventHandler SelectedIndexChanged
        {
            add { comboBox.SelectedIndexChanged += value; }
            remove { comboBox.SelectedIndexChanged -= value; }
        }

        [Category("Comportamiento"), DefaultValue(ComboBoxStyle.DropDown)]
        public ComboBoxStyle DropDownStyle { get { return comboBox.DropDownStyle; } set { comboBox.DropDownStyle = value; } }

        [Category("Datos"), DefaultValue("")]
        public override string Text { get { return comboBox.Text; } set { comboBox.Text = value ?? string.Empty; } }

        [Browsable(false)] public override object UntypedValue { get { return comboBox.SelectedItem ?? comboBox.Text; } }

        protected override void ApplyEditorTheme()
        {
            base.ApplyEditorTheme();
            if (comboBox != null) comboBox.ApplyTheme(Theme);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            // The editor opens from its own surface. This completes the hit target over
            // the field chrome without reopening a popup already opened by the editor.
            if (Enabled && e.Button == MouseButtons.Left && PopupAnchorBounds.Contains(e.Location))
                comboBox.OpenDropDown();
        }
    }

    [ToolboxItem(true)]
    [ToolboxBitmap(typeof(DateTimePicker))]
    [DefaultProperty("Value")]
    public class ZarpaDatePicker : ZarpaFieldBase
    {
        private readonly ZarpaDateEditor datePicker;
        public ZarpaDatePicker()
        {
            datePicker = new ZarpaDateEditor { CustomFormat = "dd/MM/yyyy" };
            datePicker.ValueChanged += delegate { OnValueChanged(); };
            InitializeEditor(datePicker);
        }

        [Category("Datos")]
        public DateTime Value { get { return datePicker.Value; } set { datePicker.Value = value < datePicker.MinDate ? datePicker.MinDate : value > datePicker.MaxDate ? datePicker.MaxDate : value; } }
        [Category("Datos")]
        public DateTime MinDate { get { return datePicker.MinDate; } set { datePicker.MinDate = value; } }
        [Category("Datos")]
        public DateTime MaxDate { get { return datePicker.MaxDate; } set { datePicker.MaxDate = value; } }
        [Category("Apariencia"), DefaultValue("dd/MM/yyyy")]
        public string CustomFormat { get { return datePicker.CustomFormat; } set { datePicker.CustomFormat = string.IsNullOrEmpty(value) ? "dd/MM/yyyy" : value; } }
        [Category("Comportamiento"), DefaultValue(false)]
        public bool ShowCheckBox { get { return datePicker.ShowCheckBox; } set { datePicker.ShowCheckBox = value; } }
        [Category("Comportamiento"), DefaultValue(false)]
        public bool Checked { get { return datePicker.Checked; } set { datePicker.Checked = value; } }
        [Browsable(false)] public override object UntypedValue { get { return datePicker.ShowCheckBox && !datePicker.Checked ? null : (object)datePicker.Value; } }

        protected override void ApplyEditorTheme()
        {
            base.ApplyEditorTheme();
            if (datePicker != null) datePicker.ApplyTheme(Theme);
        }
    }

    [ToolboxItem(true)]
    [ToolboxBitmap(typeof(NumericUpDown))]
    [DefaultProperty("Value")]
    public class ZarpaNumericUpDown : ZarpaFieldBase
    {
        private readonly ZarpaNumericEditor numeric;
        public ZarpaNumericUpDown()
        {
            numeric = new ZarpaNumericEditor { Maximum = 100 };
            numeric.ValueChanged += delegate { OnValueChanged(); };
            InitializeEditor(numeric);
        }

        [Category("Datos"), DefaultValue(typeof(decimal), "0")]
        public decimal Value { get { return numeric.Value; } set { numeric.Value = value < numeric.Minimum ? numeric.Minimum : value > numeric.Maximum ? numeric.Maximum : value; } }
        [Category("Datos"), DefaultValue(typeof(decimal), "0")]
        public decimal Minimum { get { return numeric.Minimum; } set { numeric.Minimum = value; } }
        [Category("Datos"), DefaultValue(typeof(decimal), "100")]
        public decimal Maximum { get { return numeric.Maximum; } set { numeric.Maximum = value; } }
        [Category("Datos"), DefaultValue(typeof(decimal), "1")]
        public decimal Increment { get { return numeric.Increment; } set { numeric.Increment = value; } }
        [Category("Apariencia"), DefaultValue(0)]
        public int DecimalPlaces { get { return numeric.DecimalPlaces; } set { numeric.DecimalPlaces = value; } }
        [Category("Apariencia"), DefaultValue("")]
        public string Prefix { get { return numeric.Prefix; } set { numeric.Prefix = value; } }
        [Category("Apariencia"), DefaultValue("")]
        public string Suffix { get { return numeric.Suffix; } set { numeric.Suffix = value; } }
        [Browsable(false)] public override object UntypedValue { get { return Value; } }

        protected override void ApplyEditorTheme()
        {
            base.ApplyEditorTheme();
            if (numeric != null) numeric.ApplyTheme(Theme);
        }
    }

    [ToolboxItem(true)]
    [ToolboxBitmap(typeof(TextBox))]
    public class ZarpaSearchBox : ZarpaTextBox
    {
        private bool showClearButton = true;
        public ZarpaSearchBox()
        {
            LabelText = string.Empty;
            Placeholder = "Buscar...";
            LeadingIconKey = "ic_fluent_search_24_regular";
            Height = 42;
            TextEditor.KeyDown += SearchKeyDown;
            TextEditor.TextChanged += delegate { PerformLayout(); Invalidate(); };
            AccessibleRole = AccessibleRole.Text;
        }

        [Category("Comportamiento"), DefaultValue(true)]
        public bool ShowClearButton { get { return showClearButton; } set { if (showClearButton == value) return; showClearButton = value; PerformLayout(); Invalidate(); } }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (showClearButton && !string.IsNullOrEmpty(Value) && e.X >= Width - 34) Value = string.Empty;
        }

        protected override void LayoutEditor()
        {
            base.LayoutEditor();
            if (Editor != null && showClearButton && !string.IsNullOrEmpty(Value))
                Editor.Width = Math.Max(10, Editor.Width - 28);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (showClearButton && !string.IsNullOrEmpty(Value))
                TextRenderer.DrawText(e.Graphics, "×", Font, new Rectangle(Width - 32, 0, 28, Height), ForeColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        private void SearchKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape && !string.IsNullOrEmpty(Value))
            {
                Value = string.Empty;
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }
    }
}

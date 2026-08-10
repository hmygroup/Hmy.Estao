using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Design;
using System.Linq;
using System.Windows.Forms;
using System.Windows.Forms.Design;

namespace ZarpaSuite.Controls.Design
{
    public sealed class FluentIconPickerEditor : UITypeEditor
    {
        public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext context)
        {
            return UITypeEditorEditStyle.Modal;
        }

        public override object EditValue(ITypeDescriptorContext context, IServiceProvider provider, object value)
        {
            IWindowsFormsEditorService service = provider == null ? null :
                provider.GetService(typeof(IWindowsFormsEditorService)) as IWindowsFormsEditorService;
            if (service == null)
                return value;

            using (FluentIconPickerForm form = new FluentIconPickerForm(value as string))
            {
                return service.ShowDialog(form) == DialogResult.OK ? form.SelectedKey : value;
            }
        }
    }

    internal sealed class FluentIconPickerForm : Form
    {
        private const int MaximumVisibleResults = 700;
        private readonly TextBox searchBox;
        private readonly ListBox iconList;
        private readonly Label resultLabel;
        private readonly Button acceptButton;
        private string selectedKey;

        internal FluentIconPickerForm(string currentKey)
        {
            selectedKey = currentKey ?? string.Empty;
            Text = "Seleccionar icono Fluent";
            Font = new Font("Segoe UI", 9F);
            BackColor = Color.FromArgb(246, 246, 248);
            ForeColor = Color.FromArgb(26, 26, 30);
            ClientSize = new Size(610, 590);
            MinimumSize = new Size(480, 420);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            ShowInTaskbar = false;

            Label title = new Label
            {
                AutoSize = true,
                Font = new Font("Segoe UI Semibold", 12F, FontStyle.Bold),
                Location = new Point(18, 16),
                Text = "Fluent UI System Icons"
            };

            searchBox = new TextBox
            {
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Location = new Point(18, 50),
                Size = new Size(574, 24)
            };

            resultLabel = new Label
            {
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                ForeColor = Color.FromArgb(110, 110, 119),
                Location = new Point(18, 80),
                Size = new Size(574, 20)
            };

            iconList = new ListBox
            {
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                DrawMode = DrawMode.OwnerDrawFixed,
                IntegralHeight = false,
                ItemHeight = 38,
                Location = new Point(18, 103),
                Size = new Size(574, 426)
            };

            Button clearButton = CreateButton("Sin icono", new Point(18, 542), 96);
            clearButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            clearButton.Click += delegate
            {
                selectedKey = string.Empty;
                DialogResult = DialogResult.OK;
                Close();
            };

            Button cancelButton = CreateButton("Cancelar", new Point(398, 542), 92);
            cancelButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            cancelButton.DialogResult = DialogResult.Cancel;

            acceptButton = CreateButton("Seleccionar", new Point(498, 542), 94);
            acceptButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            acceptButton.Enabled = false;
            acceptButton.Click += delegate { AcceptSelection(); };

            Controls.Add(title);
            Controls.Add(searchBox);
            Controls.Add(resultLabel);
            Controls.Add(iconList);
            Controls.Add(clearButton);
            Controls.Add(cancelButton);
            Controls.Add(acceptButton);
            AcceptButton = acceptButton;
            CancelButton = cancelButton;

            searchBox.TextChanged += delegate { ReloadIcons(); };
            iconList.SelectedIndexChanged += delegate
            {
                acceptButton.Enabled = iconList.SelectedItem is FluentIconInfo;
            };
            iconList.DoubleClick += delegate { AcceptSelection(); };
            iconList.DrawItem += DrawIconItem;

            ReloadIcons();
            Shown += delegate { searchBox.Focus(); };
        }

        internal string SelectedKey
        {
            get { return selectedKey; }
        }

        private void ReloadIcons()
        {
            FluentIconInfo[] matches = FluentIconCatalog.Search(searchBox.Text).ToArray();
            FluentIconInfo[] shown = matches.Take(MaximumVisibleResults).ToArray();
            iconList.BeginUpdate();
            iconList.Items.Clear();
            iconList.Items.AddRange(shown.Cast<object>().ToArray());
            iconList.EndUpdate();

            resultLabel.Text = matches.Length > shown.Length
                ? string.Format("Mostrando {0:N0} de {1:N0}. Escribe para filtrar.", shown.Length, matches.Length)
                : string.Format("{0:N0} iconos", matches.Length);

            if (!string.IsNullOrEmpty(selectedKey))
            {
                for (int index = 0; index < iconList.Items.Count; index++)
                {
                    FluentIconInfo icon = (FluentIconInfo)iconList.Items[index];
                    if (string.Equals(icon.Key, selectedKey, StringComparison.OrdinalIgnoreCase))
                    {
                        iconList.SelectedIndex = index;
                        break;
                    }
                }
            }
        }

        private void DrawIconItem(object sender, DrawItemEventArgs e)
        {
            e.DrawBackground();
            if (e.Index < 0 || e.Index >= iconList.Items.Count)
                return;

            FluentIconInfo icon = (FluentIconInfo)iconList.Items[e.Index];
            Color color = (e.State & DrawItemState.Selected) != 0 ? Color.White : ForeColor;
            Rectangle iconBounds = new Rectangle(e.Bounds.Left + 8, e.Bounds.Top + 7, 24, 24);
            FluentIconCatalog.TryDraw(e.Graphics, icon.Key, iconBounds, color, 22F);
            TextRenderer.DrawText(e.Graphics, icon.DisplayName, Font,
                new Rectangle(e.Bounds.Left + 42, e.Bounds.Top, e.Bounds.Width - 48, e.Bounds.Height),
                color, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            e.DrawFocusRectangle();
        }

        private void AcceptSelection()
        {
            FluentIconInfo icon = iconList.SelectedItem as FluentIconInfo;
            if (icon == null)
                return;
            selectedKey = icon.Key;
            DialogResult = DialogResult.OK;
            Close();
        }

        private static Button CreateButton(string text, Point location, int width)
        {
            return new Button
            {
                Text = text,
                Location = location,
                Size = new Size(width, 30),
                FlatStyle = FlatStyle.System
            };
        }
    }
}

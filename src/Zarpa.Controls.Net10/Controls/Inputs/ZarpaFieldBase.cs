using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace ZarpaSuite.Controls
{
    public enum ZarpaValidationState { None, Valid, Warning, Error }

    public sealed class ZarpaValidationEventArgs : CancelEventArgs
    {
        public ZarpaValidationEventArgs(object value) { Value = value; }
        public object Value { get; private set; }
        public string Message { get; set; }
        public ZarpaValidationState State { get; set; }
    }

    [ToolboxItem(false)]
    [DefaultEvent("ValueChanged")]
    public abstract class ZarpaFieldBase : Control, IZarpaThemeAware, IZarpaThemeBoundary
    {
        private ZarpaThemeTokens theme;
        private Control editor;
        private string labelText = "Campo", helperText = string.Empty, errorText = string.Empty;
        private string leadingIconKey = string.Empty;
        private bool required, showValidationIcon = true, focused, hot;
        private ZarpaValidationState validationState;

        protected ZarpaFieldBase()
        {
            theme = new ZarpaThemeTokens(ThemeChanged);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            Font = new Font("Segoe UI", 9F);
            Size = new Size(240, 76);
            MinimumSize = new Size(120, 58);
            BackColor = Color.Transparent;
            TabStop = false;
        }

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        protected Control Editor { get { return editor; } }

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        protected ZarpaThemeTokens Theme { get { return theme; } }

        [Category("Contenido"), DefaultValue("Campo")]
        public string LabelText { get { return labelText; } set { labelText = value ?? string.Empty; if (editor != null) editor.AccessibleName = labelText; PerformLayout(); Invalidate(); } }

        [Category("Contenido"), DefaultValue("")]
        public string HelperText { get { return helperText; } set { helperText = value ?? string.Empty; if (editor != null && string.IsNullOrEmpty(errorText)) editor.AccessibleDescription = helperText; PerformLayout(); Invalidate(); } }

        [Category("Validación"), DefaultValue("")]
        public string ErrorText { get { return errorText; } set { errorText = value ?? string.Empty; if (!string.IsNullOrEmpty(errorText)) validationState = ZarpaValidationState.Error; else if (validationState == ZarpaValidationState.Error) validationState = ZarpaValidationState.None; if (editor != null) editor.AccessibleDescription = string.IsNullOrEmpty(errorText) ? helperText : errorText; PerformLayout(); Invalidate(); } }

        [Category("Validación"), DefaultValue(false)]
        public bool Required { get { return required; } set { required = value; Invalidate(); } }

        [Category("Validación"), DefaultValue(ZarpaValidationState.None)]
        public ZarpaValidationState ValidationState { get { return validationState; } set { if (validationState == value) return; validationState = value; Invalidate(); if (ValidationChanged != null) ValidationChanged(this, EventArgs.Empty); } }

        [Category("Validación"), DefaultValue(true)]
        public bool ShowValidationIcon { get { return showValidationIcon; } set { showValidationIcon = value; PerformLayout(); Invalidate(); } }

        [Category("Icono"), DefaultValue("")]
        [Editor("ZarpaSuite.Controls.Design.FluentIconPickerEditor, Zarpa.Controls", typeof(System.Drawing.Design.UITypeEditor))]
        public string LeadingIconKey { get { return leadingIconKey; } set { leadingIconKey = value ?? string.Empty; PerformLayout(); Invalidate(); } }

        [Browsable(false)]
        public abstract object UntypedValue { get; }

        [Browsable(false)]
        internal Rectangle PopupAnchorBounds { get { return GetInputBounds(); } }

        public event EventHandler ValueChanged;
        public event EventHandler ValidationChanged;
        public event EventHandler<ZarpaValidationEventArgs> ValidateValue;

        protected void InitializeEditor(Control value)
        {
            if (editor != null) throw new InvalidOperationException("El editor ya está inicializado.");
            editor = value ?? throw new ArgumentNullException("value");
            editor.TabIndex = 0;
            editor.AccessibleName = labelText;
            editor.Enter += EditorEnter;
            editor.Leave += EditorLeave;
            editor.MouseEnter += EditorMouseEnter;
            editor.MouseLeave += EditorMouseLeave;
            Controls.Add(editor);
            ApplyEditorTheme();
            PerformLayout();
        }

        protected void OnValueChanged()
        {
            if (ValidationState != ZarpaValidationState.None) RunValidation();
            if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
        }

        public bool RunValidation()
        {
            ZarpaValidationState nextState = ZarpaValidationState.None;
            string nextMessage = string.Empty;
            object value = UntypedValue;
            if (required && IsEmptyValue(value))
            {
                nextState = ZarpaValidationState.Error;
                nextMessage = "Este campo es obligatorio.";
            }
            ZarpaValidationEventArgs args = new ZarpaValidationEventArgs(value) { State = nextState, Message = nextMessage };
            if (ValidateValue != null) ValidateValue(this, args);
            if (args.Cancel && args.State == ZarpaValidationState.None) args.State = ZarpaValidationState.Error;
            bool changed = validationState != args.State || errorText != (args.Message ?? string.Empty);
            validationState = args.State;
            errorText = args.Message ?? string.Empty;
            if (editor != null) editor.AccessibleDescription = errorText;
            Invalidate();
            if (changed && ValidationChanged != null) ValidationChanged(this, EventArgs.Empty);
            return validationState != ZarpaValidationState.Error;
        }

        public void ClearValidation()
        {
            validationState = ZarpaValidationState.None;
            errorText = string.Empty;
            if (editor != null) editor.AccessibleDescription = helperText;
            Invalidate();
        }

        public void ApplyTheme(ZarpaThemeTokens value)
        {
            if (value == null) return;
            theme = value;
            if (!string.Equals(Font.Name, theme.FontFamily, StringComparison.OrdinalIgnoreCase) || Math.Abs(Font.Size - theme.FontSize) > .01F)
                Font = new Font(theme.FontFamily, theme.FontSize);
            ForeColor = theme.Text;
            Height = Math.Max(MinimumSize.Height, LabelAreaHeight + theme.ControlHeight + SupportingAreaHeight + 3);
            ApplyEditorTheme();
            PerformLayout();
            Invalidate();
        }

        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); ApplyEditorTheme(); PerformLayout(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); if (editor != null) editor.Enabled = Enabled; Invalidate(); }
        protected override void OnEnter(EventArgs e) { base.OnEnter(e); if (editor != null && !editor.Focused) editor.Focus(); }
        protected override void OnLayout(LayoutEventArgs levent) { base.OnLayout(levent); LayoutEditor(); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hot = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            // WinForms raises MouseLeave when the pointer crosses into the child editor.
            // Keep the field hover stable while the pointer remains inside its surface.
            if (ClientRectangle.Contains(PointToClient(MousePosition))) return;
            hot = false;
            Invalidate();
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (Enabled && editor != null && GetInputBounds().Contains(e.Location)) editor.Focus();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(ZarpaPaint.EffectiveBackColor(Parent));
        }

        protected virtual Rectangle GetInputBounds()
        {
            int top = string.IsNullOrEmpty(labelText) ? 1 : LabelAreaHeight;
            int available = Math.Max(24, Height - top - SupportingAreaHeight - 2);
            int inputHeight = Math.Min(available, Math.Max(28, theme.ControlHeight));
            return new Rectangle(0, top, Math.Max(1, Width - 1), inputHeight);
        }

        protected virtual void LayoutEditor()
        {
            if (editor == null) return;
            Rectangle input = GetInputBounds();
            int left = theme.SpacingMedium + (string.IsNullOrEmpty(leadingIconKey) ? 0 : theme.IconSize + theme.SpacingSmall);
            int right = theme.SpacingMedium + (showValidationIcon && validationState != ZarpaValidationState.None ? theme.IconSize + theme.SpacingSmall : 0);
            int contentHeight = editor is TextBoxBase && !((TextBoxBase)editor).Multiline ? editor.PreferredSize.Height : input.Height - 6;
            contentHeight = Math.Max(18, Math.Min(contentHeight, input.Height - 4));
            editor.Bounds = new Rectangle(input.Left + left, input.Top + (input.Height - contentHeight) / 2,
                Math.Max(10, input.Width - left - right), contentHeight);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            Rectangle input = GetInputBounds();
            Color surface = Enabled ? (hot && !focused ? ZarpaPaint.Blend(theme.Surface, theme.SurfaceRaised, .38F) : theme.Surface) : theme.SurfaceRaised;
            Color border = GetBorderColor();
            int inputRadius = Math.Min(7, theme.CornerRadius);
            ZarpaPaint.FillRounded(e.Graphics, surface, input, inputRadius);
            ZarpaPaint.DrawRounded(e.Graphics, border, input, inputRadius, focused ? 1.5F : theme.BorderThickness);

            if (!string.IsNullOrEmpty(labelText))
            {
                string label = required ? labelText + "  *" : labelText;
                TextRenderer.DrawText(e.Graphics, label, Font, new Rectangle(1, 0, Width - 2, LabelAreaHeight - 2),
                    Enabled ? theme.Text : theme.TextMuted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }

            if (!string.IsNullOrEmpty(leadingIconKey))
            {
                Rectangle icon = new Rectangle(input.Left + theme.SpacingMedium, input.Top + (input.Height - theme.IconSize) / 2, theme.IconSize, theme.IconSize);
                FluentIconCatalog.TryDraw(e.Graphics, leadingIconKey, icon, focused ? theme.Accent : theme.TextMuted, theme.IconSize - 2F);
            }

            if (showValidationIcon && validationState != ZarpaValidationState.None)
            {
                Rectangle icon = new Rectangle(input.Right - theme.IconSize - theme.SpacingMedium,
                    input.Top + (input.Height - theme.IconSize) / 2, theme.IconSize, theme.IconSize);
                FluentIconCatalog.TryDraw(e.Graphics, GetValidationIcon(), icon, GetStateColor(), theme.IconSize - 2F);
            }

            string supporting = validationState == ZarpaValidationState.Error && !string.IsNullOrEmpty(errorText) ? errorText : helperText;
            if (!string.IsNullOrEmpty(supporting))
                using (Font supportingFont = new Font(Font.FontFamily, Math.Max(7F, Font.Size - 1F)))
                    TextRenderer.DrawText(e.Graphics, supporting, supportingFont,
                        new Rectangle(theme.SpacingSmall, input.Bottom + 2, Width - theme.SpacingMedium, SupportingAreaHeight - 2),
                        validationState == ZarpaValidationState.Error ? theme.Danger : theme.TextMuted,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        private bool HasSupportingText { get { return !string.IsNullOrEmpty(helperText) || !string.IsNullOrEmpty(errorText); } }
        private int LabelAreaHeight { get { return string.IsNullOrEmpty(labelText) ? 0 : Math.Max(20, Font.Height + 6); } }
        private int SupportingAreaHeight { get { return HasSupportingText ? Math.Max(18, Font.Height + 5) : 4; } }
        private void EditorEnter(object sender, EventArgs e) { focused = true; Invalidate(); }
        private void EditorLeave(object sender, EventArgs e) { focused = false; RunValidation(); Invalidate(); }
        private void EditorMouseEnter(object sender, EventArgs e) { hot = true; Invalidate(); }
        private void EditorMouseLeave(object sender, EventArgs e)
        {
            if (ClientRectangle.Contains(PointToClient(MousePosition))) return;
            hot = false;
            Invalidate();
        }
        private void ThemeChanged() { ApplyEditorTheme(); PerformLayout(); Invalidate(); }
        protected virtual void ApplyEditorTheme()
        {
            if (editor == null) return;
            if (!string.Equals(editor.Font.Name, theme.FontFamily, StringComparison.OrdinalIgnoreCase) || Math.Abs(editor.Font.Size - theme.FontSize) > .01F)
                editor.Font = new Font(theme.FontFamily, theme.FontSize);
            editor.BackColor = Enabled ? theme.Surface : theme.SurfaceRaised;
            editor.ForeColor = Enabled ? theme.Text : theme.TextMuted;
        }
        private Color GetBorderColor()
        {
            if (!Enabled) return theme.Border;
            if (validationState == ZarpaValidationState.Error) return theme.Danger;
            if (validationState == ZarpaValidationState.Warning) return theme.Warning;
            if (validationState == ZarpaValidationState.Valid) return theme.Success;
            return focused ? theme.Accent : theme.Border;
        }
        private Color GetStateColor()
        {
            return validationState == ZarpaValidationState.Error ? theme.Danger :
                validationState == ZarpaValidationState.Warning ? theme.Warning : theme.Success;
        }
        private string GetValidationIcon()
        {
            return validationState == ZarpaValidationState.Error ? "ic_fluent_error_circle_24_regular" :
                validationState == ZarpaValidationState.Warning ? "ic_fluent_warning_24_regular" : "ic_fluent_checkmark_circle_24_regular";
        }
        private static bool IsEmptyValue(object value)
        {
            if (value == null) return true;
            string text = value as string;
            return text != null && text.Trim().Length == 0;
        }
    }
}

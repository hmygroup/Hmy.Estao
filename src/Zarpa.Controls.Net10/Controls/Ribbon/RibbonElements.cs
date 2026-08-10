using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Design;

namespace ZarpaSuite.Controls
{
    public enum RibbonGroupLayout
    {
        Horizontal,
        CompactStack
    }

    public enum RibbonItemTone
    {
        Neutral,
        Primary,
        Success,
        Warning,
        Danger,
        Information
    }

    [TypeConverter(typeof(ExpandableObjectConverter))]
    [DesignTimeVisible(false)]
    public sealed class RibbonTab : Component
    {
        private string text = "Nueva pestaña";
        private bool visible = true;
        private readonly RibbonGroupCollection groups;

        public RibbonTab()
        {
            groups = new RibbonGroupCollection(this);
        }

        [DefaultValue("Nueva pestaña")]
        public string Text
        {
            get { return text; }
            set { text = value ?? string.Empty; NotifyChanged(); }
        }

        [DefaultValue(true)]
        public bool Visible
        {
            get { return visible; }
            set { visible = value; NotifyChanged(); }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Content)]
        [Editor("ZarpaSuite.Controls.Design.RibbonGroupCollectionEditor, Zarpa.Controls", typeof(UITypeEditor))]
        public RibbonGroupCollection Groups
        {
            get { return groups; }
        }

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal RibbonControl Owner { get; set; }

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal Rectangle Bounds { get; set; }

        internal void NotifyChanged()
        {
            if (Owner != null)
                Owner.AttachItems();
        }

        public override string ToString()
        {
            return string.IsNullOrEmpty(Text) ? base.ToString() : Text;
        }
    }

    [TypeConverter(typeof(ExpandableObjectConverter))]
    [DesignTimeVisible(false)]
    public sealed class RibbonGroup : Component
    {
        private string text = "Nuevo grupo";
        private bool showLauncher;
        private RibbonGroupLayout layoutMode;
        private int responsivePriority = 50;
        private bool allowCollapse = true;
        private readonly RibbonItemCollection items;

        public RibbonGroup()
        {
            items = new RibbonItemCollection(this);
        }

        [DefaultValue("Nuevo grupo")]
        public string Text
        {
            get { return text; }
            set { text = value ?? string.Empty; NotifyChanged(); }
        }

        [DefaultValue(false)]
        public bool ShowLauncher
        {
            get { return showLauncher; }
            set { showLauncher = value; NotifyChanged(); }
        }

        [Category("Diseño")]
        [DefaultValue(RibbonGroupLayout.Horizontal)]
        public RibbonGroupLayout LayoutMode
        {
            get { return layoutMode; }
            set { layoutMode = value; NotifyChanged(); }
        }

        [Category("Responsive")]
        [DefaultValue(50)]
        public int ResponsivePriority
        {
            get { return responsivePriority; }
            set { responsivePriority = Math.Max(0, Math.Min(100, value)); NotifyChanged(); }
        }

        [Category("Responsive")]
        [DefaultValue(true)]
        public bool AllowCollapse
        {
            get { return allowCollapse; }
            set { allowCollapse = value; NotifyChanged(); }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Content)]
        [Editor("ZarpaSuite.Controls.Design.RibbonItemCollectionEditor, Zarpa.Controls", typeof(UITypeEditor))]
        public RibbonItemCollection Items
        {
            get { return items; }
        }

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal RibbonControl Owner { get; set; }

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal Rectangle Bounds { get; set; }

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal Rectangle AddItemBounds { get; set; }

        internal void NotifyChanged()
        {
            if (Owner != null)
                Owner.AttachItems();
        }

        public override string ToString()
        {
            return string.IsNullOrEmpty(Text) ? base.ToString() : Text;
        }
    }

    [TypeConverter(typeof(ExpandableObjectConverter))]
    [DesignTimeVisible(false)]
    public abstract class RibbonItem : Component
    {
        private string text = "Elemento";
        private Image image;
        private string iconKey = string.Empty;
        private Color iconColor = Color.Empty;
        private bool enabled = true;
        private string toolTipText = string.Empty;
        private string toolTipTitle = string.Empty;
        private string shortcutText = string.Empty;
        private string badgeText = string.Empty;
        private Color badgeColor = Color.Empty;
        private RibbonItemTone tone;
        private bool busy;
        private bool useCustomBounds;
        private int responsivePriority = 50;
        private bool allowResponsiveResize = true;
        private Point customLocation = Point.Empty;
        private Size customSize = new Size(90, 58);

        [DefaultValue("Elemento")]
        public string Text
        {
            get { return text; }
            set { text = value ?? string.Empty; NotifyChanged(); }
        }

        [DefaultValue(null)]
        public Image Image
        {
            get { return image; }
            set { image = value; NotifyChanged(); }
        }

        [Category("Icono")]
        [DefaultValue("")]
        [Editor("ZarpaSuite.Controls.Design.FluentIconPickerEditor, Zarpa.Controls", typeof(UITypeEditor))]
        public string IconKey
        {
            get { return iconKey; }
            set { iconKey = value ?? string.Empty; NotifyChanged(); }
        }

        [Category("Icono")]
        public Color IconColor
        {
            get { return iconColor; }
            set { iconColor = value; NotifyChanged(); }
        }

        private bool ShouldSerializeIconColor()
        {
            return !iconColor.IsEmpty;
        }

        private void ResetIconColor()
        {
            IconColor = Color.Empty;
        }

        [DefaultValue(true)]
        public bool Enabled
        {
            get { return enabled; }
            set { enabled = value; NotifyChanged(); }
        }

        [DefaultValue("")]
        public string ToolTipText
        {
            get { return toolTipText; }
            set { toolTipText = value ?? string.Empty; }
        }

        [Category("Tooltip"), DefaultValue("")]
        public string ToolTipTitle
        {
            get { return toolTipTitle; }
            set { toolTipTitle = value ?? string.Empty; }
        }

        [Category("Tooltip"), DefaultValue("")]
        public string ShortcutText
        {
            get { return shortcutText; }
            set { shortcutText = value ?? string.Empty; }
        }

        [Category("Estado visual"), DefaultValue(RibbonItemTone.Neutral)]
        public RibbonItemTone Tone
        {
            get { return tone; }
            set { tone = value; NotifyChanged(); }
        }

        [Category("Estado visual"), DefaultValue("")]
        public string BadgeText
        {
            get { return badgeText; }
            set { badgeText = value ?? string.Empty; NotifyChanged(); }
        }

        [Category("Estado visual")]
        public Color BadgeColor
        {
            get { return badgeColor; }
            set { badgeColor = value; NotifyChanged(); }
        }

        private bool ShouldSerializeBadgeColor() { return !badgeColor.IsEmpty; }
        private void ResetBadgeColor() { BadgeColor = Color.Empty; }

        [Category("Estado visual"), DefaultValue(false)]
        public bool Busy
        {
            get { return busy; }
            set { busy = value; NotifyChanged(); }
        }

        [Category("Diseño personalizado")]
        [DefaultValue(false)]
        public bool UseCustomBounds
        {
            get { return useCustomBounds; }
            set { useCustomBounds = value; NotifyChanged(); }
        }

        [Category("Diseño personalizado")]
        [DefaultValue(typeof(Point), "0, 0")]
        public Point CustomLocation
        {
            get { return customLocation; }
            set { customLocation = new Point(Math.Max(0, value.X), Math.Max(0, value.Y)); NotifyChanged(); }
        }

        [Category("Diseño personalizado")]
        [DefaultValue(typeof(Size), "90, 58")]
        public Size CustomSize
        {
            get { return customSize; }
            set { customSize = new Size(Math.Max(30, value.Width), Math.Max(24, value.Height)); NotifyChanged(); }
        }

        [Category("Responsive")]
        [DefaultValue(50)]
        public int ResponsivePriority
        {
            get { return responsivePriority; }
            set { responsivePriority = Math.Max(0, Math.Min(100, value)); NotifyChanged(); }
        }

        [Category("Responsive")]
        [DefaultValue(true)]
        public bool AllowResponsiveResize
        {
            get { return allowResponsiveResize; }
            set { allowResponsiveResize = value; NotifyChanged(); }
        }

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal RibbonControl Owner { get; set; }

        internal Rectangle Bounds { get; set; }

        internal void NotifyChanged()
        {
            if (Owner != null)
                Owner.ItemChanged(this);
        }

        public override string ToString()
        {
            return string.IsNullOrEmpty(Text) ? GetType().Name : Text;
        }
    }

    public enum RibbonItemSize
    {
        Small,
        Large
    }

    public class RibbonButton : RibbonItem
    {
        private RibbonItemSize itemSize = RibbonItemSize.Large;

        [DefaultValue(RibbonItemSize.Large)]
        public RibbonItemSize ItemSize
        {
            get { return itemSize; }
            set { itemSize = value; NotifyChanged(); }
        }

        public event EventHandler Click;

        internal virtual void PerformClick()
        {
            if (Enabled && Click != null)
                Click(this, EventArgs.Empty);
        }
    }

    public sealed class RibbonToggleButton : RibbonButton
    {
        private bool isChecked;

        [DefaultValue(false)]
        public bool Checked
        {
            get { return isChecked; }
            set { isChecked = value; NotifyChanged(); }
        }

        public event EventHandler CheckedChanged;

        internal override void PerformClick()
        {
            if (!Enabled)
                return;

            Checked = !Checked;
            if (CheckedChanged != null)
                CheckedChanged(this, EventArgs.Empty);
            base.PerformClick();
        }
    }

    public sealed class RibbonSeparator : RibbonItem
    {
        public RibbonSeparator()
        {
            Text = string.Empty;
        }

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public new string Text
        {
            get { return base.Text; }
            set { base.Text = value; }
        }
    }
}

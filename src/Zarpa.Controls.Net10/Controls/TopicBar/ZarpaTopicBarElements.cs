using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Design;
using System.Windows.Forms;

namespace ZarpaSuite.Controls
{
    public enum ZarpaTopicLinkKind
    {
        Link,
        Separator
    }

    public sealed class ZarpaTopicPageEventArgs : EventArgs
    {
        private readonly ZarpaTopicPage page;

        public ZarpaTopicPageEventArgs(ZarpaTopicPage topicPage)
        {
            if (topicPage == null) throw new ArgumentNullException("topicPage");
            page = topicPage;
        }

        public ZarpaTopicPage Page { get { return page; } }
    }

    public sealed class ZarpaTopicLinkEventArgs : EventArgs
    {
        private readonly ZarpaTopicPage page;
        private readonly ZarpaTopicLink link;

        public ZarpaTopicLinkEventArgs(ZarpaTopicPage topicPage, ZarpaTopicLink topicLink)
        {
            if (topicPage == null) throw new ArgumentNullException("topicPage");
            if (topicLink == null) throw new ArgumentNullException("topicLink");
            page = topicPage;
            link = topicLink;
        }

        public ZarpaTopicPage Page { get { return page; } }
        public ZarpaTopicLink Link { get { return link; } }
    }

    [TypeConverter(typeof(ExpandableObjectConverter))]
    [DesignTimeVisible(false)]
    [ToolboxItem(false)]
    public sealed class ZarpaTopicPage : Component
    {
        private readonly ZarpaTopicLinkCollection links;
        private string text = "Nueva página";
        private string iconKey = string.Empty;
        private string badgeText = string.Empty;
        private string toolTipText = string.Empty;
        private Image image;
        private int imageIndex = -1;
        private bool enabled = true;
        private bool visible = true;
        private bool collapsed;
        private bool emphasized;
        private bool wrapLinkText;
        private HorizontalAlignment linkAlignment;
        private object tag;

        public ZarpaTopicPage()
        {
            links = new ZarpaTopicLinkCollection(this);
        }

        public ZarpaTopicPage(string pageText) : this()
        {
            Text = pageText;
        }

        [Category("Contenido"), DefaultValue("Nueva página")]
        public string Text
        {
            get { return text; }
            set { text = value ?? string.Empty; NotifyChanged(); }
        }

        [Category("Icono"), DefaultValue("")]
        [Editor("ZarpaSuite.Controls.Design.FluentIconPickerEditor, Zarpa.Controls", typeof(UITypeEditor))]
        public string IconKey
        {
            get { return iconKey; }
            set { iconKey = value ?? string.Empty; NotifyChanged(); }
        }

        [Category("Icono"), DefaultValue(null)]
        public Image Image
        {
            get { return image; }
            set { image = value; NotifyChanged(); }
        }

        [Category("Icono"), DefaultValue(-1)]
        public int ImageIndex
        {
            get { return imageIndex; }
            set { imageIndex = Math.Max(-1, value); NotifyChanged(); }
        }

        [Category("Estado"), DefaultValue("")]
        public string BadgeText
        {
            get { return badgeText; }
            set { badgeText = value ?? string.Empty; NotifyChanged(); }
        }

        [Category("Comportamiento"), DefaultValue("")]
        public string ToolTipText
        {
            get { return toolTipText; }
            set { toolTipText = value ?? string.Empty; NotifyChanged(); }
        }

        [Category("Comportamiento"), DefaultValue(true)]
        public bool Enabled
        {
            get { return enabled; }
            set { enabled = value; NotifyChanged(); }
        }

        [Category("Comportamiento"), DefaultValue(true)]
        public bool Visible
        {
            get { return visible; }
            set { visible = value; NotifyChanged(); }
        }

        [Category("Comportamiento"), DefaultValue(false)]
        public bool Collapsed
        {
            get { return collapsed; }
            set
            {
                if (collapsed == value) return;
                collapsed = value;
                if (Owner != null) Owner.PageCollapsedStateChanged(this);
                if (CollapsedChanged != null) CollapsedChanged(this, EventArgs.Empty);
            }
        }

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool Expanded
        {
            get { return !collapsed; }
            set { Collapsed = !value; }
        }

        [Category("Apariencia"), DefaultValue(false)]
        public bool Emphasized
        {
            get { return emphasized; }
            set { emphasized = value; NotifyChanged(); }
        }

        [Category("Diseño de enlaces"), DefaultValue(false)]
        public bool WrapLinkText
        {
            get { return wrapLinkText; }
            set { wrapLinkText = value; NotifyChanged(); }
        }

        [Category("Diseño de enlaces"), DefaultValue(HorizontalAlignment.Left)]
        public HorizontalAlignment LinkAlignment
        {
            get { return linkAlignment; }
            set { linkAlignment = value; NotifyChanged(); }
        }

        [Category("Datos"), DefaultValue(null), TypeConverter(typeof(StringConverter))]
        public object Tag
        {
            get { return tag; }
            set { tag = value; }
        }

        [Category("Datos")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Content)]
        [Editor("ZarpaSuite.Controls.Design.ZarpaTopicLinkCollectionEditor, Zarpa.Controls", typeof(UITypeEditor))]
        public ZarpaTopicLinkCollection Links { get { return links; } }

        public event EventHandler CollapsedChanged;

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal ZarpaTopicBar Owner { get; private set; }

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal Rectangle Bounds { get; set; }

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal Rectangle HeaderBounds { get; set; }

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal Rectangle ContentBounds { get; set; }

        internal void Attach(ZarpaTopicBar owner)
        {
            Owner = owner;
            links.Attach(owner);
        }

        internal void Detach(ZarpaTopicBar owner)
        {
            if (Owner != owner) return;
            links.Attach(null);
            Owner = null;
            Bounds = Rectangle.Empty;
            HeaderBounds = Rectangle.Empty;
            ContentBounds = Rectangle.Empty;
        }

        internal void NotifyChanged()
        {
            if (Owner != null) Owner.RefreshPages();
        }

        public override string ToString()
        {
            return string.IsNullOrEmpty(Text) ? base.ToString() : Text;
        }
    }

    [TypeConverter(typeof(ExpandableObjectConverter))]
    [DesignTimeVisible(false)]
    [ToolboxItem(false)]
    public sealed class ZarpaTopicLink : Component
    {
        private string text = "Nuevo enlace";
        private string description = string.Empty;
        private string key = string.Empty;
        private string iconKey = string.Empty;
        private string badgeText = string.Empty;
        private string toolTipText = string.Empty;
        private Image image;
        private int imageIndex = -1;
        private bool enabled = true;
        private bool visible = true;
        private ZarpaTopicLinkKind kind;
        private object tag;

        public ZarpaTopicLink()
        {
        }

        public ZarpaTopicLink(string linkText)
        {
            Text = linkText;
        }

        [Category("Contenido"), DefaultValue("Nuevo enlace")]
        public string Text
        {
            get { return text; }
            set { text = value ?? string.Empty; NotifyChanged(); }
        }

        [Category("Contenido"), DefaultValue("")]
        public string Description
        {
            get { return description; }
            set { description = value ?? string.Empty; NotifyChanged(); }
        }

        [Category("Datos"), DefaultValue("")]
        public string Key
        {
            get { return key; }
            set { key = value ?? string.Empty; NotifyChanged(); }
        }

        [Category("Icono"), DefaultValue("")]
        [Editor("ZarpaSuite.Controls.Design.FluentIconPickerEditor, Zarpa.Controls", typeof(UITypeEditor))]
        public string IconKey
        {
            get { return iconKey; }
            set { iconKey = value ?? string.Empty; NotifyChanged(); }
        }

        [Category("Icono"), DefaultValue(null)]
        public Image Image
        {
            get { return image; }
            set { image = value; NotifyChanged(); }
        }

        [Category("Icono"), DefaultValue(-1)]
        public int ImageIndex
        {
            get { return imageIndex; }
            set { imageIndex = Math.Max(-1, value); NotifyChanged(); }
        }

        [Category("Estado"), DefaultValue("")]
        public string BadgeText
        {
            get { return badgeText; }
            set { badgeText = value ?? string.Empty; NotifyChanged(); }
        }

        [Category("Comportamiento"), DefaultValue("")]
        public string ToolTipText
        {
            get { return toolTipText; }
            set { toolTipText = value ?? string.Empty; NotifyChanged(); }
        }

        [Category("Comportamiento"), DefaultValue(true)]
        public bool Enabled
        {
            get { return enabled; }
            set { enabled = value; NotifyChanged(); }
        }

        [Category("Comportamiento"), DefaultValue(true)]
        public bool Visible
        {
            get { return visible; }
            set { visible = value; NotifyChanged(); }
        }

        [Category("Diseño"), DefaultValue(ZarpaTopicLinkKind.Link)]
        public ZarpaTopicLinkKind Kind
        {
            get { return kind; }
            set { kind = value; NotifyChanged(); }
        }

        [Category("Datos"), DefaultValue(null), TypeConverter(typeof(StringConverter))]
        public object Tag
        {
            get { return tag; }
            set { tag = value; }
        }

        public event EventHandler Click;

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal ZarpaTopicPage OwnerPage { get; set; }

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal ZarpaTopicBar Owner { get; set; }

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal Rectangle Bounds { get; set; }

        internal void NotifyChanged()
        {
            if (Owner != null) Owner.RefreshPages();
        }

        internal void PerformClick()
        {
            if (enabled && kind == ZarpaTopicLinkKind.Link && Click != null)
                Click(this, EventArgs.Empty);
        }

        public override string ToString()
        {
            return kind == ZarpaTopicLinkKind.Separator ? "— Separador —" :
                string.IsNullOrEmpty(Text) ? base.ToString() : Text;
        }
    }
}

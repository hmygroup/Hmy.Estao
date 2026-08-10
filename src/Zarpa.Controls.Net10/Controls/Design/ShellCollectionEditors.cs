using System;
using System.ComponentModel.Design;

namespace ZarpaSuite.Controls
{
    public sealed class ZarpaNavigationCollectionEditor : CollectionEditor
    {
        public ZarpaNavigationCollectionEditor(Type type) : base(type) { }
        protected override Type CreateCollectionItemType() { return typeof(ZarpaNavigationItem); }
    }

    public sealed class ZarpaCommandCollectionEditor : CollectionEditor
    {
        public ZarpaCommandCollectionEditor(Type type) : base(type) { }
        protected override Type CreateCollectionItemType() { return typeof(ZarpaCommandItem); }
    }

    public sealed class ZarpaBreadcrumbCollectionEditor : CollectionEditor
    {
        public ZarpaBreadcrumbCollectionEditor(Type type) : base(type) { }
        protected override Type CreateCollectionItemType() { return typeof(ZarpaBreadcrumbItem); }
    }

    public sealed class ZarpaDocumentTabCollectionEditor : CollectionEditor
    {
        public ZarpaDocumentTabCollectionEditor(Type type) : base(type) { }
        protected override Type CreateCollectionItemType() { return typeof(ZarpaDocumentTab); }
    }

    public sealed class ZarpaWizardStepCollectionEditor : CollectionEditor
    {
        public ZarpaWizardStepCollectionEditor(Type type) : base(type) { }
        protected override Type CreateCollectionItemType() { return typeof(ZarpaWizardStep); }
    }
}

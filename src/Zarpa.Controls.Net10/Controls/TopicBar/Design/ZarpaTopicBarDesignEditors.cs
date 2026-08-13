using System;
using System.ComponentModel.Design;

namespace ZarpaSuite.Controls.Design
{
    public sealed class ZarpaTopicPageCollectionEditor : CollectionEditor
    {
        public ZarpaTopicPageCollectionEditor(Type type) : base(type) { }

        protected override Type[] CreateNewItemTypes()
        {
            return new[] { typeof(ZarpaTopicPage) };
        }
    }

    public sealed class ZarpaTopicLinkCollectionEditor : CollectionEditor
    {
        public ZarpaTopicLinkCollectionEditor(Type type) : base(type) { }

        protected override Type[] CreateNewItemTypes()
        {
            return new[] { typeof(ZarpaTopicLink) };
        }
    }
}

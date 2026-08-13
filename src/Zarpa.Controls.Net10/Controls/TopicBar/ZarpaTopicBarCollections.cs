using System;
using System.Collections.ObjectModel;

namespace ZarpaSuite.Controls
{
    public sealed class ZarpaTopicPageCollection : Collection<ZarpaTopicPage>
    {
        private readonly ZarpaTopicBar owner;

        internal ZarpaTopicPageCollection(ZarpaTopicBar topicBar)
        {
            owner = topicBar;
        }

        public void AddRange(ZarpaTopicPage[] pages)
        {
            if (pages == null) throw new ArgumentNullException("pages");
            foreach (ZarpaTopicPage page in pages) Add(page);
        }

        protected override void InsertItem(int index, ZarpaTopicPage item)
        {
            Validate(item, -1);
            base.InsertItem(index, item);
            item.Attach(owner);
            owner.RefreshPages();
        }

        protected override void SetItem(int index, ZarpaTopicPage item)
        {
            Validate(item, index);
            ZarpaTopicPage previous = this[index];
            previous.Detach(owner);
            base.SetItem(index, item);
            item.Attach(owner);
            owner.RefreshPages();
        }

        protected override void RemoveItem(int index)
        {
            this[index].Detach(owner);
            base.RemoveItem(index);
            owner.RefreshPages();
        }

        protected override void ClearItems()
        {
            foreach (ZarpaTopicPage page in this) page.Detach(owner);
            base.ClearItems();
            owner.RefreshPages();
        }

        private void Validate(ZarpaTopicPage item, int replacedIndex)
        {
            if (item == null) throw new ArgumentNullException("item");
            if (item.Owner != null && item.Owner != owner)
                throw new InvalidOperationException("La página ya pertenece a otro ZarpaTopicBar.");
            int current = IndexOf(item);
            if (current >= 0 && current != replacedIndex)
                throw new InvalidOperationException("La página ya existe en esta colección.");
        }
    }

    public sealed class ZarpaTopicLinkCollection : Collection<ZarpaTopicLink>
    {
        private readonly ZarpaTopicPage page;
        private ZarpaTopicBar owner;

        internal ZarpaTopicLinkCollection(ZarpaTopicPage topicPage)
        {
            page = topicPage;
        }

        public void AddRange(ZarpaTopicLink[] links)
        {
            if (links == null) throw new ArgumentNullException("links");
            foreach (ZarpaTopicLink link in links) Add(link);
        }

        protected override void InsertItem(int index, ZarpaTopicLink item)
        {
            Validate(item, -1);
            base.InsertItem(index, item);
            Attach(item);
            page.NotifyChanged();
        }

        protected override void SetItem(int index, ZarpaTopicLink item)
        {
            Validate(item, index);
            Detach(this[index]);
            base.SetItem(index, item);
            Attach(item);
            page.NotifyChanged();
        }

        protected override void RemoveItem(int index)
        {
            Detach(this[index]);
            base.RemoveItem(index);
            page.NotifyChanged();
        }

        protected override void ClearItems()
        {
            foreach (ZarpaTopicLink link in this) Detach(link);
            base.ClearItems();
            page.NotifyChanged();
        }

        internal void Attach(ZarpaTopicBar topicBar)
        {
            owner = topicBar;
            foreach (ZarpaTopicLink link in this)
            {
                link.OwnerPage = page;
                link.Owner = topicBar;
            }
        }

        private void Attach(ZarpaTopicLink link)
        {
            link.OwnerPage = page;
            link.Owner = owner;
        }

        private static void Detach(ZarpaTopicLink link)
        {
            link.OwnerPage = null;
            link.Owner = null;
            link.Bounds = System.Drawing.Rectangle.Empty;
        }

        private void Validate(ZarpaTopicLink item, int replacedIndex)
        {
            if (item == null) throw new ArgumentNullException("item");
            if (item.OwnerPage != null && item.OwnerPage != page)
                throw new InvalidOperationException("El enlace ya pertenece a otra página.");
            int current = IndexOf(item);
            if (current >= 0 && current != replacedIndex)
                throw new InvalidOperationException("El enlace ya existe en esta colección.");
        }
    }
}

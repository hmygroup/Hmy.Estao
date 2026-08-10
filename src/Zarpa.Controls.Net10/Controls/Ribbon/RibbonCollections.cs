using System;
using System.Collections.ObjectModel;

namespace ZarpaSuite.Controls
{
    public abstract class RibbonCollection<T> : Collection<T>
    {
        public void AddRange(T[] items)
        {
            if (items == null)
                throw new ArgumentNullException("items");

            foreach (T item in items)
                Add(item);
        }

        protected override void InsertItem(int index, T item)
        {
            if (item == null)
                throw new ArgumentNullException("item");

            base.InsertItem(index, item);
            OnChanged();
        }

        protected override void SetItem(int index, T item)
        {
            if (item == null)
                throw new ArgumentNullException("item");

            base.SetItem(index, item);
            OnChanged();
        }

        protected override void RemoveItem(int index)
        {
            base.RemoveItem(index);
            OnChanged();
        }

        protected override void ClearItems()
        {
            base.ClearItems();
            OnChanged();
        }

        protected virtual void OnChanged()
        {
        }
    }

    public sealed class RibbonTabCollection : RibbonCollection<RibbonTab>
    {
        private readonly RibbonControl owner;

        internal RibbonTabCollection(RibbonControl owner)
        {
            this.owner = owner;
        }

        protected override void SetItem(int index, RibbonTab item)
        {
            if (item == null) throw new ArgumentNullException("item");
            RibbonOwnership.Detach(this[index], owner);
            base.SetItem(index, item);
        }

        protected override void RemoveItem(int index)
        {
            RibbonOwnership.Detach(this[index], owner);
            base.RemoveItem(index);
        }

        protected override void ClearItems()
        {
            foreach (RibbonTab tab in this) RibbonOwnership.Detach(tab, owner);
            base.ClearItems();
        }

        protected override void OnChanged()
        {
            owner.AttachItems();
            owner.Invalidate();
        }
    }

    public sealed class RibbonGroupCollection : RibbonCollection<RibbonGroup>
    {
        private readonly RibbonTab owner;

        internal RibbonGroupCollection(RibbonTab owner)
        {
            this.owner = owner;
        }

        protected override void SetItem(int index, RibbonGroup item)
        {
            if (item == null) throw new ArgumentNullException("item");
            RibbonOwnership.Detach(this[index], owner.Owner);
            base.SetItem(index, item);
        }

        protected override void RemoveItem(int index)
        {
            RibbonOwnership.Detach(this[index], owner.Owner);
            base.RemoveItem(index);
        }

        protected override void ClearItems()
        {
            foreach (RibbonGroup group in this) RibbonOwnership.Detach(group, owner.Owner);
            base.ClearItems();
        }

        protected override void OnChanged()
        {
            owner.NotifyChanged();
        }
    }

    public sealed class RibbonItemCollection : RibbonCollection<RibbonItem>
    {
        private readonly RibbonGroup owner;

        internal RibbonItemCollection(RibbonGroup owner)
        {
            this.owner = owner;
        }

        protected override void SetItem(int index, RibbonItem item)
        {
            if (item == null) throw new ArgumentNullException("item");
            RibbonOwnership.Detach(this[index], owner.Owner);
            base.SetItem(index, item);
        }

        protected override void RemoveItem(int index)
        {
            RibbonOwnership.Detach(this[index], owner.Owner);
            base.RemoveItem(index);
        }

        protected override void ClearItems()
        {
            foreach (RibbonItem item in this) RibbonOwnership.Detach(item, owner.Owner);
            base.ClearItems();
        }

        protected override void OnChanged()
        {
            owner.NotifyChanged();
        }
    }

    internal static class RibbonOwnership
    {
        internal static void Detach(RibbonTab tab, RibbonControl expectedOwner)
        {
            if (!object.ReferenceEquals(tab.Owner, expectedOwner)) return;
            tab.Owner = null;
            foreach (RibbonGroup group in tab.Groups) Detach(group, expectedOwner);
        }

        internal static void Detach(RibbonGroup group, RibbonControl expectedOwner)
        {
            if (!object.ReferenceEquals(group.Owner, expectedOwner)) return;
            group.Owner = null;
            foreach (RibbonItem item in group.Items) Detach(item, expectedOwner);
        }

        internal static void Detach(RibbonItem item, RibbonControl expectedOwner)
        {
            if (object.ReferenceEquals(item.Owner, expectedOwner)) item.Owner = null;
        }
    }
}

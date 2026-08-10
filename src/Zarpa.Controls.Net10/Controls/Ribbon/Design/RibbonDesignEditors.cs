using System;
using System.ComponentModel.Design;

namespace ZarpaSuite.Controls.Design
{
    public sealed class RibbonTabCollectionEditor : CollectionEditor
    {
        public RibbonTabCollectionEditor(Type type) : base(type)
        {
        }

        protected override Type[] CreateNewItemTypes()
        {
            return new[] { typeof(RibbonTab) };
        }
    }

    public sealed class RibbonGroupCollectionEditor : CollectionEditor
    {
        public RibbonGroupCollectionEditor(Type type) : base(type)
        {
        }

        protected override Type[] CreateNewItemTypes()
        {
            return new[] { typeof(RibbonGroup) };
        }
    }

    public sealed class RibbonItemCollectionEditor : CollectionEditor
    {
        public RibbonItemCollectionEditor(Type type) : base(type)
        {
        }

        protected override Type[] CreateNewItemTypes()
        {
            return new[]
            {
                typeof(RibbonButton),
                typeof(RibbonToggleButton),
                typeof(RibbonDropDownButton),
                typeof(RibbonSplitButton),
                typeof(RibbonSeparator),
                typeof(RibbonTextBox),
                typeof(RibbonComboBox),
                typeof(RibbonDatePicker),
                typeof(RibbonCheckBox),
                typeof(RibbonNumericUpDown),
                typeof(RibbonLabel)
            };
        }
    }

    public sealed class RibbonMenuItemCollectionEditor : CollectionEditor
    {
        public RibbonMenuItemCollectionEditor(Type type) : base(type) { }

        protected override Type[] CreateNewItemTypes()
        {
            return new[] { typeof(RibbonMenuItem) };
        }
    }
}

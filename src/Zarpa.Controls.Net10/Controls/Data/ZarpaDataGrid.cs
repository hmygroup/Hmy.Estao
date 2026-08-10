using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace ZarpaSuite.Controls
{
    public enum ZarpaDataGridState { Normal, Loading, Empty, Error }

    public sealed class ZarpaDataGridActionEventArgs : EventArgs
    {
        internal ZarpaDataGridActionEventArgs(string actionKey, object item, int rowIndex)
        {
            ActionKey = actionKey;
            Item = item;
            RowIndex = rowIndex;
        }

        public string ActionKey { get; private set; }
        public object Item { get; private set; }
        public int RowIndex { get; private set; }
    }

    [ToolboxItem(true)]
    [ToolboxBitmap(typeof(DataGridViewButtonColumn))]
    public class ZarpaDataGridActionColumn : DataGridViewButtonColumn
    {
        private string actionKey = string.Empty;
        private string iconKey = string.Empty;

        public ZarpaDataGridActionColumn()
        {
            UseColumnTextForButtonValue = true;
            Text = "Abrir";
            ReadOnly = true;
            SortMode = DataGridViewColumnSortMode.NotSortable;
            Width = 92;
            MinimumWidth = 78;
        }

        [Category("Comportamiento"), DefaultValue("")]
        public string ActionKey { get { return actionKey; } set { actionKey = value ?? string.Empty; } }

        [Category("Icono"), DefaultValue("")]
        [Editor("ZarpaSuite.Controls.Design.FluentIconPickerEditor, Zarpa.Controls", typeof(System.Drawing.Design.UITypeEditor))]
        public string IconKey { get { return iconKey; } set { iconKey = value ?? string.Empty; } }

        public override object Clone()
        {
            ZarpaDataGridActionColumn clone = (ZarpaDataGridActionColumn)base.Clone();
            clone.ActionKey = actionKey;
            clone.IconKey = iconKey;
            return clone;
        }
    }

    [ToolboxItem(true)]
    [ToolboxBitmap(typeof(DataGridView))]
    [DefaultProperty("DataSource")]
    [DefaultEvent("SelectionChanged")]
    [Designer("ZarpaSuite.Controls.Design.ZarpaDataGridDesigner, Zarpa.Controls")]
    public class ZarpaDataGrid : UserControl, IZarpaThemeAware, IZarpaThemeBoundary
    {
        private sealed class SourceRow
        {
            internal object Item;
            internal int SourceIndex;
        }

        private sealed class GroupRow
        {
            internal string Key;
            internal int Count;
        }

        private readonly Panel filterBar;
        private readonly Label filterLabel;
        private readonly ComboBox filterColumn;
        private readonly TextBox filterValue;
        private readonly Button applyFilterButton;
        private readonly Button clearFilterButton;
        private readonly Label groupLabel;
        private readonly ComboBox groupColumn;
        private readonly DataGridView grid;
        private readonly Panel pager;
        private readonly Button previousButton;
        private readonly Button nextButton;
        private readonly ComboBox pageSizeBox;
        private readonly Label pageLabel;
        private readonly Label stateOverlay;
        private readonly Dictionary<string, string> filters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> collapsedGroups = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        private readonly HashSet<DataGridViewColumn> generatedColumns = new HashSet<DataGridViewColumn>();
        private readonly List<SourceRow> sourceRows = new List<SourceRow>();
        private ZarpaThemeTokens theme;
        private object dataSource;
        private PropertyDescriptorCollection properties;
        private IBindingList observedList;
        private string groupByColumn = string.Empty;
        private string sortColumn = string.Empty;
        private ListSortDirection sortDirection = ListSortDirection.Ascending;
        private int pageIndex;
        private int pageSize = 25;
        private int pageCount = 1;
        private int filteredCount;
        private bool rebuilding;
        private bool autoGenerateColumns = true;
        private ZarpaDataGridState viewState;
        private string emptyText = "No hay datos para mostrar.";
        private string errorText = "No fue posible cargar los datos.";

        public ZarpaDataGrid()
        {
            theme = new ZarpaThemeTokens(Invalidate);
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Size = new Size(720, 360);
            MinimumSize = new Size(420, 220);

            filterBar = new Panel { Dock = DockStyle.Top, Height = 44 };
            filterLabel = new Label { AutoSize = true, Text = "Filtro", AccessibleName = "Filtro de columna" };
            filterColumn = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Columna para filtrar" };
            filterValue = new TextBox { AccessibleName = "Valor del filtro" };
            applyFilterButton = new Button { Text = "Aplicar", FlatStyle = FlatStyle.Flat, AccessibleName = "Aplicar filtro" };
            clearFilterButton = new Button { Text = "Limpiar", FlatStyle = FlatStyle.Flat, AccessibleName = "Limpiar filtros" };
            groupLabel = new Label { AutoSize = true, Text = "Agrupar", AccessibleName = "Agrupacion" };
            groupColumn = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Columna para agrupar" };
            filterBar.Controls.AddRange(new Control[] { filterLabel, filterColumn, filterValue, applyFilterButton, clearFilterButton, groupLabel, groupColumn });

            grid = new DataGridView();
            grid.Dock = DockStyle.Fill;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToOrderColumns = true;
            grid.AutoGenerateColumns = false;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.BackgroundColor = Color.White;
            grid.BorderStyle = BorderStyle.None;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            grid.EnableHeadersVisualStyles = false;
            grid.MultiSelect = false;
            grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.AccessibleName = "Tabla de datos";

            pager = new Panel { Dock = DockStyle.Bottom, Height = 42 };
            previousButton = new Button { Text = "Anterior", FlatStyle = FlatStyle.Flat, AccessibleName = "Pagina anterior" };
            nextButton = new Button { Text = "Siguiente", FlatStyle = FlatStyle.Flat, AccessibleName = "Pagina siguiente" };
            pageSizeBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Filas por pagina" };
            pageSizeBox.Items.AddRange(new object[] { 10, 25, 50, 100 });
            pageSizeBox.SelectedItem = 25;
            pageLabel = new Label { TextAlign = ContentAlignment.MiddleCenter, AccessibleName = "Estado de paginacion" };
            pager.Controls.AddRange(new Control[] { previousButton, pageLabel, pageSizeBox, nextButton });

            stateOverlay = new Label();
            stateOverlay.Dock = DockStyle.Fill;
            stateOverlay.TextAlign = ContentAlignment.MiddleCenter;
            stateOverlay.Visible = false;
            stateOverlay.AccessibleRole = AccessibleRole.StaticText;

            Controls.Add(grid);
            Controls.Add(stateOverlay);
            Controls.Add(pager);
            Controls.Add(filterBar);
            stateOverlay.BringToFront();

            applyFilterButton.Click += ApplyFilterClick;
            clearFilterButton.Click += delegate { ClearFilters(); };
            filterValue.KeyDown += FilterValueKeyDown;
            groupColumn.SelectedIndexChanged += GroupColumnChanged;
            previousButton.Click += delegate { PreviousPage(); };
            nextButton.Click += delegate { NextPage(); };
            pageSizeBox.SelectedIndexChanged += PageSizeChanged;
            grid.ColumnHeaderMouseClick += GridColumnHeaderMouseClick;
            grid.CellValueChanged += GridCellValueChanged;
            grid.CellContentClick += GridCellContentClick;
            grid.CellPainting += GridCellPainting;
            grid.RowPrePaint += GridRowPrePaint;
            grid.SelectionChanged += delegate { if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty); };
            grid.DataError += delegate(object sender, DataGridViewDataErrorEventArgs e) { e.ThrowException = false; };
            ApplyTheme(theme);
        }

        [Category("Datos"), DefaultValue(null)]
        public object DataSource
        {
            get { return dataSource; }
            set { if (ReferenceEquals(dataSource, value)) return; DetachSource(); dataSource = value; AttachSource(); ReloadData(); }
        }

        [Category("Datos"), DefaultValue(true)]
        public bool AutoGenerateColumns
        {
            get { return autoGenerateColumns; }
            set { autoGenerateColumns = value; }
        }

        [Category("Comportamiento"), DefaultValue(false)]
        public bool ReadOnly { get { return grid.ReadOnly; } set { grid.ReadOnly = value; } }

        [Category("Comportamiento"), DefaultValue(false)]
        public bool MultiSelect { get { return grid.MultiSelect; } set { grid.MultiSelect = value; } }

        [Category("Comportamiento"), DefaultValue(DataGridViewSelectionMode.FullRowSelect)]
        public DataGridViewSelectionMode SelectionMode { get { return grid.SelectionMode; } set { grid.SelectionMode = value; } }

        [Category("Paginacion"), DefaultValue(25)]
        public int PageSize
        {
            get { return pageSize; }
            set { int next = Math.Max(1, value); if (pageSize == next) return; pageSize = next; pageIndex = 0; SelectPageSize(next); RebuildView(); }
        }

        [Browsable(false)] public int PageIndex { get { return pageIndex; } }
        [Browsable(false)] public int PageCount { get { return pageCount; } }
        [Browsable(false)] public int FilteredCount { get { return filteredCount; } }

        [Category("Agrupacion"), DefaultValue("")]
        public string GroupByColumn
        {
            get { return groupByColumn; }
            set
            {
                string next = value ?? string.Empty;
                if (string.Equals(groupByColumn, next, StringComparison.OrdinalIgnoreCase)) return;
                groupByColumn = next;
                collapsedGroups.Clear();
                pageIndex = 0;
                SelectGroupColumn();
                RebuildView();
                if (GroupingChanged != null) GroupingChanged(this, EventArgs.Empty);
            }
        }

        [Category("Estado"), DefaultValue(ZarpaDataGridState.Normal)]
        public ZarpaDataGridState ViewState
        {
            get { return viewState; }
            set { viewState = value; UpdateStateOverlay(); }
        }

        [Category("Estado"), DefaultValue("No hay datos para mostrar.")]
        public string EmptyText { get { return emptyText; } set { emptyText = value ?? string.Empty; UpdateStateOverlay(); } }

        [Category("Estado"), DefaultValue("No fue posible cargar los datos.")]
        public string ErrorText { get { return errorText; } set { errorText = value ?? string.Empty; UpdateStateOverlay(); } }

        [Browsable(false)] public DataGridView InnerGrid { get { return grid; } }
        [Browsable(false)] public DataGridViewSelectedRowCollection SelectedRows { get { return grid.SelectedRows; } }
        [Browsable(false)] public object CurrentItem { get { SourceRow row = GetSourceRow(grid.CurrentRow); return row == null ? null : row.Item; } }

        [Category("Datos")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Content)]
        [Editor("ZarpaSuite.Controls.Design.ZarpaDataGridColumnCollectionEditor, Zarpa.Controls", typeof(System.Drawing.Design.UITypeEditor))]
        public DataGridViewColumnCollection Columns { get { return grid.Columns; } }

        public event EventHandler SelectionChanged;
        public event EventHandler FilterChanged;
        public event EventHandler GroupingChanged;
        public event EventHandler PageChanged;
        public event EventHandler DataChanged;
        public event EventHandler<ZarpaDataGridActionEventArgs> ActionClick;

        public void SetFilter(string columnName, string value)
        {
            if (string.IsNullOrEmpty(columnName)) throw new ArgumentException("Se requiere una columna.", "columnName");
            if (string.IsNullOrEmpty(value)) filters.Remove(columnName); else filters[columnName] = value.Trim();
            pageIndex = 0;
            SelectFilterColumn(columnName);
            filterValue.Text = value ?? string.Empty;
            RebuildView();
            if (FilterChanged != null) FilterChanged(this, EventArgs.Empty);
        }

        public string GetFilter(string columnName)
        {
            string value;
            return columnName != null && filters.TryGetValue(columnName, out value) ? value : string.Empty;
        }

        public void ClearFilters()
        {
            if (filters.Count == 0 && filterValue.TextLength == 0) return;
            filters.Clear();
            filterValue.Clear();
            pageIndex = 0;
            RebuildView();
            if (FilterChanged != null) FilterChanged(this, EventArgs.Empty);
        }

        public void ClearGrouping() { GroupByColumn = string.Empty; }
        public void NextPage() { SetPage(pageIndex + 1); }
        public void PreviousPage() { SetPage(pageIndex - 1); }

        public void ReloadData()
        {
            BuildSourceRows();
            BuildColumns();
            PopulateSelectors();
            pageIndex = 0;
            RebuildView();
            if (DataChanged != null) DataChanged(this, EventArgs.Empty);
        }

        public void ApplyTheme(ZarpaThemeTokens value)
        {
            if (value == null) return;
            theme = value;
            Font = new Font(theme.FontFamily, theme.FontSize);
            BackColor = theme.Border;
            ForeColor = theme.Text;
            filterBar.BackColor = theme.Surface;
            pager.BackColor = theme.Surface;
            stateOverlay.BackColor = theme.Surface;
            stateOverlay.ForeColor = theme.TextMuted;
            grid.BackgroundColor = theme.Surface;
            grid.GridColor = theme.Border;
            grid.DefaultCellStyle.BackColor = theme.Surface;
            grid.DefaultCellStyle.ForeColor = theme.Text;
            grid.DefaultCellStyle.SelectionBackColor = theme.Selection;
            grid.DefaultCellStyle.SelectionForeColor = theme.Text;
            grid.DefaultCellStyle.Padding = new Padding(theme.SpacingSmall, 0, theme.SpacingSmall, 0);
            grid.AlternatingRowsDefaultCellStyle.BackColor = ZarpaPaint.Blend(theme.Surface, theme.SurfaceRaised, .45F);
            grid.ColumnHeadersDefaultCellStyle.BackColor = theme.SurfaceRaised;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = theme.Text;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = theme.SurfaceRaised;
            grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = theme.Text;
            grid.ColumnHeadersDefaultCellStyle.Font = new Font(theme.FontFamily, theme.FontSize, FontStyle.Bold);
            grid.ColumnHeadersHeight = Math.Max(32, theme.ControlHeight + theme.SpacingSmall);
            grid.RowTemplate.Height = Math.Max(30, theme.ControlHeight);
            grid.Font = Font;
            foreach (Control control in filterBar.Controls) ThemeToolbarControl(control);
            foreach (Control control in pager.Controls) ThemeToolbarControl(control);
            filterBar.Height = theme.ControlHeight + theme.SpacingMedium + theme.BorderThickness;
            pager.Height = theme.ControlHeight + theme.SpacingMedium + theme.BorderThickness;
            PerformLayout();
            grid.Invalidate();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (filterBar == null) return;
            int gap = theme.SpacingSmall;
            int y = Math.Max(2, (filterBar.Height - theme.ControlHeight) / 2);
            filterLabel.Location = new Point(theme.SpacingMedium, y + 8);
            filterColumn.SetBounds(filterLabel.Right + gap, y, 130, theme.ControlHeight);
            int available = Math.Max(90, filterBar.Width - 620);
            filterValue.SetBounds(filterColumn.Right + gap, y, available, theme.ControlHeight);
            applyFilterButton.SetBounds(filterValue.Right + gap, y, 68, theme.ControlHeight);
            clearFilterButton.SetBounds(applyFilterButton.Right + gap, y, 70, theme.ControlHeight);
            groupLabel.Location = new Point(clearFilterButton.Right + theme.SpacingMedium, y + 8);
            groupColumn.SetBounds(groupLabel.Right + gap, y, Math.Max(105, filterBar.Width - groupLabel.Right - theme.SpacingMedium), theme.ControlHeight);

            int pagerY = Math.Max(2, (pager.Height - theme.ControlHeight) / 2);
            previousButton.SetBounds(theme.SpacingMedium, pagerY, 84, theme.ControlHeight);
            nextButton.SetBounds(pager.Width - theme.SpacingMedium - 84, pagerY, 84, theme.ControlHeight);
            pageSizeBox.SetBounds(nextButton.Left - gap - 64, pagerY, 64, theme.ControlHeight);
            pageLabel.SetBounds(previousButton.Right + gap, pagerY, Math.Max(80, pageSizeBox.Left - previousButton.Right - gap * 2), theme.ControlHeight);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) DetachSource();
            base.Dispose(disposing);
        }

        private void AttachSource()
        {
            BindingSource bindingSource = dataSource as BindingSource;
            object source = bindingSource == null ? dataSource : bindingSource.List;
            DataTable table = source as DataTable;
            DataView view = source as DataView;
            observedList = (table != null ? table.DefaultView : view) as IBindingList;
            if (observedList == null) observedList = source as IBindingList;
            if (observedList != null) observedList.ListChanged += ObservedListChanged;
        }

        private void DetachSource()
        {
            if (observedList != null) observedList.ListChanged -= ObservedListChanged;
            observedList = null;
        }

        private void ObservedListChanged(object sender, ListChangedEventArgs e)
        {
            if (!rebuilding) ReloadData();
        }

        private object EffectiveSource()
        {
            BindingSource bindingSource = dataSource as BindingSource;
            return bindingSource == null ? dataSource : bindingSource.List;
        }

        private void BuildSourceRows()
        {
            sourceRows.Clear();
            properties = null;
            object source = EffectiveSource();
            if (source == null) return;
            DataTable table = source as DataTable;
            DataView view = source as DataView;
            IEnumerable items;
            if (table != null) items = table.DefaultView;
            else if (view != null) items = view;
            else
            {
                items = source as IEnumerable;
                if (items == null) throw new ArgumentException("DataSource debe ser DataTable, DataView, BindingSource o una lista tipada.");
            }
            int index = 0;
            foreach (object item in items) sourceRows.Add(new SourceRow { Item = item, SourceIndex = index++ });
            if (table == null && view == null)
            {
                ITypedList typed = source as ITypedList;
                if (typed != null) properties = typed.GetItemProperties(null);
                else if (sourceRows.Count > 0) properties = TypeDescriptor.GetProperties(sourceRows[0].Item);
            }
        }

        private void BuildColumns()
        {
            if (!autoGenerateColumns || dataSource == null) return;
            rebuilding = true;
            try
            {
                List<DataGridViewColumn> configured = new List<DataGridViewColumn>();
                foreach (DataGridViewColumn existing in grid.Columns)
                    if (!generatedColumns.Contains(existing)) configured.Add(existing);
                foreach (DataGridViewColumn existing in configured) grid.Columns.Remove(existing);
                grid.Columns.Clear();
                generatedColumns.Clear();
                object source = EffectiveSource();
                DataTable table = source as DataTable;
                DataView view = source as DataView;
                if (table != null || view != null)
                {
                    DataColumnCollection columns = table != null ? table.Columns : view.Table.Columns;
                    foreach (DataColumn column in columns)
                    {
                        DataGridViewColumn existing = TakeConfiguredColumn(configured, column.ColumnName);
                        if (existing != null) grid.Columns.Add(existing);
                        else AddGeneratedColumn(column.ColumnName, column.Caption, column.DataType, column.ReadOnly);
                    }
                }
                else if (properties != null)
                    foreach (PropertyDescriptor property in properties)
                        if (property.IsBrowsable)
                        {
                            DataGridViewColumn existing = TakeConfiguredColumn(configured, property.Name);
                            if (existing != null) grid.Columns.Add(existing);
                            else AddGeneratedColumn(property.Name, property.DisplayName, property.PropertyType, property.IsReadOnly);
                        }
                foreach (DataGridViewColumn column in configured) grid.Columns.Add(column);
            }
            finally { rebuilding = false; }
        }

        private void AddGeneratedColumn(string name, string header, Type type, bool readOnly)
        {
            DataGridViewColumn column;
            Type actualType = Nullable.GetUnderlyingType(type) ?? type;
            if (actualType == typeof(bool)) column = new DataGridViewCheckBoxColumn();
            else column = new DataGridViewTextBoxColumn();
            column.Name = name;
            column.DataPropertyName = name;
            column.HeaderText = string.IsNullOrEmpty(header) ? name : header;
            column.ReadOnly = readOnly;
            column.SortMode = DataGridViewColumnSortMode.Programmatic;
            if (actualType == typeof(DateTime)) column.DefaultCellStyle.Format = "d";
            else if (IsNumericType(actualType)) column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            grid.Columns.Add(column);
            generatedColumns.Add(column);
        }

        private static DataGridViewColumn TakeConfiguredColumn(List<DataGridViewColumn> columns, string name)
        {
            for (int index = 0; index < columns.Count; index++)
            {
                DataGridViewColumn column = columns[index];
                if (!string.Equals(ColumnKey(column), name, StringComparison.OrdinalIgnoreCase)) continue;
                columns.RemoveAt(index);
                return column;
            }
            return null;
        }

        private void PopulateSelectors()
        {
            string selectedFilter = filterColumn.SelectedItem as string;
            filterColumn.Items.Clear();
            groupColumn.Items.Clear();
            groupColumn.Items.Add("(Sin agrupacion)");
            foreach (DataGridViewColumn column in grid.Columns)
            {
                if (column is ZarpaDataGridActionColumn) continue;
                string name = ColumnKey(column);
                filterColumn.Items.Add(name);
                groupColumn.Items.Add(name);
            }
            if (filterColumn.Items.Count > 0) filterColumn.SelectedItem = selectedFilter != null && filterColumn.Items.Contains(selectedFilter) ? selectedFilter : filterColumn.Items[0];
            SelectGroupColumn();
        }

        private void RebuildView()
        {
            if (rebuilding || grid == null) return;
            rebuilding = true;
            try
            {
                object selected = CurrentItem;
                List<SourceRow> rows = sourceRows.FindAll(MatchesFilters);
                filteredCount = rows.Count;
                if (!string.IsNullOrEmpty(sortColumn)) rows.Sort(CompareRows);
                else if (!string.IsNullOrEmpty(groupByColumn)) rows.Sort(CompareGroups);
                pageCount = Math.Max(1, (int)Math.Ceiling(rows.Count / (double)pageSize));
                pageIndex = Math.Max(0, Math.Min(pageIndex, pageCount - 1));
                int start = pageIndex * pageSize;
                int end = Math.Min(rows.Count, start + pageSize);
                grid.Rows.Clear();
                string lastGroup = null;
                for (int i = start; i < end; i++)
                {
                    SourceRow row = rows[i];
                    if (!string.IsNullOrEmpty(groupByColumn))
                    {
                        string group = DisplayValue(GetValue(row.Item, groupByColumn));
                        if (!string.Equals(lastGroup, group, StringComparison.CurrentCulture))
                        {
                            lastGroup = group;
                            int count = CountGroup(rows, group);
                            int groupIndex = grid.Rows.Add();
                            grid.Rows[groupIndex].Tag = new GroupRow { Key = group, Count = count };
                            grid.Rows[groupIndex].ReadOnly = true;
                            grid.Rows[groupIndex].Height = Math.Max(grid.RowTemplate.Height, theme.ControlHeight);
                        }
                        if (collapsedGroups.Contains(group)) continue;
                    }
                    object[] values = new object[grid.Columns.Count];
                    for (int columnIndex = 0; columnIndex < grid.Columns.Count; columnIndex++)
                    {
                        DataGridViewColumn column = grid.Columns[columnIndex];
                        if (!(column is ZarpaDataGridActionColumn)) values[columnIndex] = GetValue(row.Item, ColumnKey(column));
                    }
                    int rowIndex = grid.Rows.Add(values);
                    grid.Rows[rowIndex].Tag = row;
                    if (ReferenceEquals(row.Item, selected)) grid.CurrentCell = FirstVisibleCell(grid.Rows[rowIndex]);
                }
                previousButton.Enabled = pageIndex > 0;
                nextButton.Enabled = pageIndex + 1 < pageCount;
                pageLabel.Text = string.Format(CultureInfo.CurrentCulture, "Pagina {0} de {1}  |  {2} registros", pageIndex + 1, pageCount, filteredCount);
                UpdateStateOverlay();
            }
            finally { rebuilding = false; }
        }

        private bool MatchesFilters(SourceRow row)
        {
            foreach (KeyValuePair<string, string> filter in filters)
            {
                string actual = DisplayValue(GetValue(row.Item, filter.Key));
                if (actual.IndexOf(filter.Value, StringComparison.CurrentCultureIgnoreCase) < 0) return false;
            }
            return true;
        }

        private int CompareRows(SourceRow left, SourceRow right)
        {
            int result = CompareValues(GetValue(left.Item, sortColumn), GetValue(right.Item, sortColumn));
            return sortDirection == ListSortDirection.Ascending ? result : -result;
        }

        private int CompareGroups(SourceRow left, SourceRow right)
        {
            int result = CompareValues(GetValue(left.Item, groupByColumn), GetValue(right.Item, groupByColumn));
            return result != 0 ? result : left.SourceIndex.CompareTo(right.SourceIndex);
        }

        private static int CompareValues(object left, object right)
        {
            if (left == DBNull.Value) left = null;
            if (right == DBNull.Value) right = null;
            if (left == null) return right == null ? 0 : -1;
            if (right == null) return 1;
            IComparable comparable = left as IComparable;
            if (comparable != null && left.GetType().IsAssignableFrom(right.GetType())) return comparable.CompareTo(right);
            return string.Compare(DisplayValue(left), DisplayValue(right), StringComparison.CurrentCultureIgnoreCase);
        }

        private int CountGroup(List<SourceRow> rows, string group)
        {
            int count = 0;
            foreach (SourceRow row in rows)
                if (string.Equals(DisplayValue(GetValue(row.Item, groupByColumn)), group, StringComparison.CurrentCulture)) count++;
            return count;
        }

        private object GetValue(object item, string name)
        {
            if (item == null || string.IsNullOrEmpty(name)) return null;
            DataRowView rowView = item as DataRowView;
            if (rowView != null && rowView.DataView.Table.Columns.Contains(name)) return rowView[name];
            DataRow row = item as DataRow;
            if (row != null && row.Table.Columns.Contains(name)) return row[name];
            PropertyDescriptor property = properties == null ? TypeDescriptor.GetProperties(item)[name] : properties[name];
            return property == null ? null : property.GetValue(item);
        }

        private void SetValue(object item, string name, object value)
        {
            DataRowView rowView = item as DataRowView;
            if (rowView != null) { rowView[name] = value ?? DBNull.Value; return; }
            DataRow row = item as DataRow;
            if (row != null) { row[name] = value ?? DBNull.Value; return; }
            PropertyDescriptor property = properties == null ? TypeDescriptor.GetProperties(item)[name] : properties[name];
            if (property == null || property.IsReadOnly) return;
            object converted = value;
            Type target = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (value != null && !target.IsInstanceOfType(value)) converted = Convert.ChangeType(value, target, CultureInfo.CurrentCulture);
            property.SetValue(item, converted);
        }

        private void ApplyFilterClick(object sender, EventArgs e)
        {
            string column = filterColumn.SelectedItem as string;
            if (!string.IsNullOrEmpty(column)) SetFilter(column, filterValue.Text);
        }

        private void FilterValueKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter) return;
            ApplyFilterClick(sender, EventArgs.Empty);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        private void GroupColumnChanged(object sender, EventArgs e)
        {
            if (rebuilding) return;
            string selected = groupColumn.SelectedItem as string;
            GroupByColumn = groupColumn.SelectedIndex <= 0 ? string.Empty : selected;
        }

        private void PageSizeChanged(object sender, EventArgs e)
        {
            if (rebuilding || pageSizeBox.SelectedItem == null) return;
            PageSize = Convert.ToInt32(pageSizeBox.SelectedItem, CultureInfo.InvariantCulture);
        }

        private void SetPage(int value)
        {
            int next = Math.Max(0, Math.Min(value, pageCount - 1));
            if (pageIndex == next) return;
            pageIndex = next;
            RebuildView();
            if (PageChanged != null) PageChanged(this, EventArgs.Empty);
        }

        private void GridColumnHeaderMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.ColumnIndex < 0 || grid.Columns[e.ColumnIndex] is ZarpaDataGridActionColumn) return;
            string key = ColumnKey(grid.Columns[e.ColumnIndex]);
            if (string.Equals(sortColumn, key, StringComparison.OrdinalIgnoreCase))
                sortDirection = sortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
            else { sortColumn = key; sortDirection = ListSortDirection.Ascending; }
            foreach (DataGridViewColumn column in grid.Columns) column.HeaderCell.SortGlyphDirection = SortOrder.None;
            grid.Columns[e.ColumnIndex].HeaderCell.SortGlyphDirection = sortDirection == ListSortDirection.Ascending ? SortOrder.Ascending : SortOrder.Descending;
            pageIndex = 0;
            RebuildView();
        }

        private void GridCellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (rebuilding || e.RowIndex < 0 || e.ColumnIndex < 0) return;
            SourceRow row = GetSourceRow(grid.Rows[e.RowIndex]);
            if (row == null || grid.Columns[e.ColumnIndex].ReadOnly) return;
            try { SetValue(row.Item, ColumnKey(grid.Columns[e.ColumnIndex]), grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value); }
            catch (Exception ex) { ViewState = ZarpaDataGridState.Error; ErrorText = ex.Message; }
        }

        private void GridCellContentClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            GroupRow group = grid.Rows[e.RowIndex].Tag as GroupRow;
            if (group != null)
            {
                if (!collapsedGroups.Add(group.Key)) collapsedGroups.Remove(group.Key);
                RebuildView();
                return;
            }
            ZarpaDataGridActionColumn action = e.ColumnIndex < 0 ? null : grid.Columns[e.ColumnIndex] as ZarpaDataGridActionColumn;
            SourceRow row = GetSourceRow(grid.Rows[e.RowIndex]);
            if (action != null && row != null && ActionClick != null)
                ActionClick(this, new ZarpaDataGridActionEventArgs(action.ActionKey, row.Item, e.RowIndex));
        }

        private void GridRowPrePaint(object sender, DataGridViewRowPrePaintEventArgs e)
        {
            GroupRow group = grid.Rows[e.RowIndex].Tag as GroupRow;
            if (group == null) return;
            e.Handled = true;
            Rectangle bounds = new Rectangle(e.RowBounds.X, e.RowBounds.Y, e.RowBounds.Width, e.RowBounds.Height - 1);
            using (SolidBrush brush = new SolidBrush(theme.SurfaceRaised)) e.Graphics.FillRectangle(brush, bounds);
            using (Pen pen = new Pen(theme.Border)) e.Graphics.DrawLine(pen, bounds.Left, bounds.Bottom, bounds.Right, bounds.Bottom);
            string marker = collapsedGroups.Contains(group.Key) ? ">" : "v";
            string text = string.Format(CultureInfo.CurrentCulture, "{0}  {1} ({2})", marker, string.IsNullOrEmpty(group.Key) ? "(Sin valor)" : group.Key, group.Count);
            TextRenderer.DrawText(e.Graphics, text, grid.ColumnHeadersDefaultCellStyle.Font, new Rectangle(bounds.Left + theme.SpacingMedium, bounds.Top, bounds.Width - theme.SpacingLarge, bounds.Height), theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        private void GridCellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || grid.Rows[e.RowIndex].Tag is GroupRow) return;
            ZarpaDataGridActionColumn action = grid.Columns[e.ColumnIndex] as ZarpaDataGridActionColumn;
            if (action == null) return;
            e.PaintBackground(e.CellBounds, false);
            Rectangle button = Rectangle.Inflate(e.CellBounds, -theme.SpacingSmall, -theme.SpacingSmall);
            ZarpaPaint.FillRounded(e.Graphics, theme.SurfaceRaised, button, theme.CornerRadius);
            ZarpaPaint.DrawRounded(e.Graphics, theme.Border, button, theme.CornerRadius, theme.BorderThickness);
            int left = button.Left + theme.SpacingMedium;
            if (!string.IsNullOrEmpty(action.IconKey))
            {
                Rectangle icon = new Rectangle(left, button.Top + (button.Height - theme.IconSize) / 2, theme.IconSize, theme.IconSize);
                FluentIconCatalog.TryDraw(e.Graphics, action.IconKey, icon, theme.Accent, theme.IconSize - 2F);
                left = icon.Right + theme.SpacingSmall;
            }
            TextRenderer.DrawText(e.Graphics, action.Text, Font, new Rectangle(left, button.Top, button.Right - left - theme.SpacingSmall, button.Height), theme.Accent, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            e.Handled = true;
        }

        private void UpdateStateOverlay()
        {
            if (stateOverlay == null) return;
            bool automaticEmpty = viewState == ZarpaDataGridState.Normal && filteredCount == 0;
            stateOverlay.Visible = viewState != ZarpaDataGridState.Normal || automaticEmpty;
            if (!stateOverlay.Visible) return;
            if (viewState == ZarpaDataGridState.Loading) stateOverlay.Text = "Cargando datos...";
            else if (viewState == ZarpaDataGridState.Error) stateOverlay.Text = errorText;
            else stateOverlay.Text = emptyText;
            stateOverlay.BringToFront();
        }

        private void ThemeToolbarControl(Control control)
        {
            control.Font = Font;
            control.ForeColor = theme.Text;
            control.BackColor = control is Button ? theme.SurfaceRaised : theme.Surface;
            Button button = control as Button;
            if (button != null) { button.FlatAppearance.BorderColor = theme.Border; button.FlatAppearance.BorderSize = theme.BorderThickness; }
        }

        private void SelectPageSize(int value)
        {
            if (pageSizeBox.Items.Contains(value)) pageSizeBox.SelectedItem = value;
            else { pageSizeBox.Items.Add(value); pageSizeBox.SelectedItem = value; }
        }

        private void SelectFilterColumn(string name)
        {
            if (filterColumn.Items.Contains(name)) filterColumn.SelectedItem = name;
        }

        private void SelectGroupColumn()
        {
            if (groupColumn.Items.Count == 0) return;
            rebuilding = true;
            try { groupColumn.SelectedItem = string.IsNullOrEmpty(groupByColumn) || !groupColumn.Items.Contains(groupByColumn) ? groupColumn.Items[0] : groupByColumn; }
            finally { rebuilding = false; }
        }

        private static SourceRow GetSourceRow(DataGridViewRow row) { return row == null ? null : row.Tag as SourceRow; }
        private static string ColumnKey(DataGridViewColumn column) { return string.IsNullOrEmpty(column.DataPropertyName) ? column.Name : column.DataPropertyName; }
        private static string DisplayValue(object value) { return value == null || value == DBNull.Value ? string.Empty : Convert.ToString(value, CultureInfo.CurrentCulture); }
        private static bool IsNumericType(Type type) { return type == typeof(byte) || type == typeof(short) || type == typeof(int) || type == typeof(long) || type == typeof(float) || type == typeof(double) || type == typeof(decimal); }

        private static DataGridViewCell FirstVisibleCell(DataGridViewRow row)
        {
            foreach (DataGridViewCell cell in row.Cells) if (cell.Visible) return cell;
            return null;
        }
    }
}

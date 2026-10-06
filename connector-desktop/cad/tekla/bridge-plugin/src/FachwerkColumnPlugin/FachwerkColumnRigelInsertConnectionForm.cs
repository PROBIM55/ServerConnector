#nullable disable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using Tekla.Structures.Dialog;

namespace Structura.Tekla.Fachwerk;

public sealed class FachwerkColumnRigelInsertConnectionForm : PluginFormBase
{
    private Control _attributeFkriSide;
    private Control _attributeFkriUpCtrl;
    private Control _attributeFkriLoCtrl;
    private Control _attributeFkriUpOfDep;
    private Control _attributeFkriUpIfDep;
    private Control _attributeFkriUpWDep;
    private Control _attributeFkriLoOfDep;
    private Control _attributeFkriLoIfDep;
    private Control _attributeFkriLoWDep;
    private Control _attributeFkriFlH;
    private Control _attributeFkriWebAdd;
    private Control _attributeFkriOverlap;
    private Control _attributeFkriInsfAng;
    private Control _attributeFkriPartfAng;
    private Control _attributeFkriInswAng;
    private Control _attributeFkriPartwAng;
    private Control _attributeFkriDirectAng;
    private Control _attributeFkriRoot;

    private Control _filterFkriSide;
    private Control _filterFkriUpCtrl;
    private Control _filterFkriLoCtrl;
    private Control _filterFkriUpOfDep;
    private Control _filterFkriUpIfDep;
    private Control _filterFkriUpWDep;
    private Control _filterFkriLoOfDep;
    private Control _filterFkriLoIfDep;
    private Control _filterFkriLoWDep;
    private Control _filterFkriFlH;
    private Control _filterFkriWebAdd;
    private Control _filterFkriOverlap;
    private Control _filterFkriInsfAng;
    private Control _filterFkriPartfAng;
    private Control _filterFkriInswAng;
    private Control _filterFkriPartwAng;
    private Control _filterFkriDirectAng;
    private Control _filterFkriRoot;

    private readonly List<CheckBox> _filters = new();

    public FachwerkColumnRigelInsertConnectionForm()
    {
        Text = "Примыкание стойки к ригелю со вставками";
        Width = 740;
        Height = 660;
        MinimumSize = new Size(680, 540);
        StartPosition = FormStartPosition.CenterScreen;
        RegisterPropertyBinding(
            typeof(TextBox),
            "Text",
            DataSourceUpdateMode.OnPropertyChanged);
        BuildLayout();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        root.Controls.Add(new Label
        {
            Text = "Примыкание верхней и нижней секций к ригелю со вставками",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 10),
        }, 0, 0);

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
        };
        root.Controls.Add(tabs, 0, 1);

        TableLayoutPanel geometry = AddFieldsTab(tabs, "Вставки");
        AddSection(geometry, "Общие параметры");
        AddStringCombo(
            geometry,
            "Сторона вставки стенки",
            "fkri_side",
            new[]
            {
                new ComboOption("Левая", "LEFT"),
                new ComboOption("Правая", "RIGHT"),
            },
            "LEFT");
        AddStringCombo(
            geometry,
            "Определяющий пояс верхней секции",
            "fkri_up_ctrl",
            new[]
            {
                new ComboOption("Внутренний", "INNER"),
                new ComboOption("Наружный", "OUTER"),
            },
            "INNER");
        AddStringCombo(
            geometry,
            "Определяющий пояс нижней секции",
            "fkri_lo_ctrl",
            new[]
            {
                new ComboOption("Внутренний", "INNER"),
                new ComboOption("Наружный", "OUTER"),
            },
            "INNER");
        AddDouble(
            geometry,
            "Минимальная высота вставки пояса, мм",
            "fkri_fl_h",
            250);
        AddDouble(
            geometry,
            "Превышение вставки стенки, мм",
            "fkri_web_add",
            150);
        AddDouble(
            geometry,
            "Заход деталей во вставки, мм",
            "fkri_overlap",
            30);
        AddSection(geometry, "Выравнивание вставок верхней секции");
        AddDepthCombo(geometry, "Наружный пояс", "fkri_up_of_dep");
        AddDepthCombo(geometry, "Внутренний пояс", "fkri_up_if_dep");
        AddDepthCombo(geometry, "Стенка", "fkri_up_w_dep");
        AddSection(geometry, "Выравнивание вставок нижней секции");
        AddDepthCombo(geometry, "Наружный пояс", "fkri_lo_of_dep");
        AddDepthCombo(geometry, "Внутренний пояс", "fkri_lo_if_dep");
        AddDepthCombo(geometry, "Стенка", "fkri_lo_w_dep");
        AddNote(
            geometry,
            "Параметры применяются зеркально к верхней и нижней секциям. Контактные кромки повторяют фактические грани ригеля.");

        TableLayoutPanel bevels = AddFieldsTab(tabs, "Разделки");
        AddSection(bevels, "Поперечные разделки");
        AddDouble(bevels, "Вставки поясов, град", "fkri_insf_ang", 40);
        AddDouble(bevels, "Пояса стойки, град", "fkri_partf_ang", 40);
        AddDouble(bevels, "Вставка стенки, град", "fkri_insw_ang", 40);
        AddDouble(bevels, "Стенка со вставкой, град", "fkri_partw_ang", 40);
        AddDouble(bevels, "Стенка без вставки, град", "fkri_direct_ang", 40);
        AddDouble(bevels, "Притупление поперечных торцов, мм", "fkri_root", 2);
        AddSection(bevels, "Вертикальные разделки стенок");
        AddNote(
            bevels,
            "Вертикальные разделки вставки стенки и стенки без вставки выполняются под 45 градусов с притуплением 13 мм. На вставке разделка идет на всю ее высоту; на стенке без вставки - от ригеля до фактической плоскости низа поясов.");
        AddNote(
            bevels,
            "Разделки вставок и деталей со вставками направлены наружу. Разделка стенки без вставки направлена внутрь коробчатого сечения.");

        root.Controls.Add(BuildButtons(), 0, 2);
    }

    private static TableLayoutPanel AddFieldsTab(TabControl tabs, string title)
    {
        var tab = new TabPage
        {
            Text = title,
            Padding = new Padding(6),
            UseVisualStyleBackColor = true,
        };
        var fields = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
        };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 24));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        tab.Controls.Add(fields);
        tabs.TabPages.Add(tab);
        return fields;
    }

    private Control BuildButtons()
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 10, 0, 0),
        };
        panel.Controls.Add(Button("Закрыть", (_, _) => Close()));
        panel.Controls.Add(Button("Изменить", (_, _) => ExecuteUiAction(Modify)));
        panel.Controls.Add(Button("Применить", (_, _) => ExecuteUiAction(Apply)));
        panel.Controls.Add(Button("Получить", (_, _) => ExecuteUiAction(Get)));
        panel.Controls.Add(Button("ОК", (_, _) => ExecuteUiAction(() =>
        {
            Apply();
            Close();
        })));
        panel.Controls.Add(Button("Включить все", (_, _) => SetAllFilters(true)));
        panel.Controls.Add(Button("Выключить все", (_, _) => SetAllFilters(false)));
        return panel;
    }

    private static Button Button(string text, EventHandler handler)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            Margin = new Padding(5),
        };
        button.Click += handler;
        return button;
    }

    private void SetAllFilters(bool enabled)
    {
        foreach (CheckBox filter in _filters)
            filter.Checked = enabled;
    }

    private void AddSection(TableLayoutPanel panel, string text)
    {
        var row = panel.RowCount++;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 14, 0, 6),
        };
        panel.Controls.Add(label, 0, row);
        panel.SetColumnSpan(label, 3);
    }

    private static void AddNote(TableLayoutPanel panel, string text)
    {
        var row = panel.RowCount++;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            MaximumSize = new Size(620, 0),
            Margin = new Padding(28, 3, 3, 8),
        };
        panel.Controls.Add(label, 0, row);
        panel.SetColumnSpan(label, 3);
    }

    private void AddDouble(
        TableLayoutPanel panel,
        string label,
        string name,
        double value)
    {
        AddText(
            panel,
            label,
            name,
            "Double",
            value.ToString("0.###", CultureInfo.InvariantCulture));
    }

    private void AddText(
        TableLayoutPanel panel,
        string labelText,
        string name,
        string typeName,
        string value)
    {
        var row = panel.RowCount++;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        CheckBox filter = CreateFilter(name);
        var input = new TextBox
        {
            Name = name,
            Text = value ?? string.Empty,
            Dock = DockStyle.Fill,
            Margin = new Padding(4),
        };
        structuresExtender.SetAttributeName(input, name);
        structuresExtender.SetAttributeTypeName(input, typeName);
        structuresExtender.SetBindPropertyName(input, "Text");
        AssignAttributeControl(name, input);
        panel.Controls.Add(filter, 0, row);
        panel.Controls.Add(CreateFieldLabel(labelText), 1, row);
        panel.Controls.Add(input, 2, row);
    }

    private void AddStringCombo(
        TableLayoutPanel panel,
        string labelText,
        string name,
        IReadOnlyList<ComboOption> options,
        string defaultValue)
    {
        var row = panel.RowCount++;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        CheckBox filter = CreateFilter(name);
        var combo = new ComboBox
        {
            Name = name + "_combo",
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Fill,
            Margin = new Padding(4),
            DisplayMember = nameof(ComboOption.Label),
        };
        foreach (ComboOption option in options)
            combo.Items.Add(option);

        var storage = new TextBox
        {
            Name = name,
            Text = defaultValue,
            Visible = false,
        };
        structuresExtender.SetAttributeName(storage, name);
        structuresExtender.SetAttributeTypeName(storage, "String");
        structuresExtender.SetBindPropertyName(storage, "Text");
        AssignAttributeControl(name, storage);
        Controls.Add(storage);

        SelectComboValue(combo, options, defaultValue);
        combo.SelectedIndexChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboOption selected)
            {
                AssignComboStorageText(storage, selected.Value);
                SetAttributeValue(storage, selected.Value);
            }
        };
        AttributesLoadedFromModel += (_, _) =>
            SelectComboValue(combo, options, storage.Text);
        panel.Controls.Add(filter, 0, row);
        panel.Controls.Add(CreateFieldLabel(labelText), 1, row);
        panel.Controls.Add(combo, 2, row);
    }

    internal static void AssignComboStorageText(TextBox storage, string value)
    {
        if (storage == null) throw new ArgumentNullException(nameof(storage));
        storage.Text = value ?? string.Empty;
    }

    private void AddDepthCombo(
        TableLayoutPanel panel,
        string labelText,
        string attributeName)
    {
        AddStringCombo(
            panel,
            labelText,
            attributeName,
            new[]
            {
                new ComboOption("Авто наружу", "AUTO"),
                new ComboOption("Спереди", "FRONT"),
                new ComboOption("Посередине", "MIDDLE"),
                new ComboOption("Позади", "BEHIND"),
            },
            "AUTO");
    }

    private CheckBox CreateFilter(string attributeName)
    {
        var filter = new CheckBox
        {
            Name = attributeName + "_filter",
            Checked = true,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            TabStop = false,
            Margin = new Padding(0, 3, 4, 3),
        };
        structuresExtender.SetAttributeName(filter, attributeName);
        structuresExtender.SetIsFilter(filter, true);
        AssignFilterControl(attributeName, filter);
        _filters.Add(filter);
        return filter;
    }

    private static Label CreateFieldLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(4),
    };

    private void AssignAttributeControl(string name, Control control)
    {
        switch (name)
        {
            case "fkri_side": _attributeFkriSide = control; break;
            case "fkri_up_ctrl": _attributeFkriUpCtrl = control; break;
            case "fkri_lo_ctrl": _attributeFkriLoCtrl = control; break;
            case "fkri_up_of_dep": _attributeFkriUpOfDep = control; break;
            case "fkri_up_if_dep": _attributeFkriUpIfDep = control; break;
            case "fkri_up_w_dep": _attributeFkriUpWDep = control; break;
            case "fkri_lo_of_dep": _attributeFkriLoOfDep = control; break;
            case "fkri_lo_if_dep": _attributeFkriLoIfDep = control; break;
            case "fkri_lo_w_dep": _attributeFkriLoWDep = control; break;
            case "fkri_fl_h": _attributeFkriFlH = control; break;
            case "fkri_web_add": _attributeFkriWebAdd = control; break;
            case "fkri_overlap": _attributeFkriOverlap = control; break;
            case "fkri_insf_ang": _attributeFkriInsfAng = control; break;
            case "fkri_partf_ang": _attributeFkriPartfAng = control; break;
            case "fkri_insw_ang": _attributeFkriInswAng = control; break;
            case "fkri_partw_ang": _attributeFkriPartwAng = control; break;
            case "fkri_direct_ang": _attributeFkriDirectAng = control; break;
            case "fkri_root": _attributeFkriRoot = control; break;
            default: throw new InvalidOperationException(
                "Не зарегистрировано поле формы: " + name);
        }
    }

    private void AssignFilterControl(string name, Control control)
    {
        switch (name)
        {
            case "fkri_side": _filterFkriSide = control; break;
            case "fkri_up_ctrl": _filterFkriUpCtrl = control; break;
            case "fkri_lo_ctrl": _filterFkriLoCtrl = control; break;
            case "fkri_up_of_dep": _filterFkriUpOfDep = control; break;
            case "fkri_up_if_dep": _filterFkriUpIfDep = control; break;
            case "fkri_up_w_dep": _filterFkriUpWDep = control; break;
            case "fkri_lo_of_dep": _filterFkriLoOfDep = control; break;
            case "fkri_lo_if_dep": _filterFkriLoIfDep = control; break;
            case "fkri_lo_w_dep": _filterFkriLoWDep = control; break;
            case "fkri_fl_h": _filterFkriFlH = control; break;
            case "fkri_web_add": _filterFkriWebAdd = control; break;
            case "fkri_overlap": _filterFkriOverlap = control; break;
            case "fkri_insf_ang": _filterFkriInsfAng = control; break;
            case "fkri_partf_ang": _filterFkriPartfAng = control; break;
            case "fkri_insw_ang": _filterFkriInswAng = control; break;
            case "fkri_partw_ang": _filterFkriPartwAng = control; break;
            case "fkri_direct_ang": _filterFkriDirectAng = control; break;
            case "fkri_root": _filterFkriRoot = control; break;
            default: throw new InvalidOperationException(
                "Не зарегистрирован фильтр формы: " + name);
        }
    }

    private static void SelectComboValue(
        ComboBox combo,
        IReadOnlyList<ComboOption> options,
        string value)
    {
        for (var index = 0; index < options.Count; index++)
        {
            if (string.Equals(
                    options[index].Value,
                    value,
                    StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = index;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }

    private static void ExecuteUiAction(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            Trace(exception.ToString());
            MessageBox.Show(
                exception.Message,
                "Примыкание стойки к ригелю со вставками",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static void Trace(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(
                    Path.GetTempPath(),
                    "fachwerk_column_rigel_insert_connection_form_trace.txt"),
                DateTime.Now.ToString("s", CultureInfo.InvariantCulture) +
                " " + message + Environment.NewLine);
        }
        catch
        {
        }
    }

    private sealed class ComboOption
    {
        internal ComboOption(string label, string value)
        {
            Label = label;
            Value = value;
        }

        public string Label { get; }
        public string Value { get; }
    }
}

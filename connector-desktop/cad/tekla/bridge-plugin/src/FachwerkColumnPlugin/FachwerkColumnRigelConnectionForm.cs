#nullable disable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using Tekla.Structures.Dialog;

namespace Structura.Tekla.Fachwerk;

public sealed class FachwerkColumnRigelConnectionForm : PluginFormBase
{
    // Tekla 2020 scans direct non-public Control fields when registering
    // component attributes. Keep these fields even though the layout is built
    // dynamically.
    private Control _attributeFkrcPenetration;
    private Control _attributeFkrcBevelEnabled;
    private Control _attributeFkrcBevelAngle;
    private Control _attributeFkrcBevelRoot;
    private Control _attributeFkrcIfAngle;
    private Control _attributeFkrcIfRoot;
    private Control _attributeFkrcLwAngle;
    private Control _attributeFkrcLwRoot;
    private Control _attributeFkrcRwAngle;
    private Control _attributeFkrcRwRoot;

    private Control _filterFkrcPenetration;
    private Control _filterFkrcBevelEnabled;
    private Control _filterFkrcBevelAngle;
    private Control _filterFkrcBevelRoot;
    private Control _filterFkrcIfAngle;
    private Control _filterFkrcIfRoot;
    private Control _filterFkrcLwAngle;
    private Control _filterFkrcLwRoot;
    private Control _filterFkrcRwAngle;
    private Control _filterFkrcRwRoot;

    private readonly List<CheckBox> _filters = new();

    public FachwerkColumnRigelConnectionForm()
    {
        Text = "Примыкание стойки к ригелю";
        Width = 700;
        Height = 650;
        MinimumSize = new Size(650, 520);
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
            Text = "Подгонка восьми деталей стойки к фактическим граням ригеля",
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

        var fitting = AddFieldsTab(tabs, "Подгонка");
        AddSection(fitting, "Верхняя секция");
        AddDouble(
            fitting,
            "Заход верхних деталей внутрь ригеля, мм",
            "fkrc_penetration",
            30);
        AddNote(
            fitting,
            "Плоскость получается смещением фактической верхней грани ригеля внутрь его тела.");

        var bevel = AddFieldsTab(tabs, "Разделки");
        AddSection(bevel, "Нижняя секция");
        AddStringCombo(
            bevel,
            "Создавать разделки",
            "fkrc_bevel_enabled",
            new[]
            {
                new ComboOption("Да", "YES"),
                new ComboOption("Нет", "NO"),
            },
            "YES");
        AddSection(bevel, "Наружный пояс");
        AddDouble(
            bevel,
            "Угол разделки, град",
            "fkrc_bevel_angle",
            40);
        AddDouble(
            bevel,
            "Притупление, мм",
            "fkrc_bevel_root",
            2);
        AddSection(bevel, "Внутренний пояс");
        AddDouble(
            bevel,
            "Угол разделки, град",
            "fkrc_if_angle",
            40);
        AddDouble(
            bevel,
            "Притупление, мм",
            "fkrc_if_root",
            2);
        AddSection(bevel, "Левая стенка");
        AddDouble(
            bevel,
            "Угол разделки, град",
            "fkrc_lw_angle",
            40);
        AddDouble(
            bevel,
            "Притупление, мм",
            "fkrc_lw_root",
            2);
        AddSection(bevel, "Правая стенка");
        AddDouble(
            bevel,
            "Угол разделки, град",
            "fkrc_rw_angle",
            40);
        AddDouble(
            bevel,
            "Притупление, мм",
            "fkrc_rw_root",
            2);
        AddNote(
            bevel,
            "Параметры применяются по FK_ROLE детали независимо от порядка выбора.");

        root.Controls.Add(BuildButtons(), 0, 2);
    }

    private static TableLayoutPanel AddFieldsTab(
        TabControl tabs,
        string title)
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
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
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
        foreach (var filter in _filters)
        {
            filter.Checked = enabled;
        }
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
            MaximumSize = new Size(550, 0),
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
        var filter = CreateFilter(name);
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
        var filter = CreateFilter(name);
        var combo = new ComboBox
        {
            Name = name + "_combo",
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Fill,
            Margin = new Padding(4),
            DisplayMember = nameof(ComboOption.Label),
        };
        foreach (var option in options)
        {
            combo.Items.Add(option);
        }

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
                SetAttributeValue(storage, selected.Value);
            }
        };
        AttributesLoadedFromModel += (_, _) =>
            SelectComboValue(combo, options, storage.Text);

        panel.Controls.Add(filter, 0, row);
        panel.Controls.Add(CreateFieldLabel(labelText), 1, row);
        panel.Controls.Add(combo, 2, row);
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
            case "fkrc_penetration":
                _attributeFkrcPenetration = control;
                break;
            case "fkrc_bevel_enabled":
                _attributeFkrcBevelEnabled = control;
                break;
            case "fkrc_bevel_angle":
                _attributeFkrcBevelAngle = control;
                break;
            case "fkrc_bevel_root":
                _attributeFkrcBevelRoot = control;
                break;
            case "fkrc_if_angle":
                _attributeFkrcIfAngle = control;
                break;
            case "fkrc_if_root":
                _attributeFkrcIfRoot = control;
                break;
            case "fkrc_lw_angle":
                _attributeFkrcLwAngle = control;
                break;
            case "fkrc_lw_root":
                _attributeFkrcLwRoot = control;
                break;
            case "fkrc_rw_angle":
                _attributeFkrcRwAngle = control;
                break;
            case "fkrc_rw_root":
                _attributeFkrcRwRoot = control;
                break;
            default:
                throw new InvalidOperationException(
                    "Не зарегистрировано поле формы: " + name);
        }
    }

    private void AssignFilterControl(string name, Control control)
    {
        switch (name)
        {
            case "fkrc_penetration":
                _filterFkrcPenetration = control;
                break;
            case "fkrc_bevel_enabled":
                _filterFkrcBevelEnabled = control;
                break;
            case "fkrc_bevel_angle":
                _filterFkrcBevelAngle = control;
                break;
            case "fkrc_bevel_root":
                _filterFkrcBevelRoot = control;
                break;
            case "fkrc_if_angle":
                _filterFkrcIfAngle = control;
                break;
            case "fkrc_if_root":
                _filterFkrcIfRoot = control;
                break;
            case "fkrc_lw_angle":
                _filterFkrcLwAngle = control;
                break;
            case "fkrc_lw_root":
                _filterFkrcLwRoot = control;
                break;
            case "fkrc_rw_angle":
                _filterFkrcRwAngle = control;
                break;
            case "fkrc_rw_root":
                _filterFkrcRwRoot = control;
                break;
            default:
                throw new InvalidOperationException(
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
            if (!string.Equals(
                    options[index].Value,
                    value,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            combo.SelectedIndex = index;
            return;
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
                "Примыкание стойки к ригелю",
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
                    "fachwerk_column_rigel_connection_form_trace.txt"),
                DateTime.Now.ToString("s", CultureInfo.InvariantCulture) +
                " " + message + Environment.NewLine);
        }
        catch
        {
        }
    }

    private sealed class ComboOption
    {
        public ComboOption(string label, string value)
        {
            Label = label;
            Value = value;
        }

        public string Label { get; }
        public string Value { get; }
    }
}

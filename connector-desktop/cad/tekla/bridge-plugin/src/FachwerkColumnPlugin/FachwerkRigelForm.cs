#nullable disable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Tekla.Structures.Dialog;

namespace Structura.Tekla.Fachwerk;

#pragma warning disable 0169
public sealed class FachwerkRigelForm : PluginFormBase
{
    // Tekla 2020 registers attributes by scanning direct Control fields.
    private Control _attributeFr101D00;
    private Control _attributeFr101D01;
    private Control _attributeFr101D02;
    private Control _attributeFr101D03;
    private Control _attributeFr101D04;
    private Control _attributeFr101D05;
    private Control _attributeFr101D06;
    private Control _attributeFr101D07;
    private Control _attributeFr101D08;
    private Control _attributeFr101D09;
    private Control _attributeFr101D10;
    private Control _attributeFr101D11;
    private Control _attributeFr101D12;
    private Control _attributeFr101D13;
    private Control _attributeFr101D14;
    private Control _attributeFr101D15;
    private Control _attributeFr101T00;
    private Control _attributeFr101T01;
    private Control _attributeFr101T02;
    private Control _attributeFr101T03;
    private Control _attributeFr101T04;
    private Control _attributeFr101T05;
    private Control _attributeFr101T06;
    private Control _attributeFr101T07;
    private Control _attributeFr101T08;
    private Control _attributeFr101T09;
    private Control _attributeFr101T10;
    private Control _attributeFr101T11;
    private Control _attributeFr101T12;
    private Control _attributeFr101T13;
    private Control _attributeFr101T14;
    private Control _attributeFr101T15;
    private Control _attributeFr103D00;
    private Control _attributeFr103D01;
    private Control _attributeFr103D02;
    private Control _attributeFr103D03;
    private Control _attributeFr103D04;
    private Control _attributeFr103D05;
    private Control _attributeFr103D06;
    private Control _attributeFr103D07;
    private Control _attributeFr103D08;
    private Control _attributeFr103D09;
    private Control _attributeFr103D10;
    private Control _attributeFr103D11;
    private Control _attributeFr103D12;
    private Control _attributeFr103D13;
    private Control _attributeFr103D14;
    private Control _attributeFr103D15;
    private Control _attributeFr103T00;
    private Control _attributeFr103T01;
    private Control _attributeFr103T02;
    private Control _attributeFr103T03;
    private Control _attributeFr103T04;
    private Control _attributeFr103T05;
    private Control _attributeFr103T06;
    private Control _attributeFr103T07;
    private Control _attributeFr103T08;
    private Control _attributeFr103T09;
    private Control _attributeFr103T10;
    private Control _attributeFr103T11;
    private Control _attributeFr103T12;
    private Control _attributeFr103T13;
    private Control _attributeFr103T14;
    private Control _attributeFr103T15;
    private Control _attributeFrRs2D00;
    private Control _attributeFrRs2D01;
    private Control _attributeFrRs2T00;
    private Control _attributeFrRs2T01;
    private Control _attributeFrMaterial;
    private Control _attributeFrClass;

    private Control _filterFr101D00;
    private Control _filterFr101D01;
    private Control _filterFr101D02;
    private Control _filterFr101D03;
    private Control _filterFr101D04;
    private Control _filterFr101D05;
    private Control _filterFr101D06;
    private Control _filterFr101D07;
    private Control _filterFr101D08;
    private Control _filterFr101D09;
    private Control _filterFr101D10;
    private Control _filterFr101D11;
    private Control _filterFr101D12;
    private Control _filterFr101D13;
    private Control _filterFr101D14;
    private Control _filterFr101D15;
    private Control _filterFr101T00;
    private Control _filterFr101T01;
    private Control _filterFr101T02;
    private Control _filterFr101T03;
    private Control _filterFr101T04;
    private Control _filterFr101T05;
    private Control _filterFr101T06;
    private Control _filterFr101T07;
    private Control _filterFr101T08;
    private Control _filterFr101T09;
    private Control _filterFr101T10;
    private Control _filterFr101T11;
    private Control _filterFr101T12;
    private Control _filterFr101T13;
    private Control _filterFr101T14;
    private Control _filterFr101T15;
    private Control _filterFr103D00;
    private Control _filterFr103D01;
    private Control _filterFr103D02;
    private Control _filterFr103D03;
    private Control _filterFr103D04;
    private Control _filterFr103D05;
    private Control _filterFr103D06;
    private Control _filterFr103D07;
    private Control _filterFr103D08;
    private Control _filterFr103D09;
    private Control _filterFr103D10;
    private Control _filterFr103D11;
    private Control _filterFr103D12;
    private Control _filterFr103D13;
    private Control _filterFr103D14;
    private Control _filterFr103D15;
    private Control _filterFr103T00;
    private Control _filterFr103T01;
    private Control _filterFr103T02;
    private Control _filterFr103T03;
    private Control _filterFr103T04;
    private Control _filterFr103T05;
    private Control _filterFr103T06;
    private Control _filterFr103T07;
    private Control _filterFr103T08;
    private Control _filterFr103T09;
    private Control _filterFr103T10;
    private Control _filterFr103T11;
    private Control _filterFr103T12;
    private Control _filterFr103T13;
    private Control _filterFr103T14;
    private Control _filterFr103T15;
    private Control _filterFrRs2D00;
    private Control _filterFrRs2D01;
    private Control _filterFrRs2T00;
    private Control _filterFrRs2T01;
    private Control _filterFrMaterial;
    private Control _filterFrClass;

    private readonly List<CheckBox> _filters = new();
    private readonly Dictionary<string, TextBox> _inputs =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _inputTypes =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _defaultValues =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, CheckBox> _filtersByAttribute =
        new(StringComparer.Ordinal);

    public FachwerkRigelForm()
    {
        Text = "Ригели фахверка";
        Width = 760;
        Height = 820;
        MinimumSize = new Size(680, 560);
        StartPosition = FormStartPosition.CenterScreen;
        RegisterPropertyBinding(
            typeof(TextBox),
            "Text",
            DataSourceUpdateMode.OnPropertyChanged);
        BuildLayout();
        FormInitialized += (_, _) => Trace("FormInitialized: catalog defaults only");
        AttributesLoadedFromModel += (_, _) =>
            ExecuteUiAction(() => PullAllAttributesIntoInputs("AttributesLoadedFromModel"));
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
            Text = "Автономные ригели 101, 103 и РС2 по осям КМ",
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

        var catalog = FachwerkRigelCatalog.LoadEmbedded();
        foreach (var layout in catalog.Layouts)
            AddLayoutTab(tabs, layout);

        var common = AddFieldsTab(tabs, "Общие");
        AddString(common, "Материал", "fr_material", "C355-5");
        AddString(common, "Класс Tekla", "fr_class", "20");

        root.Controls.Add(BuildButtons(), 0, 2);
        ValidateRegisteredControls();
    }

    private void AddLayoutTab(
        TabControl tabs,
        FachwerkRigelLayout layout)
    {
        var fields = AddFieldsTab(
            tabs,
            string.IsNullOrWhiteSpace(layout.Label)
                ? "Ригель " + layout.Code
                : layout.Label);
        var prefix = "fr_" + NormalizeCode(layout.Code);
        for (var index = 0; index < layout.Points.Count; index++)
        {
            var point = layout.Points[index];
            var suffix = index.ToString("D2", CultureInfo.InvariantCulture);
            AddDistance(
                fields,
                point.Label + " — ΔZ от " +
                layout.BaseElevationMm.ToString("0.###", CultureInfo.InvariantCulture),
                prefix + "_d" + suffix,
                point.ElevationDeltaMm);
            AddDistance(
                fields,
                point.Label + " — поперечное смещение",
                prefix + "_t" + suffix,
                point.CalculatedTransverseOffsetMm + layout.TransverseOffsetMm);
        }
    }

    private static string NormalizeCode(string code) =>
        string.Equals(code, "RS2", StringComparison.OrdinalIgnoreCase)
            ? "rs2"
            : (code ?? string.Empty).Trim().ToLowerInvariant();

    private static TableLayoutPanel AddFieldsTab(TabControl tabs, string title)
    {
        var tab = new TabPage
        {
            Text = title,
            Padding = new Padding(6),
            UseVisualStyleBackColor = true,
            AutoScroll = true,
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
        panel.Controls.Add(Button("Изменить", (_, _) => ExecuteUiAction(() =>
        {
            PushAllInputsIntoAttributes();
            Modify();
        })));
        panel.Controls.Add(Button("Применить", (_, _) => ExecuteUiAction(() =>
        {
            PushAllInputsIntoAttributes();
            Apply();
        })));
        panel.Controls.Add(Button("Получить", (_, _) => ExecuteUiAction(() =>
        {
            Get();
        })));
        panel.Controls.Add(Button("ОК", (_, _) => ExecuteUiAction(() =>
        {
            PushAllInputsIntoAttributes();
            Apply();
            Close();
        })));
        panel.Controls.Add(Button("Включить все", (_, _) => SetAllFilters(true)));
        panel.Controls.Add(Button("Выключить все", (_, _) => SetAllFilters(false)));
        return panel;
    }

    private void AddDistance(
        TableLayoutPanel panel,
        string label,
        string name,
        double value)
    {
        AddText(
            panel,
            label,
            name,
            "Distance",
            value.ToString("0.###", CultureInfo.InvariantCulture));
    }

    private void AddString(
        TableLayoutPanel panel,
        string label,
        string name,
        string value)
    {
        AddText(panel, label, name, "String", value);
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
        _defaultValues[name] = value ?? string.Empty;
        _inputTypes[name] = typeName;
        _inputs.Add(name, input);
        structuresExtender.SetAttributeName(input, name);
        structuresExtender.SetAttributeTypeName(input, typeName);
        structuresExtender.SetBindPropertyName(input, "Text");
        AssignControl(name, input, false);
        panel.Controls.Add(filter, 0, row);
        panel.Controls.Add(new Label
        {
            Text = labelText,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(4),
        }, 1, row);
        panel.Controls.Add(input, 2, row);
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
        AssignControl(attributeName, filter, true);
        _filters.Add(filter);
        _filtersByAttribute.Add(attributeName, filter);
        return filter;
    }

    private void AssignControl(string name, Control control, bool filter)
    {
        var fieldName =
            (filter ? "_filter" : "_attribute") + ToFieldSuffix(name);
        var field = GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null)
        {
            throw new InvalidOperationException(
                "Не зарегистрировано поле формы: " + name);
        }
        field.SetValue(this, control);
    }

    private static string ToFieldSuffix(string name)
    {
        var result = string.Empty;
        foreach (var part in (name ?? string.Empty).Split('_'))
        {
            if (part.Length == 0) continue;
            result += char.ToUpperInvariant(part[0]) + part.Substring(1);
        }
        return result;
    }

    private void ValidateRegisteredControls()
    {
        foreach (var field in GetType().GetFields(
                     BindingFlags.Instance | BindingFlags.NonPublic))
        {
            if (!field.Name.StartsWith("_attribute", StringComparison.Ordinal) &&
                !field.Name.StartsWith("_filter", StringComparison.Ordinal))
            {
                continue;
            }
            if (field.GetValue(this) == null)
            {
                throw new InvalidOperationException(
                    "Поле формы не связано с атрибутом Tekla: " + field.Name);
            }
        }
    }

    private void SetAllFilters(bool enabled)
    {
        foreach (var filter in _filters) filter.Checked = enabled;
    }

    private void PullAllAttributesIntoInputs(string source)
    {
        var loadedValues = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in _inputs)
        {
            var value = ReadAttributeText(
                pair.Key,
                pair.Value,
                _inputTypes[pair.Key]);
            var resolved = IsMissingValue(value)
                ? _defaultValues[pair.Key]
                : value;
            SetTypedAttributeValue(
                pair.Value,
                _inputTypes[pair.Key],
                resolved,
                pair.Key);
            loadedValues[pair.Key] = resolved;
        }
        Trace(source + ": loaded " + FormatSnapshot(loadedValues));
    }

    private string ReadAttributeText(
        string name,
        Control control,
        string typeName)
    {
        try
        {
            if (IsNumericType(typeName))
            {
                return GetAttributeValue<double>(control)
                    .ToString("0.###", CultureInfo.InvariantCulture);
            }
            return GetAttributeValue<string>(control) ?? string.Empty;
        }
        catch (Exception exception)
        {
            Trace(
                "Failed to read attribute " + name + ": " +
                exception.GetType().Name + " " + exception.Message);
            return control.Text ?? string.Empty;
        }
    }

    private void PushAllInputsIntoAttributes()
    {
        ValidateChildren();
        foreach (var pair in _filtersByAttribute)
        {
            SetFilterValue(pair.Key, pair.Value.Checked);
        }
        foreach (var pair in _inputs)
        {
            if (!_filtersByAttribute[pair.Key].Checked) continue;
            var typeName = _inputTypes[pair.Key];
            if (IsNumericType(typeName))
            {
                if (!TryParseNumber(pair.Value.Text, out var value))
                {
                    throw new InvalidOperationException(
                        "Поле '" + pair.Key + "' должно содержать число.");
                }
                SetAttributeValue(pair.Value, value);
                continue;
            }
            SetAttributeValue(pair.Value, pair.Value.Text ?? string.Empty);
        }
        Trace("Pushed " + FormatSnapshot(
            _inputs.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Text ?? string.Empty,
                StringComparer.Ordinal)));
    }

    private void SetTypedAttributeValue(
        Control control,
        string typeName,
        string rawValue,
        string attributeName)
    {
        if (IsNumericType(typeName))
        {
            if (!TryParseNumber(rawValue, out var number))
            {
                throw new InvalidOperationException(
                    "Поле '" + attributeName + "' должно содержать число.");
            }
            SetAttributeValue(control, number);
            return;
        }
        SetAttributeValue(control, rawValue ?? string.Empty);
    }

    private static string FormatSnapshot(
        IReadOnlyDictionary<string, string> values)
    {
        var keys = new[]
        {
            "fr_101_d00", "fr_101_d01", "fr_101_d15",
            "fr_103_d00", "fr_103_d01", "fr_103_d15",
            "fr_rs2_d00", "fr_rs2_d01",
        };
        var parts = new List<string>();
        foreach (var key in keys)
        {
            if (values.TryGetValue(key, out var value))
                parts.Add(key + "=" + value);
        }
        return values.Count + " attributes; " + string.Join("; ", parts);
    }

    private static bool IsNumericType(string typeName) =>
        string.Equals(typeName, "Distance", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(typeName, "Double", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(typeName, "Integer", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseNumber(string text, out double value)
    {
        const NumberStyles styles =
            NumberStyles.Float | NumberStyles.AllowThousands;
        return double.TryParse(
                   text,
                   styles,
                   CultureInfo.CurrentCulture,
                   out value) ||
               double.TryParse(
                   text,
                   styles,
                   CultureInfo.InvariantCulture,
                   out value);
    }

    private static bool IsMissingValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        var trimmed = value.Trim();
        if (string.Equals(
                trimmed,
                int.MinValue.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            return true;
        }
        return double.TryParse(
                   trimmed,
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out var parsed) &&
               (double.IsNaN(parsed) || double.IsInfinity(parsed) || parsed < -1e100);
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
                "Ригели фахверка",
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
                    "fachwerk_rigel_form_trace.txt"),
                DateTime.Now.ToString("s", CultureInfo.InvariantCulture) +
                " " + message + Environment.NewLine);
        }
        catch
        {
        }
    }

}
#pragma warning restore 0169

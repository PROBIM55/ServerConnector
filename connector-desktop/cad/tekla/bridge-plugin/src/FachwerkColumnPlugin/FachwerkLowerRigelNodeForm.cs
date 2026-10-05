#nullable disable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using Tekla.Structures;
using Tekla.Structures.Dialog;
using Tekla.Structures.Model;
using TeklaPoint = Tekla.Structures.Geometry3d.Point;

namespace Structura.Tekla.Fachwerk;

public sealed class FachwerkLowerRigelNodeForm : PluginFormBase
{
    // Tekla 2020 registers attributes by scanning direct private Control
    // fields. These fields must remain explicit even for a dynamic layout.
    private Control _attributeFklrGap;
    private Control _attributeFklrDiameter;
    private Control _attributeFklrPlateProfile;
    private Control _attributeFklrMaterial;
    private Control _attributeFklrClass;
    private Control _attributeFklrPlateWidth;
    private Control _attributeFklrLeftLength;
    private Control _attributeFklrRightLength;
    private Control _attributeFklrAutomaticLength;
    private Control _attributeFklrBottomClosure;
    private Control _attributeFklrOuterFlangeTubeCut;
    private Control _attributeFklrAxisCorrection;
    private Control _attributeFklrControlLine;

    private Control _filterFklrGap;
    private Control _filterFklrDiameter;
    private Control _filterFklrPlateProfile;
    private Control _filterFklrMaterial;
    private Control _filterFklrClass;
    private Control _filterFklrPlateWidth;
    private Control _filterFklrLeftLength;
    private Control _filterFklrRightLength;
    private Control _filterFklrAutomaticLength;
    private Control _filterFklrBottomClosure;
    private Control _filterFklrOuterFlangeTubeCut;
    private Control _filterFklrAxisCorrection;
    private Control _filterFklrControlLine;

    private readonly List<CheckBox> _filters = new();
    private readonly List<TextFieldBinding> _textFields = new();
    private readonly List<YesNoBinding> _yesNoBindings = new();
    private bool _synchronizingYesNo;
    private bool _loadingStorage;

    public FachwerkLowerRigelNodeForm()
    {
        Text = "Узел нижнего ригеля";
        Width = 700;
        Height = 620;
        MinimumSize = new Size(640, 500);
        StartPosition = FormStartPosition.CenterScreen;
        RegisterPropertyBinding(
            typeof(TextBox),
            "Text",
            DataSourceUpdateMode.OnPropertyChanged);
        BuildLayout();
        FormInitialized += (_, _) =>
            Execute(() => SynchronizeDefaultsAndAliases(
                "FormInitialized",
                true));
        AttributesLoadedFromModel += (_, _) =>
            Execute(() => PullPersistedValuesIntoUi("AttributesLoadedFromModel"));
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
            Text = "Узел нижнего ригеля по Grasshopper-определению",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 10),
        }, 0, 0);

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
        var scroll = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
        };
        scroll.Controls.Add(fields);
        root.Controls.Add(scroll, 0, 1);

        AddSection(fields, "Геометрия трубы");
        AddDouble(fields, "Зазор между трубами, мм", "fklr_gap", 25);
        AddDouble(fields, "Наружный диаметр трубы, мм", "fklr_diameter", 530);

        AddSection(fields, "Переходные пластины");
        AddText(fields, "Профиль пластины", "fklr_plate_profile", "PL25");
        AddText(fields, "Материал", "fklr_material", "C355-5");
        AddText(fields, "Класс Tekla", "fklr_class", "20");
        AddDouble(fields, "Ширина пластины, мм", "fklr_plate_width", 350);
        AddYesNo(
            fields,
            "Автоподбор общей длины до кромки 250 мм",
            "fklr_auto_len",
            "NO");
        AddDouble(
            fields,
            "Длина обеих пластин, мм",
            "fklr_left_length",
            500);
        AddHiddenDouble("fklr_right_length", 500);
        AddYesNo(
            fields,
            "Создавать нижнюю заглушку PL6",
            "fklr_bottom_cap",
            "NO");

        AddSection(fields, "Построение");
        AddYesNo(
            fields,
            "Создавать подрезку наружного пояса по трубе",
            "fklr_outer_cut",
            "YES");
        AddYesNo(
            fields,
            "Корректировать оси исходных труб",
            "fklr_axis_corr",
            "NO");
        AddYesNo(
            fields,
            "Создавать контрольную ось",
            "fklr_control_line",
            "YES");

        root.Controls.Add(BuildButtons(), 0, 2);
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
        panel.Controls.Add(Button(
            "Изменить",
            (_, _) => Execute(() =>
            {
                ModifySelectedComponents();
            })));
        panel.Controls.Add(Button(
            "Применить",
            (_, _) => Execute(() =>
            {
                PrepareForWrite(false);
                Apply();
            })));
        panel.Controls.Add(Button(
            "Получить",
            (_, _) => Execute(() =>
            {
                LoadSelectedComponentIntoUi("Get");
            })));
        panel.Controls.Add(Button("ОК", (_, _) => Execute(() =>
        {
            PrepareForWrite(false);
            Apply();
            Close();
        })));
        panel.Controls.Add(Button("Включить все", (_, _) => SetFilters(true)));
        panel.Controls.Add(Button("Выключить все", (_, _) => SetFilters(false)));
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

    private void SetFilters(bool enabled)
    {
        foreach (CheckBox filter in _filters)
        {
            filter.Checked = enabled;
        }
    }

    private static void AddSection(
        TableLayoutPanel panel,
        string text)
    {
        int row = panel.RowCount++;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
            Margin = new Padding(0, 14, 0, 6),
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
            value.ToString("0.###", CultureInfo.InvariantCulture),
            "Double");
    }

    private void AddHiddenDouble(
        string name,
        double value)
    {
        var input = new TextBox
        {
            Name = name,
            Text = value.ToString(
                "0.###",
                CultureInfo.InvariantCulture),
            Visible = false,
        };
        Bind(input, name, "Double");
        AssignAttribute(name, input);
        Controls.Add(input);

        CheckBox filter = CreateFilter(name);
        filter.Visible = false;
        Controls.Add(filter);
        _textFields.Add(new TextFieldBinding(
            name,
            "Double",
            input,
            filter,
            input.Text));
    }

    private void AddText(
        TableLayoutPanel panel,
        string label,
        string name,
        string value,
        string typeName = "String")
    {
        int row = panel.RowCount++;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        CheckBox filter = CreateFilter(name);
        var input = new TextBox
        {
            Name = name,
            Text = value,
            Dock = DockStyle.Fill,
            Margin = new Padding(4),
        };
        Bind(input, name, typeName);
        AssignAttribute(name, input);
        _textFields.Add(new TextFieldBinding(
            name,
            typeName,
            input,
            filter,
            value));
        panel.Controls.Add(filter, 0, row);
        panel.Controls.Add(FieldLabel(label), 1, row);
        panel.Controls.Add(input, 2, row);
    }

    private void AddYesNo(
        TableLayoutPanel panel,
        string label,
        string name,
        string defaultValue)
    {
        var options = new[]
        {
            new ComboOption("Да", "YES"),
            new ComboOption("Нет", "NO"),
        };
        int row = panel.RowCount++;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        CheckBox filter = CreateFilter(name);
        var storage = new TextBox
        {
            Name = name,
            Text = defaultValue,
            Visible = false,
        };
        Bind(storage, name, "String");
        AssignAttribute(name, storage);
        Controls.Add(storage);

        var combo = new ComboBox
        {
            Name = name + "_combo",
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Fill,
            DisplayMember = nameof(ComboOption.Label),
            Margin = new Padding(4),
        };
        foreach (ComboOption option in options)
        {
            combo.Items.Add(option);
        }
        SelectValue(combo, options, defaultValue);
        combo.SelectedIndexChanged += (_, _) =>
        {
            if (_synchronizingYesNo)
            {
                return;
            }
            if (combo.SelectedItem is ComboOption selected)
            {
                storage.Text = selected.Value;
            }
        };
        _yesNoBindings.Add(
            new YesNoBinding(
                name,
                storage,
                combo,
                filter,
                options,
                defaultValue));

        panel.Controls.Add(filter, 0, row);
        panel.Controls.Add(FieldLabel(label), 1, row);
        panel.Controls.Add(combo, 2, row);
    }

    private void Bind(
        Control control,
        string name,
        string typeName)
    {
        structuresExtender.SetAttributeName(control, name);
        structuresExtender.SetAttributeTypeName(control, typeName);
        structuresExtender.SetBindPropertyName(control, "Text");
    }

    private CheckBox CreateFilter(string name)
    {
        var filter = new CheckBox
        {
            Name = name + "_filter",
            Checked = true,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            TabStop = false,
            Margin = new Padding(0, 3, 4, 3),
        };
        structuresExtender.SetAttributeName(filter, name);
        structuresExtender.SetIsFilter(filter, true);
        AssignFilter(name, filter);
        _filters.Add(filter);
        return filter;
    }

    private static Label FieldLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(4),
    };

    private void SynchronizeDefaultsAndAliases(
        string source,
        bool synchronizeYesNoFromStorage)
    {
        EnsureDouble(_attributeFklrGap, 25);
        EnsureDouble(_attributeFklrDiameter, 530);
        EnsureText(_attributeFklrPlateProfile, "PL25");
        EnsureText(_attributeFklrMaterial, "C355-5");
        EnsureText(_attributeFklrClass, "20");
        EnsureDouble(_attributeFklrPlateWidth, 350);
        EnsureText(_attributeFklrAutomaticLength, "NO");
        EnsureText(_attributeFklrBottomClosure, "NO");
        EnsureText(_attributeFklrOuterFlangeTubeCut, "YES");
        EnsureText(_attributeFklrAxisCorrection, "NO");
        EnsureText(_attributeFklrControlLine, "YES");

        double commonLength = ReadDouble(
            _attributeFklrLeftLength,
            ReadDouble(_attributeFklrRightLength, 500));
        SetDoubleText(_attributeFklrLeftLength, commonLength);
        SetDoubleText(_attributeFklrRightLength, commonLength);
        SynchronizeFilter(
            _filterFklrLeftLength,
            _filterFklrRightLength);
        if (synchronizeYesNoFromStorage)
        {
            SynchronizeYesNoBindings();
        }
        TraceState("NORMALIZE " + source);
    }

    private void SynchronizeYesNoBindings()
    {
        _synchronizingYesNo = true;
        try
        {
            foreach (YesNoBinding binding in _yesNoBindings)
            {
                string value = IsUnset(binding.Storage.Text)
                    ? binding.DefaultValue
                    : binding.Storage.Text.Trim();
                if (IsUnset(binding.Storage.Text))
                {
                    WriteTextAttribute(binding.Storage, value);
                }
                SelectValue(
                    binding.Combo,
                    binding.Options,
                    value,
                    binding.DefaultValue);
            }
        }
        finally
        {
            _synchronizingYesNo = false;
        }
    }

    private void PullPersistedValuesIntoUi(string source)
    {
        if (_loadingStorage)
        {
            return;
        }

        _loadingStorage = true;
        try
        {
            Component selected = FindSingleSelectedNodeComponent();
            if (selected != null &&
                TryLoadComponentAttributesIntoUi(selected, source))
            {
                return;
            }

            foreach (TextFieldBinding field in _textFields)
            {
                field.Control.Text = NormalizeFieldText(
                    field.TypeName,
                    field.Control.Text,
                    field.DefaultValue);
            }

            _synchronizingYesNo = true;
            try
            {
                foreach (YesNoBinding binding in _yesNoBindings)
                {
                    string value = NormalizeYesNo(
                        binding.Storage.Text,
                        binding.DefaultValue);
                    binding.Storage.Text = value;
                    SelectValue(
                        binding.Combo,
                        binding.Options,
                        value,
                        binding.DefaultValue);
                }
            }
            finally
            {
                _synchronizingYesNo = false;
            }

            SynchronizeDefaultsAndAliases(source, true);
        }
        finally
        {
            _loadingStorage = false;
        }
    }

    private void PushUiToStorage()
    {
        foreach (TextFieldBinding field in _textFields)
        {
            if (!field.Filter.Checked)
            {
                continue;
            }
            SetTypedAttributeValue(
                field.Control,
                field.TypeName,
                field.Control.Text,
                field.Name);
        }

        foreach (YesNoBinding binding in _yesNoBindings)
        {
            if (!binding.Filter.Checked)
            {
                continue;
            }
            ComboOption selected = binding.Combo.SelectedItem as ComboOption;
            string value = selected?.Value ?? binding.DefaultValue;
            binding.Storage.Text = value;
            SetTypedAttributeValue(
                binding.Storage,
                "String",
                value,
                binding.Name);
        }
        TraceState("PUSH");
    }

    private object ReadPersistedValue(
        Control control,
        string typeName,
        string defaultValue)
    {
        try
        {
            if (string.Equals(
                    typeName,
                    "Double",
                    StringComparison.OrdinalIgnoreCase))
            {
                double value = GetAttributeValue<double>(control);
                return IsUnsetNumber(value)
                    ? ParseDouble(defaultValue, 0)
                    : value;
            }
            string valueText = GetAttributeValue<string>(control);
            return IsUnset(valueText) ? defaultValue : valueText;
        }
        catch
        {
            return IsUnset(control?.Text) ? defaultValue : control.Text;
        }
    }

    private static string NormalizeFieldText(
        string typeName,
        object value,
        string defaultValue)
    {
        if (string.Equals(
                typeName,
                "Double",
                StringComparison.OrdinalIgnoreCase))
        {
            double number = value is double typed
                ? typed
                : ParseDouble(
                    Convert.ToString(value, CultureInfo.InvariantCulture),
                    ParseDouble(defaultValue, 0));
            return number.ToString("0.###", CultureInfo.InvariantCulture);
        }
        string text = Convert.ToString(value, CultureInfo.InvariantCulture);
        return IsUnset(text) ? defaultValue : text;
    }

    private void SetTypedAttributeValue(
        Control control,
        string typeName,
        object rawValue,
        string attributeName)
    {
        try
        {
            if (string.Equals(
                    typeName,
                    "Double",
                    StringComparison.OrdinalIgnoreCase))
            {
                double value = ParseDouble(
                    Convert.ToString(rawValue, CultureInfo.CurrentCulture),
                    double.NaN);
                if (double.IsNaN(value))
                {
                    throw new InvalidOperationException(
                        "Поле должно содержать число.");
                }
                WriteDoubleAttribute(control, value);
                return;
            }
            WriteTextAttribute(
                control,
                Convert.ToString(rawValue, CultureInfo.InvariantCulture) ??
                string.Empty);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "Не удалось передать параметр '" + attributeName +
                "' в Tekla. " + exception.Message,
                exception);
        }
    }

    private void PrepareForWrite(bool requireSelectedComponent)
    {
        PushYesNoSelectionsToStorage();
        SynchronizeDefaultsAndAliases("PrepareForWrite", false);
        RefreshAutomaticLengthInUi(requireSelectedComponent);
        WriteDoubleAttribute(
            _attributeFklrRightLength,
            ReadDouble(_attributeFklrLeftLength, 500));
        SynchronizeFilter(
            _filterFklrLeftLength,
            _filterFklrRightLength);
        PushUiToStorage();
    }

    private void ModifySelectedComponents()
    {
        PushYesNoSelectionsToStorage();
        SynchronizeDefaultsAndAliases("ModifySelectedComponents", false);

        List<Component> selected = FindSelectedNodeComponents();
        if (selected.Count == 0)
        {
            throw new InvalidOperationException(
                "Выберите хотя бы один экземпляр FachwerkLowerRigelNodePlugin.");
        }

        YesNoBinding automaticBinding = FindYesNoBinding("fklr_auto_len");
        bool automaticLength = automaticBinding != null &&
            automaticBinding.Filter.Checked &&
            IsYes((automaticBinding.Combo.SelectedItem as ComboOption)?.Value);

        var model = new Model();
        if (!model.GetConnectionStatus())
        {
            throw new InvalidOperationException(
                "Нет соединения с открытой моделью Tekla.");
        }

        double firstLength = 0;
        for (int index = 0; index < selected.Count; index++)
        {
            Component component = selected[index];
            double? calculatedLength = automaticLength
                ? CalculateAutomaticLength(component)
                : null;
            if (calculatedLength.HasValue && index == 0)
            {
                firstLength = calculatedLength.Value;
                SetDoubleText(_attributeFklrLeftLength, firstLength);
                SetDoubleText(_attributeFklrRightLength, firstLength);
            }

            WriteUiAttributesToComponent(component, calculatedLength);
            if (!component.Modify())
            {
                throw new InvalidOperationException(
                    "Tekla не изменила экземпляр узла " +
                    component.Identifier.ID.ToString(
                        CultureInfo.InvariantCulture) + ".");
            }
        }

        model.CommitChanges();

        Component refreshed = model.SelectModelObject(
            selected[0].Identifier) as Component;
        TryLoadComponentAttributesIntoUi(
            refreshed ?? selected[0],
            "Modify completed");
    }

    private void WriteUiAttributesToComponent(
        Component component,
        double? automaticLength)
    {
        foreach (TextFieldBinding field in _textFields)
        {
            if (!field.Filter.Checked)
            {
                continue;
            }

            string rawValue = field.Control.Text;
            if (automaticLength.HasValue &&
                (string.Equals(
                     field.Name,
                     "fklr_left_length",
                     StringComparison.Ordinal) ||
                 string.Equals(
                     field.Name,
                     "fklr_right_length",
                     StringComparison.Ordinal)))
            {
                rawValue = automaticLength.Value.ToString(
                    "0.###############",
                    CultureInfo.InvariantCulture);
            }

            if (string.Equals(
                    field.TypeName,
                    "Double",
                    StringComparison.OrdinalIgnoreCase))
            {
                double value = ParseDouble(rawValue, double.NaN);
                if (double.IsNaN(value))
                {
                    throw new InvalidOperationException(
                        "Поле '" + field.Name + "' должно содержать число.");
                }
                component.SetAttribute(field.Name, value);
            }
            else
            {
                component.SetAttribute(field.Name, rawValue ?? string.Empty);
            }
        }

        foreach (YesNoBinding binding in _yesNoBindings)
        {
            if (!binding.Filter.Checked)
            {
                continue;
            }
            string value =
                (binding.Combo.SelectedItem as ComboOption)?.Value ??
                binding.DefaultValue;
            binding.Storage.Text = value;
            component.SetAttribute(binding.Name, value);
        }
    }

    private void LoadSelectedComponentIntoUi(string source)
    {
        Component selected = FindSingleSelectedNodeComponent();
        if (selected == null)
        {
            throw new InvalidOperationException(
                "Выберите один экземпляр FachwerkLowerRigelNodePlugin.");
        }
        if (!TryLoadComponentAttributesIntoUi(selected, source))
        {
            throw new InvalidOperationException(
                "Не удалось прочитать параметры выбранного экземпляра узла.");
        }
    }

    private bool TryLoadComponentAttributesIntoUi(
        Component component,
        string source)
    {
        if (component == null)
        {
            return false;
        }

        foreach (TextFieldBinding field in _textFields)
        {
            if (string.Equals(
                    field.TypeName,
                    "Double",
                    StringComparison.OrdinalIgnoreCase))
            {
                double value = double.NaN;
                if (component.GetAttribute(field.Name, ref value) &&
                    !IsUnsetNumber(value))
                {
                    SetDoubleText(field.Control, value);
                }
                else if (IsUnset(field.Control.Text))
                {
                    field.Control.Text = field.DefaultValue;
                }
            }
            else
            {
                string value = string.Empty;
                if (component.GetAttribute(field.Name, ref value) &&
                    !IsUnset(value))
                {
                    field.Control.Text = value;
                }
                else if (IsUnset(field.Control.Text))
                {
                    field.Control.Text = field.DefaultValue;
                }
            }
        }

        _synchronizingYesNo = true;
        try
        {
            foreach (YesNoBinding binding in _yesNoBindings)
            {
                string value = string.Empty;
                if (!component.GetAttribute(binding.Name, ref value) ||
                    IsUnset(value))
                {
                    value = IsUnset(binding.Storage.Text)
                        ? binding.DefaultValue
                        : binding.Storage.Text;
                }
                value = NormalizeYesNo(value, binding.DefaultValue);
                binding.Storage.Text = value;
                SelectValue(
                    binding.Combo,
                    binding.Options,
                    value,
                    binding.DefaultValue);
            }
        }
        finally
        {
            _synchronizingYesNo = false;
        }

        double commonLength = ReadDouble(
            _attributeFklrLeftLength,
            ReadDouble(_attributeFklrRightLength, 500));
        SetDoubleText(_attributeFklrLeftLength, commonLength);
        SetDoubleText(_attributeFklrRightLength, commonLength);
        TraceState(
            "DIRECT_LOAD " + source + " component=" +
            component.Identifier.ID.ToString(CultureInfo.InvariantCulture));
        return true;
    }

    private void PushYesNoSelectionsToStorage()
    {
        foreach (YesNoBinding binding in _yesNoBindings)
        {
            ComboOption selected = binding.Combo.SelectedItem as ComboOption;
            string value = selected?.Value ?? binding.DefaultValue;
            binding.Storage.Text = value;
        }
    }

    private YesNoBinding FindYesNoBinding(string name)
    {
        foreach (YesNoBinding binding in _yesNoBindings)
        {
            if (string.Equals(
                    binding.Name,
                    name,
                    StringComparison.Ordinal))
            {
                return binding;
            }
        }
        return null;
    }

    private void RefreshAutomaticLengthInUi(bool requireSelectedComponent)
    {
        YesNoBinding automaticBinding = FindYesNoBinding("fklr_auto_len");
        ComboOption automaticOption =
            automaticBinding?.Combo.SelectedItem as ComboOption;
        if (!IsYes(automaticOption?.Value) ||
            automaticBinding != null && !automaticBinding.Filter.Checked)
        {
            return;
        }

        if (!TryCalculateAutomaticLength(out double length))
        {
            if (requireSelectedComponent)
            {
                throw new InvalidOperationException(
                    "Для автоподбора выберите один экземпляр " +
                    "FachwerkLowerRigelNodePlugin.");
            }
            Trace(
                "AUTO_LENGTH skipped: select exactly one " +
                "FachwerkLowerRigelNodePlugin instance");
            return;
        }

        WriteDoubleAttribute(_attributeFklrLeftLength, length);
        WriteDoubleAttribute(_attributeFklrRightLength, length);
        if (_filterFklrLeftLength is CheckBox leftFilter)
        {
            leftFilter.Checked = true;
        }
        SynchronizeFilter(
            _filterFklrLeftLength,
            _filterFklrRightLength);
        Trace(
            "AUTO_LENGTH calculated=" +
            length.ToString("0.###", CultureInfo.InvariantCulture));
    }

    private bool TryCalculateAutomaticLength(out double length)
    {
        length = 0;
        Component selected = FindSingleSelectedNodeComponent();
        if (selected == null)
        {
            return false;
        }

        length = CalculateAutomaticLength(selected);
        return true;
    }

    private double CalculateAutomaticLength(Component selected)
    {
        ResolvedNodeInput input = ResolveNodeInput(selected);

        var model = new Model();
        if (!model.GetConnectionStatus())
        {
            throw new InvalidOperationException(
                "Нет соединения с открытой моделью Tekla.");
        }

        WorkPlaneHandler workPlaneHandler = model.GetWorkPlaneHandler();
        TransformationPlane previousPlane =
            workPlaneHandler.GetCurrentTransformationPlane();
        try
        {
            if (!workPlaneHandler.SetCurrentTransformationPlane(
                    new TransformationPlane()))
            {
                throw new InvalidOperationException(
                    "Tekla не установила глобальную рабочую плоскость.");
            }

            Trace(
                "AUTO_LENGTH_BASE component=" +
                selected.Identifier.ID.ToString(
                    CultureInfo.InvariantCulture) +
                " centralAxis=source-intersections");
            double result = FachwerkLowerRigelNodeAutoLength.Calculate(
                FachwerkLowerRigelNodeTeklaAdapter.ReadAxis(input.LeftTube),
                FachwerkLowerRigelNodeTeklaAdapter.ReadAxis(input.RightTube),
                FachwerkLowerRigelNodeTeklaAdapter.ReadAxis(input.LeftWeb),
                FachwerkLowerRigelNodeTeklaAdapter.ReadAxis(input.RightWeb),
                FachwerkLowerRigelNodeTeklaAdapter.ReadAxis(input.OuterFlange),
                FachwerkLowerRigelNodeTeklaAdapter.ReadAxis(input.InnerFlange),
                ReadDouble(_attributeFklrGap, 25),
                ReadDouble(_attributeFklrDiameter, 530),
                ReadDouble(_attributeFklrPlateWidth, 350),
                ReadDouble(_attributeFklrLeftLength, 500));
            Trace(
                "AUTO_LENGTH component=" +
                selected.Identifier.ID.ToString(CultureInfo.InvariantCulture) +
                " calculated=" +
                result.ToString("0.###", CultureInfo.InvariantCulture) +
                " inputs=" + input.TraceValue);
            return result;
        }
        finally
        {
            workPlaneHandler.SetCurrentTransformationPlane(previousPlane);
        }
    }

    private static ResolvedNodeInput ResolveNodeInput(Component component)
    {
        var identifiers = new List<Identifier>();
        IEnumerable componentInput = component.GetComponentInput();
        if (componentInput != null)
        {
            foreach (object itemObject in componentInput)
            {
                if (itemObject is InputItem item)
                {
                    CollectInputIdentifiers(item.GetData(), identifiers);
                }
            }
        }
        if (identifiers.Count != 6)
        {
            throw new InvalidOperationException(
                "Экземпляр узла должен содержать шесть входных деталей.");
        }

        var model = new Model();
        if (!model.GetConnectionStatus())
        {
            throw new InvalidOperationException(
                "Нет соединения с открытой моделью Tekla.");
        }

        Part outerFlange = null;
        Part innerFlange = null;
        Part leftWeb = null;
        Part rightWeb = null;
        var tubes = new List<Beam>();
        var allParts = new List<Part>();
        foreach (Identifier identifier in identifiers)
        {
            if (!(model.SelectModelObject(identifier) is Part part))
            {
                throw new InvalidOperationException(
                    "Не удалось получить входную деталь узла из модели.");
            }
            allParts.Add(part);

            string role = ReadSemanticRole(part);
            switch (role)
            {
                case "outer-flange": outerFlange = part; break;
                case "inner-flange": innerFlange = part; break;
                case "outer-web":
                case "left-web": leftWeb = part; break;
                case "inner-web":
                case "right-web": rightWeb = part; break;
                default:
                    if (part is Beam beam)
                    {
                        tubes.Add(beam);
                    }
                    break;
            }
        }

        if (outerFlange == null || innerFlange == null ||
            leftWeb == null || rightWeb == null || tubes.Count != 2)
        {
            throw new InvalidOperationException(
                "Не удалось восстановить роли входов узла. " +
                "Ожидались две трубы и детали стойки с FK_ROLE: " +
                "outer-flange, inner-flange, outer-web, inner-web.");
        }

        IReadOnlyList<global::Tekla.Structures.Geometry3d.Point> firstTubeAxis =
            FachwerkLowerRigelNodeTeklaAdapter.ReadAxis(tubes[0]);
        IReadOnlyList<global::Tekla.Structures.Geometry3d.Point> secondTubeAxis =
            FachwerkLowerRigelNodeTeklaAdapter.ReadAxis(tubes[1]);
        IReadOnlyList<global::Tekla.Structures.Geometry3d.Point> leftWebAxis =
            FachwerkLowerRigelNodeTeklaAdapter.ReadAxis(leftWeb);
        IReadOnlyList<global::Tekla.Structures.Geometry3d.Point> rightWebAxis =
            FachwerkLowerRigelNodeTeklaAdapter.ReadAxis(rightWeb);
        double directCost =
            MinimumAxisDistanceSquared(firstTubeAxis, leftWebAxis) +
            MinimumAxisDistanceSquared(secondTubeAxis, rightWebAxis);
        double swappedCost =
            MinimumAxisDistanceSquared(secondTubeAxis, leftWebAxis) +
            MinimumAxisDistanceSquared(firstTubeAxis, rightWebAxis);
        Beam leftTube = directCost <= swappedCost ? tubes[0] : tubes[1];
        Beam rightTube = directCost <= swappedCost ? tubes[1] : tubes[0];

        return new ResolvedNodeInput(
            leftTube,
            rightTube,
            outerFlange,
            innerFlange,
            leftWeb,
            rightWeb);
    }

    private static string ReadSemanticRole(Part part)
    {
        string role = string.Empty;
        try
        {
            part.GetUserProperty("FK_ROLE", ref role);
        }
        catch { }
        return (role ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static double MinimumAxisDistanceSquared(
        IReadOnlyList<global::Tekla.Structures.Geometry3d.Point> first,
        IReadOnlyList<global::Tekla.Structures.Geometry3d.Point> second)
    {
        double result = double.MaxValue;
        for (int index = 0; index < first.Count; index++)
        {
            for (int segment = 0; segment < second.Count - 1; segment++)
            {
                result = Math.Min(
                    result,
                    PointToSegmentDistanceSquared(
                        first[index],
                        second[segment],
                        second[segment + 1]));
            }
        }
        for (int index = 0; index < second.Count; index++)
        {
            for (int segment = 0; segment < first.Count - 1; segment++)
            {
                result = Math.Min(
                    result,
                    PointToSegmentDistanceSquared(
                        second[index],
                        first[segment],
                        first[segment + 1]));
            }
        }
        return result;
    }

    private static double PointToSegmentDistanceSquared(
        global::Tekla.Structures.Geometry3d.Point point,
        global::Tekla.Structures.Geometry3d.Point start,
        global::Tekla.Structures.Geometry3d.Point end)
    {
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double dz = end.Z - start.Z;
        double denominator = dx * dx + dy * dy + dz * dz;
        double parameter = denominator <= 1e-9
            ? 0
            : ((point.X - start.X) * dx +
               (point.Y - start.Y) * dy +
               (point.Z - start.Z) * dz) / denominator;
        parameter = Math.Max(0, Math.Min(1, parameter));
        double x = start.X + dx * parameter;
        double y = start.Y + dy * parameter;
        double z = start.Z + dz * parameter;
        double px = point.X - x;
        double py = point.Y - y;
        double pz = point.Z - z;
        return px * px + py * py + pz * pz;
    }

    private static Component FindSingleSelectedNodeComponent()
    {
        List<Component> selected = FindSelectedNodeComponents();
        return selected.Count == 1 ? selected[0] : null;
    }

    private static List<Component> FindSelectedNodeComponents()
    {
        var selected = new List<Component>();
        ModelObjectEnumerator objects =
            new global::Tekla.Structures.Model.UI.ModelObjectSelector()
                .GetSelectedObjects();
        while (objects.MoveNext())
        {
            if (!(objects.Current is Component candidate) ||
                !string.Equals(
                    candidate.Name,
                    "FachwerkLowerRigelNodePlugin",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            selected.Add(candidate);
        }
        return selected;
    }

    private static void CollectInputIdentifiers(
        object value,
        ICollection<Identifier> identifiers)
    {
        if (value == null)
        {
            return;
        }
        if (value is Identifier identifier)
        {
            identifiers.Add(identifier);
            return;
        }
        if (value is ModelObject modelObject &&
            modelObject.Identifier != null &&
            modelObject.Identifier.ID > 0)
        {
            identifiers.Add(modelObject.Identifier);
            return;
        }
        if (value is string || !(value is IEnumerable sequence))
        {
            return;
        }
        foreach (object item in sequence)
        {
            CollectInputIdentifiers(item, identifiers);
        }
    }

    private static bool IsYes(string value)
    {
        return string.Equals(
                value?.Trim(),
                "YES",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                value?.Trim(),
                "ДА",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value?.Trim(), "1", StringComparison.Ordinal);
    }

    private void EnsureDouble(
        Control control,
        double fallback)
    {
        if (IsUnset(control?.Text))
        {
            SetDoubleText(control, fallback);
        }
    }

    private static void SynchronizeFilter(
        Control source,
        Control target)
    {
        if (source is CheckBox sourceCheckBox &&
            target is CheckBox targetCheckBox)
        {
            targetCheckBox.Checked = sourceCheckBox.Checked;
        }
    }

    private void EnsureText(
        Control control,
        string fallback)
    {
        if (IsUnset(control?.Text))
        {
            control.Text = fallback ?? string.Empty;
        }
    }

    private static void SetDoubleText(Control control, double value)
    {
        if (control != null)
        {
            control.Text = value.ToString(
                "0.###############",
                CultureInfo.InvariantCulture);
        }
    }

    private void WriteDoubleAttribute(
        Control control,
        double value)
    {
        if (control == null)
        {
            return;
        }
        control.Text = value.ToString("0.###", CultureInfo.InvariantCulture);
        SetAttributeValue(control, value);
    }

    private void WriteTextAttribute(
        Control control,
        string value)
    {
        if (control == null)
        {
            return;
        }
        control.Text = value ?? string.Empty;
        SetAttributeValue(control, control.Text);
    }

    private static double ReadDouble(
        Control control,
        double fallback)
    {
        string raw = control?.Text;
        if (IsUnset(raw))
        {
            return fallback;
        }
        if (double.TryParse(
                raw,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double invariant))
        {
            return invariant;
        }
        if (double.TryParse(
                raw,
                NumberStyles.Float,
                CultureInfo.CurrentCulture,
                out double current))
        {
            return current;
        }
        return fallback;
    }

    private static double ParseDouble(
        string raw,
        double fallback)
    {
        if (double.TryParse(
                raw,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double invariant))
        {
            return invariant;
        }
        if (double.TryParse(
                raw,
                NumberStyles.Float,
                CultureInfo.CurrentCulture,
                out double current))
        {
            return current;
        }
        return fallback;
    }

    private static bool IsUnsetNumber(double value)
    {
        return double.IsNaN(value) ||
            double.IsInfinity(value) ||
            Math.Abs(value - int.MinValue) < 0.5;
    }

    private static string NormalizeYesNo(
        string value,
        string fallback)
    {
        if (IsUnset(value))
        {
            return fallback;
        }
        return IsYes(value) ? "YES" : "NO";
    }

    private static bool IsUnset(string value)
    {
        return string.IsNullOrWhiteSpace(value) ||
            string.Equals(
                value.Trim(),
                int.MinValue.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
    }

    private void AssignAttribute(string name, Control control)
    {
        switch (name)
        {
            case "fklr_gap": _attributeFklrGap = control; break;
            case "fklr_diameter": _attributeFklrDiameter = control; break;
            case "fklr_plate_profile": _attributeFklrPlateProfile = control; break;
            case "fklr_material": _attributeFklrMaterial = control; break;
            case "fklr_class": _attributeFklrClass = control; break;
            case "fklr_plate_width": _attributeFklrPlateWidth = control; break;
            case "fklr_left_length": _attributeFklrLeftLength = control; break;
            case "fklr_right_length": _attributeFklrRightLength = control; break;
            case "fklr_auto_len": _attributeFklrAutomaticLength = control; break;
            case "fklr_bottom_cap": _attributeFklrBottomClosure = control; break;
            case "fklr_outer_cut": _attributeFklrOuterFlangeTubeCut = control; break;
            case "fklr_axis_corr": _attributeFklrAxisCorrection = control; break;
            case "fklr_control_line": _attributeFklrControlLine = control; break;
            default:
                throw new InvalidOperationException(
                    "Не зарегистрировано поле формы: " + name);
        }
    }

    private void AssignFilter(string name, Control control)
    {
        switch (name)
        {
            case "fklr_gap": _filterFklrGap = control; break;
            case "fklr_diameter": _filterFklrDiameter = control; break;
            case "fklr_plate_profile": _filterFklrPlateProfile = control; break;
            case "fklr_material": _filterFklrMaterial = control; break;
            case "fklr_class": _filterFklrClass = control; break;
            case "fklr_plate_width": _filterFklrPlateWidth = control; break;
            case "fklr_left_length": _filterFklrLeftLength = control; break;
            case "fklr_right_length": _filterFklrRightLength = control; break;
            case "fklr_auto_len": _filterFklrAutomaticLength = control; break;
            case "fklr_bottom_cap": _filterFklrBottomClosure = control; break;
            case "fklr_outer_cut": _filterFklrOuterFlangeTubeCut = control; break;
            case "fklr_axis_corr": _filterFklrAxisCorrection = control; break;
            case "fklr_control_line": _filterFklrControlLine = control; break;
            default:
                throw new InvalidOperationException(
                    "Не зарегистрирован фильтр формы: " + name);
        }
    }

    private static void SelectValue(
        ComboBox combo,
        IReadOnlyList<ComboOption> options,
        string value,
        string fallbackValue = "YES")
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
        for (var index = 0; index < options.Count; index++)
        {
            if (string.Equals(
                    options[index].Value,
                    fallbackValue,
                    StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = index;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }

    private static void Execute(Action action)
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
                "Узел нижнего ригеля",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void TraceState(string source)
    {
        Trace(
            source +
            " gap=" + (_attributeFklrGap?.Text ?? string.Empty) +
            " diameter=" + (_attributeFklrDiameter?.Text ?? string.Empty) +
            " profile=" + (_attributeFklrPlateProfile?.Text ?? string.Empty) +
            " material=" + (_attributeFklrMaterial?.Text ?? string.Empty) +
            " class=" + (_attributeFklrClass?.Text ?? string.Empty) +
            " width=" + (_attributeFklrPlateWidth?.Text ?? string.Empty) +
            " length=" + (_attributeFklrLeftLength?.Text ?? string.Empty) +
            " auto=" + (_attributeFklrAutomaticLength?.Text ?? string.Empty) +
            " cap=" + (_attributeFklrBottomClosure?.Text ?? string.Empty) +
            " axis=" + (_attributeFklrAxisCorrection?.Text ?? string.Empty) +
            " control=" + (_attributeFklrControlLine?.Text ?? string.Empty));
    }

    private static void Trace(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(
                    Path.GetTempPath(),
                    "fachwerk_lower_rigel_node_form_trace.txt"),
                DateTime.Now.ToString(
                    "s",
                    CultureInfo.InvariantCulture) +
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

    private sealed class YesNoBinding
    {
        internal YesNoBinding(
            string name,
            TextBox storage,
            ComboBox combo,
            CheckBox filter,
            IReadOnlyList<ComboOption> options,
            string defaultValue)
        {
            Name = name;
            Storage = storage;
            Combo = combo;
            Filter = filter;
            Options = options;
            DefaultValue = defaultValue;
        }

        internal string Name { get; }
        internal TextBox Storage { get; }
        internal ComboBox Combo { get; }
        internal CheckBox Filter { get; }
        internal IReadOnlyList<ComboOption> Options { get; }
        internal string DefaultValue { get; }
    }

    private sealed class ResolvedNodeInput
    {
        internal ResolvedNodeInput(
            Beam leftTube,
            Beam rightTube,
            Part outerFlange,
            Part innerFlange,
            Part leftWeb,
            Part rightWeb)
        {
            LeftTube = leftTube;
            RightTube = rightTube;
            OuterFlange = outerFlange;
            InnerFlange = innerFlange;
            LeftWeb = leftWeb;
            RightWeb = rightWeb;
        }

        internal Beam LeftTube { get; }
        internal Beam RightTube { get; }
        internal Part OuterFlange { get; }
        internal Part InnerFlange { get; }
        internal Part LeftWeb { get; }
        internal Part RightWeb { get; }

        internal string TraceValue =>
            Id(LeftTube) + "," +
            Id(RightTube) + "," +
            Id(OuterFlange) + "," +
            Id(InnerFlange) + "," +
            Id(LeftWeb) + "," +
            Id(RightWeb);

        private static string Id(ModelObject value) =>
            value?.Identifier?.ID.ToString(CultureInfo.InvariantCulture) ?? "0";
    }

    private sealed class TextFieldBinding
    {
        internal TextFieldBinding(
            string name,
            string typeName,
            TextBox control,
            CheckBox filter,
            string defaultValue)
        {
            Name = name;
            TypeName = typeName;
            Control = control;
            Filter = filter;
            DefaultValue = defaultValue;
        }

        internal string Name { get; }
        internal string TypeName { get; }
        internal TextBox Control { get; }
        internal CheckBox Filter { get; }
        internal string DefaultValue { get; }
    }
}

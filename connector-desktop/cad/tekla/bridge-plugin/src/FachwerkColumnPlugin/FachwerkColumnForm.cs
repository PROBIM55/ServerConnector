#nullable disable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Tekla.Structures.Dialog;
using Tekla.Structures.Model;
using TeklaPoint = Tekla.Structures.Geometry3d.Point;

namespace Structura.Tekla.Fachwerk;

public sealed class FachwerkColumnForm : PluginFormBase
{
    // Tekla 2020 FormBase.ScanTaggedControls scans direct non-public Control
    // fields only. Controls kept exclusively in collections are invisible to
    // the dialog attribute manager, even when StructuresExtender tags exist.
    private Control _attributeFkProfileKey;
    private Control _attributeFkMark;
    private Control _attributeFkMaterial;
    private Control _attributeFkClass;
    private Control _attributeFkRotationDeg;
    private Control _attributeFkCatalogPath;
    private Control _attributeFkBevelProfile;
    private Control _attributeFkBreak1Z;
    private Control _attributeFkBreak1Mode;
    private Control _attributeFkBreak2Z;
    private Control _attributeFkBreak2Mode;
    private Control _attributeFkBreak3Z;
    private Control _attributeFkBreak3Mode;
    private Control _attributeFkBreak4Z;
    private Control _attributeFkBreak4Mode;
    private Control _attributeFkBreak5Z;
    private Control _attributeFkBreak5Mode;
    private Control _attributeFkBreak6Z;
    private Control _attributeFkBreak6Mode;
    private Control _attributeFkBreak7Z;
    private Control _attributeFkBreak7Mode;
    private Control _attributeFkBreak8Z;
    private Control _attributeFkBreak8Mode;
    private Control _attributeFkFlLoOffs;
    private Control _attributeFkFlHiOffs;
    private Control _attributeFkWebLoOffs;
    private Control _attributeFkWebHiOffs;
    private Control _attributeFkOffsetSchema;
    private Control _attributeFkBreak1FlLo;
    private Control _attributeFkBreak1FlHi;
    private Control _attributeFkBreak1WebLo;
    private Control _attributeFkBreak1WebHi;
    private Control _attributeFkBreak2FlLo;
    private Control _attributeFkBreak2FlHi;
    private Control _attributeFkBreak2WebLo;
    private Control _attributeFkBreak2WebHi;
    private Control _attributeFkBreak3FlLo;
    private Control _attributeFkBreak3FlHi;
    private Control _attributeFkBreak3WebLo;
    private Control _attributeFkBreak3WebHi;
    private Control _attributeFkBreak4FlLo;
    private Control _attributeFkBreak4FlHi;
    private Control _attributeFkBreak4WebLo;
    private Control _attributeFkBreak4WebHi;
    private Control _attributeFkBreak5FlLo;
    private Control _attributeFkBreak5FlHi;
    private Control _attributeFkBreak5WebLo;
    private Control _attributeFkBreak5WebHi;
    private Control _attributeFkBreak6FlLo;
    private Control _attributeFkBreak6FlHi;
    private Control _attributeFkBreak6WebLo;
    private Control _attributeFkBreak6WebHi;
    private Control _attributeFkBreak7FlLo;
    private Control _attributeFkBreak7FlHi;
    private Control _attributeFkBreak7WebLo;
    private Control _attributeFkBreak7WebHi;
    private Control _attributeFkBreak8FlLo;
    private Control _attributeFkBreak8FlHi;
    private Control _attributeFkBreak8WebLo;
    private Control _attributeFkBreak8WebHi;
    private Control _attributeFkStiffEnabled;
    private Control _attributeFkStiffProfile;
    private Control _attributeFkStiffMaterial;
    private Control _attributeFkStiffChamfer;
    private Control _attributeFkStiffLevels;
    private Control _attributeFkStiffMode;
    private Control _attributeFkStiffSpacing;
    private Control _attributeFkStiffDistances;
    private Control _attributeFkStiffInnerGap;
    private Control _attributeFkStiffAltGap;

    private Control _filterFkProfileKey;
    private Control _filterFkMark;
    private Control _filterFkMaterial;
    private Control _filterFkClass;
    private Control _filterFkRotationDeg;
    private Control _filterFkCatalogPath;
    private Control _filterFkBevelProfile;
    private Control _filterFkBreak1Z;
    private Control _filterFkBreak1Mode;
    private Control _filterFkBreak2Z;
    private Control _filterFkBreak2Mode;
    private Control _filterFkBreak3Z;
    private Control _filterFkBreak3Mode;
    private Control _filterFkBreak4Z;
    private Control _filterFkBreak4Mode;
    private Control _filterFkBreak5Z;
    private Control _filterFkBreak5Mode;
    private Control _filterFkBreak6Z;
    private Control _filterFkBreak6Mode;
    private Control _filterFkBreak7Z;
    private Control _filterFkBreak7Mode;
    private Control _filterFkBreak8Z;
    private Control _filterFkBreak8Mode;
    private Control _filterFkFlLoOffs;
    private Control _filterFkFlHiOffs;
    private Control _filterFkWebLoOffs;
    private Control _filterFkWebHiOffs;
    private Control _filterFkOffsetSchema;
    private Control _filterFkBreak1FlLo;
    private Control _filterFkBreak1FlHi;
    private Control _filterFkBreak1WebLo;
    private Control _filterFkBreak1WebHi;
    private Control _filterFkBreak2FlLo;
    private Control _filterFkBreak2FlHi;
    private Control _filterFkBreak2WebLo;
    private Control _filterFkBreak2WebHi;
    private Control _filterFkBreak3FlLo;
    private Control _filterFkBreak3FlHi;
    private Control _filterFkBreak3WebLo;
    private Control _filterFkBreak3WebHi;
    private Control _filterFkBreak4FlLo;
    private Control _filterFkBreak4FlHi;
    private Control _filterFkBreak4WebLo;
    private Control _filterFkBreak4WebHi;
    private Control _filterFkBreak5FlLo;
    private Control _filterFkBreak5FlHi;
    private Control _filterFkBreak5WebLo;
    private Control _filterFkBreak5WebHi;
    private Control _filterFkBreak6FlLo;
    private Control _filterFkBreak6FlHi;
    private Control _filterFkBreak6WebLo;
    private Control _filterFkBreak6WebHi;
    private Control _filterFkBreak7FlLo;
    private Control _filterFkBreak7FlHi;
    private Control _filterFkBreak7WebLo;
    private Control _filterFkBreak7WebHi;
    private Control _filterFkBreak8FlLo;
    private Control _filterFkBreak8FlHi;
    private Control _filterFkBreak8WebLo;
    private Control _filterFkBreak8WebHi;
    private Control _filterFkStiffEnabled;
    private Control _filterFkStiffProfile;
    private Control _filterFkStiffMaterial;
    private Control _filterFkStiffChamfer;
    private Control _filterFkStiffLevels;
    private Control _filterFkStiffMode;
    private Control _filterFkStiffSpacing;
    private Control _filterFkStiffDistances;
    private Control _filterFkStiffInnerGap;
    private Control _filterFkStiffAltGap;

    private readonly List<TextFieldBinding> _textFields = new();
    private readonly List<ComboFieldBinding> _comboFields = new();
    private readonly List<CheckBox> _filterControls = new();
    private readonly Dictionary<string, CheckBox> _filtersByAttribute =
        new(StringComparer.Ordinal);
    private Panel _hiddenBindingsPanel;
    private ComboBox _createBevelsCombo;
    private ComboBox _createSplitsCombo;
    private JointOffsetStorage _jointOffsetStorage;
    private bool _loadingStorage;
    private string _lastProfileKey = "СФ1";

    public FachwerkColumnForm()
    {
        Text = "Стойка фахверка";
        Width = 760;
        Height = 780;
        MinimumSize = new Size(700, 660);
        StartPosition = FormStartPosition.CenterScreen;
        RegisterPropertyBinding(typeof(TextBox), "Text", DataSourceUpdateMode.OnPropertyChanged);
        BuildLayout();

        FormInitialized += (_, _) => ExecuteUiAction(
            () => SynchronizeDerivedUi("FormInitialized"));
        AttributesLoadedFromModel += (_, _) => ExecuteUiAction(
            () => SynchronizeDerivedUi("AttributesLoadedFromModel"));
        TraceForm("Constructed assembly=" + GetType().Assembly.Location);
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        root.Controls.Add(new Label
        {
            Text = "Параметрическая стойка СФ1-СФ47 по эскизам КМ",
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

        _hiddenBindingsPanel = new Panel { Visible = false, Dock = DockStyle.Fill };
        root.Controls.Add(_hiddenBindingsPanel, 0, 2);
        _jointOffsetStorage = new JointOffsetStorage(
            AddHiddenStringBinding("fk_fl_lo_offs"),
            AddHiddenStringBinding("fk_fl_hi_offs"),
            AddHiddenStringBinding("fk_web_lo_offs"),
            AddHiddenStringBinding("fk_web_hi_offs"));
        AddHiddenStringBinding("fk_offset_schema");

        var mainFields = AddFieldsTab(tabs, "Основное");
        var splitFields = AddFieldsTab(tabs, "Членение");
        var stiffenerFields = AddFieldsTab(tabs, "Рёбра");

        var profileValues = new List<string>();
        for (var index = 1; index <= 47; index++)
        {
            profileValues.Add("СФ" + index.ToString(CultureInfo.InvariantCulture));
        }
        var classValues = new List<string>();
        for (var index = 1; index <= 14; index++)
        {
            classValues.Add(index.ToString(CultureInfo.InvariantCulture));
        }

        AddSection(mainFields, "Параметры стойки");
        AddStringCombo(
            mainFields,
            "Марка профиля",
            "fk_profile_key",
            profileValues,
            "СФ1");
        AddText(mainFields, "Марка деталей", "fk_mark", "String", "СФ1");
        AddText(mainFields, "Материал", "fk_material", "String", "C355-5");
        AddStringCombo(mainFields, "Класс Tekla", "fk_class", classValues, "3");
        AddDouble(mainFields, "Доп. поворот от выбранной локальной +X, град", "fk_rotation_deg", 0);
        AddText(mainFields, "Каталог траекторий (пусто = рядом с DLL)", "fk_catalog_path", "String", string.Empty);

        AddSection(mainFields, "Разделка стенок");
        _createBevelsCombo = AddLocalYesNoCombo(mainFields, "Создавать разделку", true);
        AddStringCombo(mainFields, "Профиль разделки", "fk_bevel_profile", new[] { "TRI_A14*14" }, "TRI_A14*14");
        AddNote(mainFields, "Геометрия разделки включается после фиксации параметров фактической детали TRI_A14*14 из модели.");

        AddSection(stiffenerFields, "Внутренние рёбра");
        AddNote(
            stiffenerFields,
            "Рёбра строятся сверху вниз по истинной длине наружного пояса. Авторасстановка учитывает перпендикулярные стыки и переход 450/380 как уже существующие рёбра, но игнорирует монтажные стыки.");
        AddStringMappedCombo(
            stiffenerFields,
            "Создавать рёбра",
            "fk_stiff_enabled",
            new[]
            {
                new ComboOption("Нет", "NO"),
                new ComboOption("Да", "YES"),
            },
            "YES");
        AddStringMappedCombo(
            stiffenerFields,
            "Режим расстановки",
            "fk_stiff_mode",
            new[]
            {
                new ComboOption("По абсолютным отметкам", "ELEVATIONS"),
                new ComboOption("По шагу вдоль наружного пояса", "SPACING"),
                new ComboOption("Авторасстановка", "AUTO"),
            },
            "ELEVATIONS");
        AddDouble(
            stiffenerFields,
            "Шаг авторасстановки, мм",
            "fk_stiff_spacing",
            1500);
        AddText(
            stiffenerFields,
            "Профиль пластины",
            "fk_stiff_profile",
            "String",
            "PL8");
        AddText(
            stiffenerFields,
            "Материал",
            "fk_stiff_material",
            "String",
            "C355-5");
        AddDouble(
            stiffenerFields,
            "Фаски у наружного пояса, мм",
            "fk_stiff_chamfer",
            20);
        AddDouble(
            stiffenerFields,
            "Зазор от внутреннего пояса, мм",
            "fk_stiff_inner_gap",
            20);
        AddStringMappedCombo(
            stiffenerFields,
            "Чередовать зазор у поясов",
            "fk_stiff_alt_gap",
            new[]
            {
                new ComboOption("Нет", "NO"),
                new ComboOption("Да", "YES"),
            },
            "NO");
        AddText(
            stiffenerFields,
            "Последовательные расстояния вдоль наружного пояса, мм (через пробел или ';')",
            "fk_stiff_distances",
            "String",
            "1500");
        AddText(
            stiffenerFields,
            "Абсолютные отметки, мм (через ';')",
            "fk_stiff_levels",
            "String",
            string.Empty);

        AddSection(splitFields, "Членение по абсолютным отметкам модели");
        AddNote(splitFields, "Перпендикулярная плоскость разрывает 4 детали. Опорная точка рёбер участвует только в авторасстановке и не членит стойку.");
        _createSplitsCombo = AddLocalYesNoCombo(splitFields, "Создавать членение", false);
        for (var index = 1; index <= 8; index++)
        {
            var breakField = AddDouble(splitFields, $"Отметка {index}, мм (0 = не задана)", $"fk_break_{index}_z", 0);
            breakField.TextChanged += (_, _) =>
            {
                if (!_loadingStorage && HasActiveBreaks()) SelectYesNo(_createSplitsCombo, true);
            };
            var modeCombo = AddStringMappedCombo(
                splitFields,
                $"Режим {index}",
                $"fk_break_{index}_mode",
                new[]
            {
                new ComboOption(
                    "Перпендикулярная плоскость",
                    FachwerkColumnBreak.AllFourMode),
                new ComboOption(
                    "Монтажный стык",
                    FachwerkColumnBreak.ErectionSpliceMode),
                new ComboOption(
                    "Опорная точка рёбер (без членения)",
                    FachwerkColumnBreak.StiffenerReferenceMode),
            },
                FachwerkColumnBreak.AllFourMode);
            var jointIndex = index;
            modeCombo.SelectedIndexChanged += (_, _) =>
            {
                if (_loadingStorage ||
                    !string.Equals(
                        SelectedComboValue(
                            $"fk_break_{jointIndex}_mode",
                            FachwerkColumnBreak.AllFourMode),
                        FachwerkColumnBreak.ErectionSpliceMode,
                        StringComparison.Ordinal))
                {
                    return;
                }
                ApplyErectionJointPreset(jointIndex);
            };
            BindJointOffsetEditor(AddDouble(
                splitFields,
                $"Стык {index}: пояс нижней секции, мм",
                $"fk_break_{index}_fl_lo",
                0));
            BindJointOffsetEditor(AddDouble(
                splitFields,
                $"Стык {index}: пояс верхней секции, мм",
                $"fk_break_{index}_fl_hi",
                0));
            BindJointOffsetEditor(AddDouble(
                splitFields,
                $"Стык {index}: стенка нижней секции, мм",
                $"fk_break_{index}_web_lo",
                0));
            BindJointOffsetEditor(AddDouble(
                splitFields,
                $"Стык {index}: стенка верхней секции, мм",
                $"fk_break_{index}_web_hi",
                0));
        }

        root.Controls.Add(BuildButtons(), 0, 3);
    }

    private static TableLayoutPanel AddFieldsTab(TabControl tabs, string title)
    {
        var tab = new TabPage
        {
            Text = title,
            Padding = new Padding(6),
            UseVisualStyleBackColor = true,
        };
        var scroll = new Panel
        {
            Dock = DockStyle.Fill,
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
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 290));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scroll.Controls.Add(fields);
        tab.Controls.Add(scroll);
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
            PushUiToStorage();
            Modify();
        })));
        panel.Controls.Add(Button("Применить", (_, _) => ExecuteUiAction(() =>
        {
            PushUiToStorage();
            Apply();
        })));
        panel.Controls.Add(Button("Получить", (_, _) => ExecuteUiAction(() =>
        {
            TraceForm("GET requested");
            Get();
        })));
        panel.Controls.Add(Button("ОК", (_, _) => ExecuteUiAction(() =>
        {
            PushUiToStorage();
            Apply();
            Close();
        })));
        panel.Controls.Add(Button("Включить все", (_, _) => SetAllFilters(true)));
        panel.Controls.Add(Button("Выключить все", (_, _) => SetAllFilters(false)));
        return panel;
    }

    private void SetAllFilters(bool enabled)
    {
        foreach (var filter in _filterControls)
        {
            filter.Checked = enabled;
        }
    }

    private static Button Button(string text, EventHandler handler)
    {
        var button = new Button { Text = text, AutoSize = true, Margin = new Padding(5) };
        button.Click += handler;
        return button;
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

    private void AddNote(TableLayoutPanel panel, string text)
    {
        var row = panel.RowCount++;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            MaximumSize = new Size(600, 0),
            Margin = new Padding(28, 3, 3, 8),
        };
        panel.Controls.Add(label, 0, row);
        panel.SetColumnSpan(label, 3);
    }

    private TextBox AddDouble(TableLayoutPanel panel, string label, string name, double value) =>
        AddText(panel, label, name, "Double", value.ToString("0.###", CultureInfo.InvariantCulture));

    private TextBox AddText(TableLayoutPanel panel, string labelText, string name, string typeName, string value)
    {
        var row = panel.RowCount++;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var filter = CreateFilter(name);
        var label = CreateFieldLabel(labelText);
        var input = CreateBoundTextBox(name, typeName, value, false);
        _textFields.Add(new TextFieldBinding(name, typeName, input, value));
        panel.Controls.Add(filter, 0, row);
        panel.Controls.Add(label, 1, row);
        panel.Controls.Add(input, 2, row);
        return input;
    }

    private void AddStringCombo(
        TableLayoutPanel panel,
        string label,
        string name,
        IEnumerable<string> values,
        string defaultValue)
    {
        var options = new List<ComboOption>();
        foreach (var value in values)
        {
            options.Add(new ComboOption(value, value));
        }
        AddStringMappedCombo(panel, label, name, options, defaultValue);
    }

    private ComboBox AddStringMappedCombo(
        TableLayoutPanel panel,
        string labelText,
        string name,
        IEnumerable<ComboOption> values,
        string defaultValue) =>
        AddCombo(panel, labelText, name, "String", values, defaultValue);

    private ComboBox AddLocalYesNoCombo(TableLayoutPanel panel, string labelText, bool defaultValue)
    {
        var row = panel.RowCount++;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var placeholder = new Panel { Width = 1, Height = 1 };
        var combo = new ComboBox
        {
            Name = "local_" + row.ToString(CultureInfo.InvariantCulture),
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Fill,
            Margin = new Padding(4),
        };
        combo.Items.AddRange(new object[] { "Нет", "Да" });
        SelectYesNo(combo, defaultValue);
        panel.Controls.Add(placeholder, 0, row);
        panel.Controls.Add(CreateFieldLabel(labelText), 1, row);
        panel.Controls.Add(combo, 2, row);
        return combo;
    }

    private ComboBox AddCombo(
        TableLayoutPanel panel,
        string labelText,
        string name,
        string typeName,
        IEnumerable<ComboOption> values,
        object defaultValue)
    {
        var optionList = new List<ComboOption>();
        foreach (var value in values) optionList.Add(value);
        var options = optionList.ToArray();
        var row = panel.RowCount++;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var filter = CreateFilter(name);
        var label = CreateFieldLabel(labelText);
        var combo = new ComboBox
        {
            Name = name + "_combo",
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Fill,
            Margin = new Padding(4),
        };
        combo.DisplayMember = nameof(ComboOption.Label);
        combo.Items.AddRange(options);
        var storage = CreateBoundTextBox(name, typeName, ToInvariantText(defaultValue), true);
        var binding = new ComboFieldBinding(name, typeName, combo, storage, options, defaultValue);
        _comboFields.Add(binding);
        SelectComboValue(binding, defaultValue);
        combo.SelectedIndexChanged += (_, _) =>
        {
            if (_loadingStorage) return;
            if (string.Equals(binding.Name, "fk_profile_key", StringComparison.Ordinal))
                SynchronizeMarkWithProfile();
            PushComboToStorage(binding);
            if (string.Equals(binding.Name, "fk_stiff_mode", StringComparison.Ordinal) ||
                string.Equals(binding.Name, "fk_profile_key", StringComparison.Ordinal))
            {
                ExecuteUiAction(() => RefreshAutomaticStiffenerDistancesInUi());
            }
        };
        panel.Controls.Add(filter, 0, row);
        panel.Controls.Add(label, 1, row);
        panel.Controls.Add(combo, 2, row);
        return combo;
    }

    private void ApplyErectionJointPreset(int jointIndex)
    {
        SetTextFieldValue(
            $"fk_break_{jointIndex}_fl_lo",
            FormatJointOffset(FachwerkColumnBreak.ErectionFlangeLowerOffset),
            true);
        SetTextFieldValue(
            $"fk_break_{jointIndex}_fl_hi",
            FormatJointOffset(FachwerkColumnBreak.ErectionFlangeUpperOffset),
            true);
        SetTextFieldValue(
            $"fk_break_{jointIndex}_web_lo",
            FormatJointOffset(FachwerkColumnBreak.ErectionWebLowerOffset),
            true);
        SetTextFieldValue(
            $"fk_break_{jointIndex}_web_hi",
            FormatJointOffset(FachwerkColumnBreak.ErectionWebUpperOffset),
            true);
        SelectYesNo(_createSplitsCombo, true);
    }

    private static Label CreateFieldLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(4),
    };

    private TextBox CreateBoundTextBox(string name, string typeName, string value, bool hidden)
    {
        var input = new TextBox
        {
            Name = hidden ? name + "_storage" : name,
            Text = value ?? string.Empty,
            Dock = hidden ? DockStyle.None : DockStyle.Fill,
            Visible = !hidden,
            TabStop = !hidden,
            Margin = new Padding(4),
        };
        structuresExtender.SetAttributeName(input, name);
        structuresExtender.SetAttributeTypeName(input, typeName);
        structuresExtender.SetBindPropertyName(input, "Text");
        AssignAttributeControl(name, input);
        if (hidden) _hiddenBindingsPanel.Controls.Add(input);
        return input;
    }

    private TextBox AddHiddenStringBinding(string name)
    {
        return AddHiddenBinding(name, "String", string.Empty);
    }

    private TextBox AddHiddenBinding(
        string name,
        string typeName,
        string defaultValue)
    {
        var filter = CreateFilter(name);
        filter.Visible = false;
        _hiddenBindingsPanel.Controls.Add(filter);
        var storage = CreateBoundTextBox(
            name,
            typeName,
            defaultValue,
            true);
        _textFields.Add(new TextFieldBinding(
            name,
            typeName,
            storage,
            defaultValue));
        return storage;
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
        _filterControls.Add(filter);
        _filtersByAttribute.Add(attributeName, filter);
        return filter;
    }

    private bool IsFilterEnabled(string attributeName)
    {
        var hasFilter = _filtersByAttribute.TryGetValue(attributeName, out var filter);
        return ShouldTransferAttribute(hasFilter, hasFilter && filter.Checked);
    }

    internal static bool ShouldTransferAttribute(bool hasFilter, bool filterChecked) =>
        !hasFilter || filterChecked;

    private void SynchronizeDerivedUi(string source)
    {
        if (_loadingStorage)
        {
            TraceForm("STORAGE_LOAD ignored source=" + source);
            return;
        }
        _loadingStorage = true;
        try
        {
            TraceForm("STORAGE_LOAD begin source=" + source);
            PullPersistedValuesIntoUi();
            NormalizeUiValues();
            RefreshAutomaticStiffenerDistancesInUi();
            TraceForm("STORAGE_LOAD end source=" + source);
        }
        finally
        {
            _loadingStorage = false;
        }
    }

    private void PushUiToStorage()
    {
        NormalizeUiValues();
        if (!IsYes(_createSplitsCombo))
        {
            foreach (var field in _textFields)
            {
                if (IsBreakNumericAttribute(field.Name) &&
                    IsFilterEnabled(field.Name))
                {
                    field.Control.Text = "0";
                }
            }
        }
        RefreshAutomaticStiffenerDistancesInUi(validateSpacing: true);
        var transfersDirectJointOffsets = HasTransferredDirectJointOffset();
        SetTextFieldValue(
            "fk_offset_schema",
            FachwerkColumnGeometry.DirectOffsetSchema,
            false);
        if (_filtersByAttribute.TryGetValue(
                "fk_offset_schema",
                out var offsetSchemaFilter))
        {
            offsetSchemaFilter.Checked = transfersDirectJointOffsets;
        }
        if (transfersDirectJointOffsets)
        {
            RetireLegacyJointOffsetStorage();
        }
        foreach (var field in _textFields)
        {
            if (!IsFilterEnabled(field.Name)) continue;
            SetTypedAttributeValue(field.Control, field.TypeName, field.Control.Text, field.Name);
        }
        foreach (var field in _comboFields)
        {
            if (!IsFilterEnabled(field.Name)) continue;
            PushComboToStorage(field);
        }
        ComboFieldBinding bevelField = null;
        foreach (var candidate in _comboFields)
        {
            if (candidate.Name != "fk_bevel_profile") continue;
            bevelField = candidate;
            break;
        }
        if (bevelField != null &&
            IsFilterEnabled(bevelField.Name) &&
            !IsYes(_createBevelsCombo))
        {
            bevelField.Storage.Text = string.Empty;
            SetTypedAttributeValue(bevelField.Storage, bevelField.TypeName, string.Empty, bevelField.Name);
        }
    }

    private void PullPersistedValuesIntoUi()
    {
        PullOrdinaryTextFields();
        MigrateLegacyJointOffsets();
        var bevelEnabled = false;
        foreach (var field in _comboFields)
        {
            object value = ReadPersistedValue(
                field.Storage,
                field.TypeName,
                field.DefaultValue);
            if (field.Name == "fk_bevel_profile")
                bevelEnabled = !string.IsNullOrWhiteSpace(ToInvariantText(value));
            SelectComboValue(field, value);
        }
        SelectYesNo(_createBevelsCombo, bevelEnabled);
        SelectYesNo(_createSplitsCombo, HasActiveBreaks());
        TraceLoadedState();
    }

    private void PullOrdinaryTextFields()
    {
        foreach (var field in _textFields)
        {
            if (UsesTeklaBoundValueWithoutSecondRead(field.Name))
                continue;
            var value = ReadPersistedValue(
                field.Control,
                field.TypeName,
                field.DefaultValue);
            field.Control.Text = NormalizeFieldText(
                field.TypeName,
                ToInvariantText(value),
                field.DefaultValue);
        }
    }

    private object ReadPersistedValue(
        Control control,
        string typeName,
        object defaultValue)
    {
        try
        {
            if (string.Equals(
                    typeName,
                    "Double",
                    StringComparison.OrdinalIgnoreCase))
            {
                return GetAttributeValue<double>(control);
            }
            if (string.Equals(
                    typeName,
                    "Integer",
                    StringComparison.OrdinalIgnoreCase))
            {
                return GetAttributeValue<int>(control);
            }
            return GetAttributeValue<string>(control);
        }
        catch
        {
            var raw = control?.Text;
            return IsUnsetText(raw) ? defaultValue : raw;
        }
    }

    internal static bool UsesTeklaBoundValueWithoutSecondRead(string name) =>
        IsDirectJointOffsetAttribute(name);

    private void TraceLoadedState()
    {
        TraceForm(
            "LOADED profile=" +
            SelectedComboValue("fk_profile_key", "СФ1") +
            " stiffEnabled=" +
            SelectedComboValue("fk_stiff_enabled", "NO") +
            " stiffMode=" +
            SelectedComboValue("fk_stiff_mode", "ELEVATIONS") +
            " stiffDistances=" +
            (FindTextField("fk_stiff_distances")?.Control.Text ?? string.Empty) +
            " stiffSpacing=" +
            (FindTextField("fk_stiff_spacing")?.Control.Text ?? string.Empty) +
            " stiffProfile=" +
            (FindTextField("fk_stiff_profile")?.Control.Text ?? string.Empty) +
            " stiffChamfer=" +
            (FindTextField("fk_stiff_chamfer")?.Control.Text ?? string.Empty) +
            " stiffGap=" +
            (FindTextField("fk_stiff_inner_gap")?.Control.Text ?? string.Empty) +
            " stiffAlternateGap=" +
            SelectedComboValue("fk_stiff_alt_gap", "NO"));
    }

    private void MigrateLegacyJointOffsets()
    {
        var schema = FindTextField("fk_offset_schema");
        if (schema != null &&
            string.Equals(
                schema.Control.Text,
                FachwerkColumnGeometry.DirectOffsetSchema,
                StringComparison.Ordinal))
        {
            return;
        }
        if (!HasLegacyJointOffsetStorage(
                _jointOffsetStorage.FlangeLower.Text,
                _jointOffsetStorage.FlangeUpper.Text,
                _jointOffsetStorage.WebLower.Text,
                _jointOffsetStorage.WebUpper.Text))
        {
            TraceForm("JOINT_OFFSETS migration skipped: legacy storage is empty");
            return;
        }

        var flangeLower = FachwerkColumnGeometry.ParseOffsetValues(
            _jointOffsetStorage.FlangeLower.Text);
        var flangeUpper = FachwerkColumnGeometry.ParseOffsetValues(
            _jointOffsetStorage.FlangeUpper.Text);
        var webLower = FachwerkColumnGeometry.ParseOffsetValues(
            _jointOffsetStorage.WebLower.Text);
        var webUpper = FachwerkColumnGeometry.ParseOffsetValues(
            _jointOffsetStorage.WebUpper.Text);
        for (var index = 0; index < 8; index++)
        {
            var number = (index + 1).ToString(CultureInfo.InvariantCulture);
            SetTextFieldValue(
                "fk_break_" + number + "_fl_lo",
                FormatJointOffset(flangeLower[index]),
                true);
            SetTextFieldValue(
                "fk_break_" + number + "_fl_hi",
                FormatJointOffset(flangeUpper[index]),
                true);
            SetTextFieldValue(
                "fk_break_" + number + "_web_lo",
                FormatJointOffset(webLower[index]),
                true);
            SetTextFieldValue(
                "fk_break_" + number + "_web_hi",
                FormatJointOffset(webUpper[index]),
                true);
        }
        SetTextFieldValue(
            "fk_offset_schema",
            FachwerkColumnGeometry.DirectOffsetSchema,
            true);
        TraceForm(
            "JOINT_OFFSETS MIGRATED " +
            "flLo=" + _jointOffsetStorage.FlangeLower.Text +
            " flHi=" + _jointOffsetStorage.FlangeUpper.Text +
            " webLo=" + _jointOffsetStorage.WebLower.Text +
            " webHi=" + _jointOffsetStorage.WebUpper.Text);
    }

    internal static bool HasLegacyJointOffsetStorage(
        string flangeLower,
        string flangeUpper,
        string webLower,
        string webUpper) =>
        !string.IsNullOrWhiteSpace(flangeLower) ||
        !string.IsNullOrWhiteSpace(flangeUpper) ||
        !string.IsNullOrWhiteSpace(webLower) ||
        !string.IsNullOrWhiteSpace(webUpper);

    private void RetireLegacyJointOffsetStorage()
    {
        SetTextFieldValue("fk_fl_lo_offs", string.Empty, true);
        SetTextFieldValue("fk_fl_hi_offs", string.Empty, true);
        SetTextFieldValue("fk_web_lo_offs", string.Empty, true);
        SetTextFieldValue("fk_web_hi_offs", string.Empty, true);
        TraceForm("JOINT_OFFSETS legacy storage retired");
    }

    private void BindJointOffsetEditor(TextBox control)
    {
        control.TextChanged += (_, _) =>
        {
            if (_loadingStorage) return;
            if (HasActiveBreaks()) SelectYesNo(_createSplitsCombo, true);
        };
    }

    private TextFieldBinding FindTextField(string name)
    {
        foreach (var field in _textFields)
        {
            if (string.Equals(field.Name, name, StringComparison.Ordinal))
                return field;
        }
        return null;
    }

    private void SetTextFieldValue(
        string name,
        string value,
        bool enableFilter)
    {
        var field = FindTextField(name);
        if (field == null)
            throw new InvalidOperationException("Не найдено поле формы: " + name);
        field.Control.Text = value ?? string.Empty;
        if (enableFilter &&
            _filtersByAttribute.TryGetValue(name, out var filter))
            filter.Checked = true;
    }

    private static string FormatJointOffset(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static bool IsBreakNumericAttribute(string name) =>
        name.StartsWith("fk_break_", StringComparison.Ordinal) &&
        (name.EndsWith("_z", StringComparison.Ordinal) ||
         IsDirectJointOffsetAttribute(name));

    internal static bool IsDirectJointOffsetAttribute(string name) =>
        name != null &&
        name.StartsWith("fk_break_", StringComparison.Ordinal) &&
        (name.EndsWith("_fl_lo", StringComparison.Ordinal) ||
         name.EndsWith("_fl_hi", StringComparison.Ordinal) ||
         name.EndsWith("_web_lo", StringComparison.Ordinal) ||
         name.EndsWith("_web_hi", StringComparison.Ordinal));

    private bool HasTransferredDirectJointOffset()
    {
        foreach (var field in _textFields)
        {
            if (IsDirectJointOffsetAttribute(field.Name) &&
                IsFilterEnabled(field.Name))
                return true;
        }
        return false;
    }

    private void NormalizeUiValues()
    {
        var profileKey = SelectedComboValue("fk_profile_key", "СФ1");
        foreach (var field in _textFields)
        {
            if (!IsFilterEnabled(field.Name)) continue;
            var defaultValue = string.Equals(field.Name, "fk_mark", StringComparison.Ordinal)
                ? profileKey
                : field.DefaultValue;
            field.Control.Text = NormalizeFieldText(field.TypeName, field.Control.Text, defaultValue);
        }
        _lastProfileKey = profileKey;
    }

    private void SynchronizeMarkWithProfile()
    {
        var profileKey = SelectedComboValue("fk_profile_key", "СФ1");
        TextFieldBinding mark = null;
        foreach (var field in _textFields)
        {
            if (!string.Equals(field.Name, "fk_mark", StringComparison.Ordinal)) continue;
            mark = field;
            break;
        }
        if (mark != null &&
            (IsUnsetText(mark.Control.Text) || string.Equals(mark.Control.Text.Trim(), _lastProfileKey, StringComparison.OrdinalIgnoreCase)))
        {
            mark.Control.Text = profileKey;
        }
        _lastProfileKey = profileKey;
    }

    private string SelectedComboValue(string name, string fallback)
    {
        ComboFieldBinding binding = null;
        foreach (var field in _comboFields)
        {
            if (!string.Equals(field.Name, name, StringComparison.Ordinal)) continue;
            binding = field;
            break;
        }
        var option = binding?.Combo.SelectedItem as ComboOption;
        var value = option == null ? string.Empty : ToInvariantText(option.Value);
        return IsUnsetText(value) ? fallback : value;
    }

    private void RefreshAutomaticStiffenerDistancesInUi(bool validateSpacing = false)
    {
        if (!string.Equals(
                SelectedComboValue("fk_stiff_mode", "ELEVATIONS"),
                "AUTO",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        var spacingField = FindTextField("fk_stiff_spacing");
        if (spacingField == null)
        {
            throw new InvalidOperationException(
                "Не найдено поле шага авторасстановки рёбер.");
        }
        var spacing = ResolveAutomaticStiffenerSpacing(
            spacingField.Control.Text,
            spacingField.DefaultValue,
            validateSpacing);
        if (!validateSpacing &&
            (!TryParseDouble(spacingField.Control.Text, out var displayedSpacing) ||
             displayedSpacing <= 0))
        {
            spacingField.Control.Text = spacing.ToString(
                "0.###",
                CultureInfo.InvariantCulture);
            TraceForm("STIFF_AUTO restored default spacing=" + spacingField.Control.Text);
        }

        if (!TryResolveSelectedColumnInsertionElevation(out var insertionElevation))
        {
            TraceForm("STIFF_AUTO skipped: select exactly one FachwerkColumnPlugin instance");
            return;
        }

        var catalogPath = FindTextField("fk_catalog_path")?.Control.Text ?? string.Empty;
        var profileKey = SelectedComboValue("fk_profile_key", "СФ1");
        var profile = FachwerkColumnProfileCatalog.Load(catalogPath).Require(profileKey);
        var frame = new FachwerkColumnFrame(
            new TeklaPoint(0, 0, 0),
            new FachwerkVector(1, 0, 0),
            insertionElevation);
        var distances = FachwerkColumnGeometry.CalculateAutomaticStiffenerDistances(
            profile,
            frame,
            ReadBreaksFromUi(),
            spacing);
        var serialized = FachwerkColumnGeometry.SerializeStiffenerDistances(distances);
        SetTextFieldValue("fk_stiff_distances", serialized, true);
        TraceForm(
            "STIFF_AUTO profile=" + profileKey +
            " insertionZ=" + insertionElevation.ToString("0.###", CultureInfo.InvariantCulture) +
            " spacing=" + spacing.ToString("0.###", CultureInfo.InvariantCulture) +
            " distances=" + serialized);
    }

    internal static double ResolveAutomaticStiffenerSpacing(
        string rawValue,
        string defaultValue,
        bool validate)
    {
        if (TryParseDouble(rawValue, out var spacing) &&
            !double.IsNaN(spacing) &&
            !double.IsInfinity(spacing) &&
            spacing > 0)
        {
            return spacing;
        }
        if (validate)
        {
            throw new InvalidOperationException(
                "Шаг авторасстановки рёбер должен быть положительным числом.");
        }
        if (TryParseDouble(defaultValue, out var fallback) &&
            !double.IsNaN(fallback) &&
            !double.IsInfinity(fallback) &&
            fallback > 0)
        {
            return fallback;
        }
        return 1500.0;
    }

    private List<FachwerkColumnBreak> ReadBreaksFromUi()
    {
        var result = new List<FachwerkColumnBreak>();
        if (!IsYes(_createSplitsCombo)) return result;

        for (var index = 1; index <= 8; index++)
        {
            var suffix = index.ToString(CultureInfo.InvariantCulture);
            var elevationField = FindTextField("fk_break_" + suffix + "_z");
            if (elevationField == null ||
                !TryParseDouble(elevationField.Control.Text, out var elevation) ||
                Math.Abs(elevation) <= 1e-6)
            {
                continue;
            }
            var mode = SelectedComboValue(
                "fk_break_" + suffix + "_mode",
                FachwerkColumnBreak.AllFourMode);
            result.Add(new FachwerkColumnBreak(elevation, mode, index));
        }
        return result;
    }

    private static bool TryResolveSelectedColumnInsertionElevation(out double elevation)
    {
        elevation = 0;
        var selectedComponent = FindSingleSelectedColumnComponent();
        if (selectedComponent == null) return false;

        var input = selectedComponent.GetComponentInput();
        if (input == null) return false;
        foreach (var itemObject in input)
        {
            if (!(itemObject is InputItem item)) continue;
            var points = new List<TeklaPoint>();
            CollectInputPoints(item.GetData(), points);
            if (points.Count == 0) continue;

            var model = new Model();
            if (!model.GetConnectionStatus()) return false;
            var currentPlane = model.GetWorkPlaneHandler().GetCurrentTransformationPlane();
            var globalPoint = currentPlane.TransformationMatrixToGlobal.Transform(points[0]);
            elevation = globalPoint.Z;
            return true;
        }
        return false;
    }

    private static Component FindSingleSelectedColumnComponent()
    {
        Component selected = null;
        var objects = new global::Tekla.Structures.Model.UI.ModelObjectSelector().GetSelectedObjects();
        while (objects.MoveNext())
        {
            if (!(objects.Current is Component candidate) ||
                !string.Equals(
                    candidate.Name,
                    "FachwerkColumnPlugin",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (selected != null) return null;
            selected = candidate;
        }
        return selected;
    }

    private static void CollectInputPoints(object value, List<TeklaPoint> points)
    {
        if (value == null) return;
        if (value is TeklaPoint point)
        {
            points.Add(point);
            return;
        }
        if (value is string || !(value is IEnumerable sequence)) return;
        foreach (var item in sequence) CollectInputPoints(item, points);
    }

    internal static string NormalizeFieldText(string typeName, string rawValue, string defaultValue)
    {
        var fallback = defaultValue ?? string.Empty;
        if (string.Equals(typeName, "Double", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseDouble(rawValue, out var value) ||
                double.IsNaN(value) ||
                double.IsInfinity(value) ||
                value <= -1_000_000)
            {
                return fallback;
            }
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
        return IsUnsetText(rawValue) ? fallback : rawValue;
    }

    private static bool IsUnsetText(string value) =>
        string.IsNullOrWhiteSpace(value) ||
        string.Equals(value.Trim(), "-2147483648", StringComparison.Ordinal);

    private bool HasActiveBreaks()
    {
        foreach (var field in _textFields)
        {
            if (!field.Name.StartsWith("fk_break_", StringComparison.Ordinal) ||
                !field.Name.EndsWith("_z", StringComparison.Ordinal))
            {
                continue;
            }
            if (TryParseDouble(field.Control.Text, out var value) && Math.Abs(value) > 1e-6)
                return true;
        }
        return false;
    }

    private static bool IsYes(ComboBox combo) =>
        combo != null && string.Equals(combo.SelectedItem as string, "Да", StringComparison.Ordinal);

    private static void SelectYesNo(ComboBox combo, bool value)
    {
        if (combo != null) combo.SelectedItem = value ? "Да" : "Нет";
    }

    private void PushComboToStorage(ComboFieldBinding field)
    {
        var option = field.Combo.SelectedItem as ComboOption;
        if (option == null && field.Options.Length > 0) option = field.Options[0];
        if (option == null) return;
        field.Storage.Text = ToInvariantText(option.Value);
        SetTypedAttributeValue(field.Storage, field.TypeName, option.Value, field.Name);
    }

    private static void SelectComboValue(ComboFieldBinding field, object value)
    {
        var raw = ToInvariantText(value);
        ComboOption option = null;
        for (var index = 0; index < field.Options.Length; index++)
        {
            if (!string.Equals(ToInvariantText(field.Options[index].Value), raw, StringComparison.OrdinalIgnoreCase)) continue;
            option = field.Options[index];
            break;
        }
        if (option == null)
        {
            for (var index = 0; index < field.Options.Length; index++)
            {
                if (!string.Equals(field.Options[index].Label, raw, StringComparison.OrdinalIgnoreCase)) continue;
                option = field.Options[index];
                break;
            }
        }
        if (option == null)
        {
            var defaultValue = ToInvariantText(field.DefaultValue);
            for (var index = 0; index < field.Options.Length; index++)
            {
                if (!string.Equals(ToInvariantText(field.Options[index].Value), defaultValue, StringComparison.OrdinalIgnoreCase)) continue;
                option = field.Options[index];
                break;
            }
        }
        if (option == null && field.Options.Length > 0) option = field.Options[0];
        if (option != null) field.Combo.SelectedItem = option;
    }

    private void SetTypedAttributeValue(Control control, string typeName, object rawValue, string attributeName)
    {
        try
        {
            if (string.Equals(typeName, "Double", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseDouble(Convert.ToString(rawValue, CultureInfo.CurrentCulture), out var value))
                    throw new InvalidOperationException("Поле должно содержать число.");
                SetAttributeValue(control, value);
                return;
            }
            if (string.Equals(typeName, "Integer", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(ToInvariantText(rawValue), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                    throw new InvalidOperationException("Поле должно содержать целое число.");
                SetAttributeValue(control, value);
                return;
            }
            SetAttributeValue(control, Convert.ToString(rawValue, CultureInfo.InvariantCulture) ?? string.Empty);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "Не удалось передать параметр '" + attributeName + "' в Tekla. " + exception.Message,
                exception);
        }
    }

    private static bool TryParseDouble(string text, out double value)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return true;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)) return true;
        return double.TryParse((text ?? string.Empty).Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string ToInvariantText(object value)
    {
        if (value == null) return string.Empty;
        if (value is IFormattable formattable) return formattable.ToString(null, CultureInfo.InvariantCulture);
        return value.ToString() ?? string.Empty;
    }

    private static void ExecuteUiAction(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            TraceForm("ERROR " + exception);
            MessageBox.Show(exception.Message, "Стойка фахверка", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void AssignAttributeControl(string name, Control control)
    {
        AssignDirectControl("_attribute" + AttributeFieldSuffix(name), control, name);
    }

    private void AssignFilterControl(string name, Control control)
    {
        AssignDirectControl("_filter" + AttributeFieldSuffix(name), control, name);
    }

    private void AssignDirectControl(
        string fieldName,
        Control control,
        string attributeName)
    {
        var field = GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null || !typeof(Control).IsAssignableFrom(field.FieldType))
        {
            throw new InvalidOperationException(
                "Не зарегистрировано прямое поле формы для атрибута: " +
                attributeName);
        }
        field.SetValue(this, control);
    }

    internal static string AttributeFieldSuffix(string attributeName)
    {
        var parts = (attributeName ?? string.Empty).Split('_');
        var result = string.Empty;
        foreach (var part in parts)
        {
            if (part.Length == 0) continue;
            result += char.ToUpperInvariant(part[0]) + part.Substring(1);
        }
        return result;
    }

    private static void TraceForm(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "fachwerk_column_form_trace.txt"),
                DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + " " + message + Environment.NewLine);
        }
        catch
        {
        }
    }

    private sealed class TextFieldBinding
    {
        public TextFieldBinding(string name, string typeName, TextBox control, string defaultValue)
        {
            Name = name;
            TypeName = typeName;
            Control = control;
            DefaultValue = defaultValue ?? string.Empty;
        }

        public string Name { get; }
        public string TypeName { get; }
        public TextBox Control { get; }
        public string DefaultValue { get; }
    }

    private sealed class JointOffsetStorage
    {
        public JointOffsetStorage(
            TextBox flangeLower,
            TextBox flangeUpper,
            TextBox webLower,
            TextBox webUpper)
        {
            FlangeLower = flangeLower;
            FlangeUpper = flangeUpper;
            WebLower = webLower;
            WebUpper = webUpper;
        }

        public TextBox FlangeLower { get; }
        public TextBox FlangeUpper { get; }
        public TextBox WebLower { get; }
        public TextBox WebUpper { get; }
    }

    private sealed class ComboFieldBinding
    {
        public ComboFieldBinding(
            string name,
            string typeName,
            ComboBox combo,
            TextBox storage,
            ComboOption[] options,
            object defaultValue)
        {
            Name = name;
            TypeName = typeName;
            Combo = combo;
            Storage = storage;
            Options = options;
            DefaultValue = defaultValue;
        }

        public string Name { get; }
        public string TypeName { get; }
        public ComboBox Combo { get; }
        public TextBox Storage { get; }
        public ComboOption[] Options { get; }
        public object DefaultValue { get; }
    }

    private sealed class ComboOption
    {
        public ComboOption(string label, object value)
        {
            Label = label;
            Value = value;
        }

        public string Label { get; }
        public object Value { get; }
        public override string ToString() => Label;
    }
}

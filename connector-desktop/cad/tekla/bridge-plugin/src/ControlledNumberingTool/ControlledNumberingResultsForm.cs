using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Tekla.Structures;
using Tekla.Structures.Model;

namespace Structura.Tekla.ControlledNumbering
{
    internal sealed class ControlledNumberingResultsForm : Form
    {
        private readonly Model _model;
        private readonly IReadOnlyList<NumberingChange> _changes;
        private readonly DataGridView _grid;

        public ControlledNumberingResultsForm(Model model, NumberingSession session)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _changes = session?.ChangedEntries ?? Array.Empty<NumberingChange>();

            Text = "Контроль нумерации — результат";
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(900, 540);
            Size = new Size(1120, 700);
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            ShowIcon = false;
            MaximizeBox = true;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(12)
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            var title = new Label
            {
                AutoSize = true,
                Font = new Font(Font, FontStyle.Bold),
                Margin = new Padding(0, 0, 0, 8),
                Text = BuildSummary(_changes)
            };
            root.Controls.Add(title, 0, 0);

            var workflow = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(1040, 0),
                Margin = new Padding(0, 0, 0, 10),
                ForeColor = session.SynchronizeWithMasterModel == false
                    ? System.Drawing.Color.DarkGreen
                    : System.Drawing.Color.DarkRed,
                Text = BuildWorkflowMessage(session)
            };
            root.Controls.Add(workflow, 0, 1);

            _grid = CreateGrid();
            _grid.DataSource = _changes.ToList();
            _grid.CellDoubleClick += (_, __) => SelectRowsInModel();
            root.Controls.Add(_grid, 0, 2);

            var buttons = new FlowLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Margin = new Padding(0, 10, 0, 0)
            };

            var close = new Button
            {
                AutoSize = true,
                Text = "Закрыть",
                DialogResult = DialogResult.OK,
                Padding = new Padding(18, 4, 18, 4)
            };
            AcceptButton = close;
            buttons.Controls.Add(close);

            var copy = new Button
            {
                AutoSize = true,
                Text = "Копировать список",
                Padding = new Padding(12, 4, 12, 4)
            };
            copy.Click += (_, __) => CopyChangesToClipboard();
            buttons.Controls.Add(copy);

            var select = new Button
            {
                AutoSize = true,
                Text = "Выделить выбранные в модели",
                Padding = new Padding(12, 4, 12, 4)
            };
            select.Click += (_, __) => SelectRowsInModel();
            buttons.Controls.Add(select);

            root.Controls.Add(buttons, 0, 3);
        }

        private static string BuildSummary(IReadOnlyList<NumberingChange> changes)
        {
            var parts = changes.Count(change => change.ObjectType == "Part");
            var assemblies = changes.Count(change => change.ObjectType == "Assembly");
            var newlyNumbered = changes.Count(change => change.IsNew);
            return $"Изменено марок: {changes.Count}. Детали: {parts}; сборки: {assemblies}; новых: {newlyNumbered}.";
        }

        private static string BuildWorkflowMessage(NumberingSession session)
        {
            if (session.SynchronizeWithMasterModel == true)
            {
                return "ВНИМАНИЕ: журнал сообщает, что синхронизация с основной моделью была включена. " +
                       "Tekla могла сохранить модель и очистить Undo History. Не продолжайте работу, пока настройка не будет проверена.";
            }

            if (!session.SynchronizeWithMasterModel.HasValue)
            {
                return "ВНИМАНИЕ: журнал не подтвердил отключение синхронизации. Проверьте настройку перед следующим запуском.";
            }

            return "Модель не сохранялась инструментом. Если результат подходит — сохраните её вручную. " +
                   "Если не подходит — закройте это окно, нажмите Ctrl+Z один раз для отмены нумерации, " +
                   "затем продолжайте Ctrl+Z, чтобы отменить исходные изменения модели.";
        }

        private static DataGridView CreateGrid()
        {
            var grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AutoGenerateColumns = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                MultiSelect = true,
                ReadOnly = true,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText,
                BackgroundColor = SystemColors.Window,
                BorderStyle = BorderStyle.FixedSingle
            };

            AddTextColumn(grid, "Тип", nameof(NumberingChange.ObjectTypeDisplay), 95);
            AddTextColumn(grid, "Результат", nameof(NumberingChange.ChangeTypeDisplay), 110);
            AddTextColumn(grid, "Было", nameof(NumberingChange.OldMark), 165);
            AddTextColumn(grid, "Стало", nameof(NumberingChange.NewMark), 165);
            AddTextColumn(grid, "Серия", nameof(NumberingChange.Series), 145);
            AddTextColumn(grid, "GUID объекта", nameof(NumberingChange.ObjectGuid), 260);
            return grid;
        }

        private static void AddTextColumn(DataGridView grid, string header, string property, int width)
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = header,
                DataPropertyName = property,
                Width = width,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
        }

        private void SelectRowsInModel()
        {
            var selectedChanges = _grid.SelectedRows
                .Cast<DataGridViewRow>()
                .Select(row => row.DataBoundItem as NumberingChange)
                .Where(change => change != null)
                .Distinct()
                .ToArray();

            if (selectedChanges.Length == 0)
            {
                MessageBox.Show(this, "Сначала выберите строки в таблице.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var objects = new ArrayList();
            foreach (var change in selectedChanges)
            {
                var modelObject = _model.SelectModelObject(new Identifier(change.ObjectGuid));
                if (modelObject != null)
                {
                    objects.Add(modelObject);
                }
            }

            if (objects.Count == 0)
            {
                MessageBox.Show(this, "Выбранные объекты не найдены в текущем состоянии модели.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            new global::Tekla.Structures.Model.UI.ModelObjectSelector().Select(objects);
        }

        private void CopyChangesToClipboard()
        {
            var builder = new StringBuilder();
            builder.AppendLine("Тип\tРезультат\tБыло\tСтало\tСерия\tGUID");
            foreach (var change in _changes)
            {
                builder.Append(change.ObjectTypeDisplay).Append('\t')
                    .Append(change.ChangeTypeDisplay).Append('\t')
                    .Append(change.OldMark).Append('\t')
                    .Append(change.NewMark).Append('\t')
                    .Append(change.Series).Append('\t')
                    .Append(change.ObjectGuid)
                    .AppendLine();
            }

            try
            {
                Clipboard.SetText(builder.ToString());
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось скопировать список: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}

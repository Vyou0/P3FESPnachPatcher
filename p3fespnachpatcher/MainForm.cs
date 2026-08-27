using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace P3FesPnachPatcher;

/// <summary>
/// Small, single-window front end for Program.RunElf / Program.RunIso.
/// Deliberately kept compact: pick mode, pick input/output, pick pnach
/// files, hit Patch. The log box just mirrors whatever Program.cs already
/// prints via Console.WriteLine -- no separate reporting logic to maintain.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly RadioButton _modeElf = new() { Text = "Patch ELF file", AutoSize = true, Checked = true };
    private readonly RadioButton _modeIso = new() { Text = "Patch ISO image", AutoSize = true };

    private readonly TextBox _txtInput = new() { ReadOnly = true, Height = 25 };
    private readonly TextBox _txtOutput = new() { ReadOnly = true, Height = 25 };
    private readonly TextBox _txtElfName = new() { Text = "SLUS_216.21", Height = 25 };
    private readonly Label _lblElfName = new() { Text = "ELF Name in ISO:", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Label _lblOutput = new() { Text = "Output file:", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Label _lblInput = new() { Text = "Input file:", AutoSize = true, Anchor = AnchorStyles.Left };

    private readonly ListBox _lstPnach = new() { SelectionMode = SelectionMode.MultiExtended, AllowDrop = true, IntegralHeight = false };
    private readonly List<string> _pnachPaths = new();

    private readonly Button _btnPatch = new() { Text = "Patch Executable", Height = 36, Width = 140, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
    private readonly TextBox _txtLog = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Font = new Font("Consolas", 9.0f), WordWrap = false,
        BorderStyle = BorderStyle.FixedSingle
    };
    private readonly Label _lblStatus = new() { Text = "Ready.", AutoSize = true, Anchor = AnchorStyles.Left, Font = new Font("Segoe UI", 9f, FontStyle.Italic) };

    private TableLayoutPanel _fileSettingsLayout = null!;

    public MainForm()
    {
        Text = "P3FES Pnach Patcher";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(540, 660);
        MinimumSize = new Size(500, 600);
        Font = new Font("Segoe UI", 9.5f);
        Padding = new Padding(12);

        LoadApplicationIcon();
        BuildLayout();

        _modeElf.CheckedChanged += (_, _) => UpdateModeVisibility();
        _modeIso.CheckedChanged += (_, _) => UpdateModeVisibility();
        UpdateModeVisibility();

        _lstPnach.DragEnter += LstPnach_DragEnter;
        _lstPnach.DragDrop += LstPnach_DragDrop;
        _btnPatch.Click += async (_, _) => await RunPatchAsync();
    }

    private void LoadApplicationIcon()
    {
        // 1. Try loading icon.ico directly from application directory or current directory
        try
        {
            string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico");
            if (File.Exists(iconPath))
            {
                Icon = new Icon(iconPath);
                return;
            }
            if (File.Exists("icon.ico"))
            {
                Icon = new Icon("icon.ico");
                return;
            }
        }
        catch
        {
            // Ignore failure loading loose icon file
        }

        // 2. Fallback to extracting embedded icon from executable binary
        try
        {
            if (!string.IsNullOrEmpty(Application.ExecutablePath) && File.Exists(Application.ExecutablePath))
            {
                var assocIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (assocIcon != null)
                {
                    Icon = assocIcon;
                }
            }
        }
        catch
        {
            // Fallback to default form icon if icon loading fails
        }
    }

    private void BuildLayout()
    {
        var mainLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(0),
            Margin = new Padding(0)
        };

        mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));             // Mode Selection
        mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));             // File Settings
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 38f));         // Pnach files (Drag & Drop)
        mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));             // Action row (Status + Patch button)
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 62f));         // Log window

        // --- 1. Mode Group ---
        var grpMode = new GroupBox
        {
            Text = "Patch Mode",
            Dock = DockStyle.Top,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
            Padding = new Padding(10, 8, 10, 10)
        };
        var modeFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0)
        };
        _modeElf.Margin = new Padding(4, 0, 24, 0);
        _modeIso.Margin = new Padding(0);
        modeFlow.Controls.Add(_modeElf);
        modeFlow.Controls.Add(_modeIso);
        grpMode.Controls.Add(modeFlow);
        mainLayout.Controls.Add(grpMode, 0, 0);

        // --- 2. Files & Settings Group ---
        var grpFiles = new GroupBox
        {
            Text = "File Locations & Settings",
            Dock = DockStyle.Top,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
            Padding = new Padding(10, 10, 10, 10)
        };

        _fileSettingsLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 3,
            RowCount = 3,
            Margin = new Padding(0)
        };
        _fileSettingsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115f));
        _fileSettingsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _fileSettingsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 45f));

        // Input Row
        var btnBrowseInput = CreateBrowseButton(BrowseInput);
        _fileSettingsLayout.Controls.Add(_lblInput, 0, 0);
        _fileSettingsLayout.Controls.Add(_txtInput, 1, 0);
        _fileSettingsLayout.Controls.Add(btnBrowseInput, 2, 0);

        // Output Row
        var btnBrowseOutput = CreateBrowseButton(BrowseOutput);
        _fileSettingsLayout.Controls.Add(_lblOutput, 0, 1);
        _fileSettingsLayout.Controls.Add(_txtOutput, 1, 1);
        _fileSettingsLayout.Controls.Add(btnBrowseOutput, 2, 1);

        // ELF Name Row (for ISO mode)
        _fileSettingsLayout.Controls.Add(_lblElfName, 0, 2);
        _fileSettingsLayout.Controls.Add(_txtElfName, 1, 2);

        // Set control margins in TableLayoutPanel for consistent alignment
        _txtInput.Dock = DockStyle.Fill;
        _txtOutput.Dock = DockStyle.Fill;
        _txtElfName.Dock = DockStyle.Fill;

        _lblInput.Margin = new Padding(0, 4, 4, 6);
        _lblOutput.Margin = new Padding(0, 4, 4, 6);
        _lblElfName.Margin = new Padding(0, 4, 4, 6);

        _txtInput.Margin = new Padding(0, 0, 4, 6);
        _txtOutput.Margin = new Padding(0, 0, 4, 6);
        _txtElfName.Margin = new Padding(0, 0, 4, 6);

        btnBrowseInput.Margin = new Padding(0, 0, 0, 6);
        btnBrowseOutput.Margin = new Padding(0, 0, 0, 6);

        grpFiles.Controls.Add(_fileSettingsLayout);
        mainLayout.Controls.Add(grpFiles, 0, 1);

        // --- 3. Pnach List Group ---
        var grpPnach = new GroupBox
        {
            Text = "Pnach Files / Folders (Drag && Drop)",
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 8),
            Padding = new Padding(10, 10, 10, 10)
        };

        var pnachLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0)
        };
        pnachLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        pnachLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115f));
        pnachLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        _lstPnach.Dock = DockStyle.Fill;
        _lstPnach.Margin = new Padding(0, 0, 8, 0);
        pnachLayout.Controls.Add(_lstPnach, 0, 0);

        var pnachBtnStack = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 3,
            Margin = new Padding(0)
        };
        pnachBtnStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        pnachBtnStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        pnachBtnStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var btnAddFiles = CreateActionButton("Add Files...", AddPnachFiles);
        var btnAddFolder = CreateActionButton("Add Folder...", AddPnachFolder);
        var btnRemove = CreateActionButton("Remove", RemoveSelectedPnach);

        pnachBtnStack.Controls.Add(btnAddFiles, 0, 0);
        pnachBtnStack.Controls.Add(btnAddFolder, 0, 1);
        pnachBtnStack.Controls.Add(btnRemove, 0, 2);

        pnachLayout.Controls.Add(pnachBtnStack, 1, 0);
        grpPnach.Controls.Add(pnachLayout);
        mainLayout.Controls.Add(grpPnach, 0, 2);

        // --- 4. Action Row (Patch & Status) ---
        var actionPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 8)
        };
        actionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        actionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _lblStatus.Anchor = AnchorStyles.Left;
        _lblStatus.Margin = new Padding(4, 0, 0, 0);

        _btnPatch.Margin = new Padding(0);

        actionPanel.Controls.Add(_lblStatus, 0, 0);
        actionPanel.Controls.Add(_btnPatch, 1, 0);
        mainLayout.Controls.Add(actionPanel, 0, 3);

        // --- 5. Log Group ---
        var grpLog = new GroupBox
        {
            Text = "Execution Log",
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            Padding = new Padding(10, 10, 10, 10)
        };
        _txtLog.Dock = DockStyle.Fill;
        _txtLog.Margin = new Padding(0);
        grpLog.Controls.Add(_txtLog);
        mainLayout.Controls.Add(grpLog, 0, 4);

        Controls.Add(mainLayout);
    }

    private static Button CreateBrowseButton(EventHandler onClick)
    {
        var btn = new Button
        {
            Text = "...",
            Dock = DockStyle.Fill,
            Height = 25,
            Margin = new Padding(0)
        };
        btn.Click += onClick;
        return btn;
    }

    private static Button CreateActionButton(string text, Action onClick)
    {
        var btn = new Button
        {
            Text = text,
            Dock = DockStyle.Top,
            Height = 30,
            Margin = new Padding(0, 0, 0, 6)
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private void UpdateModeVisibility()
    {
        bool iso = _modeIso.Checked;
        _lblElfName.Visible = iso;
        _txtElfName.Visible = iso;
        _lblInput.Text = iso ? "Input ISO:" : "Input ELF:";
        _lblOutput.Text = iso ? "Output folder:" : "Output ELF:";
        _txtInput.Text = "";
        _txtOutput.Text = "";
    }

    private void BrowseInput(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog
        {
            Filter = _modeIso.Checked
                ? "PS2 disc image (*.iso)|*.iso|All files (*.*)|*.*"
                : "All files (*.*)|*.*|ELF files (*.elf)|*.elf",
            Title = "Select input " + (_modeIso.Checked ? "ISO" : "ELF"),
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _txtInput.Text = dlg.FileName;
            if (string.IsNullOrWhiteSpace(_txtOutput.Text))
            {
                _txtOutput.Text = _modeIso.Checked
                    ? Path.Combine(Path.GetDirectoryName(dlg.FileName) ?? ".", Path.GetFileNameWithoutExtension(dlg.FileName) + "_patched")
                    : Path.Combine(Path.GetDirectoryName(dlg.FileName) ?? ".",
                        Path.GetFileNameWithoutExtension(dlg.FileName) + "_patched" + Path.GetExtension(dlg.FileName));
            }
        }
    }

    private void BrowseOutput(object? sender, EventArgs e)
    {
        if (_modeIso.Checked)
        {
            using var dlg = new FolderBrowserDialog { Description = "Select output folder for the patched disc contents" };
            if (dlg.ShowDialog(this) == DialogResult.OK)
                _txtOutput.Text = dlg.SelectedPath;
        }
        else
        {
            using var dlg = new SaveFileDialog { Filter = "ELF file (*.elf)|*.elf|All files (*.*)|*.*", Title = "Save patched ELF as" };
            if (dlg.ShowDialog(this) == DialogResult.OK)
                _txtOutput.Text = dlg.FileName;
        }
    }

    private void AddPnachFiles()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "Pnach files (*.pnach)|*.pnach|All files (*.*)|*.*",
            Multiselect = true,
            Title = "Select pnach file(s)",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            AddPnachPaths(dlg.FileNames);
    }

    private void AddPnachFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "Select a folder containing .pnach files" };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            AddPnachPaths(new[] { dlg.SelectedPath });
    }

    private void RemoveSelectedPnach()
    {
        foreach (int index in _lstPnach.SelectedIndices.Cast<int>().OrderByDescending(i => i))
        {
            _pnachPaths.RemoveAt(index);
            _lstPnach.Items.RemoveAt(index);
        }
    }

    private void AddPnachPaths(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (_pnachPaths.Contains(p, StringComparer.OrdinalIgnoreCase)) continue;
            _pnachPaths.Add(p);
            bool isFolder = Directory.Exists(p);
            _lstPnach.Items.Add(isFolder ? Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar)) + "  (folder)" : Path.GetFileName(p));
        }
    }

    private void LstPnach_DragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void LstPnach_DragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths)
            AddPnachPaths(paths);
    }

    private async Task RunPatchAsync()
    {
        string input = _txtInput.Text.Trim();
        string output = _txtOutput.Text.Trim();
        bool iso = _modeIso.Checked;

        if (string.IsNullOrWhiteSpace(input) || !File.Exists(input))
        {
            MessageBox.Show(this, "Please choose a valid input file.", "Missing input", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(output))
        {
            MessageBox.Show(this, "Please choose an output " + (iso ? "folder" : "file") + ".", "Missing output", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (iso && string.IsNullOrWhiteSpace(_txtElfName.Text))
        {
            MessageBox.Show(this, "Please enter the ELF filename as it appears in the ISO.", "Missing ELF name", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (_pnachPaths.Count == 0)
        {
            MessageBox.Show(this, "Add at least one .pnach file or folder.", "Nothing to patch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _txtLog.Clear();
        SetBusy(true);

        var writer = new TextBoxWriter(_txtLog, this);
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        Console.SetOut(writer);
        Console.SetError(writer);

        string[] pnachArgs = _pnachPaths.ToArray();
        string elfName = _txtElfName.Text.Trim();

        try
        {
            await Task.Run(() =>
            {
                if (iso)
                    Program.RunIso(input, output, elfName, pnachArgs);
                else
                    Program.RunElf(input, output, pnachArgs);
            });
            _lblStatus.Text = "Done.";
            MessageBox.Show(this, "Patching finished successfully.", "Done", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _lblStatus.Text = "Failed.";
            writer.WriteLine($"ERROR: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Patching failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _btnPatch.Enabled = !busy;
        _modeElf.Enabled = !busy;
        _modeIso.Enabled = !busy;
        _lstPnach.Enabled = !busy;
        _txtElfName.Enabled = !busy;
        if (busy) _lblStatus.Text = "Patching...";
    }

    /// <summary>Thread-safe TextWriter that appends every line straight into the log TextBox.</summary>
    private sealed class TextBoxWriter : TextWriter
    {
        private readonly TextBox _box;
        private readonly Control _syncTarget;
        private readonly StringBuilder _lineBuf = new();

        public TextBoxWriter(TextBox box, Control syncTarget)
        {
            _box = box;
            _syncTarget = syncTarget;
        }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (value == '\n')
            {
                string line = _lineBuf.ToString();
                _lineBuf.Clear();
                Append(line + Environment.NewLine);
            }
            else if (value != '\r')
            {
                _lineBuf.Append(value);
            }
        }

        public override void Write(string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            foreach (char c in value) Write(c);
        }

        public override void WriteLine(string? value)
        {
            Write(value);
            Write('\n');
        }

        private void Append(string text)
        {
            if (_syncTarget.IsDisposed) return;
            if (_syncTarget.InvokeRequired)
                _syncTarget.BeginInvoke(new Action(() => AppendOnUiThread(text)));
            else
                AppendOnUiThread(text);
        }

        private void AppendOnUiThread(string text)
        {
            if (_box.IsDisposed) return;
            _box.AppendText(text);
        }
    }
}

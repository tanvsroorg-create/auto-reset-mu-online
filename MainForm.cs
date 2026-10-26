using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MuAutoReset;

public sealed class MainForm : Form
{
    private const string GameModule = "main.exe";

    // Level
    private const int LevelBaseOffset = 0x1740BB4;

    // XTeam 18.1.3: map ID address supplied as main.exe+1715A98.
    private const int CurrentMapIdOffset = 0x1715A98;
    // XTeam database[2] from the supplied config string. Treat as the send-function address.
    private static readonly IntPtr PacketSendFunction = new(0x00E71445);
    private readonly string _mapPacketConfigPath = Path.Combine(AppContext.BaseDirectory, "map_packets.json");

    // Pointer di chuyển và tọa độ đích
    private const int MoveBaseOffset = 0x181D014;
    private const int MoveOffset = 0x2198;
    private const int DestinationXOffset = 0x219C;
    private const int DestinationYOffset = 0x219E;

    // Tọa độ hiện tại
    private const int CurrentXOffset = 0x180;
    private const int CurrentYOffset = 0x184;

    // Pointer auto
    private const int AutoBaseOffset = 0x1740868;
    private const int AutoToggleOffset = 0x8;
    private const int AutoXOffset = 0x44;
    private const int AutoYOffset = 0x48;

    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const uint WM_CHAR = 0x0102;
    private const int VK_RETURN = 0x0D;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private readonly DataGridView _processGrid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeColumns = false,
        AllowUserToResizeRows = false,
        AllowUserToOrderColumns = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
        RowHeadersVisible = false,
        MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        EditMode = DataGridViewEditMode.EditOnEnter
    };

    private readonly DataGridView _spots = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = true,
        AllowUserToDeleteRows = true,
        AllowUserToResizeColumns = false,
        AllowUserToResizeRows = false,
        AllowUserToOrderColumns = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
        RowHeadersVisible = false,
        MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.CellSelect,
        EditMode = DataGridViewEditMode.EditOnEnter
    };

    private readonly TextBox _log = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        WordWrap = false,
        Dock = DockStyle.Fill
    };

    private readonly NumericUpDown _poll = new()
    {
        Minimum = 500,
        Maximum = 30000,
        Increment = 500,
        Value = 1500,
        Width = 85
    };

    private readonly TextBox _resetCommand = new()
    {
        Text = "/reset",
        Width = 100
    };

    private readonly NumericUpDown _resetLevel = new()
    {
        Minimum = 1,
        Maximum = 1000,
        Value = 400,
        Width = 75
    };

    private readonly Button _refresh = new()
    {
        Text = "Làm mới",
        Width = 90
    };

    private readonly Button _start = new()
    {
        Text = "Chạy nhân vật đã chọn",
        Width = 175
    };

    private readonly Button _stop = new()
    {
        Text = "Dừng nhân vật đã chọn",
        Width = 175
    };

    private readonly Button _selectAll = new()
    {
        Text = "Chọn tất cả",
        Width = 95
    };

    private readonly Button _unselectAll = new()
    {
        Text = "Bỏ chọn",
        Width = 85
    };

    private readonly Button _save = new()
    {
        Text = "Lưu cấu hình",
        Width = 100
    };

    private readonly Button _load = new()
    {
        Text = "Nạp cấu hình",
        Width = 100
    };

    private readonly Label _summary = new()
    {
        AutoSize = true,
        Padding = new Padding(5, 7, 0, 0),
        Text = "Process: 0 | Đang chạy: 0"
    };

    private readonly System.Windows.Forms.Timer _uiTimer = new();
    private readonly Dictionary<int, CharacterRuntime> _runtimes = new();

    private readonly string _configPath =
        Path.Combine(AppContext.BaseDirectory, "config.json");

    private bool _refreshing;
    private bool _closing;

    private sealed class ProcessItem
    {
        public int Pid { get; init; }
        public string Name { get; init; } = "";
        public string Title { get; init; } = "";
    }

    private sealed class CharacterRuntime : IDisposable
    {
        public int Pid { get; init; }
        public Process Process { get; init; } = null!;
        public MemoryWriter Memory { get; init; } = null!;
        public System.Windows.Forms.Timer Timer { get; init; } = null!;

        public bool Busy { get; set; }
        public bool Running { get; set; }
        public bool AutoEnabled { get; set; }
        public string Status { get; set; } = "Đã dừng";

        public int? Level { get; set; }
        public ushort? X { get; set; }
        public ushort? Y { get; set; }

        public int? LastSpotLevel { get; set; }
        public DateTime LastResetSent { get; set; } = DateTime.MinValue;
        public DateTime LastMoveSent { get; set; } = DateTime.MinValue;

        public IntPtr? PendingMoveAddress { get; set; }
        public int? PendingSpotLevel { get; set; }
        public bool MoveWasStarted { get; set; }

        public void Dispose()
        {
            Timer.Stop();
            Timer.Dispose();

            try
            {
                Memory.Dispose();
            }
            catch { }

            try
            {
                Process.Dispose();
            }
            catch { }
        }
    }

    private sealed class SpotConfig
    {
        public bool Enabled { get; set; }
        public int Level { get; set; }
        public string Map { get; set; } = "";
        public int? X { get; set; }
        public int? Y { get; set; }
    }

    private sealed class AppConfig
    {
        public int PollMs { get; set; } = 1500;
        public string ResetCommand { get; set; } = "/reset";
        public int ResetLevel { get; set; } = 400;
        public List<SpotConfig> Spots { get; set; } = new();
    }

    public MainForm()
    {
        Text = "MU Online Season 18 — Multi Auto Train / Reset";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(950, 650);
        Size = new Size(1120, 780);
        Font = new Font("Segoe UI", 9F);

        BuildProcessGrid();
        BuildSpotGrid();
        BuildLayout();
        LoadDefaults();

        _refresh.Click += (_, _) => RefreshProcesses();
        _start.Click += (_, _) => StartSelected();
        _stop.Click += (_, _) => StopSelected();
        _selectAll.Click += (_, _) => SetSelected(true);
        _unselectAll.Click += (_, _) => SetSelected(false);
        _save.Click += (_, _) => SaveConfig();
        _load.Click += (_, _) => LoadConfig();

        _poll.ValueChanged += (_, _) =>
        {
            foreach (CharacterRuntime runtime in _runtimes.Values)
                runtime.Timer.Interval = (int)_poll.Value;
        };

        _uiTimer.Interval = 1000;
        _uiTimer.Tick += (_, _) => RefreshRuntimeDisplay();
        _uiTimer.Start();

        FormClosing += (_, _) =>
        {
            _closing = true;
            _uiTimer.Stop();
            StopAll();
        };

        RefreshProcesses();
        Log("Sẵn sàng. Chọn các nhân vật muốn chạy.");
    }

    private void BuildProcessGrid()
    {
        _processGrid.Columns.Clear();
        ConfigureGrid(_processGrid);

        AddColumn(_processGrid, new DataGridViewCheckBoxColumn
        {
            Name = "Selected",
            HeaderText = "Chọn",
            Width = 48
        });

        AddColumn(_processGrid, new DataGridViewTextBoxColumn
        {
            Name = "Pid",
            HeaderText = "PID",
            Width = 70,
            ReadOnly = true
        });

        AddColumn(_processGrid, new DataGridViewTextBoxColumn
        {
            Name = "Process",
            HeaderText = "Process / cửa sổ",
            Width = 300,
            ReadOnly = true
        });

        AddColumn(_processGrid, new DataGridViewTextBoxColumn
        {
            Name = "Level",
            HeaderText = "Level",
            Width = 70,
            ReadOnly = true
        });

        AddColumn(_processGrid, new DataGridViewTextBoxColumn
        {
            Name = "X",
            HeaderText = "X",
            Width = 65,
            ReadOnly = true
        });

        AddColumn(_processGrid, new DataGridViewTextBoxColumn
        {
            Name = "Y",
            HeaderText = "Y",
            Width = 65,
            ReadOnly = true
        });

        AddColumn(_processGrid, new DataGridViewTextBoxColumn
        {
            Name = "Status",
            HeaderText = "Trạng thái",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 160,
            ReadOnly = true
        });
    }

    private void BuildSpotGrid()
    {
        _spots.Columns.Clear();
        ConfigureGrid(_spots);

        AddColumn(_spots, new DataGridViewCheckBoxColumn
        {
            Name = "Enabled",
            HeaderText = "Bật",
            Width = 45
        });

        AddColumn(_spots, new DataGridViewTextBoxColumn
        {
            Name = "Level",
            HeaderText = "Level từ",
            Width = 85
        });

        AddColumn(_spots, new DataGridViewTextBoxColumn
        {
            Name = "Map",
            HeaderText = "Tên map",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 150
        });

        AddColumn(_spots, new DataGridViewTextBoxColumn
        {
            Name = "X",
            HeaderText = "X đích",
            Width = 90
        });

        AddColumn(_spots, new DataGridViewTextBoxColumn
        {
            Name = "Y",
            HeaderText = "Y đích",
            Width = 90
        });
        _spots.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_spots.IsCurrentCellDirty)
                _spots.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
    }

    private static void AddColumn(
        DataGridView grid,
        DataGridViewColumn column)
    {
        column.Resizable = DataGridViewTriState.False;
        column.SortMode = DataGridViewColumnSortMode.NotSortable;
        grid.Columns.Add(column);
    }

    private static void ConfigureGrid(DataGridView grid)
    {
        grid.AllowUserToResizeColumns = false;
        grid.AllowUserToResizeRows = false;
        grid.AllowUserToOrderColumns = false;

        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;

        grid.RowHeadersVisible = false;
        grid.ColumnHeadersHeightSizeMode =
            DataGridViewColumnHeadersHeightSizeMode.DisableResizing;

        grid.RowTemplate.Height = 25;
        grid.ScrollBars = ScrollBars.Both;
        grid.BackgroundColor = SystemColors.ControlDark;
        grid.BorderStyle = BorderStyle.FixedSingle;
    }
    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(8)
        };

        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 43));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 27));

        Controls.Add(root);

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            Padding = new Padding(0, 0, 0, 6)
        };

        toolbar.Controls.AddRange(new Control[]
        {
            _refresh, _start, _stop, _selectAll,
            _unselectAll, _save, _load, _summary
        });

        root.Controls.Add(toolbar, 0, 0);

        var processGroup = new GroupBox
        {
            Text = "Các process MU Online",
            Dock = DockStyle.Fill,
            Padding = new Padding(6)
        };

        processGroup.Controls.Add(_processGrid);
        root.Controls.Add(processGroup, 0, 1);

        var settingsGroup = new GroupBox
        {
            Text = "Cấu hình train / reset",
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(6)
        };

        var settings = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true
        };

        AddSetting(settings, "Poll ms", _poll);
        AddSetting(settings, "Lệnh reset", _resetCommand);
        AddSetting(settings, "Reset level", _resetLevel);

        settingsGroup.Controls.Add(settings);
        root.Controls.Add(settingsGroup, 0, 2);

        var spotGroup = new GroupBox
        {
            Text = "Các bãi train — nhập map và X/Y đích",
            Dock = DockStyle.Fill,
            Padding = new Padding(6)
        };

        spotGroup.Controls.Add(_spots);
        root.Controls.Add(spotGroup, 0, 3);

        var logGroup = new GroupBox
        {
            Text = "Trạng thái / log",
            Dock = DockStyle.Fill,
            Padding = new Padding(6)
        };

        logGroup.Controls.Add(_log);
        root.Controls.Add(logGroup, 0, 4);
    }

    private static void AddSetting(
        FlowLayoutPanel panel, string caption, Control control)
    {
        panel.Controls.Add(new Label
        {
            Text = caption,
            AutoSize = true,
            Margin = new Padding(5, 7, 3, 0)
        });

        panel.Controls.Add(control);
    }

    private void LoadDefaults()
    {
        AddSpot(true, 1, "", null, null);
        AddSpot(true, 10, "", null, null);
        AddSpot(true, 40, "", null, null);
        AddSpot(true, 100, "", null, null);
        AddSpot(true, 160, "", null, null);
        AddSpot(true, 300, "", null, null);
    }

    private void AddSpot(
        bool enabled, int level, string map, int? x, int? y)
    {
        _spots.Rows.Add(enabled, level, map, x, y);
    }

    private void RefreshProcesses()
    {
        if (_refreshing || _closing)
            return;

        _refreshing = true;

        try
        {
            var found = new Dictionary<int, ProcessItem>();

            foreach (Process process in Process.GetProcessesByName("main"))
            {
                try
                {
                    if (process.HasExited)
                        continue;

                    found[process.Id] = new ProcessItem
                    {
                        Pid = process.Id,
                        Name = process.ProcessName,
                        Title = process.MainWindowTitle
                    };
                }
                catch
                {
                    // Process có thể đã thoát trong lúc quét.
                }
                finally
                {
                    process.Dispose();
                }
            }

            foreach (int pid in _runtimes.Keys.ToList())
            {
                if (found.ContainsKey(pid))
                    continue;

                CharacterRuntime runtime = _runtimes[pid];
                runtime.Running = false;
                runtime.Status = "Process đã thoát";
                runtime.Timer.Stop();

                if (!runtime.Busy)
                {
                    try { runtime.Dispose(); } catch { }
                    _runtimes.Remove(pid);
                }

                Log($"PID {pid}: process đã thoát.");
            }

            var checkedPids = new HashSet<int>();

            foreach (DataGridViewRow row in _processGrid.Rows)
            {
                if (row.Tag is int pid &&
                    row.Cells["Selected"].Value is bool selected &&
                    selected)
                {
                    checkedPids.Add(pid);
                }
            }

            _processGrid.Rows.Clear();

            foreach (ProcessItem item in found.Values.OrderBy(p => p.Pid))
            {
                _runtimes.TryGetValue(item.Pid, out CharacterRuntime? runtime);

                int rowIndex = _processGrid.Rows.Add(
                    checkedPids.Contains(item.Pid) || runtime?.Running == true,
                    item.Pid,
                    string.IsNullOrWhiteSpace(item.Title)
                        ? item.Name
                        : item.Name + " — " + item.Title,
                    runtime?.Level?.ToString() ?? "--",
                    runtime?.X?.ToString() ?? "--",
                    runtime?.Y?.ToString() ?? "--",
                    runtime?.Status ?? "Chưa chạy");

                _processGrid.Rows[rowIndex].Tag = item.Pid;
                _processGrid.Rows[rowIndex].Height = 24;
            }

            UpdateSummary();
            Log($"Đã quét {found.Count} process main.exe.");
        }
        catch (Exception ex)
        {
            Log("Lỗi quét process: " + ex.Message);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void StartSelected()
    {
        CommitGridEdits();

        List<int> pids = GetSelectedPids();

        if (pids.Count == 0)
        {
            MessageBox.Show(
                this, "Hãy đánh dấu chọn ít nhất một nhân vật.",
                "Thông báo", MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            ValidateSpots();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this, ex.Message, "Cấu hình chưa hợp lệ",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        foreach (int pid in pids)
        {
            if (_runtimes.TryGetValue(pid, out CharacterRuntime? existing)
                && existing.Running)
            {
                continue;
            }

            try
            {
                Process process = Process.GetProcessById(pid);

                if (process.HasExited)
                {
                    process.Dispose();
                    Log($"PID {pid}: process đã thoát.");
                    continue;
                }

                var memory = new MemoryWriter(process);

                var timer = new System.Windows.Forms.Timer
                {
                    Interval = (int)_poll.Value
                };

                var runtime = new CharacterRuntime
                {
                    Pid = pid,
                    Process = process,
                    Memory = memory,
                    Timer = timer,
                    Running = true,
                    Status = "Đang khởi động"
                };

                timer.Tick += async (_, _) =>
                    await TickCharacterAsync(runtime);

                _runtimes[pid] = runtime;
                timer.Start();

                runtime.Status = "Đang train";
                Log($"PID {pid}: đã bắt đầu.");
            }
            catch (Exception ex)
            {
                Log($"PID {pid}: không thể bắt đầu — {ex.Message}");
            }
        }

        RefreshRuntimeDisplay();
    }

    private void StopSelected()
    {
        List<int> pids = GetSelectedPids();

        if (pids.Count == 0)
        {
            MessageBox.Show(
                this, "Hãy đánh dấu chọn nhân vật cần dừng.",
                "Thông báo", MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        foreach (int pid in pids)
            StopAllCharacters();

        RefreshRuntimeDisplay();
    }

    private void StopAll()
    {
        foreach (int pid in _runtimes.Keys.ToList())
            StopAllCharacters();
    }

    private List<int> GetSelectedPids()
    {
        CommitGridEdits();

        var pids = new List<int>();

        foreach (DataGridViewRow row in _processGrid.Rows)
        {
            bool selected =
                row.Cells["Selected"].Value is bool value && value;

            if (selected && row.Tag is int pid)
                pids.Add(pid);
        }

        return pids;
    }

    private void SetSelected(bool value)
    {
        CommitGridEdits();

        foreach (DataGridViewRow row in _processGrid.Rows)
            row.Cells["Selected"].Value = value;
    }

    private void CommitGridEdits()
    {
        _processGrid.EndEdit();
        _spots.EndEdit();
    }

    private void StopAllCharacters()
    {
        foreach (CharacterRuntime runtime in _runtimes.Values.ToList())
        {
            try
            {
                if (!runtime.Process.HasExited)
                    SetAuto(runtime, false);
            }
            catch (Exception ex)
            {
                Log($"PID {runtime.Pid}: lỗi tắt Auto: {ex.Message}");
            }

            runtime.Timer.Stop();
            runtime.Running = false;
            runtime.PendingMoveAddress = null;
            runtime.PendingSpotLevel = null;
            runtime.MoveWasStarted = false;
            runtime.Status = "Đã dừng";

            Log($"PID {runtime.Pid}: Đã dừng.");
        }

        RefreshRuntimeDisplay();
    }

    private async Task TickCharacterAsync(CharacterRuntime runtime)
    {
        if (!runtime.Running || runtime.Busy || _closing)
            return;

        runtime.Busy = true;

        try
        {
            if (runtime.Process.HasExited)
            {
                runtime.Running = false;
                runtime.Status = "Process đã thoát";
                runtime.Timer.Stop();
                return;
            }

            // 1. Đọc level
            IntPtr levelAddress = runtime.Memory.ResolvePointerChainAddress(
                runtime.Process,
                GameModule,
                LevelBaseOffset,
                new[] { 0 });

            if (levelAddress == IntPtr.Zero)
                throw new InvalidOperationException(
                    "Không đọc được địa chỉ level.");

            ushort level = runtime.Memory.ReadUInt16(levelAddress);
            runtime.Level = level;

            // 2. Đọc tọa độ hiện tại theo đúng chữ ký hàm hiện có
            ReadCurrentCoordinates(runtime);

            // 3. Commit checkbox để lấy trạng thái mới nhất
            if (_spots.IsCurrentCellDirty)
                _spots.CommitEdit(DataGridViewDataErrorContexts.Commit);

            _spots.EndEdit();

            // Chỉ chọn bãi được bật và đủ level
            List<SpotConfig> eligibleSpots = GetSpots()
                .Where(s => s.Enabled && s.Level <= level)
                .OrderByDescending(s => s.Level)
                .ToList();

            // Không có bãi phù hợp: không gửi /move
            if (eligibleSpots.Count == 0)
            {
                if (runtime.AutoEnabled)
                    SetAuto(runtime, false);

                runtime.PendingMoveAddress = null;
                runtime.PendingSpotLevel = null;
                runtime.MoveWasStarted = false;

                runtime.Status = "Không có bãi bật phù hợp level";
                return;
            }

            SpotConfig spot = eligibleSpots[0];

            if (string.IsNullOrWhiteSpace(spot.Map))
                throw new InvalidOperationException(
                    $"Bãi level {spot.Level} chưa có tên map.");

            if (spot.X is null || spot.Y is null)
                throw new InvalidOperationException(
                    $"Bãi level {spot.Level} chưa có X/Y đích.");

            if (spot.X.Value < 0 || spot.X.Value > ushort.MaxValue ||
                spot.Y.Value < 0 || spot.Y.Value > ushort.MaxValue)
            {
                throw new InvalidOperationException(
                    $"Tọa độ bãi level {spot.Level} không hợp lệ.");
            }

            ushort targetX = (ushort)spot.X.Value;
            ushort targetY = (ushort)spot.Y.Value;

            const int coordinateTolerance = 8;

            bool atDestination =
                runtime.X.HasValue &&
                runtime.Y.HasValue &&
                Math.Abs(runtime.X.Value - targetX) <= coordinateTolerance &&
                Math.Abs(runtime.Y.Value - targetY) <= coordinateTolerance;

            // 4. Reset theo ngưỡng được cấu hình trên giao diện
            if (level >= (int)_resetLevel.Value &&
                DateTime.Now - runtime.LastResetSent >
                    TimeSpan.FromSeconds(20))
            {
                if (runtime.AutoEnabled)
                    SetAuto(runtime, false);

                SendChatCommand(runtime, _resetCommand.Text.Trim());

                runtime.LastResetSent = DateTime.Now;
                runtime.LastSpotLevel = null;
                runtime.Status = "Đã gửi lệnh reset";
                await Task.Delay(3000);
                return;
            }
            if (atDestination)
            {
                runtime.PendingMoveAddress = null;
                runtime.PendingSpotLevel = null;
                runtime.MoveWasStarted = false;

                if (!runtime.AutoEnabled)
                {
                    SetAuto(runtime, true, targetX, targetY);
                }

                runtime.LastSpotLevel = spot.Level;
                runtime.Status =
                    $"Auto tại {spot.Map} ({targetX},{targetY})";

                return;
            }

            // 6. Chưa tới đích thì tắt Auto
            if (runtime.AutoEnabled)
                SetAuto(runtime, false);

            // 7. Đang có lệnh di chuyển thì chờ, không gửi /move mới
            if (runtime.PendingMoveAddress.HasValue)
            {
                ushort moveState = runtime.Memory.ReadUInt16(
                    runtime.PendingMoveAddress.Value);

                if (moveState == 2)
                {
                    runtime.MoveWasStarted = true;
                    runtime.Status =
                        $"Đang di chuyển tới {spot.Map} ({targetX},{targetY})";
                    return;
                }

                if (moveState == 0 && runtime.MoveWasStarted)
                {
                    runtime.PendingMoveAddress = null;
                    runtime.PendingSpotLevel = null;
                    runtime.MoveWasStarted = false;

                    runtime.Status = "Đang kiểm tra tọa độ sau di chuyển";
                    return;
                }

                runtime.Status = "Đang chờ cờ di chuyển";
                return;
            }

            // 8. Giới hạn tần suất gửi lệnh
            if (DateTime.Now - runtime.LastMoveSent <
                TimeSpan.FromSeconds(8))
            {
                runtime.Status = $"Đang chờ tới {spot.Map}";
                return;
            }

            // 9. Lấy địa chỉ hiện tại
            IntPtr moduleBase = runtime.Memory.GetModuleBaseAddress(
                runtime.Process, GameModule);

            if (moduleBase == IntPtr.Zero)
                throw new InvalidOperationException(
                    "Không tìm thấy module main.exe.");

            IntPtr pointerAddress = IntPtr.Add(
                moduleBase, MoveBaseOffset);

            uint pointerValue =
                runtime.Memory.ReadUInt32(pointerAddress);

            if (pointerValue == 0)
                throw new InvalidOperationException(
                    "Pointer di chuyển bằng 0.");

            IntPtr pointerBase =
                new(unchecked((int)pointerValue));

            // 10. Chỉ gửi packet khi map hiện tại khác map đích.
            IntPtr mapIdAddress = IntPtr.Add(moduleBase, CurrentMapIdOffset);
            ushort currentMapId = runtime.Memory.ReadUInt16(mapIdAddress);
            MapPacketEntry? targetMap = LoadMapPackets()
                .FirstOrDefault(m => string.Equals(m.Map, spot.Map, StringComparison.OrdinalIgnoreCase));

            if (targetMap == null)
                throw new InvalidOperationException(
                    $"Chưa có mapId/packet cho '{spot.Map}' trong map_packets.json.");

            if (currentMapId == targetMap.MapId)
            {
                runtime.Status = $"Đã ở map {spot.Map} (ID {currentMapId}); bỏ qua packet chuyển map";
                Log($"PID {runtime.Pid}: map hiện tại đã là {spot.Map} (ID {currentMapId}), không gửi packet.");
                // Không gửi chuyển map; tiếp tục xử lý tọa độ trong vòng tick kế tiếp.
                return;
            }

            runtime.Status = $"Đang chuyển map bằng packet: {spot.Map}";
            Log($"PID {runtime.Pid}: map {currentMapId} -> {targetMap.MapId} ({spot.Map}), gửi packet.");
            using (var sender = new PacketSender(runtime.Process))
            {
                // Stub được port từ chuỗi lệnh AutoIt trong BuildPacketStub().
                sender.SendPacket(targetMap.PacketHex, PacketSendFunction, BuildPacketStub);
            }

            await Task.Delay(3000);

            if (!runtime.Running || _closing || runtime.Process.HasExited)
                return;

            // 11. Đọc lại pointer sau khi chuyển map
            moduleBase = runtime.Memory.GetModuleBaseAddress(
                runtime.Process, GameModule);

            if (moduleBase == IntPtr.Zero)
                throw new InvalidOperationException(
                    "Không tìm thấy module sau khi chuyển map.");

            pointerAddress = IntPtr.Add(moduleBase, MoveBaseOffset);
            pointerValue = runtime.Memory.ReadUInt32(pointerAddress);

            if (pointerValue == 0)
                throw new InvalidOperationException(
                    "Pointer di chuyển bằng 0 sau khi chuyển map.");

            pointerBase = new IntPtr(unchecked((int)pointerValue));

            IntPtr moveAddress =
                IntPtr.Add(pointerBase, MoveOffset);

            IntPtr destinationXAddress =
                IntPtr.Add(pointerBase, DestinationXOffset);

            IntPtr destinationYAddress =
                IntPtr.Add(pointerBase, DestinationYOffset);

            // 12. Ghi và xác minh tọa độ đích
            runtime.Memory.WriteUInt16(destinationXAddress, targetX);
            runtime.Memory.WriteUInt16(destinationYAddress, targetY);

            ushort readX = runtime.Memory.ReadUInt16(destinationXAddress);
            ushort readY = runtime.Memory.ReadUInt16(destinationYAddress);

            if (readX != targetX || readY != targetY)
                throw new IOException(
                    $"Tọa độ ghi không khớp: X={readX}, Y={readY}.");

            // 13. Kích hoạt di chuyển
            runtime.Memory.WriteUInt16(moveAddress, 2);

            runtime.PendingMoveAddress = moveAddress;
            runtime.PendingSpotLevel = spot.Level;
            runtime.MoveWasStarted = false;
            runtime.LastMoveSent = DateTime.Now;

            runtime.Status =
                $"Đang di chuyển tới {spot.Map} ({targetX},{targetY})";

        }
        catch (Exception ex)
        {
            runtime.Status = "Lỗi: " + ex.Message;
            Log($"PID {runtime.Pid}: {ex.Message}");
        }
        finally
        {
            runtime.Busy = false;

            if (!runtime.Running &&
                _runtimes.ContainsKey(runtime.Pid))
            {
                try
                {
                    runtime.Dispose();
                }
                catch { }

                _runtimes.Remove(runtime.Pid);
            }

            if (!_closing)
                RefreshRuntimeDisplay();
        }
    }


    private void ReadCurrentCoordinates(CharacterRuntime runtime)
    {
        IntPtr moduleBase = runtime.Memory.GetModuleBaseAddress(
            runtime.Process, GameModule);

        IntPtr pointerAddress = IntPtr.Add(
            moduleBase, MoveBaseOffset);

        uint pointerValue = runtime.Memory.ReadUInt32(pointerAddress);

        if (pointerValue == 0)
        {
            runtime.X = null;
            runtime.Y = null;
            return;
        }

        IntPtr pointerBase =
            new(unchecked((int)pointerValue));

        IntPtr xAddress = IntPtr.Add(pointerBase, CurrentXOffset);
        IntPtr yAddress = IntPtr.Add(pointerBase, CurrentYOffset);

        runtime.X = runtime.Memory.ReadUInt16(xAddress);
        runtime.Y = runtime.Memory.ReadUInt16(yAddress);
    }

    private void SetAuto(
        CharacterRuntime runtime,
        bool enabled,
        ushort? targetX = null,
        ushort? targetY = null)
    {
        IntPtr moduleBase =
            runtime.Memory.GetModuleBaseAddress(runtime.Process, GameModule);

        if (moduleBase == IntPtr.Zero)
            throw new InvalidOperationException("Không tìm thấy module main.exe.");

        IntPtr autoBaseAddress =
            runtime.Memory.ResolvePointerChainAddress(
                runtime.Process,
                GameModule,
                AutoBaseOffset,
                new[] { 0 });

        if (autoBaseAddress == IntPtr.Zero)
            throw new InvalidOperationException("Không đọc được địa chỉ Auto.");

        if (enabled)
        {
            ushort x = targetX ?? runtime.X ?? 0;
            ushort y = targetY ?? runtime.Y ?? 0;

            runtime.Memory.WriteUInt16(
                IntPtr.Add(autoBaseAddress, AutoXOffset), x);

            runtime.Memory.WriteUInt16(
                IntPtr.Add(autoBaseAddress, AutoYOffset), y);
        }

        runtime.Memory.WriteUInt16(
            IntPtr.Add(autoBaseAddress, AutoToggleOffset),
            enabled ? (ushort)1 : (ushort)0);

        runtime.AutoEnabled = enabled;
    }


    private List<MapPacketEntry> LoadMapPackets()
    {
        if (!File.Exists(_mapPacketConfigPath))
            throw new FileNotFoundException("Không tìm thấy map_packets.json cạnh file EXE.", _mapPacketConfigPath);
        return JsonSerializer.Deserialize<List<MapPacketEntry>>(
            File.ReadAllText(_mapPacketConfigPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? new List<MapPacketEntry>();
    }

    private static byte[] BuildPacketStub(IntPtr packetBuffer, IntPtr sendFunction, bool secondVariant)
    {
        // Port trực tiếp các lệnh AutoIt:
        // pushad(); push(0); push(secondVariant ? 0 : 1);
        // mov_ecx(packetBuffer); mov_eax(sendFunction); call_eax(); popad(); ret();
        // Các hằng số 32-bit được ghi little-endian giống int2hex() trong AutoIt.
        if (IntPtr.Size != 4)
            throw new PlatformNotSupportedException(
                "Stub AutoIt này dùng mã máy x86 32-bit. Hãy chạy ứng dụng C# x86 với client MU 32-bit.");

        static void AppendUInt32(List<byte> bytes, uint value)
        {
            bytes.Add((byte)(value & 0xFF));
            bytes.Add((byte)((value >> 8) & 0xFF));
            bytes.Add((byte)((value >> 16) & 0xFF));
            bytes.Add((byte)((value >> 24) & 0xFF));
        }

        var code = new List<byte>(23)
        {
            0x60,                   // pushad
            0x68                    // push imm32 (0)
        };
        AppendUInt32(code, 0);
        code.Add(0x68);             // push imm32 (sendpacket: 1; sendpacket2: 0)
        AppendUInt32(code, secondVariant ? 0u : 1u);
        code.Add(0xB9);             // mov ecx, packetBuffer
        AppendUInt32(code, unchecked((uint)packetBuffer.ToInt32()));
        code.Add(0xB8);             // mov eax, sendFunction
        AppendUInt32(code, unchecked((uint)sendFunction.ToInt32()));
        code.AddRange(new byte[] { 0xFF, 0xD0, 0x61, 0xC3 }); // call eax; popad; ret
        return code.ToArray();
    }

    private sealed class MapPacketEntry
    {
        public string Map { get; set; } = "";
        public ushort MapId { get; set; }
        public string PacketHex { get; set; } = "";
    }

    private void SendChatCommand(
        CharacterRuntime runtime, string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return;

        if (!runtime.Running || runtime.Process.HasExited)
            return;

        IntPtr hWnd = runtime.Process.MainWindowHandle;

        if (hWnd == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "Không tìm thấy cửa sổ của process.");
        }

        // Enter lần 1: mở khung chat
        PostMessage(hWnd, WM_KEYDOWN,
            (IntPtr)VK_RETURN, IntPtr.Zero);
        PostMessage(hWnd, WM_KEYUP,
            (IntPtr)VK_RETURN, IntPtr.Zero);

        Thread.Sleep(200);

        // Gửi nhanh toàn bộ ký tự, không delay từng ký tự
        foreach (char c in command)
        {
            PostMessage(hWnd, WM_CHAR,
                (IntPtr)c, IntPtr.Zero);
        }

        // Chờ client nhận nội dung trước khi xác nhận
        Thread.Sleep(1000);

        // Enter lần 2: xác nhận thực hiện lệnh
        PostMessage(hWnd, WM_KEYDOWN,
            (IntPtr)VK_RETURN, IntPtr.Zero);

        Thread.Sleep(100);

        PostMessage(hWnd, WM_KEYUP,
            (IntPtr)VK_RETURN, IntPtr.Zero);
    }


    private void RefreshRuntimeDisplay()
    {
        if (_closing || IsDisposed || _refreshing)
            return;

        foreach (DataGridViewRow row in _processGrid.Rows)
        {
            if (row.Tag is not int pid)
                continue;

            if (_runtimes.TryGetValue(pid, out CharacterRuntime? runtime))
            {
                row.Cells["Level"].Value = runtime.Level?.ToString() ?? "--";
                row.Cells["X"].Value = runtime.X?.ToString() ?? "--";
                row.Cells["Y"].Value = runtime.Y?.ToString() ?? "--";
                row.Cells["Status"].Value = runtime.Status;
            }
            else
            {
                row.Cells["Level"].Value = "--";
                row.Cells["X"].Value = "--";
                row.Cells["Y"].Value = "--";
                row.Cells["Status"].Value = "Chưa chạy";
            }
        }

        UpdateSummary();
    }

    private void UpdateSummary()
    {
        int running = _runtimes.Values.Count(r => r.Running);

        _summary.Text =
            $"Process: {_processGrid.Rows.Count} | Đang chạy: {running}";
    }

    private List<SpotConfig> GetSpots()
    {
        var list = new List<SpotConfig>();

        foreach (DataGridViewRow row in _spots.Rows)
        {
            if (row.IsNewRow)
                continue;

            bool enabled =
                row.Cells["Enabled"].Value is bool value && value;

            if (!int.TryParse(
                Convert.ToString(row.Cells["Level"].Value),
                out int level))
            {
                if (enabled)
                    throw new InvalidOperationException(
                        "Level ở bãi train không hợp lệ.");

                continue;
            }

            string map =
                Convert.ToString(row.Cells["Map"].Value) ?? "";

            int? x = int.TryParse(
                Convert.ToString(row.Cells["X"].Value),
                out int parsedX) ? parsedX : null;

            int? y = int.TryParse(
                Convert.ToString(row.Cells["Y"].Value),
                out int parsedY) ? parsedY : null;

            list.Add(new SpotConfig
            {
                Enabled = enabled,
                Level = level,
                Map = map.Trim(),
                X = x,
                Y = y
            });
        }

        return list;
    }

    private void ValidateSpots()
    {
        List<SpotConfig> enabled = GetSpots()
            .Where(s => s.Enabled)
            .OrderBy(s => s.Level)
            .ToList();

        if (enabled.Count == 0)
            throw new InvalidOperationException(
                "Cần ít nhất một bãi train được bật.");

        if (enabled.Any(s => s.Level < 1))
            throw new InvalidOperationException(
                "Level phải lớn hơn 0.");

        if (enabled.Any(s => string.IsNullOrWhiteSpace(s.Map)))
            throw new InvalidOperationException(
                "Tên map không được để trống.");

        if (enabled.Any(s => s.X is null || s.Y is null))
            throw new InvalidOperationException(
                "Hãy nhập X/Y đích cho các bãi đang bật.");

        if (enabled.Any(s =>
            s.X < 0 || s.X > ushort.MaxValue ||
            s.Y < 0 || s.Y > ushort.MaxValue))
        {
            throw new InvalidOperationException(
                "X/Y phải nằm trong khoảng 0..65535.");
        }

        if (enabled.Select(s => s.Level).Distinct().Count()
            != enabled.Count)
        {
            throw new InvalidOperationException(
                "Level của các bãi đang bật không được trùng.");
        }
    }

    private void SaveConfig()
    {
        try
        {
            CommitGridEdits();

            var config = new AppConfig
            {
                PollMs = (int)_poll.Value,
                ResetCommand = _resetCommand.Text,
                ResetLevel = (int)_resetLevel.Value,
                Spots = GetSpots()
            };

            File.WriteAllText(
                _configPath,
                JsonSerializer.Serialize(
                    config,
                    new JsonSerializerOptions { WriteIndented = true }));

            Log("Đã lưu cấu hình.");
        }
        catch (Exception ex)
        {
            Log("Lưu cấu hình lỗi: " + ex.Message);
        }
    }

    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(_configPath))
                throw new FileNotFoundException(
                    "Không tìm thấy config.json.");

            AppConfig config =
                JsonSerializer.Deserialize<AppConfig>(
                    File.ReadAllText(_configPath))
                ?? throw new InvalidDataException(
                    "config.json không hợp lệ.");

            _poll.Value = Math.Clamp(
                config.PollMs,
                (int)_poll.Minimum,
                (int)_poll.Maximum);

            _resetCommand.Text = config.ResetCommand ?? "/reset";

            _resetLevel.Value = Math.Clamp(
                config.ResetLevel,
                (int)_resetLevel.Minimum,
                (int)_resetLevel.Maximum);

            _spots.Rows.Clear();

            foreach (SpotConfig spot in config.Spots ?? new())
                AddSpot(spot.Enabled, spot.Level, spot.Map, spot.X, spot.Y);

            Log("Đã nạp cấu hình.");
        }
        catch (Exception ex)
        {
            Log("Nạp cấu hình lỗi: " + ex.Message);
        }
    }

    private void Log(string message)
    {
        if (_closing || IsDisposed || _log.IsDisposed)
            return;

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(new Action(() => Log(message)));
            }
            catch (InvalidOperationException) { }

            return;
        }

        _log.AppendText(
            $"[{DateTime.Now:HH:mm:ss}] {message}" +
            Environment.NewLine);
    }
}
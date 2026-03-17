using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using HidLibrary;
using TrainCrew;
using Windows.Gaming.Input;
using ZuikiProToTraincrew.Models;

namespace ZuikiProToTraincrew;

public partial class MainWindow : Window
{
    // Zuiki Mascon Pro
    private const ushort VID = 0x33DD;
    private const ushort PID = 0x0006;

    private RawGameController? _controller;
    private HidDevice? _hidDevice;
    private CancellationTokenSource? _cts;
    private Task? _pollTask;

    // 前回値
    private double[]? _prevAxes;
    private bool[]? _prevButtons;
    private GameControllerSwitchPosition[]? _prevSwitches;
    private int _prevNotch = int.MinValue;
    private int _prevReverser = int.MinValue;
    private int _prevHornState = -1; // 0=AirOnly, 1=Ele, 2=Off, 3=Air
    private bool _prevGradient = false;
    private bool _prevEBReset = false;

    private AppSettings _settings = new();

    // ボタン割り当てUI
    private readonly record struct ButtonMapEntry(string Key, string Label);
    private static readonly ButtonMapEntry[] CustomButtons =
    [
        new("Btn0", "Y"),
        new("Btn1", "B"),
        new("Btn2", "A"),
        new("Btn3", "X"),
        new("Btn4", "L"),
        new("Btn5", "R"),
        new("Btn7", "ZR"),
        new("Btn8", "−"),
        new("Btn9", "+"),
        new("Btn12", "Home"),
        new("Btn13", "キャプチャー"),
        new("SwUp", "Switch↑"),
        new("SwDown", "Switch↓"),
    ];

    // InputAction の表示名テーブル
    private static readonly (string Name, string Display)[] ActionEntries =
    [
        ("None", "(なし)"),
        ("HornAir", "空笛"),
        ("HornEle", "電笛"),
        ("Buzzer", "連絡ブザ"),
        ("GradientStart", "勾配起動"),
        ("EBReset", "EBリセット"),
        ("ViewChange", "視点変更"),
        ("PauseMenu", "ポーズメニュー"),
        ("ViewDiagram", "ダイヤの表示・非表示"),
        ("ViewUserInterface", "UIの表示・非表示"),
        ("ViewHome", "視点のリセット"),
        ("DriverViewR", "右側小窓視点へ"),
        ("DriverViewL", "左側小窓視点へ"),
        ("DriverViewC", "運転席視点へ"),
        ("LightLow", "前灯減光"),
        ("DoorOpn", "ドア開扉"),
        ("DoorCls", "ドア閉扉"),
        ("ReOpenSW", "再開閉SW"),
        ("JoukouSokusin", "乗降促進SW"),
        ("DoorKey", "ドアスイッチ鍵操作"),
        ("Housou", "車内放送再生"),
        ("ConductorViewB", "後方確認"),
    ];

    private readonly Dictionary<string, ComboBox> _mapCombos = new();

    // 設定ファイルパス
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZuikiProToTraincrew");
    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    public MainWindow()
    {
        InitializeComponent();
        BuildButtonMapGrid();
    }

    // ─── ライフサイクル ─────────────────────────────────────────────

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        TrainCrewInput.Init();
        LoadSettings();
        RefreshDeviceList();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        StopPolling();
        _hidDevice?.CloseDevice();
        _hidDevice?.Dispose();
        _hidDevice = null;
        TrainCrewInput.Dispose();
    }

    // ─── デバイス一覧 ────────────────────────────────────────────

    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => RefreshDeviceList();

    private void RefreshDeviceList()
    {
        _deviceCombo.Items.Clear();
        var controllers = RawGameController.RawGameControllers;
        for (int i = 0; i < controllers.Count; i++)
        {
            var c = controllers[i];
            _deviceCombo.Items.Add(
                $"[{i}] {c.DisplayName ?? "(名前なし)"}  VID=0x{c.HardwareVendorId:X4} PID=0x{c.HardwareProductId:X4}");
        }
        if (_deviceCombo.Items.Count > 0)
        {
            // VID/PID が一致するデバイスを自動選択
            for (int i = 0; i < controllers.Count; i++)
            {
                if (controllers[i].HardwareVendorId == VID && controllers[i].HardwareProductId == PID)
                {
                    _deviceCombo.SelectedIndex = i;
                    break;
                }
            }
            if (_deviceCombo.SelectedIndex < 0)
                _deviceCombo.SelectedIndex = 0;
        }
    }

    // ─── 接続/切断 ──────────────────────────────────────────────

    private void BtnConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_deviceCombo.SelectedIndex < 0) return;

        var controllers = RawGameController.RawGameControllers;
        int idx = _deviceCombo.SelectedIndex;
        if (idx >= controllers.Count) return;

        _controller = controllers[idx];

        // HidDevice を開く（OutputReport 送信用）
        var hidDevices = HidDevices.Enumerate(
            _controller.HardwareVendorId, _controller.HardwareProductId);
        foreach (var d in hidDevices)
        {
            if (d.IsConnected)
            {
                d.OpenDevice();
                if (d.IsOpen)
                {
                    _hidDevice = d;
                    break;
                }
            }
        }

        // 前回値初期化
        _prevAxes = new double[_controller.AxisCount];
        _prevButtons = new bool[_controller.ButtonCount];
        _prevSwitches = new GameControllerSwitchPosition[_controller.SwitchCount];
        _controller.GetCurrentReading(_prevButtons, _prevSwitches, _prevAxes);
        _prevNotch = int.MinValue;
        _prevReverser = int.MinValue;
        _prevHornState = -1;
        _prevGradient = false;
        _prevEBReset = false;

        // 設定スナップショット取得
        _settings.AlwaysSend = _radioAlways.IsChecked == true;
        ReadButtonMapFromUI();

        StartPolling();

        _btnConnect.IsEnabled = false;
        _btnDisconnect.IsEnabled = true;
        _statusText.Text = $"接続中: {_controller.DisplayName ?? "(名前なし)"}";
    }

    private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
    {
        StopPolling();
        _hidDevice?.CloseDevice();
        _hidDevice?.Dispose();
        _hidDevice = null;
        _controller = null;

        _btnConnect.IsEnabled = true;
        _btnDisconnect.IsEnabled = false;
        _statusText.Text = "切断しました";
    }

    // ─── ポーリング ─────────────────────────────────────────────

    private void StartPolling()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _pollTask = Task.Run(() => PollLoop(token), token);
    }

    private void StopPolling()
    {
        _cts?.Cancel();
        try { _pollTask?.Wait(2000); } catch { /* ignore */ }
        _cts?.Dispose();
        _cts = null;
        _pollTask = null;
    }

    private void PollLoop(CancellationToken ct)
    {
        var axes = new double[_prevAxes!.Length];
        var buttons = new bool[_prevButtons!.Length];
        var switches = new GameControllerSwitchPosition[_prevSwitches!.Length];

        bool alwaysSend = _settings.AlwaysSend;
        var buttonMapSnapshot = new Dictionary<string, string>(_settings.ButtonMap);

        // カスタムボタン前回状態
        var prevCustom = new Dictionary<string, bool>();

        int loopCount = 0;
        // ランプ前回状態
        bool prevDoorLamp = false;
        bool prevEBLamp = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                _controller!.GetCurrentReading(buttons, switches, axes);

                // --- ノッチ ---
                int notch = AxisToNotch(axes[1]);
                if (notch != _prevNotch || alwaysSend)
                {
                    TrainCrewInput.SetNotch(notch);
                    _prevNotch = notch;
                }

                // --- レバーサ ---
                int reverser = AxisToReverser(axes[3]);
                if (reverser != _prevReverser || alwaysSend)
                {
                    TrainCrewInput.SetReverser(reverser);
                    _prevReverser = reverser;
                }

                // --- 警笛 (Axis[0]) ---
                int hornState = AxisToHornState(axes[0]);
                if (hornState != _prevHornState)
                {
                    // まず両方OFF
                    TrainCrewInput.SetButton(InputAction.HornAir, false);
                    TrainCrewInput.SetButton(InputAction.HornEle, false);

                    switch (hornState)
                    {
                        case 0: // 空笛のみ (raw 0-31)
                            TrainCrewInput.SetButton(InputAction.HornAir, true);
                            break;
                        case 1: // 電笛 (raw 32-95)
                            TrainCrewInput.SetButton(InputAction.HornEle, true);
                            break;
                        case 2: // OFF (raw 96-191)
                            break;
                        case 3: // 空笛 (raw 192-255)
                            TrainCrewInput.SetButton(InputAction.HornAir, true);
                            break;
                    }
                    _prevHornState = hornState;
                }

                // --- 勾配起動 (Axis[2]) ---
                bool gradient = AxisToGradient(axes[2]);
                if (gradient != _prevGradient)
                {
                    TrainCrewInput.SetButton(InputAction.GradientStart, gradient);
                    _prevGradient = gradient;
                }

                // --- 固定ボタン: Button[10] EBリセット ---
                if (buttons.Length > 10)
                {
                    bool ebReset = buttons[10];
                    if (ebReset != _prevEBReset)
                    {
                        TrainCrewInput.SetButton(InputAction.EBReset, ebReset);
                        _prevEBReset = ebReset;
                    }
                }

                // --- カスタムボタン ---
                ProcessCustomButtons(buttons, switches, buttonMapSnapshot, prevCustom);

                // --- ランプ制御 (10ループに1回) ---
                loopCount++;
                if (loopCount % 10 == 0 && _hidDevice != null && _hidDevice.IsOpen)
                {
                    var state = TrainCrewInput.GetTrainState();
                    bool doorLamp = state.Lamps[PanelLamp.DoorClose];
                    bool ebLamp = state.Lamps[PanelLamp.EB_Timer] || state.Lamps[PanelLamp.EmagencyBrake];

                    if (doorLamp != prevDoorLamp || ebLamp != prevEBLamp)
                    {
                        UpdateLamps(doorLamp, ebLamp);
                        prevDoorLamp = doorLamp;
                        prevEBLamp = ebLamp;
                    }
                }

                // --- UI更新 ---
                string notchStr = NotchToString(notch);
                string reverserStr = reverser switch { 1 => "前進", 0 => "中立", -1 => "後進", _ => "-" };
                string hornStr = hornState switch { 0 => "空笛", 1 => "電笛", 2 => "OFF", 3 => "空笛", _ => "-" };

                Dispatcher.InvokeAsync(() =>
                {
                    _txtNotch.Text = notchStr;
                    _txtReverser.Text = reverserStr;
                    _txtHorn.Text = hornStr;
                });

                Thread.Sleep(16); // ~60fps
            }
            catch (OperationCanceledException) { break; }
            catch { break; }
        }
    }

    // ─── 軸変換 ─────────────────────────────────────────────────

    private static int AxisToNotch(double v)
    {
        int raw = (int)Math.Round(v * 255);
        return raw switch
        {
            <= 11 => -8,    // EB
            <= 38 => -7,    // B7/B8 → TC B6
            <= 52 => -6,    // B6 → TC B5
            <= 66 => -5,    // B5 → TC B4
            <= 79 => -4,    // B4 → TC B3
            <= 93 => -3,    // B3 → TC B2
            <= 107 => -2,   // B2 → TC B1
            <= 121 => -1,   // B1 → 抑速
            <= 143 => 0,    // N
            <= 169 => 1,    // P1
            <= 194 => 2,    // P2
            <= 218 => 3,    // P3
            <= 242 => 4,    // P4
            _ => 5,         // P5
        };
    }

    private static int AxisToReverser(double v)
    {
        int raw = (int)Math.Round(v * 255);
        return raw switch
        {
            <= 63 => 0,     // 切/中立
            <= 191 => 1,    // 前進
            _ => -1,        // 後進
        };
    }

    private static int AxisToHornState(double v)
    {
        int raw = (int)Math.Round(v * 255);
        return raw switch
        {
            <= 31 => 0,     // 空笛
            <= 95 => 1,     // 電笛
            <= 191 => 2,    // OFF
            _ => 3,         // 空笛
        };
    }

    private static bool AxisToGradient(double v)
    {
        int raw = (int)Math.Round(v * 255);
        return raw >= 192;
    }

    private static string NotchToString(int notch) => notch switch
    {
        -8 => "EB",
        -1 => "抑速",
        0 => "N",
        > 0 => $"P{notch}",
        _ => $"B{-notch - 1}",
    };

    // ─── カスタムボタン ──────────────────────────────────────────

    private static readonly Dictionary<string, int> ButtonKeyToIndex = new()
    {
        ["Btn0"] = 0, ["Btn1"] = 1, ["Btn2"] = 2, ["Btn3"] = 3,
        ["Btn4"] = 4, ["Btn5"] = 5, ["Btn7"] = 7, ["Btn8"] = 8,
        ["Btn9"] = 9, ["Btn12"] = 12, ["Btn13"] = 13,
    };

    private void ProcessCustomButtons(
        bool[] buttons,
        GameControllerSwitchPosition[] switches,
        Dictionary<string, string> map,
        Dictionary<string, bool> prevCustom)
    {
        foreach (var entry in CustomButtons)
        {
            if (!map.TryGetValue(entry.Key, out var actionStr) || actionStr == "None")
                continue;

            if (!Enum.TryParse<InputAction>(actionStr, out var action))
                continue;

            bool pressed;
            if (entry.Key == "SwUp")
            {
                pressed = switches.Length > 0 &&
                    (switches[0] == GameControllerSwitchPosition.Up ||
                     switches[0] == GameControllerSwitchPosition.UpLeft ||
                     switches[0] == GameControllerSwitchPosition.UpRight);
            }
            else if (entry.Key == "SwDown")
            {
                pressed = switches.Length > 0 &&
                    (switches[0] == GameControllerSwitchPosition.Down ||
                     switches[0] == GameControllerSwitchPosition.DownLeft ||
                     switches[0] == GameControllerSwitchPosition.DownRight);
            }
            else if (ButtonKeyToIndex.TryGetValue(entry.Key, out int btnIdx) && btnIdx < buttons.Length)
            {
                pressed = buttons[btnIdx];
            }
            else
            {
                continue;
            }

            prevCustom.TryGetValue(entry.Key, out bool prevState);
            if (pressed != prevState)
            {
                TrainCrewInput.SetButton(action, pressed);
                prevCustom[entry.Key] = pressed;
            }
        }
    }

    // ─── ランプ制御 ──────────────────────────────────────────────

    private void UpdateLamps(bool doorClose, bool eb)
    {
        if (_hidDevice == null || !_hidDevice.IsOpen) return;

        var report = _hidDevice.CreateReport();
        // OutputReport[3] = 戸締め灯, OutputReport[4] = EB灯
        if (report.Data.Length > 4)
        {
            report.Data[3] = (byte)(doorClose ? 0xFF : 0x00);
            report.Data[4] = (byte)(eb ? 0xFF : 0x00);
        }
        _hidDevice.WriteReport(report);

        Dispatcher.InvokeAsync(() =>
        {
            _txtDoorLamp.Text = doorClose ? "ON" : "OFF";
            _txtEBLamp.Text = eb ? "ON" : "OFF";
        });
    }

    // ─── ボタン割り当て UI ──────────────────────────────────────

    private void BuildButtonMapGrid()
    {
        // 左列(0-6)と右列(7-12)に分ける
        int leftCount = 7;
        int totalRows = leftCount;

        for (int i = 0; i < totalRows; i++)
            _buttonMapGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int i = 0; i < CustomButtons.Length; i++)
        {
            int col, row;
            if (i < leftCount)
            {
                col = 0;
                row = i;
            }
            else
            {
                col = 3;
                row = i - leftCount;
            }

            var label = new TextBlock
            {
                Text = CustomButtons[i].Label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2),
            };
            Grid.SetRow(label, row);
            Grid.SetColumn(label, col);
            _buttonMapGrid.Children.Add(label);

            var combo = new ComboBox { Width = 170, Margin = new Thickness(2) };
            foreach (var ae in ActionEntries)
                combo.Items.Add(ae.Display);
            combo.SelectedIndex = 0; // (なし)
            Grid.SetRow(combo, row);
            Grid.SetColumn(combo, col + 1);
            _buttonMapGrid.Children.Add(combo);

            _mapCombos[CustomButtons[i].Key] = combo;
        }
    }

    // ─── 設定 ────────────────────────────────────────────────────

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                _settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            _settings = new AppSettings();
        }

        ApplySettingsToUI();
    }

    private void ApplySettingsToUI()
    {
        if (_settings.AlwaysSend)
            _radioAlways.IsChecked = true;
        else
            _radioDiff.IsChecked = true;

        foreach (var entry in CustomButtons)
        {
            if (!_mapCombos.TryGetValue(entry.Key, out var combo)) continue;

            if (_settings.ButtonMap.TryGetValue(entry.Key, out var actionStr))
            {
                // actionStr → Display名を探す
                int idx = Array.FindIndex(ActionEntries, ae => ae.Name == actionStr);
                combo.SelectedIndex = idx >= 0 ? idx : 0;
            }
            else
            {
                combo.SelectedIndex = 0;
            }
        }
    }

    private void ReadButtonMapFromUI()
    {
        _settings.ButtonMap.Clear();
        foreach (var entry in CustomButtons)
        {
            if (!_mapCombos.TryGetValue(entry.Key, out var combo)) continue;
            int idx = combo.SelectedIndex;
            if (idx > 0 && idx < ActionEntries.Length)
                _settings.ButtonMap[entry.Key] = ActionEntries[idx].Name;
        }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        _settings.AlwaysSend = _radioAlways.IsChecked == true;
        ReadButtonMapFromUI();

        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
            _statusText.Text = "設定を保存しました";
        }
        catch (Exception ex)
        {
            _statusText.Text = $"保存失敗: {ex.Message}";
        }
    }
}

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using HdrScope.Analysis;
using HdrScope.Capture;
using HdrScope.Interop;
using HdrScope.Rendering;

namespace HdrScope;

public sealed class MainForm : Form
{
    private readonly ComboBox _screenCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
    private readonly TextBox _nitsBox = new() { Dock = DockStyle.Top, Text = "1,2,4,10,25,50,100,203,300,400,500" };
    private readonly Button _statusBtn = new() { Text = "Оновити стан HDR", Dock = DockStyle.Top };
    private readonly Button _wizardBtn = new() { Text = "▶ МАЙСТЕР КАЛІБРУВАННЯ (12 кроків, ~10 хв)", Dock = DockStyle.Top, Height = 40, Font = new Font("Segoe UI", 10, FontStyle.Bold) };
    private readonly Button _genFixBtn = new() { Text = "Створити і встановити HDR-профіль gamma2.2-fix (з останньої сесії)", Dock = DockStyle.Top, Height = 32 };
    private readonly Button _genNeutralBtn = new() { Text = "Створити і встановити HDR-профіль neutral (для порівняння)", Dock = DockStyle.Top, Height = 28 };
    private readonly Button _openHdrSettingsBtn = new() { Text = "Відкрити налаштування Windows HDR (повзунок SDR → 31)", Dock = DockStyle.Top, Height = 28 };
    private readonly Button _restartHdrBtn = new() { Text = "Перезапустити HDR (застосувати профіль)", Dock = DockStyle.Top, Height = 28 };
    private readonly Label _liveStatus = new() { Dock = DockStyle.Top, Height = 24, Font = new Font("Consolas", 9, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 2000 };
    private readonly Button _showBtn = new() { Text = "Діагностика: показати тестові патчі", Dock = DockStyle.Top };
    private readonly Button _captureBtn = new() { Text = "Діагностика: захопити і проаналізувати", Dock = DockStyle.Top };
    private readonly TextBox _log = new() { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, ReadOnly = true, Font = new Font("Consolas", 9) };
    private readonly DataGridView _grid = new() { Dock = DockStyle.Bottom, Height = 220, ReadOnly = true, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };

    private PatternForm? _patternForm;
    private readonly string _outDir;

    public MainForm()
    {
        Text = "HdrScope — діагностика HDR-сигналу";
        Width = 900;
        Height = 800;

        _outDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "HdrScope-Results");
        Directory.CreateDirectory(_outDir);

        foreach (var s in Screen.AllScreens)
            _screenCombo.Items.Add($"{s.DeviceName}  {s.Bounds.Width}x{s.Bounds.Height} @ ({s.Bounds.X},{s.Bounds.Y}){(s.Primary ? "  [основний]" : "")}");
        if (_screenCombo.Items.Count > 0) _screenCombo.SelectedIndex = 0;

        _statusBtn.Click += (_, _) => RefreshStatus();
        _wizardBtn.Click += (_, _) => StartWizard();
        _genFixBtn.Click += (_, _) => GenerateFromLastSession(gammaFix: true);
        _genNeutralBtn.Click += (_, _) => GenerateFromLastSession(gammaFix: false);
        _showBtn.Click += (_, _) => ShowPattern();
        _captureBtn.Click += (_, _) => CaptureAndAnalyze();

        _openHdrSettingsBtn.Click += (_, _) =>
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "ms-settings:display-hdr", UseShellExecute = true });
            Log("Відкрито налаштування Windows. Знайдіть повзунок «Яскравість вмісту SDR», поставте 31 (=204 nits).");
            Log("Значення в рядку статусу вгорі оновиться автоматично, коли повзунок стане на місце.");
        };
        _restartHdrBtn.Click += (_, _) =>
        {
            Log("Перезапускаю HDR (екран блимне двічі)...");
            _restartHdrBtn.Enabled = false;
            Application.DoEvents();
            bool ok = Interop.AdvancedColorInfo.RestartHdr();
            _restartHdrBtn.Enabled = true;
            Log(ok ? "HDR перезапущено — профіль застосовано." : "Не вдалося перезапустити HDR через API. Скористайтесь Win+Alt+B двічі.");
        };
        _statusTimer.Tick += (_, _) => UpdateLiveStatus();
        _statusTimer.Start();
        UpdateLiveStatus();

        var tips = new ToolTip { AutoPopDelay = 30000, InitialDelay = 300, ReshowDelay = 100 };
        tips.SetToolTip(_screenCombo,
            "На якому моніторі показувати тести і який калібрувати.\n" +
            "У вас один монітор — залиште як є.");
        tips.SetToolTip(_statusBtn,
            "Показує поточний стан HDR у Windows: чи увімкнено, глибину кольору (біт),\n" +
            "тип сигналу (RGB/YCbCr) і рівень 'SDR content brightness' у нітах.\n" +
            "Корисно після зміни повзунка яскравості SDR — щоб побачити нове значення.");
        tips.SetToolTip(_wizardBtn,
            "ГОЛОВНА КНОПКА. 12 візуальних тестів (~10 хв): пік яскравості, чорний,\n" +
            "градації, гамма, контраст. Результати зберігаються в JSON —\n" +
            "його можна віддати Claude для аналізу.");
        tips.SetToolTip(_genFixBtn,
            "РЕКОМЕНДОВАНИЙ ПРОФІЛЬ. Бере результати останнього майстра і встановлює\n" +
            "ICC-профіль, який: 1) виправляє вицвілий SDR-контент у HDR-режимі (гамма-фікс),\n" +
            "2) повідомляє іграм ваш реальний пік/чорний (565 / 0.05 ніт).\n" +
            "HDR має бути УВІМКНЕНИЙ. Повторюйте після зміни повзунка SDR-яскравості.");
        tips.SetToolTip(_genNeutralBtn,
            "Альтернатива для порівняння: той самий профіль, але БЕЗ гамма-фіксу —\n" +
            "тільки коректні пік/чорний для ігор. Встановіть, якщо з gamma2.2-fix\n" +
            "щось не подобається (наприклад, тіні у HDR-фільмах стали затемними).\n" +
            "Останній встановлений профіль стає активним.");
        tips.SetToolTip(_nitsBox,
            "Рівні яскравості (в нітах) для діагностичних колонок нижче.\n" +
            "Використовується тільки кнопками 'Діагностика'.");
        tips.SetToolTip(_showBtn,
            "ДІАГНОСТИКА (не обов'язково). Малює на екрані колонки заданої яскравості\n" +
            "через HDR-рендер — щоб перевірити, що Windows передає сигнал без спотворень.");
        tips.SetToolTip(_captureBtn,
            "ДІАГНОСТИКА (не обов'язково). Знімає екран з відеопам'яті і порівнює,\n" +
            "що реально відправляється на монітор, з тим, що замовлено кнопкою вище.\n" +
            "Δ% ≈ 0 — сигнал чистий. Результати (CSV/PNG) — у Документи\\HdrScope-Results.");
        tips.SetToolTip(_openHdrSettingsBtn,
            "Відкриває сторінку налаштувань Windows з повзунком «Яскравість вмісту SDR».\n" +
            "Ціль: 31 (= 204 ніт, студійний стандарт). Рядок статусу вгорі покаже поточне значення.");
        tips.SetToolTip(_restartHdrBtn,
            "Вимикає та вмикає HDR (як Win+Alt+B двічі), щоб Windows перечитала колірний профіль.\n" +
            "Натискайте ПІСЛЯ встановлення профілю. Екран блимне — це нормально.");
        tips.SetToolTip(_liveStatus,
            "Живий стан HDR — оновлюється кожні 2 секунди.");

        Controls.Add(_log);
        Controls.Add(_grid);
        Controls.Add(_captureBtn);
        Controls.Add(_showBtn);
        Controls.Add(_nitsBox);
        Controls.Add(new Label { Text = "Цільові рівні (ніт), через кому:", Dock = DockStyle.Top, Height = 20 });
        Controls.Add(_genNeutralBtn);
        Controls.Add(_restartHdrBtn);
        Controls.Add(_openHdrSettingsBtn);
        Controls.Add(_genFixBtn);
        Controls.Add(_wizardBtn);
        Controls.Add(_statusBtn);
        Controls.Add(_liveStatus);
        Controls.Add(_screenCombo);
        Controls.Add(new Label { Text = "Монітор:", Dock = DockStyle.Top, Height = 20 });

        Load += (_, _) => RefreshStatus();
    }

    private void Log(string s) => _log.AppendText(s + Environment.NewLine);

    private void UpdateLiveStatus()
    {
        var acs = Interop.AdvancedColorInfo.QueryAll().FirstOrDefault();
        if (acs is null)
        {
            _liveStatus.Text = "Стан дисплея недоступний";
            _liveStatus.ForeColor = System.Drawing.Color.Gray;
            return;
        }
        bool whiteOk = Math.Abs(acs.SdrWhiteLevelNits - 204) <= 6;
        string white = $"SDR white: {acs.SdrWhiteLevelNits:F0} nits" + (whiteOk ? " ✓" : " (ціль 204 = повзунок 31)");
        _liveStatus.Text = acs.Enabled
            ? $"HDR: УВІМКНЕНО ✓  {acs.BitsPerColorChannel}-bit  {white}"
            : "HDR: ВИМКНЕНО — профіль і тести HDR працюють лише при увімкненому HDR";
        _liveStatus.ForeColor = acs.Enabled && whiteOk ? System.Drawing.Color.DarkGreen
            : acs.Enabled ? System.Drawing.Color.DarkOrange
            : System.Drawing.Color.Firebrick;
    }

    private void RefreshStatus()
    {
        _log.Clear();
        Log("=== Стан Advanced Color / HDR (з Win32 DisplayConfig API) ===");
        foreach (var st in AdvancedColorInfo.QueryAll())
        {
            Log($"Target {st.TargetId}: Supported={st.Supported} Enabled={st.Enabled} " +
                $"Encoding={st.ColorEncoding} Bits={st.BitsPerColorChannel} SDRWhite={st.SdrWhiteLevelNits:F0} nits " +
                $"(BT.2408 reference = 203 nits)");
        }
        Log("");
        Log("Натисніть «Показати тестові патчі», перетягніть вікно не потрібно — воно саме розгорнеться на обраному моніторі.");
    }

    private float[] ParseNits()
    {
        return _nitsBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
    }

    private Screen SelectedScreen => Screen.AllScreens[_screenCombo.SelectedIndex];

    private void GenerateFromLastSession(bool gammaFix)
    {
        try
        {
            var lastSession = Directory.GetFiles(_outDir, "session-*.json")
                .OrderByDescending(f => f).FirstOrDefault();

            double maxL = 450, minL = 0.05;
            if (lastSession is not null)
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(lastSession));
                if (doc.RootElement.TryGetProperty("Conclusions", out var c))
                {
                    if (c.TryGetProperty("maxTML_nits", out var mx)) maxL = mx.GetDouble();
                    if (c.TryGetProperty("minTML_nits", out var mn)) minL = mn.GetDouble();
                }
                Log($"Дані з сесії: {Path.GetFileName(lastSession)} (maxTML={maxL:F0}, minTML={minL:F3})");
            }
            else
            {
                Log("Сесій не знайдено — використовую типові значення (450 / 0.05). Радимо пройти майстер.");
            }

            var acs = Interop.AdvancedColorInfo.QueryAll().FirstOrDefault();
            if (acs is null || !acs.Enabled)
            {
                Log("ПОМИЛКА: HDR вимкнено. Увімкніть (Win+Alt+B) і повторіть — профіль прив'язаний до поточного SDR white level.");
                return;
            }

            double w = acs.SdrWhiteLevelNits;
            var edid = Interop.Edid.ReadForMonitor("DELD175") ?? Interop.Edid.ReadForMonitor("DEL");
            var spec = new Color.Mhc2ProfileSpec
            {
                Description = gammaFix
                    ? $"HdrScope G2724D HDR gamma2.2 fix (W={w:F0} peak={maxL:F0})"
                    : $"HdrScope G2724D HDR neutral (peak={maxL:F0})",
                MinLuminanceNits = minL,
                MaxLuminanceNits = maxL,
                RegammaLut = gammaFix ? Color.Tf.BuildSdrGammaFixLut(4096, w) : null,
                RedPrimary = edid?.Red ?? (0.640, 0.330),
                GreenPrimary = edid?.Green ?? (0.300, 0.600),
                BluePrimary = edid?.Blue ?? (0.150, 0.060),
                WhitePoint = edid?.White ?? (0.3127, 0.3290),
                LumiNits = w,
            };

            string name = gammaFix ? "HdrScope-G2724D-gamma22fix.icm" : "HdrScope-G2724D-neutral.icm";
            string path = Path.Combine(_outDir, name);
            Color.Mhc2IccWriter.Write(path, spec);
            Log($"Профіль записано: {path} (SDR white = {w:F0} nits)");

            string status = Color.ProfileInstaller.InstallAndAssociateHdr(path, acs.AdapterId, acs.SourceId);
            Log(status);

            Log("Перезапускаю HDR для застосування профілю (екран блимне)...");
            Application.DoEvents();
            bool restarted = Interop.AdvancedColorInfo.RestartHdr();
            Log(restarted
                ? "✓ Готово. SDR-контент у HDR-режимі має стати контрастнішим у тінях — порівняйте будь-який сайт."
                : "Перезапуск через API не вдався — натисніть Win+Alt+B двічі вручну.");
            Log("ВАЖЛИВО: якщо зміните повзунок 'SDR content brightness' — натисніть цю кнопку ще раз (LUT прив'язана до рівня).");
        }
        catch (Exception ex)
        {
            Log("ПОМИЛКА генерації профілю: " + ex);
        }
    }

    private void StartWizard()
    {
        var session = new Calibration.Session(_outDir);
        session.Environment["machine"] = Environment.MachineName;
        session.Environment["os"] = Environment.OSVersion.VersionString;
        session.Environment["screen"] = SelectedScreen.DeviceName;
        var wizard = new Calibration.WizardForm(SelectedScreen, MonitorHelper.GetHMonitor(SelectedScreen.Bounds), session);
        wizard.FormClosed += (_, _) =>
        {
            Log($"Сесію калібрування збережено: {session.JsonPath}");
            Log("Передайте цей JSON Claude для аналізу.");
        };
        wizard.Show(this);
    }

    private void ShowPattern()
    {
        _patternForm?.Close();
        var nits = ParseNits();
        _patternForm = new PatternForm(SelectedScreen.Bounds, nits);
        _patternForm.Show();
        Log($"Показано {nits.Length} патчів на {SelectedScreen.DeviceName}: [{string.Join(", ", nits)}] нит.");
    }

    private void CaptureAndAnalyze()
    {
        if (_patternForm is null || _patternForm.IsDisposed)
        {
            MessageBox.Show(this, "Спершу натисніть «Показати тестові патчі».", "HdrScope");
            return;
        }

        try
        {
            var hmon = MonitorHelper.GetHMonitor(SelectedScreen.Bounds);
            Log("Захоплення кадру через Windows.Graphics.Capture (R16G16B16A16Float / scRGB)...");
            var frame = HdrFrameCapture.CaptureMonitor(hmon);
            Log($"Кадр захоплено: {frame.Width}x{frame.Height}.");

            var results = HdrAnalyzer.AnalyzePatches(frame, _patternForm.Patches);
            _grid.DataSource = results.Select(r => new
            {
                Target_nits = r.TargetNits,
                Виміряно_nits = MathF.Round(r.MeanNits, 1),
                Min = MathF.Round(r.MinNits, 1),
                Max = MathF.Round(r.MaxNits, 1),
                StdDev = MathF.Round(r.StdDevNits, 2),
                Δ_nits = MathF.Round(r.DeltaNits, 1),
                Δ_percent = MathF.Round(r.DeltaPercent, 1),
            }).ToList();

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var csvPath = Path.Combine(_outDir, $"patches-{stamp}.csv");
            var pngPath = Path.Combine(_outDir, $"falsecolor-{stamp}.png");
            var histPath = Path.Combine(_outDir, $"histogram-{stamp}.csv");

            HdrAnalyzer.SavePatchReportCsv(csvPath, results);
            float maxNits = MathF.Max(500, results.Max(r => r.TargetNits) * 1.2f);
            HdrAnalyzer.SaveFalseColorPng(frame, pngPath, maxNits);
            var hist = HdrAnalyzer.BuildHistogram(frame, 200, maxNits);
            HdrAnalyzer.SaveHistogramCsv(histPath, hist, maxNits);

            Log($"Збережено: {csvPath}");
            Log($"Хибнокольорове зображення (nits -> heat LUT): {pngPath}");
            Log($"Гістограма: {histPath}");
            Log("");
            Log("Інтерпретація:");
            Log(" - Δ_percent близько 0 на низьких рівнях = сигнал коректний.");
            Log(" - Якщо декілька останніх колонок з РІЗНИМИ Target_nits дають ОДНАКОВЕ Виміряно_nits — це стеля клипінгу (панель або SDR-brightness slider), а не помилка захоплення.");
            Log(" - StdDev великий всередині одного патча може вказувати на banding/dithering.");
            Log(" - Це вимірювання СИГНАЛУ (те, що Windows композитить), НЕ реального світла з панелі. Для фотометричної калібровки потрібен колориметр.");

            Process.Start(new ProcessStartInfo { FileName = _outDir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log("ПОМИЛКА: " + ex);
        }
    }
}

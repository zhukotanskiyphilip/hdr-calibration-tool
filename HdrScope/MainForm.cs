using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using HdrScope.Interop;

namespace HdrScope;

public sealed class MainForm : Form
{
    private readonly Label _liveStatus = new() { Dock = DockStyle.Top, Height = 26, Font = new Font("Consolas", 9, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button _wizardBtn = new() { Text = "▶ МАЙСТЕР КАЛІБРУВАННЯ (12 кроків, ~10 хв)", Dock = DockStyle.Top, Height = 44, Font = new Font("Segoe UI", 10, FontStyle.Bold) };
    private readonly Button _competBtn = new() { Text = "★ HDR: ПІДНЯТІ ТІНІ — для ігор", Dock = DockStyle.Top, Height = 36 };
    private readonly Button _accurateBtn = new() { Text = "HDR: ТОЧНИЙ — як відкалібровано", Dock = DockStyle.Top, Height = 36 };
    private readonly Button _sdrBtn = new() { Text = "SDR: профіль монітора (для color-managed програм)", Dock = DockStyle.Top, Height = 36 };
    private readonly Button _restartHdrBtn = new() { Text = "Перезапустити HDR", Dock = DockStyle.Top, Height = 30 };
    private readonly TextBox _log = new() { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, ReadOnly = true, Font = new Font("Consolas", 9) };
    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 2000 };

    private readonly string _outDir;

    public MainForm()
    {
        Text = "HdrScope — калібрування Dell G2724D";
        Width = 640;
        Height = 560;

        _outDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "HdrScope-Results");
        Directory.CreateDirectory(_outDir);

        _wizardBtn.Click += (_, _) => StartWizard();
        _competBtn.Click += (_, _) => GenerateHdrProfile(ProfileMode.Competitive);
        _accurateBtn.Click += (_, _) => GenerateHdrProfile(ProfileMode.Neutral);
        _sdrBtn.Click += (_, _) => GenerateSdrProfile();
        _restartHdrBtn.Click += (_, _) =>
        {
            Log("Перезапускаю HDR (екран блимне двічі)...");
            _restartHdrBtn.Enabled = false;
            Application.DoEvents();
            bool ok = AdvancedColorInfo.RestartHdr();
            _restartHdrBtn.Enabled = true;
            Log(ok ? "HDR перезапущено." : "Не вдалося через API — натисніть Win+Alt+B двічі.");
        };
        _statusTimer.Tick += (_, _) => UpdateLiveStatus();
        _statusTimer.Start();
        UpdateLiveStatus();

        var tips = new ToolTip { AutoPopDelay = 30000, InitialDelay = 300, ReshowDelay = 100 };
        tips.SetToolTip(_wizardBtn,
            "12 візуальних тестів (~10 хв): пік яскравості, чорний, градації, гамма, контраст.\n" +
            "Результати зберігаються в JSON — його можна віддати Claude для аналізу.\n" +
            "Профілі нижче беруть числа з останньої пройденої сесії.");
        tips.SetToolTip(_competBtn,
            "Для ігор (Hunt тощо): підіймає тіні 0–25 ніт у HDR-режимі — програмний Dark Stabilizer.\n" +
            "Плюс чесний виміряний пік для тонмапера гри. Діє ТІЛЬКИ коли HDR увімкнено;\n" +
            "на SDR-режим не впливає взагалі. В грі: peak 575, exposure 1.0.");
        tips.SetToolTip(_accurateBtn,
            "Максимально точний HDR «як задумано»: жодних змін кривої, тільки ваші виміряні\n" +
            "пік/чорний (їх читають ігри та Windows). Для фільмів і атмосферних ігор.\n" +
            "Діє тільки в HDR-режимі. Останній натиснутий HDR-профіль стає активним.");
        tips.SetToolTip(_sdrBtn,
            "Описує ваш відкалібрований монітор (заводські праймеріз з EDID, гамма 2.2)\n" +
            "для звичайного не-HDR режиму. Його використовують color-managed програми\n" +
            "(браузер, перегляд фото). Кривих не змінює — ваш SDR калібрований у самому моніторі.");
        tips.SetToolTip(_restartHdrBtn,
            "Вимкнути та увімкнути HDR (як Win+Alt+B двічі) — щоб Windows перечитала профіль.\n" +
            "Профільні кнопки роблять це самі; ця кнопка — про всяк випадок.");
        tips.SetToolTip(_liveStatus, "Живий стан HDR — оновлюється кожні 2 секунди.");

        Controls.Add(_log);
        Controls.Add(_restartHdrBtn);
        Controls.Add(_sdrBtn);
        Controls.Add(_accurateBtn);
        Controls.Add(_competBtn);
        Controls.Add(_wizardBtn);
        Controls.Add(_liveStatus);

        Load += (_, _) =>
        {
            Log("HDR-профілі («тіні» / «точний») діють лише при увімкненому HDR (Win+Alt+B).");
            Log("SDR-режим завжди працює на калібровці самого монітора — профільні LUT його не чіпають.");
        };
    }

    private void Log(string s) => _log.AppendText(s + Environment.NewLine);

    private void UpdateLiveStatus()
    {
        var acs = AdvancedColorInfo.QueryAll().FirstOrDefault();
        if (acs is null)
        {
            _liveStatus.Text = "Стан дисплея недоступний";
            _liveStatus.ForeColor = System.Drawing.Color.Gray;
            return;
        }
        _liveStatus.Text = acs.Enabled
            ? $"HDR: УВІМКНЕНО ✓  {acs.BitsPerColorChannel}-bit  SDR white: {acs.SdrWhiteLevelNits:F0} nits"
            : "HDR: ВИМКНЕНО — SDR-режим (монітор працює на власній калібровці)";
        _liveStatus.ForeColor = acs.Enabled ? System.Drawing.Color.DarkGreen : System.Drawing.Color.DarkSlateGray;
    }

    private (double maxL, double minL) ReadLastSession()
    {
        double maxL = 450, minL = 0.05;
        var lastSession = Directory.GetFiles(_outDir, "session-*.json")
            .OrderByDescending(f => f).FirstOrDefault();
        if (lastSession is not null)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(lastSession));
            if (doc.RootElement.TryGetProperty("Conclusions", out var c))
            {
                if (c.TryGetProperty("maxTML_nits", out var mx)) maxL = mx.GetDouble();
                if (c.TryGetProperty("minTML_nits", out var mn)) minL = mn.GetDouble();
            }
            Log($"Дані з сесії: {Path.GetFileName(lastSession)} (пік={maxL:F0}, чорний={minL:F3})");
        }
        else
        {
            Log("Сесій не знайдено — типові значення (450 / 0.05). Радимо пройти майстер.");
        }
        return (maxL, minL);
    }

    public enum ProfileMode { GammaFix, Neutral, Competitive }

    private void GenerateHdrProfile(ProfileMode mode)
    {
        try
        {
            var (maxL, minL) = ReadLastSession();

            var acs = AdvancedColorInfo.QueryAll().FirstOrDefault();
            if (acs is null || !acs.Enabled)
            {
                Log("ПОМИЛКА: HDR вимкнено. Увімкніть (Win+Alt+B) і повторіть.");
                return;
            }

            double w = acs.SdrWhiteLevelNits;
            var edid = Edid.ReadForMonitor("DELD175") ?? Edid.ReadForMonitor("DEL");
            var spec = new Color.Mhc2ProfileSpec
            {
                Description = mode switch
                {
                    ProfileMode.GammaFix => $"HdrScope G2724D HDR gamma2.2 fix (W={w:F0} peak={maxL:F0})",
                    ProfileMode.Competitive => $"HdrScope G2724D HDR competitive shadow-lift (peak={maxL:F0})",
                    _ => $"HdrScope G2724D HDR accurate (peak={maxL:F0})",
                },
                MinLuminanceNits = minL,
                MaxLuminanceNits = maxL,
                RegammaLut = mode switch
                {
                    ProfileMode.GammaFix => Color.Tf.BuildSdrGammaFixLut(4096, w),
                    ProfileMode.Competitive => Color.Tf.BuildShadowLiftLut(4096, kneeNits: 25, strength: 0.75),
                    _ => null,
                },
                RedPrimary = edid?.Red ?? (0.640, 0.330),
                GreenPrimary = edid?.Green ?? (0.300, 0.600),
                BluePrimary = edid?.Blue ?? (0.150, 0.060),
                WhitePoint = edid?.White ?? (0.3127, 0.3290),
                LumiNits = w,
            };

            string name = mode switch
            {
                ProfileMode.GammaFix => "HdrScope-G2724D-gamma22fix.icm",
                ProfileMode.Competitive => "HdrScope-G2724D-competitive.icm",
                _ => "HdrScope-G2724D-accurate.icm",
            };
            string path = Path.Combine(_outDir, name);
            Color.Mhc2IccWriter.Write(path, spec);
            Log($"Профіль записано: {name}");

            Log(Color.ProfileInstaller.InstallAndAssociateHdr(path, acs.AdapterId, acs.SourceId));

            Log("Перезапускаю HDR (екран блимне)...");
            Application.DoEvents();
            bool restarted = AdvancedColorInfo.RestartHdr();
            Log(restarted
                ? mode switch
                {
                    ProfileMode.Competitive => "✓ Активний профіль: ПІДНЯТІ ТІНІ. В грі: peak 575, exposure 1.0.",
                    ProfileMode.GammaFix => "✓ Активний профіль: gamma2.2-fix (SDR-контент у HDR-режимі виправлено).",
                    _ => "✓ Активний профіль: ТОЧНИЙ (без корекцій кривої).",
                }
                : "Перезапуск не вдався — натисніть Win+Alt+B двічі вручну.");
        }
        catch (Exception ex)
        {
            Log("ПОМИЛКА генерації профілю: " + ex);
        }
    }

    private void GenerateSdrProfile()
    {
        try
        {
            var acs = AdvancedColorInfo.QueryAll().FirstOrDefault();
            if (acs is null)
            {
                Log("ПОМИЛКА: не вдалося визначити дисплей.");
                return;
            }

            var edid = Edid.ReadForMonitor("DELD175") ?? Edid.ReadForMonitor("DEL");
            var spec = new Color.Mhc2ProfileSpec
            {
                Description = "HdrScope G2724D SDR (native gamma 2.2, EDID primaries)",
                IncludeMhc2 = false,
                RedPrimary = edid?.Red ?? (0.640, 0.330),
                GreenPrimary = edid?.Green ?? (0.300, 0.600),
                BluePrimary = edid?.Blue ?? (0.150, 0.060),
                WhitePoint = edid?.White ?? (0.3127, 0.3290),
                LumiNits = 300,
            };

            string path = Path.Combine(_outDir, "HdrScope-G2724D-SDR.icm");
            Color.Mhc2IccWriter.Write(path, spec);
            Log("Профіль записано: HdrScope-G2724D-SDR.icm");

            Log(Color.ProfileInstaller.InstallAndAssociateSdr(path, acs.AdapterId, acs.SourceId));
            Log("✓ Діє у звичайному (не-HDR) режимі для програм з керуванням кольором. Перезапуск не потрібен.");
        }
        catch (Exception ex)
        {
            Log("ПОМИЛКА генерації SDR-профілю: " + ex);
        }
    }

    private void StartWizard()
    {
        var screen = Screen.PrimaryScreen!;
        var session = new Calibration.Session(_outDir);
        session.Environment["machine"] = Environment.MachineName;
        session.Environment["os"] = Environment.OSVersion.VersionString;
        session.Environment["screen"] = screen.DeviceName;
        var wizard = new Calibration.WizardForm(screen, MonitorHelper.GetHMonitor(screen.Bounds), session);
        wizard.FormClosed += (_, _) =>
        {
            Log($"Сесію калібрування збережено: {Path.GetFileName(session.JsonPath)}");
            Log("Передайте цей JSON Claude для аналізу.");
        };
        wizard.Show(this);
    }
}

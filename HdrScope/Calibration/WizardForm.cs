using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using HdrScope.Color;
using HdrScope.Interop;
using HdrScope.Rendering;

namespace HdrScope.Calibration;

public sealed class WizardForm : Form
{
    private enum Step
    {
        HdrPrep, MaxTml, MaxFullFrame, MinTml, ShadowLadder, ClipLadder,
        SdrPrep, Gamma50, Gamma25, Gamma75, GrayTint, ContrastBars,
        GenerateProfiles, Done
    }

    private readonly Screen _screen;
    private readonly IntPtr _hmonitor;
    private readonly Session _session;
    private readonly Label _title = new() { Dock = DockStyle.Top, Font = new Font("Segoe UI", 12, FontStyle.Bold), Height = 30 };
    private readonly Label _instruction = new() { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 10) };
    private readonly Label _value = new() { Dock = DockStyle.Bottom, Font = new Font("Consolas", 11, FontStyle.Bold), Height = 26, ForeColor = System.Drawing.Color.DarkGreen };
    private readonly System.Windows.Forms.Timer _pollTimer = new() { Interval = 600 };

    private PatternForm? _hdrPattern;
    private SdrPatternForm? _sdrPattern;
    private DdcCi? _ddc;

    private Step _step = Step.HdrPrep;
    private double _adjustPq;          // current adjustable value in PQ [0..1]
    private int _countAnswer = -1;
    private StepRecord? _rec;

    private readonly double[] _shadowNits = [0.01, 0.05, 0.1, 0.3, 0.5, 1, 2, 5];
    private readonly double[] _clipNits = [300, 350, 400, 425, 450, 475, 500, 600];

    private double _maxTmlNits = double.NaN, _maxFfNits = double.NaN, _minTmlNits = double.NaN;
    private readonly Dictionary<double, int> _gammaMatches = new(); // density -> matched code
    private uint _ddcGainR0, _ddcGainG0, _ddcGainB0, _ddcContrast0;

    public WizardForm(Screen screen, IntPtr hmonitor, Session session)
    {
        _screen = screen;
        _hmonitor = hmonitor;
        _session = session;

        Text = "HdrScope — майстер калібрування";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        TopMost = true;
        Width = 760; Height = 240;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(screen.Bounds.X + (screen.Bounds.Width - Width) / 2, screen.Bounds.Y + 8);
        KeyPreview = true;

        Controls.Add(_instruction);
        Controls.Add(_value);
        Controls.Add(_title);

        _pollTimer.Tick += (_, _) => PollState();
        Load += (_, _) => EnterStep(Step.HdrPrep);
        FormClosed += (_, _) => Cleanup();
    }

    private void Cleanup()
    {
        _pollTimer.Stop();
        _hdrPattern?.Close(); _hdrPattern = null;
        _sdrPattern?.Close(); _sdrPattern = null;
        _ddc?.Dispose(); _ddc = null;
        _session.Save();
    }

    private static AdvancedColorStatus? Acs() => AdvancedColorInfo.QueryAll().FirstOrDefault();

    private void ShowHdrScene(IEnumerable<ScenePatch> patches, double bgNits = 0)
    {
        _sdrPattern?.Hide();
        if (_hdrPattern is null || _hdrPattern.IsDisposed)
        {
            _hdrPattern = new PatternForm(_screen.Bounds);
            _hdrPattern.Show();
        }
        _hdrPattern.Show();
        _hdrPattern.SetScene(patches, bgNits);
        Activate(); // keep keyboard focus on the wizard
    }

    private SdrPatternForm SdrPattern()
    {
        _hdrPattern?.Hide();
        if (_sdrPattern is null || _sdrPattern.IsDisposed)
        {
            _sdrPattern = new SdrPatternForm(_screen.Bounds);
            _sdrPattern.Show();
        }
        _sdrPattern.Show();
        Activate();
        return _sdrPattern;
    }

    private void EnterStep(Step step)
    {
        _step = step;
        _pollTimer.Stop();
        _countAnswer = -1;

        switch (step)
        {
            case Step.HdrPrep:
                _title.Text = "Крок 1/12 — Підготовка HDR";
                _instruction.Text =
                    "Увімкніть HDR (Win+Alt+B), встановіть на моніторі пресет Smart HDR = «DisplayHDR 400» (OSD → Color).\n" +
                    "Вимкніть нічне світло/f.lux. Кімнату бажано затемнити.\n" +
                    "Статус оновлюється автоматично. Enter — далі. S — пропустити HDR-секцію. Esc — вихід.";
                _rec = _session.AddStep("hdr-prep", "HDR preparation");
                SnapshotState(_rec);
                _pollTimer.Start();
                PollState();
                break;

            case Step.MaxTml:
                _title.Text = "Крок 2/12 — Пік яскравості (MaxTML, вікно 10%)";
                _instruction.Text =
                    "У центрі два поля: ліве — максимальний сигнал (10000 nits), праве — регульоване.\n" +
                    "СТРІЛКИ: ↑/↓ грубо, →/← точно. Збільшуйте праве, ДОКИ МЕЖА МІЖ ПОЛЯМИ НЕ ЗНИКНЕ.\n" +
                    "Щойно межа зникла — не йдіть далі вгору, натисніть Enter.";
                _adjustPq = Tf.NitsToPq(300);
                _rec = _session.AddStep("max-tml", "Peak luminance 10% window");
                RenderMaxTml(fullFrame: false);
                break;

            case Step.MaxFullFrame:
                _title.Text = "Крок 3/12 — Пік яскравості на весь екран (MaxFFTML)";
                _instruction.Text =
                    "Те саме, але поля займають майже весь екран (перевірка просадки на повній площі).\n" +
                    "↑/↓ грубо, →/← точно. Коли межа зникне — Enter.";
                _adjustPq = Tf.NitsToPq(Math.Min(double.IsNaN(_maxTmlNits) ? 300 : _maxTmlNits - 60, 400));
                _rec = _session.AddStep("max-ff-tml", "Peak luminance full frame");
                RenderMaxTml(fullFrame: true);
                break;

            case Step.MinTml:
                _title.Text = "Крок 4/12 — Рівень чорного (MinTML)";
                _instruction.Text =
                    "Затемніть кімнату, дайте очам 20-30 секунд звикнути.\n" +
                    "У центрі темний квадрат на чорному тлі. ↓/↑ грубо, ←/→ точно.\n" +
                    "ЗМЕНШУЙТЕ яскравість квадрата, доки він ПОВНІСТЮ не зіллється з фоном — тоді Enter.";
                _adjustPq = Tf.NitsToPq(0.5);
                _rec = _session.AddStep("min-tml", "Black level threshold");
                RenderMinTml();
                break;

            case Step.ShadowLadder:
                _title.Text = "Крок 5/12 — Градації в тінях";
                _instruction.Text =
                    "8 квадратів зі зростаючою яскравістю (0.01→5 nits) на чорному тлі, зліва направо.\n" +
                    "Порахуйте, СКІЛЬКИ квадратів ви розрізняєте (хоч ледь-ледь).\n" +
                    "Натисніть цифру 0–8, потім Enter.";
                _rec = _session.AddStep("shadow-ladder", "Near-black gradation");
                _rec.Params["nits"] = _shadowNits;
                ShowLadder(_shadowNits);
                break;

            case Step.ClipLadder:
                _title.Text = "Крок 6/12 — Градації біля піку";
                _instruction.Text =
                    "8 квадратів 300→600 nits зліва направо.\n" +
                    "Порахуйте, скільки РІЗНИХ рівнів яскравості ви бачите (квадрати, що злилися в один — рахуються як один).\n" +
                    "Натисніть цифру 1–8, потім Enter.";
                _rec = _session.AddStep("clip-ladder", "Near-peak gradation");
                _rec.Params["nits"] = _clipNits;
                ShowLadder(_clipNits);
                break;

            case Step.SdrPrep:
                _title.Text = "Крок 7/12 — Підготовка SDR";
                _instruction.Text =
                    "Тепер ВИМКНІТЬ HDR (Win+Alt+B). Статус оновлюється автоматично.\n" +
                    "На моніторі встановіть Preset Mode = Standard, Brightness/Contrast за замовчуванням.\n" +
                    "Enter — далі (коли HDR вимкнеться). F — завершити без SDR-секції.";
                _rec = _session.AddStep("sdr-prep", "SDR preparation");
                _hdrPattern?.Hide();
                _pollTimer.Start();
                PollState();
                break;

            case Step.Gamma50:
            case Step.Gamma25:
            case Step.Gamma75:
            {
                double density = _step switch { Step.Gamma25 => 0.25, Step.Gamma75 => 0.75, _ => 0.5 };
                int idx = _step switch { Step.Gamma50 => 8, Step.Gamma25 => 9, _ => 10 };
                _title.Text = $"Крок {idx}/12 — Гамма панелі (шахівниця {density * 100:F0}%)";
                _instruction.Text =
                    "Зліва — дрібна шахівниця, справа — суцільний сірий. ВІДІЙДІТЬ НА 2-3 МЕТРИ або примружтесь.\n" +
                    "↑/↓ = ±5, →/← = ±1. Підберіть сірий так, щоб ОБИДВА поля мали ОДНАКОВУ яскравість.\n" +
                    "Коли зрівнялись — Enter.";
                int initial = (int)(255 * Math.Pow(density, 1 / 2.2));
                _rec = _session.AddStep($"gamma-{density * 100:F0}", $"Checkerboard gamma match {density}");
                _rec.Params["density"] = density;
                var f = SdrPattern();
                f.SetCheckerboard(density, initial);
                UpdateValueLabel();
                break;
            }

            case Step.GrayTint:
                _title.Text = "Крок 11/12 — Нейтральність сірого (DDC/CI)";
                _instruction.Text =
                    "Екран залито сірим. Порівняйте з білим аркушем паперу при денному світлі.\n" +
                    "F1/F2 = Червоний − / +     F3/F4 = Зелений − / +     F5/F6 = Синій − / +     Home = скинути.\n" +
                    "Приберіть відтінок (рожевий/зелений/синюшний), щоб сірий став нейтральним. Enter — готово.";
                _rec = _session.AddStep("gray-tint", "Grayscale neutrality via DDC gains");
                SetupDdcForTint();
                SdrPattern().SetSolidGray(180);
                UpdateValueLabel();
                break;

            case Step.ContrastBars:
                _title.Text = "Крок 12/12 — Контраст без клипінгу білого";
                _instruction.Text =
                    "На білому тлі 5 ледь темніших смуг (коди 250–254).\n" +
                    "↑/↓ регулюють Contrast монітора через DDC/CI.\n" +
                    "Знайдіть НАЙВИЩИЙ контраст, при якому ВСІ 5 смуг ще розрізняються. Enter — готово.";
                _rec = _session.AddStep("contrast-bars", "Contrast white clipping");
                _ddc ??= DdcCi.Open(_hmonitor);
                _ddcContrast0 = _ddc?.Read(DdcCi.VcpContrast)?.Current ?? 75;
                _rec.Params["initialContrast"] = _ddcContrast0;
                SdrPattern().SetContrastBars();
                UpdateValueLabel();
                break;

            case Step.GenerateProfiles:
                _title.Text = "Фінал — Генерація ICC-профілів (MHC2)";
                _instruction.Text =
                    "H — увімкнути/перезапустити HDR прямо звідси.   O — відкрити повзунок SDR-яскравості (ціль: 31 = 204 nits).\n" +
                    "1 — профіль «gamma 2.2 fix» (виправляє вицвілий SDR у HDR-режимі) + ваші виміряні пік/чорний. РЕКОМЕНДОВАНО.\n" +
                    "2 — «neutral» профіль (тільки пік/чорний для ігор, без гамма-фіксу) — для порівняння.\n" +
                    "Після 1/2 HDR перезапуститься автоматично. Enter — завершити. Відкат: colorcpl.exe.";
                _sdrPattern?.Hide();
                _hdrPattern?.Hide();
                _rec = _session.AddStep("generate", "Profile generation");
                _pollTimer.Start();
                PollState();
                break;

            case Step.Done:
                FinishSession();
                break;
        }
    }

    // ---------- rendering helpers ----------

    private void RenderMaxTml(bool fullFrame)
    {
        double refNits = 10000;
        double adjNits = Tf.PqToNits(_adjustPq);
        List<ScenePatch> scene = fullFrame
            ? [ScenePatch.Gray(0.01, 0.01, 0.49, 0.98, refNits), ScenePatch.Gray(0.50, 0.01, 0.49, 0.98, adjNits)]
            : [ScenePatch.Gray(0.335, 0.35, 0.165, 0.3, refNits), ScenePatch.Gray(0.50, 0.35, 0.165, 0.3, adjNits)];
        ShowHdrScene(scene);
        UpdateValueLabel();
    }

    private void RenderMinTml()
    {
        double adjNits = Tf.PqToNits(_adjustPq);
        ShowHdrScene([ScenePatch.Gray(0.4, 0.35, 0.2, 0.3, adjNits)]);
        UpdateValueLabel();
    }

    private void ShowLadder(double[] nits)
    {
        var scene = new List<ScenePatch>();
        double w = 0.09, gap = 0.115;
        for (int i = 0; i < nits.Length; i++)
            scene.Add(ScenePatch.Gray(0.04 + i * gap, 0.4, w, 0.2, nits[i]));
        ShowHdrScene(scene);
    }

    private void SetupDdcForTint()
    {
        _ddc ??= DdcCi.Open(_hmonitor);
        if (_ddc is null) { _instruction.Text += "\n⚠ DDC/CI недоступний — пропустіть (Enter)."; return; }
        _ddcGainR0 = _ddc.Read(DdcCi.VcpGainRed)?.Current ?? 100;
        _ddcGainG0 = _ddc.Read(DdcCi.VcpGainGreen)?.Current ?? 100;
        _ddcGainB0 = _ddc.Read(DdcCi.VcpGainBlue)?.Current ?? 100;
        _rec!.Params["initialGains"] = new[] { _ddcGainR0, _ddcGainG0, _ddcGainB0 };
        _rec.Notes = "Для регулювання RGB gains монітор має бути в Custom Color preset. " +
                     "Якщо клавіші не діють — OSD: Color → Preset Modes → Custom Color.";
    }

    // ---------- state polling ----------

    private void PollState()
    {
        var acs = Acs();
        if (_step == Step.HdrPrep)
        {
            _value.Text = acs is null
                ? "Не вдалося прочитати стан дисплея"
                : $"HDR: {(acs.Enabled ? "УВІМКНЕНО ✓" : "вимкнено ✗")}   {acs.BitsPerColorChannel}-bit   SDR white: {acs.SdrWhiteLevelNits:F0} nits";
        }
        else if (_step == Step.SdrPrep)
        {
            _value.Text = acs is null ? "?" : (acs.Enabled ? "HDR ще увімкнено — натисніть Win+Alt+B" : "HDR вимкнено ✓ — Enter");
        }
        else if (_step == Step.GenerateProfiles)
        {
            if (acs is null) { _value.Text = "?"; return; }
            if (!acs.Enabled) { _value.Text = "HDR вимкнено — натисніть H, щоб увімкнути."; return; }
            bool whiteOk = Math.Abs(acs.SdrWhiteLevelNits - 204) <= 6;
            _value.Text = whiteOk
                ? $"HDR ✓  SDR white = {acs.SdrWhiteLevelNits:F0} nits ✓ — все готово, тисніть 1."
                : $"HDR ✓  SDR white = {acs.SdrWhiteLevelNits:F0} nits — рекомендую O → повзунок на 31 (204), потім 1.";
        }
    }

    private void SnapshotState(StepRecord rec)
    {
        var acs = AdvancedColorInfo.QueryAll();
        rec.Result["advancedColor"] = acs.Select(a => new
        {
            a.TargetId, a.SourceId, a.Supported, a.Enabled, a.ColorEncoding, a.BitsPerColorChannel, a.SdrWhiteLevelNits
        }).ToList<object>();

        var edid = Edid.ReadForMonitor("DELD175") ?? Edid.ReadForMonitor("DEL");
        if (edid is not null)
        {
            rec.Result["edid"] = new
            {
                edid.MonitorId,
                red = new[] { edid.Red.x, edid.Red.y },
                green = new[] { edid.Green.x, edid.Green.y },
                blue = new[] { edid.Blue.x, edid.Blue.y },
                white = new[] { edid.White.x, edid.White.y },
                hdrMaxLum = edid.HdrMaxLuminance,
                hdrMaxFrameAvg = edid.HdrMaxFrameAvgLuminance,
                hdrMinLum = edid.HdrMinLuminance,
            };
            _session.MonitorState["edid"] = rec.Result["edid"];
        }

        using var ddc = DdcCi.Open(_hmonitor);
        if (ddc is not null)
        {
            var snap = ddc.Snapshot().ToDictionary(
                kv => kv.Key,
                kv => (object?)(kv.Value is null ? null : new { kv.Value.Current, kv.Value.Maximum }));
            rec.Result["ddcSnapshot"] = snap;
            _session.MonitorState["ddcInitial"] = snap;
            _session.MonitorState["ddcCaps"] = ddc.ReadCapabilities();
        }
        _session.Save();
    }

    // ---------- input ----------

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (HandleKey(keyData)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private bool HandleKey(Keys key)
    {
        switch (_step)
        {
            case Step.HdrPrep:
                if (key == Keys.Enter) { ConcludePrep(); EnterStep(Step.MaxTml); return true; }
                if (key == Keys.S) { _rec!.Notes = "HDR section skipped"; EnterStep(Step.SdrPrep); return true; }
                break;

            case Step.MaxTml:
            case Step.MaxFullFrame:
            case Step.MinTml:
            {
                double coarse = 0.010, fine = 0.002;
                double delta = key switch
                {
                    Keys.Up => coarse, Keys.Down => -coarse,
                    Keys.Right => fine, Keys.Left => -fine,
                    _ => 0
                };
                if (delta != 0)
                {
                    _adjustPq = Math.Clamp(_adjustPq + delta, 0, 1);
                    if (_step == Step.MinTml) RenderMinTml(); else RenderMaxTml(_step == Step.MaxFullFrame);
                    return true;
                }
                if (key == Keys.Enter) { ConcludeAdjust(); return true; }
                break;
            }

            case Step.ShadowLadder:
            case Step.ClipLadder:
            {
                if (key >= Keys.D0 && key <= Keys.D9) { _countAnswer = key - Keys.D0; UpdateValueLabel(); return true; }
                if (key >= Keys.NumPad0 && key <= Keys.NumPad9) { _countAnswer = key - Keys.NumPad0; UpdateValueLabel(); return true; }
                if (key == Keys.Enter && _countAnswer >= 0) { ConcludeCount(); return true; }
                break;
            }

            case Step.SdrPrep:
                if (key == Keys.Enter)
                {
                    var acs = Acs();
                    if (acs is not null && acs.Enabled) return true; // still HDR — ignore
                    EnterStep(Step.Gamma50);
                    return true;
                }
                if (key == Keys.F) { EnterStep(Step.GenerateProfiles); return true; }
                break;

            case Step.Gamma50:
            case Step.Gamma25:
            case Step.Gamma75:
            {
                int delta = key switch
                {
                    Keys.Up => 5, Keys.Down => -5,
                    Keys.Right => 1, Keys.Left => -1,
                    _ => 0
                };
                if (delta != 0 && _sdrPattern is not null)
                {
                    _sdrPattern.MatchCode = Math.Clamp(_sdrPattern.MatchCode + delta, 0, 255);
                    _sdrPattern.Invalidate();
                    UpdateValueLabel();
                    return true;
                }
                if (key == Keys.Enter) { ConcludeGamma(); return true; }
                break;
            }

            case Step.GrayTint:
            {
                if (_ddc is not null)
                {
                    (byte code, int d)? adj = key switch
                    {
                        Keys.F1 => (DdcCi.VcpGainRed, -1), Keys.F2 => (DdcCi.VcpGainRed, +1),
                        Keys.F3 => (DdcCi.VcpGainGreen, -1), Keys.F4 => (DdcCi.VcpGainGreen, +1),
                        Keys.F5 => (DdcCi.VcpGainBlue, -1), Keys.F6 => (DdcCi.VcpGainBlue, +1),
                        _ => null
                    };
                    if (adj is not null)
                    {
                        var cur = _ddc.Read(adj.Value.code);
                        if (cur is not null)
                            _ddc.Write(adj.Value.code, (uint)Math.Clamp((int)cur.Current + adj.Value.d, 0, (int)cur.Maximum));
                        UpdateValueLabel();
                        return true;
                    }
                    if (key == Keys.Home)
                    {
                        _ddc.Write(DdcCi.VcpGainRed, _ddcGainR0);
                        _ddc.Write(DdcCi.VcpGainGreen, _ddcGainG0);
                        _ddc.Write(DdcCi.VcpGainBlue, _ddcGainB0);
                        UpdateValueLabel();
                        return true;
                    }
                }
                if (key == Keys.Enter) { ConcludeTint(); return true; }
                break;
            }

            case Step.ContrastBars:
            {
                if (_ddc is not null && (key == Keys.Up || key == Keys.Down))
                {
                    var cur = _ddc.Read(DdcCi.VcpContrast);
                    if (cur is not null)
                        _ddc.Write(DdcCi.VcpContrast, (uint)Math.Clamp((int)cur.Current + (key == Keys.Up ? 1 : -1), 0, (int)cur.Maximum));
                    UpdateValueLabel();
                    return true;
                }
                if (key == Keys.Enter) { ConcludeContrast(); return true; }
                break;
            }

            case Step.GenerateProfiles:
                if (key == Keys.D1 || key == Keys.NumPad1) { GenerateProfile(gammaFix: true); return true; }
                if (key == Keys.D2 || key == Keys.NumPad2) { GenerateProfile(gammaFix: false); return true; }
                if (key == Keys.O)
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "ms-settings:display-hdr", UseShellExecute = true });
                    return true;
                }
                if (key == Keys.H)
                {
                    _value.Text = "Перезапускаю HDR (екран блимне)...";
                    Application.DoEvents();
                    bool ok = AdvancedColorInfo.RestartHdr();
                    _value.Text = ok ? "HDR активний ✓" : "Не вдалося — Win+Alt+B вручну.";
                    return true;
                }
                if (key == Keys.Enter) { EnterStep(Step.Done); return true; }
                break;
        }

        if (key == Keys.Escape) { Close(); return true; }
        return false;
    }

    private void UpdateValueLabel()
    {
        switch (_step)
        {
            case Step.MaxTml:
            case Step.MaxFullFrame:
            case Step.MinTml:
                _value.Text = $"Поточне значення: {Tf.PqToNits(_adjustPq):F3} nits (PQ {_adjustPq:F4})";
                break;
            case Step.ShadowLadder:
            case Step.ClipLadder:
                _value.Text = _countAnswer < 0 ? "Введіть цифру..." : $"Ваша відповідь: {_countAnswer}. Enter — підтвердити.";
                break;
            case Step.Gamma50:
            case Step.Gamma25:
            case Step.Gamma75:
                _value.Text = $"Сірий код: {_sdrPattern?.MatchCode}";
                break;
            case Step.GrayTint:
                if (_ddc is not null)
                    _value.Text = $"R={_ddc.Read(DdcCi.VcpGainRed)?.Current} G={_ddc.Read(DdcCi.VcpGainGreen)?.Current} B={_ddc.Read(DdcCi.VcpGainBlue)?.Current}";
                break;
            case Step.ContrastBars:
                if (_ddc is not null)
                    _value.Text = $"Contrast = {_ddc.Read(DdcCi.VcpContrast)?.Current}";
                break;
        }
    }

    // ---------- conclusions ----------

    private void ConcludePrep()
    {
        var acs = Acs();
        _rec!.Result["hdrEnabledAtStart"] = acs?.Enabled;
        _session.Save();
    }

    private void ConcludeAdjust()
    {
        double nits = Tf.PqToNits(_adjustPq);
        _rec!.Result["pq"] = _adjustPq;
        _rec.Result["nits"] = nits;
        switch (_step)
        {
            case Step.MaxTml:
                _maxTmlNits = nits;
                _session.Conclusions["maxTML_nits"] = nits;
                EnterStep(Step.MaxFullFrame);
                break;
            case Step.MaxFullFrame:
                _maxFfNits = nits;
                _session.Conclusions["maxFFTML_nits"] = nits;
                EnterStep(Step.MinTml);
                break;
            case Step.MinTml:
                _minTmlNits = nits;
                _session.Conclusions["minTML_nits"] = nits;
                EnterStep(Step.ShadowLadder);
                break;
        }
        _session.Save();
    }

    private void ConcludeCount()
    {
        _rec!.Result["visibleCount"] = _countAnswer;
        if (_step == Step.ShadowLadder)
        {
            int invisible = _shadowNits.Length - _countAnswer;
            _rec.Result["shadowFloorNits"] = _countAnswer >= _shadowNits.Length ? 0.0 : _shadowNits[Math.Max(invisible - 1, 0)];
            EnterStep(Step.ClipLadder);
        }
        else
        {
            _rec.Result["distinctNearPeak"] = _countAnswer;
            EnterStep(Step.SdrPrep);
        }
        _session.Save();
    }

    private void ConcludeGamma()
    {
        double density = _step switch { Step.Gamma25 => 0.25, Step.Gamma75 => 0.75, _ => 0.5 };
        int code = _sdrPattern?.MatchCode ?? 0;
        double gamma = Math.Log(density) / Math.Log(code / 255.0);
        _gammaMatches[density] = code;
        _rec!.Result["matchedCode"] = code;
        _rec.Result["impliedGamma"] = gamma;
        _session.Save();
        EnterStep(_step switch { Step.Gamma50 => Step.Gamma25, Step.Gamma25 => Step.Gamma75, _ => Step.GrayTint });
        if (_step == Step.GrayTint && _gammaMatches.Count == 3)
        {
            double avg = _gammaMatches.Select(kv => Math.Log(kv.Key) / Math.Log(kv.Value / 255.0)).Average();
            _session.Conclusions["panelGammaSdr"] = avg;
            _session.Save();
        }
    }

    private void ConcludeTint()
    {
        if (_ddc is not null)
        {
            _rec!.Result["finalGains"] = new[]
            {
                _ddc.Read(DdcCi.VcpGainRed)?.Current ?? 0,
                _ddc.Read(DdcCi.VcpGainGreen)?.Current ?? 0,
                _ddc.Read(DdcCi.VcpGainBlue)?.Current ?? 0,
            };
        }
        _session.Save();
        EnterStep(Step.ContrastBars);
    }

    private void ConcludeContrast()
    {
        if (_ddc is not null)
            _rec!.Result["finalContrast"] = _ddc.Read(DdcCi.VcpContrast)?.Current;
        _session.Save();
        EnterStep(Step.GenerateProfiles);
    }

    private void GenerateProfile(bool gammaFix)
    {
        var acs = Acs();
        if (acs is null || !acs.Enabled)
        {
            _value.Text = "HDR вимкнено! Win+Alt+B, потім спробуйте знову.";
            return;
        }

        double w = acs.SdrWhiteLevelNits;
        double maxL = double.IsNaN(_maxTmlNits) ? 450 : _maxTmlNits;
        double minL = double.IsNaN(_minTmlNits) ? 0 : _minTmlNits;

        var edid = Edid.ReadForMonitor("DELD175") ?? Edid.ReadForMonitor("DEL");
        var spec = new Mhc2ProfileSpec
        {
            Description = gammaFix
                ? $"HdrScope G2724D HDR gamma2.2 fix (W={w:F0} peak={maxL:F0})"
                : $"HdrScope G2724D HDR neutral (peak={maxL:F0})",
            MinLuminanceNits = minL,
            MaxLuminanceNits = maxL,
            RegammaLut = gammaFix ? Tf.BuildSdrGammaFixLut(4096, w) : null,
            RedPrimary = edid?.Red ?? (0.640, 0.330),
            GreenPrimary = edid?.Green ?? (0.300, 0.600),
            BluePrimary = edid?.Blue ?? (0.150, 0.060),
            WhitePoint = edid?.White ?? (0.3127, 0.3290),
            LumiNits = w,
        };

        string name = gammaFix ? "HdrScope-G2724D-gamma22fix.icm" : "HdrScope-G2724D-neutral.icm";
        string path = Path.Combine(_session.OutputDirectory, name);
        Mhc2IccWriter.Write(path, spec);

        string status = ProfileInstaller.InstallAndAssociateHdr(path, acs.AdapterId, acs.SourceId);

        var rec = _session.AddStep(gammaFix ? "profile-gammafix" : "profile-neutral", "ICC profile generated");
        rec.Params["sdrWhiteNits"] = w;
        rec.Params["maxLuminance"] = maxL;
        rec.Params["minLuminance"] = minL;
        rec.Result["path"] = path;
        rec.Result["installStatus"] = status;
        _session.Save();

        _value.Text = "Профіль встановлено, перезапускаю HDR (екран блимне)...";
        Application.DoEvents();
        bool restarted = AdvancedColorInfo.RestartHdr();
        _value.Text = restarted ? $"✓ {name} застосовано. Enter — завершити." : status + " Потім Win+Alt+B ×2.";
        rec.Result["hdrRestarted"] = restarted;
        _session.Save();
    }

    private void FinishSession()
    {
        _session.Conclusions["finishedUtc"] = DateTime.UtcNow;
        _session.Save();
        WriteReport();
        MessageBox.Show(this,
            $"Сесію збережено:\n{_session.JsonPath}\n\nПередайте JSON-файл Claude для аналізу і подальших рекомендацій.",
            "HdrScope — готово");
        Close();
    }

    private void WriteReport()
    {
        var p = Path.Combine(_session.OutputDirectory, $"report-{_session.StartedUtc:yyyyMMdd-HHmmss}.md");
        var c = _session.Conclusions;
        string Get(string k) => c.TryGetValue(k, out var v) ? v?.ToString() ?? "—" : "—";
        File.WriteAllText(p, $"""
            # HdrScope — звіт калібрування {_session.StartedUtc:yyyy-MM-dd HH:mm} UTC

            | Параметр | Значення |
            |---|---|
            | MaxTML (пік, 10% вікно) | {Get("maxTML_nits")} nits |
            | MaxFFTML (пік, повний кадр) | {Get("maxFFTML_nits")} nits |
            | MinTML (чорний) | {Get("minTML_nits")} nits |
            | Гамма панелі (SDR) | {Get("panelGammaSdr")} |

            ## Що робити з цими числами
            - В іграх з HDR-калібруванням (HGIG): peak brightness = MaxTML, як виміряно вище.
            - Повний лог: `{Path.GetFileName(_session.JsonPath)}` — передайте його Claude для аналізу.
            """);
    }
}

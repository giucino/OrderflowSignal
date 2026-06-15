using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using ATAS.Indicators;
using OFT.Attributes;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;

namespace OrderflowSignal
{
    [DisplayName("Orderflow Signal (Multi-Condition)")]
    [HelpLink("https://giucino.github.io/OrderflowSignal/OrderflowSignal_Doku.html")]
    [Description("Stufe 2 / Trigger — verrechnet mehrere Orderflow-Bedingungen (Delta, " +
                 "Volumen, Footprint-Absorption, VWAP-Lage) zu einem richtungsgewichteten " +
                 "Bull/Bear-Score. Schwellen per Perzentil-Auto-Kalibrierung (rollend, mit " +
                 "Freeze) -> instrument-/TF-agnostisch. Absorption footprint-basiert -> laeuft " +
                 "auf Tick UND Renko. Marker an der Kerze + HUD. Rein informativ.")]
    public class OrderflowSignal : Indicator
    {
        // ─────────────────────────────────────────────────────────────────
        //  EINSTELLUNGEN — Allgemein
        // ─────────────────────────────────────────────────────────────────
        // Kalibrierungs-Fenster: Anzahl Bars, ueber die die Perzentil-Schwellen
        // bestimmt werden (= semaPHoreks "Last N Bars").
        private int _lookback = 50;

        private int _signalThreshold = 50;   // Mindest-Gewichtssumme der dominanten Seite
        private int _signalCooldownBars = 3; // Mindestabstand zwischen Markern (0 = aus)

        private bool _showHud = true;
        private bool _showMarkers = true;
        private bool _showCalibration = true;
        private bool _freezeCalibration = false;

        // ─────────────────────────────────────────────────────────────────
        //  EINSTELLUNGEN — Kalibrierung (Perzentil)
        // ─────────────────────────────────────────────────────────────────
        // Globaler Perzentil-Wert (Basic). Schwelle = dieses Perzentil der letzten
        // N Bars; ein Bar ist "aktiv", wenn seine Metrik >= Schwelle (also in den
        // oberen (100 - P) % liegt).
        private int _globalPercentile = 85;

        // Advanced: pro Bedingung eigenen Perzentil-Wert verwenden.
        private bool _useAdvancedPercentiles = false;
        private int _volPercentile = 85;
        private int _deltaPercentile = 85;
        private int _absPercentile = 85;

        // ─────────────────────────────────────────────────────────────────
        //  EINSTELLUNGEN — Bedingungen (aktiv? + Gewicht)
        // ─────────────────────────────────────────────────────────────────
        // 1) Delta signifikant. Richtung = Vorzeichen des Candle-Deltas.
        private bool _deltaEnabled = true;
        private int _deltaWeight = 30;

        // 2) Volumen-Spike. Richtung = Kerzenkoerper (Close vs Open).
        private bool _volEnabled = true;
        private int _volWeight = 20;

        // 3) Footprint-Absorption: groesstes Level-Delta (Ask-Bid je Preislevel)
        //    ueber der Schwelle -> der Aggressor wurde absorbiert. Richtung =
        //    REVERSAL gegen den Aggressor. Range-unabhaengig -> auch auf Renko.
        private bool _absEnabled = true;
        private int _absWeight = 25;

        // 4) VWAP-Lage als Bias (keine Kalibrierung). Close ueber/unter Session-VWAP.
        private bool _vwapEnabled = true;
        private int _vwapWeight = 25;

        // ─────────────────────────────────────────────────────────────────
        //  EINSTELLUNGEN — Darstellung / Farben
        // ─────────────────────────────────────────────────────────────────
        private int _fontSize = 14;
        private int _offsetX = 20;
        private int _offsetY = 20;
        private bool _topLeft = false;
        private int _markerTickOffset = 4;

        private Color _colorBull = Color.FromArgb(230, 50, 205, 80);
        private Color _colorBear = Color.FromArgb(230, 225, 60, 60);
        private Color _colorNeutral = Color.FromArgb(230, 200, 170, 60);
        private Color _colorBackground = Color.FromArgb(180, 18, 20, 26);
        private Color _colorText = Color.FromArgb(235, 220, 225, 235);
        private Color _colorWarn = Color.FromArgb(235, 235, 150, 45);
        private Color _colorDim = Color.FromArgb(190, 140, 150, 170);

        // ─────────────────────────────────────────────────────────────────
        //  STATE
        // ─────────────────────────────────────────────────────────────────
        // Pro Bar gespeicherte Metriken (fuer das rollende Perzentil-Fenster).
        private readonly List<decimal> _volArr = new();   // Candle-Volumen
        private readonly List<decimal> _absDArr = new();  // |Candle-Delta|
        private readonly List<decimal> _mldArr = new();   // |max. Level-Delta|

        // Session-VWAP-Akkumulation der abgeschlossenen Bars (Reset je Session).
        private decimal _cumPv, _cumVol;

        private int _lastProcessedBar = -1;
        private readonly List<int> _signals = new();
        private int _lastSignalBar = -1;

        // Zuletzt berechnete Schwellen (fuer HUD + Freeze-Snapshot).
        private decimal _liveVolThr, _liveDeltaThr, _liveAbsThr;
        // Eingefrorene Schwellen (gehalten, solange Freeze aktiv ist).
        private decimal _frzVol, _frzDelta, _frzAbs;

        private string _lastRenderKey = "";

        // Gerenderte HUD-Werte.
        private int _hudBull, _hudBear, _hudSignal;
        private string _hudTags = "";
        private string _hudWarn = "";
        private string _chartLabel = "";

        private RenderFont _font = null!;
        private RenderFont _fontBig = null!;
        private RenderFont _fontMarker = null!;

        // ─────────────────────────────────────────────────────────────────
        //  PROPERTIES — Allgemein
        // ─────────────────────────────────────────────────────────────────
        [Display(Name = "Lookback (Bars)", GroupName = "Allgemein", Order = 1,
            Description = "Kalibrierungs-Fenster: ueber so viele Bars werden die Perzentil-Schwellen bestimmt.")]
        [Range(10, 1000)]
        public int Lookback { get => _lookback; set { _lookback = Math.Max(10, value); RecalculateValues(); } }

        [Display(Name = "Signal-Schwelle (Gewichtspunkte)", GroupName = "Allgemein", Order = 2,
            Description = "Mindest-Gewichtssumme der dominanten Seite, damit ein Marker feuert. " +
                          "Bei Default-Gewichten (Summe ~100) ist 50 = Mehrheit.")]
        [Range(0, 300)]
        public int SignalThreshold { get => _signalThreshold; set { _signalThreshold = Math.Max(0, value); RecalculateValues(); } }

        [Display(Name = "Signal-Cooldown (Bars)", GroupName = "Allgemein", Order = 3,
            Description = "Mindestabstand zwischen Markern. Rausch-Bremse fuer Tick-Charts. 0 = aus.")]
        [Range(0, 100)]
        public int SignalCooldownBars { get => _signalCooldownBars; set { _signalCooldownBars = Math.Max(0, value); RecalculateValues(); } }

        [Display(Name = "HUD anzeigen", GroupName = "Allgemein", Order = 4)]
        public bool ShowHud { get => _showHud; set { _showHud = value; RedrawChart(); } }

        [Display(Name = "Marker anzeigen", GroupName = "Allgemein", Order = 5)]
        public bool ShowMarkers { get => _showMarkers; set { _showMarkers = value; RedrawChart(); } }

        [Display(Name = "Kalibrierung im HUD zeigen", GroupName = "Allgemein", Order = 6)]
        public bool ShowCalibration { get => _showCalibration; set { _showCalibration = value; RedrawChart(); } }

        // ─────────────────────────────────────────────────────────────────
        //  PROPERTIES — Kalibrierung
        // ─────────────────────────────────────────────────────────────────
        [Display(Name = "Globaler Perzentil", GroupName = "Kalibrierung", Order = 10,
            Description = "Schwelle = dieses Perzentil der letzten N Bars. 85 = feuert in den oberen 15%.")]
        [Range(50, 99)]
        public int GlobalPercentile { get => _globalPercentile; set { _globalPercentile = Math.Clamp(value, 50, 99); RecalculateValues(); } }

        [Display(Name = "Advanced: Perzentil pro Bedingung", GroupName = "Kalibrierung", Order = 11,
            Description = "Aus = globaler Wert fuer alle. Ein = je Bedingung eigener Perzentil unten.")]
        public bool UseAdvancedPercentiles { get => _useAdvancedPercentiles; set { _useAdvancedPercentiles = value; RecalculateValues(); } }

        [Display(Name = "Freeze (Kalibrierung einfrieren)", GroupName = "Kalibrierung", Order = 12,
            Description = "Friert die aktuellen Schwellen ein (wie ein semaPHorek-Template). Aus = rollend live.")]
        public bool FreezeCalibration
        {
            get => _freezeCalibration;
            set
            {
                // Beim Einfrieren die zuletzt berechneten Live-Schwellen als Snapshot uebernehmen.
                if (value && !_freezeCalibration)
                {
                    _frzVol = _liveVolThr;
                    _frzDelta = _liveDeltaThr;
                    _frzAbs = _liveAbsThr;
                }
                _freezeCalibration = value;
                RecalculateValues();
            }
        }

        [Display(Name = "Perzentil Volumen", GroupName = "Kalibrierung", Order = 13)]
        [Range(50, 99)]
        public int VolPercentile { get => _volPercentile; set { _volPercentile = Math.Clamp(value, 50, 99); RecalculateValues(); } }

        [Display(Name = "Perzentil Delta", GroupName = "Kalibrierung", Order = 14)]
        [Range(50, 99)]
        public int DeltaPercentile { get => _deltaPercentile; set { _deltaPercentile = Math.Clamp(value, 50, 99); RecalculateValues(); } }

        [Display(Name = "Perzentil Absorption", GroupName = "Kalibrierung", Order = 15)]
        [Range(50, 99)]
        public int AbsPercentile { get => _absPercentile; set { _absPercentile = Math.Clamp(value, 50, 99); RecalculateValues(); } }

        // ─────────────────────────────────────────────────────────────────
        //  PROPERTIES — Bedingungen
        // ─────────────────────────────────────────────────────────────────
        [Display(Name = "Delta aktiv", GroupName = "Bedingung: Delta", Order = 20)]
        public bool DeltaEnabled { get => _deltaEnabled; set { _deltaEnabled = value; RecalculateValues(); } }

        [Display(Name = "Delta Gewicht", GroupName = "Bedingung: Delta", Order = 21)]
        [Range(0, 100)]
        public int DeltaWeight { get => _deltaWeight; set { _deltaWeight = value; RecalculateValues(); } }

        [Display(Name = "Volumen aktiv", GroupName = "Bedingung: Volumen", Order = 30)]
        public bool VolEnabled { get => _volEnabled; set { _volEnabled = value; RecalculateValues(); } }

        [Display(Name = "Volumen Gewicht", GroupName = "Bedingung: Volumen", Order = 31)]
        [Range(0, 100)]
        public int VolWeight { get => _volWeight; set { _volWeight = value; RecalculateValues(); } }

        [Display(Name = "Absorption aktiv", GroupName = "Bedingung: Absorption", Order = 40)]
        public bool AbsEnabled { get => _absEnabled; set { _absEnabled = value; RecalculateValues(); } }

        [Display(Name = "Absorption Gewicht", GroupName = "Bedingung: Absorption", Order = 41,
            Description = "Footprint-Absorption: groesstes Level-Delta ueber Schwelle. Richtung = Reversal gegen den Aggressor.")]
        [Range(0, 100)]
        public int AbsWeight { get => _absWeight; set { _absWeight = value; RecalculateValues(); } }

        [Display(Name = "VWAP aktiv", GroupName = "Bedingung: VWAP", Order = 50)]
        public bool VwapEnabled { get => _vwapEnabled; set { _vwapEnabled = value; RecalculateValues(); } }

        [Display(Name = "VWAP Gewicht", GroupName = "Bedingung: VWAP", Order = 51,
            Description = "Bias: Close ueber Session-VWAP = bullish, darunter = bearish. VWAP ankert taeglich (IsNewSession).")]
        [Range(0, 100)]
        public int VwapWeight { get => _vwapWeight; set { _vwapWeight = value; RecalculateValues(); } }

        // ─────────────────────────────────────────────────────────────────
        //  PROPERTIES — Darstellung / Farben
        // ─────────────────────────────────────────────────────────────────
        [Display(Name = "Schriftgroesse", GroupName = "Darstellung", Order = 60)]
        [Range(8, 30)]
        public int FontSize { get => _fontSize; set { _fontSize = Math.Clamp(value, 8, 30); BuildFonts(); RedrawChart(); } }

        [Display(Name = "Oben Links (aus = Oben Rechts)", GroupName = "Darstellung", Order = 61)]
        public bool TopLeft { get => _topLeft; set { _topLeft = value; RedrawChart(); } }

        [Display(Name = "Abstand X (px)", GroupName = "Darstellung", Order = 62)]
        [Range(0, 600)]
        public int OffsetX { get => _offsetX; set { _offsetX = value; RedrawChart(); } }

        [Display(Name = "Abstand Y (px)", GroupName = "Darstellung", Order = 63)]
        [Range(0, 600)]
        public int OffsetY { get => _offsetY; set { _offsetY = value; RedrawChart(); } }

        [Display(Name = "Marker-Abstand (Ticks)", GroupName = "Darstellung", Order = 64)]
        [Range(0, 100)]
        public int MarkerTickOffset { get => _markerTickOffset; set { _markerTickOffset = value; RedrawChart(); } }

        [Display(Name = "Farbe Bull", GroupName = "Farben", Order = 70)]
        public Color ColorBull { get => _colorBull; set { _colorBull = value; RedrawChart(); } }

        [Display(Name = "Farbe Bear", GroupName = "Farben", Order = 71)]
        public Color ColorBear { get => _colorBear; set { _colorBear = value; RedrawChart(); } }

        [Display(Name = "Farbe Neutral", GroupName = "Farben", Order = 72)]
        public Color ColorNeutral { get => _colorNeutral; set { _colorNeutral = value; RedrawChart(); } }

        [Display(Name = "Hintergrund", GroupName = "Farben", Order = 73)]
        public Color ColorBackground { get => _colorBackground; set { _colorBackground = value; RedrawChart(); } }

        // ─────────────────────────────────────────────────────────────────
        //  CTOR
        // ─────────────────────────────────────────────────────────────────
        public OrderflowSignal() : base(true)
        {
            EnableCustomDrawing = true;
            DrawAbovePrice = true;
            DataSeries[0].IsHidden = true;

            SubscribeToDrawingEvents(DrawingLayouts.Historical | DrawingLayouts.Final);
            BuildFonts();
        }

        private void BuildFonts()
        {
            _font = new RenderFont("Consolas", _fontSize);
            _fontBig = new RenderFont("Consolas", _fontSize + 4, FontStyle.Bold);
            _fontMarker = new RenderFont("Consolas", _fontSize + 6, FontStyle.Bold);
        }

        // ─────────────────────────────────────────────────────────────────
        //  HAUPTBERECHNUNG
        // ─────────────────────────────────────────────────────────────────
        protected override void OnCalculate(int bar, decimal value)
        {
            if (bar == 0)
                ResetState();

            int lastClosed = CurrentBar - 2;
            while (_lastProcessedBar < lastClosed)
            {
                _lastProcessedBar++;
                ProcessClosedBar(_lastProcessedBar);
            }

            if (bar != CurrentBar - 1)
                return;

            ComputeLive();

            string key = $"{_hudBull}|{_hudBear}|{_hudSignal}|{_hudTags}|{_hudWarn}|{_chartLabel}|" +
                         $"{_liveVolThr}|{_liveDeltaThr}|{_liveAbsThr}|{_freezeCalibration}|{CurrentBar}";
            if (key != _lastRenderKey)
            {
                _lastRenderKey = key;
                RedrawChart();
            }
        }

        private void ResetState()
        {
            _volArr.Clear();
            _absDArr.Clear();
            _mldArr.Clear();
            _cumPv = 0;
            _cumVol = 0;
            _lastProcessedBar = -1;
            _signals.Clear();
            _lastSignalBar = -1;
            // _frz* NICHT zuruecksetzen -> eingefrorene Kalibrierung ueberlebt Recalc.
            _hudBull = _hudBear = _hudSignal = 0;
            _hudTags = "";
            _hudWarn = "";
            _chartLabel = "";
            _lastRenderKey = "";
        }

        private void ProcessClosedBar(int bar)
        {
            var c = GetCandle(bar);
            if (c == null)
                return;

            // Metriken fuer das Perzentil-Fenster speichern.
            decimal signedMld = MaxLevelDeltaSigned(c);
            StoreMetric(_volArr, bar, c.Volume);
            StoreMetric(_absDArr, bar, Math.Abs(c.Delta));
            StoreMetric(_mldArr, bar, Math.Abs(signedMld));

            // Session-VWAP fortschreiben.
            if (IsNewSession(bar))
            {
                _cumPv = 0;
                _cumVol = 0;
            }
            _cumPv += BarPriceForVwap(c) * c.Volume;
            _cumVol += c.Volume;
            decimal vwap = _cumVol > 0 ? _cumPv / _cumVol : 0;

            var o = EvaluateBar(bar, c, signedMld, vwap, _cumVol > 0);
            int sig = DetermineSignal(o.Bull, o.Bear);
            if (sig != 0 && _lastSignalBar >= 0 && _signalCooldownBars > 0
                && (bar - _lastSignalBar) <= _signalCooldownBars)
                sig = 0;

            SetSignal(bar, sig);
            if (sig != 0)
                _lastSignalBar = bar;
        }

        private void ComputeLive()
        {
            int last = CurrentBar - 1;
            var c = GetCandle(last);
            if (c == null)
                return;

            decimal signedMld = MaxLevelDeltaSigned(c);

            decimal basePv = _cumPv, baseVol = _cumVol;
            if (IsNewSession(last)) { basePv = 0; baseVol = 0; }
            basePv += BarPriceForVwap(c) * c.Volume;
            baseVol += c.Volume;
            decimal liveVwap = baseVol > 0 ? basePv / baseVol : 0;

            var o = EvaluateBar(last, c, signedMld, liveVwap, baseVol > 0);
            int sig = DetermineSignal(o.Bull, o.Bear);
            if (sig != 0 && _lastSignalBar >= 0 && _signalCooldownBars > 0
                && (last - _lastSignalBar) <= _signalCooldownBars)
                sig = 0;
            SetSignal(last, sig);

            // Schwellen rollend halten, solange nicht eingefroren.
            if (!_freezeCalibration)
            {
                _frzVol = _liveVolThr;
                _frzDelta = _liveDeltaThr;
                _frzAbs = _liveAbsThr;
            }

            _hudBull = o.Bull;
            _hudBear = o.Bear;
            _hudSignal = sig;
            _hudTags = $"Δ{Arrow(o.DirDelta)} Vol{Arrow(o.DirVol)} Abs{Arrow(o.DirAbs)} VW{Arrow(o.DirVwap)}";
            _hudWarn = BuildWarning();
            _chartLabel = BuildChartLabel();
        }

        // ─────────────────────────────────────────────────────────────────
        //  BEDINGUNGS-AUSWERTUNG
        // ─────────────────────────────────────────────────────────────────
        private struct EvalOut
        {
            public int Bull, Bear;
            public int DirDelta, DirVol, DirAbs, DirVwap;
        }

        private EvalOut EvaluateBar(int bar, IndicatorCandle c, decimal signedMld, decimal vwap, bool vwapValid)
        {
            var o = new EvalOut();

            if (!Thresholds(bar, out decimal volThr, out decimal deltaThr, out decimal absThr))
                return o;

            _liveVolThr = volThr;
            _liveDeltaThr = deltaThr;
            _liveAbsThr = absThr;

            // 1) Delta signifikant.
            if (_deltaEnabled && deltaThr > 0 && Math.Abs(c.Delta) >= deltaThr)
            {
                o.DirDelta = Math.Sign(c.Delta);
                Accumulate(ref o, o.DirDelta, _deltaWeight);
            }

            // 2) Volumen-Spike (Richtung = Kerzenkoerper).
            if (_volEnabled && volThr > 0 && c.Volume >= volThr)
            {
                o.DirVol = c.Close > c.Open ? 1 : c.Close < c.Open ? -1 : 0;
                Accumulate(ref o, o.DirVol, _volWeight);
            }

            // 3) Footprint-Absorption (Reversal gegen den absorbierten Aggressor).
            if (_absEnabled && absThr > 0 && Math.Abs(signedMld) >= absThr)
            {
                o.DirAbs = -Math.Sign(signedMld);
                Accumulate(ref o, o.DirAbs, _absWeight);
            }

            // 4) VWAP-Lage (Bias, keine Kalibrierung).
            if (_vwapEnabled && vwapValid && vwap > 0)
            {
                o.DirVwap = c.Close > vwap ? 1 : c.Close < vwap ? -1 : 0;
                Accumulate(ref o, o.DirVwap, _vwapWeight);
            }

            return o;
        }

        private static void Accumulate(ref EvalOut o, int dir, int weight)
        {
            if (dir > 0) o.Bull += weight;
            else if (dir < 0) o.Bear += weight;
        }

        private int DetermineSignal(int bull, int bear)
        {
            if (bull >= _signalThreshold && bull > bear) return 1;
            if (bear >= _signalThreshold && bear > bull) return -1;
            return 0;
        }

        // Liefert die Schwellen fuer einen Bar. Eingefroren -> feste Snapshots,
        // sonst rollendes Perzentil ueber [bar-Lookback, bar-1]. false in der
        // Anlaufphase (zu wenig Historie).
        private bool Thresholds(int bar, out decimal volThr, out decimal deltaThr, out decimal absThr)
        {
            volThr = deltaThr = absThr = 0;

            if (_freezeCalibration && _frzVol > 0)
            {
                volThr = _frzVol;
                deltaThr = _frzDelta;
                absThr = _frzAbs;
                return true;
            }

            int start = bar - _lookback;
            if (start < 0)
                return false;

            volThr = Percentile(_volArr, start, _lookback, EffPercentile(_volPercentile));
            deltaThr = Percentile(_absDArr, start, _lookback, EffPercentile(_deltaPercentile));
            absThr = Percentile(_mldArr, start, _lookback, EffPercentile(_absPercentile));
            return true;
        }

        private double EffPercentile(int specific)
            => _useAdvancedPercentiles ? specific : _globalPercentile;

        // Perzentil (Nearest-Rank) ueber src[from .. from+count-1].
        private static decimal Percentile(List<decimal> src, int from, int count, double p)
        {
            if (count <= 0 || from < 0 || from + count > src.Count)
                return decimal.MaxValue; // keine valide Schwelle -> Bedingung feuert nicht

            var tmp = new decimal[count];
            for (int i = 0; i < count; i++)
                tmp[i] = src[from + i];
            Array.Sort(tmp);

            int rank = (int)Math.Ceiling(p / 100.0 * count); // 1-basiert
            rank = Math.Clamp(rank, 1, count);
            return tmp[rank - 1];
        }

        // Groesstes (betragsmaessig) Level-Delta (Ask-Bid je Preislevel) der Kerze.
        // Footprint-basiert -> range-unabhaengig (auch auf Renko gueltig).
        // Fallback ohne Cluster-Daten: das Candle-Delta als einzelnes Level.
        private static decimal MaxLevelDeltaSigned(IndicatorCandle c)
        {
            decimal best = 0;
            bool any = false;
            foreach (var pv in c.GetAllPriceLevels())
            {
                any = true;
                decimal d = pv.Ask - pv.Bid;
                if (Math.Abs(d) > Math.Abs(best))
                    best = d;
            }
            return any ? best : c.Delta;
        }

        private static decimal BarPriceForVwap(IndicatorCandle c)
            => c.VWAP > 0 ? c.VWAP : (c.High + c.Low + c.Close) / 3m;

        // ─────────────────────────────────────────────────────────────────
        //  SPEICHER-HELFER
        // ─────────────────────────────────────────────────────────────────
        private static void StoreMetric(List<decimal> list, int bar, decimal v)
        {
            while (list.Count <= bar)
                list.Add(0);
            list[bar] = v;
        }

        private void SetSignal(int bar, int v)
        {
            while (_signals.Count <= bar)
                _signals.Add(0);
            _signals[bar] = v;
        }

        private int GetSignal(int bar)
            => bar >= 0 && bar < _signals.Count ? _signals[bar] : 0;

        // ─────────────────────────────────────────────────────────────────
        //  HUD-HELFER
        // ─────────────────────────────────────────────────────────────────
        private static string Arrow(int dir) => dir > 0 ? "▲" : dir < 0 ? "▼" : "·";

        private string BuildChartLabel()
        {
            var ct = ChartInfo?.ChartType ?? "";
            var tf = ChartInfo?.TimeFrame ?? "";
            string s = (ct + " " + tf).Trim();
            return s.Length > 0 ? $"({s})" : "";
        }

        // Warnt nur noch bei Volumen-Bars: dort ist das Volumen je Bar konstant,
        // das Volumen-Perzentil also bedeutungslos. (Absorption ist jetzt
        // footprint-basiert und damit auch auf Range/Renko gueltig.)
        private string BuildWarning()
        {
            var ct = (ChartInfo?.ChartType ?? "").ToLowerInvariant();
            if (_volEnabled && ct.Contains("volume"))
                return "! Volumen-Bars: Volumen-Bedingung ungueltig";
            return "";
        }

        // ─────────────────────────────────────────────────────────────────
        //  RENDER
        // ─────────────────────────────────────────────────────────────────
        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            if (_font == null)
                return;

            if (_showMarkers)
                DrawMarkers(context);

            if (_showHud)
                DrawHud(context);
        }

        private void DrawMarkers(RenderContext context)
        {
            var pc = ChartInfo?.PriceChartContainer;
            if (pc == null)
                return;

            decimal tick = InstrumentInfo?.TickSize ?? 0m;
            decimal offset = tick * _markerTickOffset;
            int from = Math.Max(0, FirstVisibleBarNumber);
            int to = Math.Min(CurrentBar - 1, LastVisibleBarNumber);

            for (int b = from; b <= to; b++)
            {
                int s = GetSignal(b);
                if (s == 0)
                    continue;

                var c = GetCandle(b);
                if (c == null)
                    continue;

                decimal price = s > 0 ? c.Low - offset : c.High + offset;
                int x = pc.GetXByBarNumber(b);
                int y = pc.GetYByPrice(price, true);

                string glyph = s > 0 ? "▲" : "▼";
                var col = s > 0 ? _colorBull : _colorBear;
                var sz = context.MeasureString(glyph, _fontMarker);

                int drawY = s > 0 ? y : y - sz.Height;
                context.DrawString(glyph, _fontMarker, col, x - sz.Width / 2, drawY);
            }
        }

        private void DrawHud(RenderContext context)
        {
            var sigColor = _hudSignal > 0 ? _colorBull
                         : _hudSignal < 0 ? _colorBear
                         : _colorNeutral;

            string sigText = _hudSignal > 0 ? "LONG" : _hudSignal < 0 ? "SHORT" : "—";
            string header = $"ORDERFLOW {_chartLabel}".Trim();

            var lines = new List<(string text, Color col, RenderFont font)>
            {
                (header, sigColor, _fontBig),
                ($"▲ {_hudBull}    ▼ {_hudBear}", _colorText, _font),
                ($"SIGNAL: {sigText}", sigColor, _font),
                (_hudTags, _colorText, _font),
            };

            if (_showCalibration)
            {
                int p = _useAdvancedPercentiles ? 0 : _globalPercentile;
                string pTxt = _useAdvancedPercentiles ? "adv" : p.ToString();
                string frozen = _freezeCalibration ? " ❄" : "";
                lines.Add(($"Cal P{pTxt} N{_lookback}{frozen}", _colorDim, _font));
                lines.Add(($"V≥{_liveVolThr:0}  Δ≥{_liveDeltaThr:0}  A≥{_liveAbsThr:0}", _colorDim, _font));
            }

            if (_hudWarn.Length > 0)
                lines.Add((_hudWarn, _colorWarn, _font));

            int w = 0, h = 0;
            const int padX = 12, padY = 10, lineGap = 4;
            foreach (var ln in lines)
            {
                var sz = context.MeasureString(ln.text, ln.font);
                if (sz.Width > w) w = sz.Width;
                h += sz.Height + lineGap;
            }
            h += padY * 2 - lineGap;
            w += padX * 2;

            int posX = _topLeft ? _offsetX : context.ClipBounds.Width - w - _offsetX;
            int posY = _offsetY;

            var box = new Rectangle(posX, posY, w, h);
            context.FillRectangle(_colorBackground, box);
            context.DrawRectangle(new RenderPen(sigColor, 2), box);
            context.FillRectangle(sigColor, new Rectangle(posX, posY, 4, h));

            int y = posY + padY;
            foreach (var ln in lines)
            {
                var sz = context.MeasureString(ln.text, ln.font);
                context.DrawString(ln.text, ln.font, ln.col, posX + padX, y);
                y += sz.Height + lineGap;
            }
        }
    }
}

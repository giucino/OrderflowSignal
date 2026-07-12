using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using ATAS.Indicators;
using OFT.Attributes;
using OFT.Attributes.Editors;
using OFT.Rendering.Context;
using OFT.Rendering.Control;
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
        private bool _showReversalMarkers = true;
        private bool _showCalibration = true;
        private bool _freezeCalibration = false;

        // Nur Marker mit Score >= diesem Wert zeichnen. 60 = mind. 3 Bedingungen
        // ausgerichtet (filtert die schwachen 50/55-Zweier-Konfluenzen weg).
        private int _minMarkerScore = 60;

        // ─────────────────────────────────────────────────────────────────
        //  EINSTELLUNGEN — Kalibrierung (Perzentil)
        // ─────────────────────────────────────────────────────────────────
        // Globaler Perzentil-Wert (Basic). Schwelle = dieses Perzentil der letzten
        // N Bars; ein Bar ist "aktiv", wenn seine Metrik >= Schwelle (also in den
        // oberen (100 - P) % liegt). Default 95 = semaPHoreks erprobte Selektivitaet.
        private int _globalPercentile = 95;

        // Advanced: pro Bedingung eigenen Perzentil-Wert verwenden.
        private bool _useAdvancedPercentiles = false;
        private int _volPercentile = 85;
        private int _deltaPercentile = 85;
        private int _absPercentile = 85;

        // ─────────────────────────────────────────────────────────────────
        //  EINSTELLUNGEN — Bedingungen (aktiv? + Gewicht)
        // ─────────────────────────────────────────────────────────────────
        // Gewichte summieren sich als Default auf 100; der Score wird auf den
        // Anteil der AKTIVEN Gewichte normiert (0-100 %), damit Filter/Schwelle
        // stabil bleiben, egal wie viele Bedingungen aktiv sind.

        // 1) Delta signifikant. Richtung = Vorzeichen des Candle-Deltas.
        private bool _deltaEnabled = true;
        private int _deltaWeight = 20;

        // 2) Volumen-Spike. Richtung = Kerzenkoerper (Close vs Open).
        private bool _volEnabled = true;
        private int _volWeight = 15;

        // 3) Footprint-Absorption: groesstes Level-Delta (Ask-Bid je Preislevel)
        //    ueber der Schwelle -> der Aggressor wurde absorbiert. Richtung =
        //    REVERSAL gegen den Aggressor. Range-unabhaengig -> auch auf Renko.
        private bool _absEnabled = true;
        private int _absWeight = 20;

        // 4) VWAP-Lage als Bias (keine Kalibrierung). Close ueber/unter Session-VWAP.
        private bool _vwapEnabled = true;
        private int _vwapWeight = 15;

        // 5) Imbalance (diagonal): gestapelte Ask[p] >= Ratio*Bid[p-Tick] (Buy) bzw.
        //    Bid[p] >= Ratio*Ask[p+Tick] (Sell). Richtung = dominante Stapelseite.
        private bool _imbEnabled = true;
        private int _imbWeight = 15;
        private decimal _imbRatio = 2.0m;
        private int _imbMinCount = 3;

        // 6) vPOC-in-Wick: POC im unteren Docht = Kaeufer-Rejection (bullish),
        //    im oberen Docht = Verkaeufer-Rejection (bearish). Location-basiert.
        private bool _vpocEnabled = true;
        private int _vpocWeight = 15;

        // 7) Tape / Big Trades: einzelne Cumulative-Trades >= Mindestgroesse.
        //    Buy = bullish, Sell = bearish. LIVE-ONLY (ab Laden vorwaerts), daher
        //    default AUS. Braucht Tick-Trade-Daten (OnCumulativeTrade).
        private bool _tapeEnabled = false;
        private int _tapeWeight = 15;
        private int _tapeMinContracts = 100;

        // ── BIG-TRADE-LEVELS ───────────────────────────────────────────────
        // Grosse Prints als verteidigte Levels markieren; Alarm beim Re-Test.
        // Session-abhaengige Mindestgroesse (London niedriger als US).
        private bool _bigEnabled = true;
        private bool _bigSeparated = true;     // einzelne Prints (OnNewTrade) statt kumulativ
        private int _bigMinLondon = 25;
        private int _bigMinDefault = 40;       // US / sonstige Zeiten
        private int _bigLondonStartHour = 9;   // Chart-Zeitzone (wie Sessions sonst)
        private int _bigLondonEndHour = 15;
        private int _bigArmTicks = 8;          // so weit muss der Preis weg, bevor Re-Test zaehlt
        private int _bigHitTolerance = 2;      // Toleranz in Ticks fuer Hit/Dedupe
        private bool _bigAlertOnHit = true;
        private bool _bigKeepAfterHit = true;   // gehittete Levels behalten (gedimmt/gestrichelt)
        private Color _colorBigBuy = Color.FromArgb(220, 70, 200, 120);
        private Color _colorBigSell = Color.FromArgb(220, 230, 100, 100);

        // ── REVERSAL-ENGINE ────────────────────────────────────────────────
        // Getrennte Umkehr-Logik (Divergenz, Absorption am Extrem, vPOC-Docht,
        // Exhaustion), nur an lokalen Extrema. Eigener Score + Rauten-Marker.
        private bool _reversalEnabled = true;
        private int _reversalLookback = 14;   // Bars fuer Extrem-/Divergenz-Referenz
        private int _reversalThreshold = 70;  // Mindest-Reversal-Score (%) -> ~3 Bedingungen
        private int _revDivWeight = 30;       // Delta-Divergenz
        private int _revAbsWeight = 30;       // Absorption am Extrem
        private int _revVpocWeight = 20;      // vPOC im Docht
        private int _revExhWeight = 20;       // Exhaustion (duennes Aggressor-Vol)
        private int _revSpeedWeight = 15;     // Speed of Tape: Klimax-Spike am Extrem
        private int _revImbWeight = 0;        // (A) frischer Imbalance-Flip am Extrem (Default aus)
        private int _revAuctionWeight = 0;    // (B) Finished Auction am Extrem (Default aus)
        // semaPHorek-Ergaenzungen (alle Default-Gewicht 0 = neutral, aendern nichts):
        private int _revAcntWeight = 0;         // (C) Absorption Count: mehrere absorbierte Level
        private int _revAcntMin = 2;            // min. Anzahl absorbierter Level ("X of Y")
        private decimal _revAcntFrac = 0.5m;    // Level zaehlt ab Frac * staerkstem Level-Delta
        private int _revPwrWeight = 0;          // (D) Volume Power Bar: Power-Kerze in Umkehr-Richtung
        private decimal _revPwrVolMult = 1.5m;  // Volumen >= Mult * Ø-Bar-Volumen im Fenster
        private int _revCdeltaWeight = 0;       // (E) Kerzen-Delta im Reversal (Netto-Delta der Umkehrkerze)
        private decimal _revCdeltaFactor = 0.5m;// Kerzen-Delta >= Faktor * Ø-|Bar-Delta|
        private decimal _revExhFactor = 0.5m; // Exhaustion: Aggressor-Vol am Extrem <= Faktor * Ø (niedriger = strenger)

        // Reversal-Diagnose: Treiber-Aufschluesselung der letzten ANGEZEIGTEN Raute.
        private bool _showRevDebug = false;
        private struct RevDbg { public decimal Eff; public bool Strong, Div, Abs, Vp, Exh, Spd, Imb, Auc, Acn, Pwr, Cdl; public int AcnN; public decimal PwrX, CdlX, SpdX; }
        private RevDbg _revCand; private int _revCandDir, _revCandPct;
        private readonly Dictionary<int, RevDbg> _revDbgByBar = new();   // Treiber je Raute (fuer Hover-Diagnose)
        private int _hoverRevBar = -1;                                   // Raute unter dem Mauszeiger
        private decimal _revSpeedFactor = 1.5m; // Spike, wenn Bar-Speed >= Faktor * Ø-Speed
        // Impuls-Filter: in einem gesunden, gerichteten Impuls braucht ein Gegentrend-
        // Reversal echte CVD-Divergenz (Absorption allein reicht nicht). Default AUS.
        private bool _revImpulseFilter = false;
        private decimal _revImpulseEff = 0.5m;  // ab dieser Effizienz gilt das Bein als Impuls
        private decimal _revDivMinFactor = 1.5m; // im Impuls: CVD-Divergenz >= Faktor * Ø-Bar-Delta
        // 2-Kerzen-Bestaetigung: Umkehr nur, wenn die Folgekerze in Umkehr-
        // Richtung schliesst (ISA-Prinzip). Default an.
        private bool _revConfirm = true;

        // Phase 2b: Reversal-Rauten NUR an Range-Kanten anzeigen (reiner Display-
        // Filter, aendert die gespeicherten Signale nicht). Default AUS.
        private bool _revEdgeOnly = false;
        private int _revEdgeTolerance = 6;   // Toleranz in Ticks zur Kante (High/Low/vPOC)

        // ── KeyLevels-Konfluenz (Link zu KeyLevels; REINE Markierung, aendert Signale nicht) ──
        private bool _klConfluence = true;
        private int _klTolTicks = 4;
        private readonly List<(decimal Price, string Label)> _klLevels = new();
        private DateTime _klLastRead = DateTime.MinValue;

        // ── Backtest-Log (CSV): jede Umkehr + auto-gemessener Ausgang (MFE/MAE/Net) ──
        private bool _btLog = false;
        private int _btHorizon = 15;   // geschlossene Bars fuer die Ausgangsmessung
        private struct BtPending { public int Dir, Age, Pct; public decimal Entry, Mfe, Mae; public DateTime Time; public RevDbg D; public bool Kl; }
        private readonly List<BtPending> _btPending = new();

        // ── ALARM (Telegram via ATAS-Alarme) ───────────────────────────────
        // Latch: erst nach dem Historien-Nachladen alarmieren -> kein Spam alter Umkehren.
        private bool _histDone;
        private bool _alertOnReversal = true;
        private bool _alertLong = true;
        private bool _alertShort = true;
        private string _alertSound = "alert1.wav";

        // ── BALANCE-RANGE (Phase 2a) ───────────────────────────────────────
        // Value Area (VAH/VAL/vPOC) ueber ein rollendes Fenster -> zeichnet die
        // aktuelle Balance. (Reversal-Gate an den Raendern folgt in Phase 2b.)
        private bool _rangeEnabled = false;   // rollendes VA-Band: Default AUS (Referenz)
        private int _rangeLookback = 100;     // Bars fuer das Volumen-Profil
        private int _rangeValuePct = 70;      // Value-Area-Anteil (%)
        private bool _rangeExtendRight = true;

        // ── RANGE-DETEKTOR (H-Range-Stil) ──────────────────────────────────
        // Sucht diskrete Konsolidierungen (Balance nach Imbalance) und markiert
        // jede als Box, gefaerbt nach Ausbruchsrichtung.
        private bool _detectorEnabled = true;
        private int _detectorLookback = 500;     // Bars rueckwaerts absuchen (Start bei Recalc)
        private int _detectorMinBars = 10;       // Mindest-Bars fuer eine Range
        private decimal _detectorWidthFactor = 3.0m; // max. Range-Hoehe = Faktor * ATR
        private int _detectorAtrPeriod = 14;     // Periode der Breiten-ATR (stabil, kein Drift)
        private bool _detectorVolumeFilter = true;    // Volumen-Akzeptanz: nur echte Auktions-Balances
        private decimal _detectorPocFactor = 1.5m;    // klarer vPOC: POC-Vol >= Faktor * Ø Level-Vol
        private decimal _detectorMinVolFactor = 0.7m; // genug Volumen: Range-Ø-Bar-Vol >= Faktor * Umfeld
        private bool _detectorMerge = true;           // benachbarte, ueberlappende Balances vereinen
        private int _detectorMergeGapBars = 20;       // max. Lueckenbars zwischen zwei Ranges fuers Merging
        private Color _colorBreakUp = Color.FromArgb(200, 60, 190, 90);   // hoch ausgebrochen
        private Color _colorBreakDn = Color.FromArgb(200, 225, 70, 70);   // runter ausgebrochen
        private Color _colorFlat = Color.FromArgb(170, 150, 150, 150);    // noch aktiv/flat

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
        private Color _colorRevBull = Color.FromArgb(235, 70, 220, 220);   // Reversal Long = tuerkis
        private Color _colorRevBear = Color.FromArgb(235, 240, 140, 40);   // Reversal Short = orange
        private Color _colorRangeBand = Color.FromArgb(30, 120, 140, 185); // Value-Area-Band (transparent)
        private Color _colorRangeEdge = Color.FromArgb(200, 130, 160, 210);// VAH/VAL-Linien
        private Color _colorRangePoc = Color.FromArgb(220, 235, 200, 90);  // vPOC-Linie

        // ─────────────────────────────────────────────────────────────────
        //  STATE
        // ─────────────────────────────────────────────────────────────────
        // Pro Bar gespeicherte Metriken (fuer das rollende Perzentil-Fenster).
        private readonly List<decimal> _volArr = new();   // Candle-Volumen
        private readonly List<decimal> _absDArr = new();  // |Candle-Delta|
        private readonly List<decimal> _mldArr = new();   // |max. Level-Delta|

        // Kumuliertes Delta (CVD) je Bar, Session-Reset -> fuer Delta-Divergenz.
        private readonly List<decimal> _cumDeltaArr = new();
        private decimal _cumDeltaRun;

        // Session-VWAP-Akkumulation der abgeschlossenen Bars (Reset je Session).
        private decimal _cumPv, _cumVol;

        private int _lastProcessedBar = -1;
        // Pro Bar gespeicherter SIGNIERTER Score: > 0 Long, < 0 Short, 0 keins.
        // Betrag = Gewichtssumme der dominanten Seite (Staerke, wie semaPHoreks Lichter-Zahl).
        private readonly List<int> _signals = new();
        private int _lastSignalBar = -1;

        // Tape: pro Bar netto-signiertes Big-Trade-Volumen (Buy +, Sell -).
        // Wird live aus OnCumulativeTrade (Fremd-Thread) befuellt -> Concurrent.
        private readonly ConcurrentDictionary<int, int> _tapeNet = new();

        // Big-Trade-Levels: grosse Prints als verteidigte Levels. Aus OnCumulativeTrade
        // (Fremd-Thread) befuellt -> per Lock geschuetzt.
        private sealed class BigLevel { public decimal Price; public int Volume; public int BuyVol; public int SellVol; public int Dir; public int Bar; public bool Armed; public bool Hit; }
        private readonly object _bigLock = new();
        private readonly List<BigLevel> _bigLevels = new();
        private int _bigMaxLevels = 20;   // Obergrenze sichtbarer Levels (gegen Zumuellen)
        private bool _bigReqDone;      // Historien-Anfrage pro Instanz nur einmal (erstes Laden)
        private bool _bigDirty;        // Big-Trade-Setting geaendert -> Levels neu aus Historie holen
        private bool _bigReplaying;    // waehrend historischem Hit-Nachspielen -> keine Alarme
        private int _bigReqId;         // Request-ID zum Zuordnen der Antwort

        // Reversal: pro Bar signierter Reversal-Score (> 0 Long-Umkehr, < 0 Short).
        private readonly List<int> _revSignals = new();
        private int _lastRevBar = -1;   // Cooldown-Tracking fuer Reversal-Marker

        // Balance-Range (Value Area) — zuletzt berechnete Werte fuer das Rendering.
        private decimal _rangeVah, _rangeVal, _rangeVpoc;
        private int _rangeStartBar;
        private bool _rangeValid;

        // Range-Detektor: erkannte Konsolidierungs-Boxen.
        private struct DetRange { public int Start, End; public decimal High, Low; public int Dir; public decimal Poc; }
        private readonly List<DetRange> _detRanges = new();   // EINGEFRORENE Ranges (kein Repaint)

        // Inkrementeller Detektor-State: nur die aktive Kandidaten-Range waechst live.
        private int _lastDetBar = -1;
        private bool _candActive;
        private int _candStart;
        private decimal _candHi, _candLo, _candWidth;

        // Imbalance-Zonen: gestapelte Footprint-Imbalances (Bid/Ask-Dominanz) als
        // verteidigte Preiszonen. Aus historischen Candle-Clustern -> ueberlebt Reload.
        private sealed class ImbZone { public decimal Low, High; public int Dir; public int Bar; public int Stack; public bool Armed; public bool Filled; public bool Alerted; }
        private readonly List<ImbZone> _imbZones = new();
        private bool _imbZonesEnabled = false;
        private int _imbZoneMinStack = 3;        // min. konsekutive imbalanced Level
        private int _imbSkipEdgeLevels = 1;      // Docht-Rand ignorieren (Finished-Auction-Tips)
        private bool _imbIncludeVoids = true;    // Single Prints / Volumen-Voids ueberbruecken den Stack
        private int _imbVoidThreshold = 0;       // Level gilt als Void, wenn Gesamtvolumen <= Schwelle (0 = nur echte 0/0)
        private decimal _imbZoneRatio = 3.0m;    // Ask >= Ratio*Bid (bzw. umgekehrt); 0-Gegenseite = extrem
        private int _imbZoneMaxZones = 20;
        private int _imbZoneArmTicks = 8;        // Preis muss so weit weg sein, bevor Re-Test zaehlt
        private bool _imbZoneAlertOnHit = true;
        private Color _colorImbBuy = Color.FromArgb(60, 70, 200, 120);
        private Color _colorImbSell = Color.FromArgb(60, 230, 100, 100);

        // Zuletzt berechnete Schwellen (fuer HUD + Freeze-Snapshot).
        private decimal _liveVolThr, _liveDeltaThr, _liveAbsThr;
        // Eingefrorene Schwellen (gehalten, solange Freeze aktiv ist).
        private decimal _frzVol, _frzDelta, _frzAbs;

        private string _lastRenderKey = "";

        // Gerenderte HUD-Werte.
        private int _hudBull, _hudBear, _hudSignal, _hudRev;
        private string _hudTags = "";
        private string _hudWarn = "";
        private string _chartLabel = "";

        private RenderFont _font = null!;
        private RenderFont _fontBig = null!;
        private RenderFont _fontMarker = null!;

        // ─────────────────────────────────────────────────────────────────
        //  PROPERTIES — Allgemein
        // ─────────────────────────────────────────────────────────────────
        [Tab(TabName = "Allgemein", TabOrder = 1)]
        [Display(Name = "Lookback (Bars)", GroupName = "Allgemein", Order = 100,
            Description = "Kalibrierungs-Fenster: ueber so viele Bars werden die Perzentil-Schwellen bestimmt.")]
        [Range(10, 1000)]
        public int Lookback { get => _lookback; set { _lookback = Math.Max(10, value); RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Signal-Schwelle (Gewichtspunkte)", GroupName = "Signal", Order = 200,
            Description = "Mindest-Gewichtssumme der dominanten Seite, damit ein Marker feuert. " +
                          "Bei Default-Gewichten (Summe ~100) ist 50 = Mehrheit. (Standard: 50)")]
        [Range(0, 300)]
        [NumericEditor(NumericEditorTypes.TrackBar, 0.0, 300.0, Step = 5.0)]
        public int SignalThreshold { get => _signalThreshold; set { _signalThreshold = Math.Max(0, value); RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Signal-Cooldown (Bars)", GroupName = "Signal", Order = 202,
            Description = "Mindestabstand zwischen Markern. Rausch-Bremse fuer Tick-Charts. 0 = aus.")]
        [Range(0, 100)]
        public int SignalCooldownBars { get => _signalCooldownBars; set { _signalCooldownBars = Math.Max(0, value); RecalculateValues(); } }

        [Tab(TabName = "Allgemein", TabOrder = 1)]
        [Display(Name = "HUD anzeigen", GroupName = "HUD & Panel", Order = 110,
            Description = "Blendet das Info-Panel (HUD) mit Scores, Kalibrierung und Status ein/aus.")]
        public bool ShowHud { get => _showHud; set { _showHud = value; RedrawChart(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Signal-Marker anzeigen", GroupName = "Marker", Order = 210,
            Description = "Zeigt die Bull/Bear-Signal-Marker (Dreiecke). Betrifft NICHT die Reversal-Rauten (eigener Schalter im Reiter Reversal).")]
        public bool ShowMarkers { get => _showMarkers; set { _showMarkers = value; RedrawChart(); } }

        [Tab(TabName = "Allgemein", TabOrder = 1)]
        [Display(Name = "Kalibrierung im HUD zeigen", GroupName = "HUD & Panel", Order = 112,
            Description = "Zeigt die aktuellen Kalibrierungs-Schwellen (Volumen/Delta/Absorption) im HUD.")]
        public bool ShowCalibration { get => _showCalibration; set { _showCalibration = value; RedrawChart(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Min-Score für Marker", GroupName = "Marker", Order = 212,
            Description = "Nur Marker mit Score >= diesem Wert zeichnen. 60 = mind. 3 Bedingungen " +
                          "ausgerichtet (versteckt die schwachen 50/55). 0 = alle. (Standard: 60)")]
        [Range(0, 100)]
        [NumericEditor(NumericEditorTypes.TrackBar, 0.0, 100.0, Step = 5.0)]
        public int MinMarkerScore { get => _minMarkerScore; set { _minMarkerScore = Math.Clamp(value, 0, 100); RedrawChart(); } }

        // ─────────────────────────────────────────────────────────────────
        //  PROPERTIES — Kalibrierung
        // ─────────────────────────────────────────────────────────────────
        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Globaler Perzentil", GroupName = "Kalibrierung", Order = 230,
            Description = "Schwelle = dieses Perzentil der letzten N Bars. 85 = feuert in den oberen 15%. (Standard: 95)")]
        [Range(50, 99)]
        [NumericEditor(NumericEditorTypes.TrackBar, 50.0, 99.0, Step = 1.0)]
        public int GlobalPercentile { get => _globalPercentile; set { _globalPercentile = Math.Clamp(value, 50, 99); RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Advanced: Perzentil pro Bedingung", GroupName = "Kalibrierung", Order = 232,
            Description = "Aus = globaler Wert fuer alle. Ein = je Bedingung eigener Perzentil unten.")]
        public bool UseAdvancedPercentiles { get => _useAdvancedPercentiles; set { _useAdvancedPercentiles = value; RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Freeze (Kalibrierung einfrieren)", GroupName = "Kalibrierung", Order = 234,
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

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Perzentil Volumen", GroupName = "Kalibrierung", Order = 236,
            Description = "Perzentil-Schwelle nur fuer Volumen (wenn Advanced an). Standard: 85.")]
        [Range(50, 99)]
        [NumericEditor(NumericEditorTypes.TrackBar, 50.0, 99.0, Step = 1.0)]
        [VisibleWhen(nameof(UseAdvancedPercentiles), true)]
        public int VolPercentile { get => _volPercentile; set { _volPercentile = Math.Clamp(value, 50, 99); RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Perzentil Delta", GroupName = "Kalibrierung", Order = 238,
            Description = "Perzentil-Schwelle nur fuer Delta (wenn Advanced an). Standard: 85.")]
        [Range(50, 99)]
        [NumericEditor(NumericEditorTypes.TrackBar, 50.0, 99.0, Step = 1.0)]
        [VisibleWhen(nameof(UseAdvancedPercentiles), true)]
        public int DeltaPercentile { get => _deltaPercentile; set { _deltaPercentile = Math.Clamp(value, 50, 99); RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Perzentil Absorption", GroupName = "Kalibrierung", Order = 240,
            Description = "Perzentil-Schwelle nur fuer Absorption (wenn Advanced an). Standard: 85.")]
        [Range(50, 99)]
        [NumericEditor(NumericEditorTypes.TrackBar, 50.0, 99.0, Step = 1.0)]
        [VisibleWhen(nameof(UseAdvancedPercentiles), true)]
        public int AbsPercentile { get => _absPercentile; set { _absPercentile = Math.Clamp(value, 50, 99); RecalculateValues(); } }

        // ─────────────────────────────────────────────────────────────────
        //  PROPERTIES — Bedingungen
        // ─────────────────────────────────────────────────────────────────
        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Delta aktiv", GroupName = "Bedingung: Delta", Order = 250,
            Description = "Bedingung Delta in die Bull/Bear-Wertung einbeziehen.")]
        public bool DeltaEnabled { get => _deltaEnabled; set { _deltaEnabled = value; RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Delta Gewicht", GroupName = "Bedingung: Delta", Order = 252,
            Description = "Gewichtspunkte der Delta-Bedingung in der Gesamt-Score.")]
        [Range(0, 100)]
        public int DeltaWeight { get => _deltaWeight; set { _deltaWeight = value; RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Volumen aktiv", GroupName = "Bedingung: Volumen", Order = 254,
            Description = "Bedingung Relatives Volumen in die Wertung einbeziehen.")]
        public bool VolEnabled { get => _volEnabled; set { _volEnabled = value; RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Volumen Gewicht", GroupName = "Bedingung: Volumen", Order = 256,
            Description = "Gewichtspunkte der Volumen-Bedingung.")]
        [Range(0, 100)]
        public int VolWeight { get => _volWeight; set { _volWeight = value; RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Absorption aktiv", GroupName = "Bedingung: Absorption", Order = 258,
            Description = "Bedingung Absorption (Footprint) in die Wertung einbeziehen.")]
        public bool AbsEnabled { get => _absEnabled; set { _absEnabled = value; RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Absorption Gewicht", GroupName = "Bedingung: Absorption", Order = 260,
            Description = "Footprint-Absorption: groesstes Level-Delta ueber Schwelle. Richtung = Reversal gegen den Aggressor.")]
        [Range(0, 100)]
        public int AbsWeight { get => _absWeight; set { _absWeight = value; RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "VWAP aktiv", GroupName = "Bedingung: VWAP", Order = 262,
            Description = "Bedingung VWAP-Bias in die Wertung einbeziehen.")]
        public bool VwapEnabled { get => _vwapEnabled; set { _vwapEnabled = value; RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "VWAP Gewicht", GroupName = "Bedingung: VWAP", Order = 264,
            Description = "Bias: Close ueber Session-VWAP = bullish, darunter = bearish. VWAP ankert taeglich (IsNewSession).")]
        [Range(0, 100)]
        public int VwapWeight { get => _vwapWeight; set { _vwapWeight = value; RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Imbalance aktiv", GroupName = "Bedingung: Imbalance", Order = 266,
            Description = "Bedingung Diagonale Imbalance in die Wertung einbeziehen.")]
        public bool ImbEnabled { get => _imbEnabled; set { _imbEnabled = value; RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Imbalance Gewicht", GroupName = "Bedingung: Imbalance", Order = 268,
            Description = "Gewichtspunkte der Imbalance-Bedingung.")]
        [Range(0, 100)]
        public int ImbWeight { get => _imbWeight; set { _imbWeight = value; RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Imbalance Ratio", GroupName = "Bedingung: Imbalance", Order = 270,
            Description = "Diagonale Schwelle: Ask[p] >= Ratio * Bid[p-Tick] (Buy) bzw. umgekehrt. Default 2.0 = 200%. (Standard: 2.0)")]
        [Range(1.0, 20.0)]
        [NumericEditor(NumericEditorTypes.TrackBar, 1.0, 10.0, Step = 0.5, DisplayFormat = "0.0")]
        public decimal ImbRatio { get => _imbRatio; set { _imbRatio = value; RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Imbalance Mindest-Anzahl", GroupName = "Bedingung: Imbalance", Order = 272,
            Description = "Mindestanzahl diagonaler Imbalances auf der dominanten Seite (gestapelt).")]
        [Range(1, 50)]
        public int ImbMinCount { get => _imbMinCount; set { _imbMinCount = Math.Max(1, value); RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "vPOC-in-Wick aktiv", GroupName = "Bedingung: vPOC", Order = 274,
            Description = "Bedingung vPOC-im-Docht in die Wertung einbeziehen.")]
        public bool VpocEnabled { get => _vpocEnabled; set { _vpocEnabled = value; RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "vPOC Gewicht", GroupName = "Bedingung: vPOC", Order = 276,
            Description = "POC im unteren Docht = bullish (Kaeufer-Rejection), oberer Docht = bearish.")]
        [Range(0, 100)]
        public int VpocWeight { get => _vpocWeight; set { _vpocWeight = value; RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Tape aktiv (LIVE-ONLY)", GroupName = "Bedingung: Tape", Order = 278,
            Description = "Big Trades ab Mindestgroesse. Erfasst NUR live ab Laden vorwaerts (keine Historie). " +
                          "Buy = bullish, Sell = bearish.")]
        public bool TapeEnabled { get => _tapeEnabled; set { _tapeEnabled = value; RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Tape Gewicht", GroupName = "Bedingung: Tape", Order = 280,
            Description = "Gewichtspunkte der Tape-Bedingung (Big Trades live).")]
        [Range(0, 100)]
        public int TapeWeight { get => _tapeWeight; set { _tapeWeight = value; RecalculateValues(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Tape Mindest-Kontrakte", GroupName = "Bedingung: Tape", Order = 282,
            Description = "Ein einzelner Cumulative-Trade ab dieser Groesse zaehlt als Big Trade. Pro Instrument tunen.")]
        [Range(1, 100000)]
        public int TapeMinContracts { get => _tapeMinContracts; set { _tapeMinContracts = Math.Max(1, value); RecalculateValues(); } }

        // ── Reversal-Engine ────────────────────────────────────────────────
        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Reversal aktiv", GroupName = "Reversal", Order = 300,
            Description = "Eigene Umkehr-Logik an lokalen Extrema (Divergenz, Absorption, vPOC-Docht, Exhaustion). Eigener Rauten-Marker.")]
        public bool ReversalEnabled { get => _reversalEnabled; set { _reversalEnabled = value; RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Reversal-Marker anzeigen", GroupName = "Reversal", Order = 301,
            Description = "Zeigt die Reversal-Rauten. Unabhaengig vom Signal-Marker-Schalter (Reiter Signal).")]
        public bool ShowReversalMarkers { get => _showReversalMarkers; set { _showReversalMarkers = value; RedrawChart(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Reversal-Diagnose (Hover)", GroupName = "Reversal", Order = 306,
            Description = "An = beim Hovern ueber eine Raute erscheint ein farbcodierter Tooltip mit den Treibern (hell = gefeuert): DIV/ABS/VP/EXH/SPD/IMB/AUC, plus Effizienz (eff) und Impuls-Flag (IMP). Zum Diagnostizieren, warum eine Raute kam.")]
        public bool ShowRevDebug { get => _showRevDebug; set { _showRevDebug = value; RedrawChart(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Reversal Lookback (Bars)", GroupName = "Reversal", Order = 302,
            Description = "Fenster fuer Extrem-/Divergenz-Referenz: neues Tief/Hoch ueber so viele Bars = Umkehr-Kandidat.")]
        [Range(2, 200)]
        public int ReversalLookback { get => _reversalLookback; set { _reversalLookback = Math.Max(2, value); RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Reversal-Schwelle (%)", GroupName = "Reversal", Order = 304,
            Description = "Mindest-Reversal-Score, damit eine Raute feuert. (Standard: 70)")]
        [Range(0, 100)]
        [NumericEditor(NumericEditorTypes.TrackBar, 0.0, 100.0, Step = 5.0)]
        public int ReversalThreshold { get => _reversalThreshold; set { _reversalThreshold = Math.Clamp(value, 0, 100); RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Gewicht Delta-Divergenz", GroupName = "Treiber-Gewichte", Order = 310,
            Description = "Gewicht der CVD-Divergenz als Umkehr-Treiber.")]
        [Range(0, 100)]
        public int RevDivWeight { get => _revDivWeight; set { _revDivWeight = value; RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Gewicht Absorption am Extrem", GroupName = "Treiber-Gewichte", Order = 312,
            Description = "Gewicht der Absorption am Extrem als Umkehr-Treiber.")]
        [Range(0, 100)]
        public int RevAbsWeight { get => _revAbsWeight; set { _revAbsWeight = value; RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Gewicht vPOC-im-Docht", GroupName = "Treiber-Gewichte", Order = 314,
            Description = "Gewicht von vPOC-im-Docht als Umkehr-Treiber.")]
        [Range(0, 100)]
        public int RevVpocWeight { get => _revVpocWeight; set { _revVpocWeight = value; RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Gewicht Exhaustion", GroupName = "Treiber-Gewichte", Order = 316,
            Description = "Gewicht der Exhaustion (duennes Aggressor-Volumen am Extrem).")]
        [Range(0, 100)]
        public int RevExhWeight { get => _revExhWeight; set { _revExhWeight = value; RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Gewicht Speed of Tape (Klimax)", GroupName = "Treiber-Gewichte", Order = 318,
            Description = "Speed-Spike am Extrem (schnelles Tape = Klimax/Kapitulation) staerkt die Umkehr. 0 = aus.")]
        [Range(0, 100)]
        public int RevSpeedWeight { get => _revSpeedWeight; set { _revSpeedWeight = value; RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Gewicht Imbalance-Flip", GroupName = "Treiber-Gewichte", Order = 319,
            Description = "(A) Frischer gestapelter Imbalance-Flip am Extrem (am Tief Buy-Stack, am Hoch Sell-Stack = Aggression kippt). Docht-Rand ausgeschlossen. 0 = aus (Default).")]
        [Range(0, 100)]
        public int RevImbWeight { get => _revImbWeight; set { _revImbWeight = value; RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Gewicht Finished Auction", GroupName = "Treiber-Gewichte", Order = 319,
            Description = "(B) Auktion am Extrem ist abgeschlossen (kein Imbalance-Tip = Gegenseite hat gestoppt = Erschoepfung). Unfinished (Tip noch imbalanced) zaehlt nicht. 0 = aus (Default).")]
        [Range(0, 100)]
        public int RevAuctionWeight { get => _revAuctionWeight; set { _revAuctionWeight = value; RecalculateValues(); } }

        // ── semaPHorek-Ergaenzungen (Default-Gewicht 0 = neutral) ──────────
        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Gewicht Absorption Count", GroupName = "semaPHorek-Ergaenzungen", Order = 330,
            Description = "(C) Mehrere absorbierte Level am Extrem (nicht nur das staerkste). 0 = aus (Default). Per-Kerze -> saturiert nicht.")]
        [Range(0, 100)]
        public int RevAcntWeight { get => _revAcntWeight; set { _revAcntWeight = value; RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Absorption Count: min. Level", GroupName = "semaPHorek-Ergaenzungen", Order = 331,
            Description = "Ab wie vielen absorbierten Leveln der Treiber feuert (semaPHorek 'X of Y'). Ein Level zaehlt ab 50% des staerksten Level-Deltas. Default 2.")]
        [Range(1, 10)]
        public int RevAcntMin { get => _revAcntMin; set { _revAcntMin = Math.Max(1, value); RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Gewicht Volume Power Bar", GroupName = "semaPHorek-Ergaenzungen", Order = 332,
            Description = "(D) Power-Kerze: hohes Volumen + Kerzenkoerper in Umkehr-Richtung. 0 = aus (Default). Per-Kerze -> saturiert nicht.")]
        [Range(0, 100)]
        public int RevPwrWeight { get => _revPwrWeight; set { _revPwrWeight = value; RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Power Bar: Volumen-Faktor", GroupName = "semaPHorek-Ergaenzungen", Order = 333,
            Description = "Kerzen-Volumen >= Faktor * Ø-Bar-Volumen im Fenster, damit es als Power-Kerze zaehlt. Default 1.5.")]
        [Range(1.0, 10.0)]
        public decimal RevPwrVolMult { get => _revPwrVolMult; set { _revPwrVolMult = Math.Max(1.0m, value); RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Gewicht Kerzen-Delta", GroupName = "semaPHorek-Ergaenzungen", Order = 334,
            Description = "(E) Netto-Delta der Umkehrkerze in Umkehr-Richtung (Long: Kaeufer-Delta am Tief, Short: Verkaeufer-Delta am Hoch). 0 = aus (Default).")]
        [Range(0, 100)]
        public int RevCdeltaWeight { get => _revCdeltaWeight; set { _revCdeltaWeight = value; RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Kerzen-Delta: Faktor", GroupName = "semaPHorek-Ergaenzungen", Order = 335,
            Description = "Kerzen-Delta muss >= Faktor * Ø-|Bar-Delta| sein (in Umkehr-Richtung). Default 0.5.")]
        [Range(0.0, 5.0)]
        public decimal RevCdeltaFactor { get => _revCdeltaFactor; set { _revCdeltaFactor = Math.Max(0m, value); RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Speed-Spike Faktor (x Ø-Speed)", GroupName = "Treiber-Gewichte", Order = 320,
            Description = "Spike, wenn der Bar-Tape-Speed (Ticks/Sekunde) >= Faktor * Durchschnitt im Fenster. (Standard: 1.5)")]
        [Range(1.0, 10.0)]
        [NumericEditor(NumericEditorTypes.TrackBar, 1.0, 10.0, Step = 0.5, DisplayFormat = "0.0")]
        public decimal RevSpeedFactor { get => _revSpeedFactor; set { _revSpeedFactor = value; RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Exhaustion-Faktor", GroupName = "Treiber-Gewichte", Order = 321,
            Description = "Exhaustion feuert, wenn Aggressor-Volumen am Extrem <= Faktor * Ø-Level-Volumen. Niedriger = strenger (nur wirklich duennes Volumen). Standard: 0.5 = wie bisher.")]
        [Range(0.1, 1.0)]
        [NumericEditor(NumericEditorTypes.TrackBar, 0.1, 1.0, Step = 0.05, DisplayFormat = "0.00")]
        public decimal RevExhFactor { get => _revExhFactor; set { _revExhFactor = Math.Clamp(value, 0.1m, 1.0m); RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Impuls-Filter (kein Gegentrend-Picken)", GroupName = "Impuls-Filter", Order = 330,
            Description = "In einem gesunden, gerichteten Impuls braucht die Umkehr echte CVD-Divergenz (Absorption allein reicht nicht). Filtert Trend-Picks. Default AUS.")]
        public bool RevImpulseFilter { get => _revImpulseFilter; set { _revImpulseFilter = value; RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Impuls-Effizienz-Schwelle", GroupName = "Impuls-Filter", Order = 332,
            Description = "Ab dieser Effizienz (|Netto-Weg|/Pfad, 0..1) gilt das Bein als gesunder Impuls. Hoeher = nur sehr gerichtete Impulse gelten. (Standard: 0.5)")]
        [Range(0.1, 1.0)]
        [NumericEditor(NumericEditorTypes.TrackBar, 0.1, 1.0, Step = 0.05, DisplayFormat = "0.00")]
        [VisibleWhen(nameof(RevImpulseFilter), true)]
        public decimal RevImpulseEff { get => _revImpulseEff; set { _revImpulseEff = Math.Clamp(value, 0.1m, 1.0m); RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Impuls: min. Divergenz (x Ø-Bar-Delta)", GroupName = "Impuls-Filter", Order = 334,
            Description = "Im Impuls muss der CVD-Bruch >= Faktor * Ø-Bar-Delta sein (echte Erschoepfung statt Rauschen). Hoeher = strenger. Nur aktiv mit Impuls-Filter. (Standard: 1.5)")]
        [Range(0.0, 20.0)]
        [NumericEditor(NumericEditorTypes.TrackBar, 0.0, 10.0, Step = 0.5, DisplayFormat = "0.0")]
        [VisibleWhen(nameof(RevImpulseFilter), true)]
        public decimal RevDivMinFactor { get => _revDivMinFactor; set { _revDivMinFactor = Math.Max(0m, value); RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Folgekerzen-Bestätigung (2-Kerzen)", GroupName = "Bestaetigung & Kanten", Order = 340,
            Description = "Umkehr nur, wenn die naechste Kerze in Umkehr-Richtung schliesst. Hoehere Qualitaet, 1 Bar Verzoegerung.")]
        public bool RevConfirm { get => _revConfirm; set { _revConfirm = value; RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Nur an Range-Kanten (Phase 2b)", GroupName = "Bestaetigung & Kanten", Order = 342,
            Description = "Reiner Display-Filter: zeigt Reversal-Rauten nur, wenn das Extrem nahe einer Range-Kante (High/Low/vPOC) liegt. Default AUS -> aendert nichts.")]
        public bool RevEdgeOnly { get => _revEdgeOnly; set { _revEdgeOnly = value; RedrawChart(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Kanten-Toleranz (Ticks)", GroupName = "Bestaetigung & Kanten", Order = 344,
            Description = "Wie nah das Reversal-Extrem an einer Range-Kante liegen muss (in Ticks).")]
        [Range(0, 100)]
        [VisibleWhen(nameof(RevEdgeOnly), true)]
        public int RevEdgeTolerance { get => _revEdgeTolerance; set { _revEdgeTolerance = Math.Max(0, value); RedrawChart(); } }

        // ── KeyLevels-Konfluenz (reine Markierung) ─────────────────────────
        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "KeyLevels-Konfluenz markieren", GroupName = "KeyLevels-Konfluenz", Order = 360,
            Description = "An = Reversals nahe an einem KeyLevel werden hervorgehoben (heller/groesserer Halo + 'KL'-Tag, im Hover das Level). Aendert die Signal-MENGE NICHT. Voraussetzung: in KeyLevels den 'Level-Export' aktivieren (Reiter Sync).")]
        public bool KlConfluence { get => _klConfluence; set { _klConfluence = value; RedrawChart(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "KeyLevels-Toleranz (Ticks)", GroupName = "KeyLevels-Konfluenz", Order = 362,
            Description = "Max. Abstand des Umkehr-Extrems zum KeyLevel, damit es als konfluent gilt. Default 4.")]
        [Range(0, 50)]
        [VisibleWhen(nameof(KlConfluence), true)]
        public int KlTolTicks { get => _klTolTicks; set { _klTolTicks = Math.Max(0, value); RedrawChart(); } }

        // ── Backtest-Log ───────────────────────────────────────────────────
        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Backtest-Log (CSV)", GroupName = "Backtest", Order = 380,
            Description = "An = jede Umkehr wird mit allen Treibern + auto-gemessenem Ausgang (MFE/MAE/Net ueber N Bars) in %APPDATA%\\ATAS\\ofs_backtest\\<Instrument>.csv geschrieben. Historie laden -> anhaken -> ganze Historie wird protokolliert. Reine Diagnose, keine Signal-Aenderung.")]
        public bool BtLog { get => _btLog; set { _btLog = value; RecalculateValues(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Log-Horizont (Bars)", GroupName = "Backtest", Order = 381,
            Description = "Ueber wie viele geschlossene Bars der Ausgang (MFE/MAE/Net) gemessen wird. Default 15.")]
        [Range(3, 100)]
        [VisibleWhen(nameof(BtLog), true)]
        public int BtHorizon { get => _btHorizon; set { _btHorizon = Math.Max(3, value); RecalculateValues(); } }

        // ── Alarm (Telegram) ───────────────────────────────────────────────
        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Alarm bei Umkehr (Telegram)", GroupName = "Alarm", Order = 350,
            Description = "Loest bei bestaetigter Umkehr einen ATAS-Alarm aus (geht ueber die ATAS-Telegram-Anbindung). Nur live, nicht rueckwirkend.")]
        public bool AlertOnReversal { get => _alertOnReversal; set { _alertOnReversal = value; } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Alarm bei Long-Umkehr", GroupName = "Alarm", Order = 352,
            Description = "Alarm nur fuer Long-Umkehren (Boden) senden.")]
        public bool AlertLong { get => _alertLong; set { _alertLong = value; } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Alarm bei Short-Umkehr", GroupName = "Alarm", Order = 354,
            Description = "Alarm nur fuer Short-Umkehren (Top) senden.")]
        public bool AlertShort { get => _alertShort; set { _alertShort = value; } }

        [Tab(TabName = "Allgemein", TabOrder = 1)]
        [Display(Name = "Alarm-Sound (Datei)", GroupName = "Alarm (global)", Order = 130,
            Description = "Sound-Datei fuer den ATAS-Alarm (z.B. alert1.wav).")]
        public string AlertSound { get => _alertSound; set { _alertSound = value; } }

        [Tab(TabName = "Allgemein", TabOrder = 1)]
        [Display(Name = "▶ Test-Alarm jetzt senden", GroupName = "Alarm (global)", Order = 132,
            Description = "Loest sofort einen Test-Alarm aus (zum Pruefen der Telegram-Anbindung, ohne auf eine Umkehr zu warten).")]
        public bool AlertTest
        {
            get => false;   // wie ein Taster: zeigt sich immer als 'aus'
            set
            {
                if (!value) return;
                AlertsEnabled = true;
                try { AddAlert(_alertSound, $"Orderflow TEST-Alarm | {InstrumentInfo?.Instrument} | {DateTime.Now:HH:mm:ss}"); }
                catch { }
            }
        }

        // ── Big-Trade-Levels ───────────────────────────────────────────────
        [Tab(TabName = "Big Trades", TabOrder = 5)]
        [Display(Name = "Big-Trade-Levels aktiv", GroupName = "Erkennung", Order = 500,
            Description = "Grosse Prints als verteidigte Levels markieren + Alarm beim Re-Test. LIVE-ONLY (braucht Tick-Trade-Daten).")]
        public bool BigEnabled { get => _bigEnabled; set { if (value != _bigEnabled) { _bigEnabled = value; _bigDirty = true; RedrawChart(); } } }

        [Tab(TabName = "Big Trades", TabOrder = 5)]
        [Display(Name = "Einzeltrades (separated)", GroupName = "Erkennung", Order = 502,
            Description = "An = jeder einzelne Print >= Schwelle zaehlt (separated). Aus = kumulativ (aufeinanderfolgende Trades am gleichen Preis zusammengefasst).")]
        public bool BigSeparated { get => _bigSeparated; set { _bigSeparated = value; } }

        [Tab(TabName = "Big Trades", TabOrder = 5)]
        [Display(Name = "Min-Kontrakte London", GroupName = "Erkennung", Order = 504,
            Description = "Mindestgroesse eines Big Trades im London-Fenster.")]
        [Range(1, 100000)]
        public int BigMinLondon { get => _bigMinLondon; set { int v = Math.Max(1, value); if (v != _bigMinLondon) { _bigMinLondon = v; _bigDirty = true; } } }

        [Tab(TabName = "Big Trades", TabOrder = 5)]
        [Display(Name = "Min-Kontrakte US/Default", GroupName = "Erkennung", Order = 506,
            Description = "Mindestgroesse ausserhalb des London-Fensters (US-Session etc.).")]
        [Range(1, 100000)]
        public int BigMinDefault { get => _bigMinDefault; set { int v = Math.Max(1, value); if (v != _bigMinDefault) { _bigMinDefault = v; _bigDirty = true; } } }

        [Tab(TabName = "Big Trades", TabOrder = 5)]
        [Display(Name = "London-Fenster Start (Stunde)", GroupName = "Erkennung", Order = 508,
            Description = "Stunde (Chart-Zeitzone), ab der die London-Schwelle gilt.")]
        [Range(0, 23)]
        public int BigLondonStartHour { get => _bigLondonStartHour; set { int v = Math.Clamp(value, 0, 23); if (v != _bigLondonStartHour) { _bigLondonStartHour = v; _bigDirty = true; } } }

        [Tab(TabName = "Big Trades", TabOrder = 5)]
        [Display(Name = "London-Fenster Ende (Stunde)", GroupName = "Erkennung", Order = 510,
            Description = "Stunde (Chart-Zeitzone), bis zu der die London-Schwelle gilt.")]
        [Range(0, 23)]
        public int BigLondonEndHour { get => _bigLondonEndHour; set { int v = Math.Clamp(value, 0, 23); if (v != _bigLondonEndHour) { _bigLondonEndHour = v; _bigDirty = true; } } }

        [Tab(TabName = "Big Trades", TabOrder = 5)]
        [Display(Name = "Arm-Distanz (Ticks)", GroupName = "Re-Test & Hit", Order = 520,
            Description = "So weit muss der Preis vom Level weg sein, bevor ein erneutes Anlaufen als Re-Test/Hit zaehlt.")]
        [Range(0, 1000)]
        public int BigArmTicks { get => _bigArmTicks; set { int v = Math.Max(0, value); if (v != _bigArmTicks) { _bigArmTicks = v; _bigDirty = true; } } }

        [Tab(TabName = "Big Trades", TabOrder = 5)]
        [Display(Name = "Hit-Toleranz (Ticks)", GroupName = "Re-Test & Hit", Order = 522,
            Description = "Wie nah der Preis ans Level kommen muss, damit es als getroffen gilt (auch fuers Zusammenfassen am gleichen Preis).")]
        [Range(0, 100)]
        public int BigHitTolerance { get => _bigHitTolerance; set { int v = Math.Max(0, value); if (v != _bigHitTolerance) { _bigHitTolerance = v; _bigDirty = true; } } }

        [Tab(TabName = "Big Trades", TabOrder = 5)]
        [Display(Name = "Alarm bei Level-Hit (Telegram)", GroupName = "Re-Test & Hit", Order = 524,
            Description = "ATAS-/Telegram-Alarm ausloesen, wenn ein Big-Trade-Level erneut angelaufen wird.")]
        public bool BigAlertOnHit { get => _bigAlertOnHit; set { _bigAlertOnHit = value; } }

        [Tab(TabName = "Big Trades", TabOrder = 5)]
        [Display(Name = "Levels nach Hit behalten (gestrichelt)", GroupName = "Darstellung", Order = 530,
            Description = "An = gehittete Levels bleiben als gedimmte, gestrichelte Referenz sichtbar. Aus = werden nach dem Hit entfernt.")]
        public bool BigKeepAfterHit { get => _bigKeepAfterHit; set { _bigKeepAfterHit = value; RedrawChart(); } }

        [Tab(TabName = "Big Trades", TabOrder = 5)]
        [Display(Name = "Max. sichtbare Levels", GroupName = "Darstellung", Order = 532,
            Description = "Obergrenze gleichzeitig gezeichneter Big-Trade-Levels. Aeltere fallen raus, wenn ueberschritten. Niedriger = weniger Zumuellung.")]
        [Range(1, 200)]
        public int BigMaxLevels
        {
            get => _bigMaxLevels;
            set
            {
                _bigMaxLevels = Math.Clamp(value, 1, 200);
                lock (_bigLock)
                {
                    while (_bigLevels.Count > _bigMaxLevels && _bigLevels.Count > 0)
                        _bigLevels.RemoveAt(0);
                }
                RedrawChart();
            }
        }

        [Tab(TabName = "Big Trades", TabOrder = 5)]
        [Display(Name = "Alle Big-Levels loeschen", GroupName = "Darstellung", Order = 534,
            Description = "Button: entfernt alle aktuell gezeichneten Big-Trade-Levels. (Bei Chart-Reload werden sie aus der Historie neu aufgebaut.)")]
        public bool BigClearAll
        {
            get => false;
            set
            {
                if (!value) return;
                lock (_bigLock) { _bigLevels.Clear(); }
                RedrawChart();
            }
        }

        [Tab(TabName = "Big Trades", TabOrder = 5)]
        [Display(Name = "Farbe Big-Buy-Level", GroupName = "Darstellung", Order = 540,
            Description = "Farbe der Big-Buy-Level (Kaeufer-verteidigt).")]
        public Color ColorBigBuy { get => _colorBigBuy; set { _colorBigBuy = value; RedrawChart(); } }

        [Tab(TabName = "Big Trades", TabOrder = 5)]
        [Display(Name = "Farbe Big-Sell-Level", GroupName = "Darstellung", Order = 542,
            Description = "Farbe der Big-Sell-Level (Verkaeufer-verteidigt).")]
        public Color ColorBigSell { get => _colorBigSell; set { _colorBigSell = value; RedrawChart(); } }

        // ── Imbalance-Zonen ────────────────────────────────────────────────
        [Tab(TabName = "Imbalance", TabOrder = 6)]
        [Display(Name = "Imbalance-Zonen aktiv", GroupName = "Erkennung", Order = 600,
            Description = "Gestapelte Footprint-Imbalances (Bid/Ask-Dominanz) als verteidigte Preiszonen markieren. Aus historischen Candle-Clustern -> ueberlebt Reload. Default aus.")]
        public bool ImbZonesEnabled { get => _imbZonesEnabled; set { _imbZonesEnabled = value; RecalculateValues(); } }

        [Tab(TabName = "Imbalance", TabOrder = 6)]
        [Display(Name = "Min-Stack (konsekutive Level)", GroupName = "Erkennung", Order = 602,
            Description = "So viele direkt benachbarte Preislevel muessen dieselbe Seite imbalanced sein, damit eine Zone entsteht (Footprint-Standard). Standard: 3.")]
        [Range(2, 20)]
        [NumericEditor(NumericEditorTypes.TrackBar, 2, 20, Step = 1)]
        public int ImbZoneMinStack { get => _imbZoneMinStack; set { _imbZoneMinStack = Math.Clamp(value, 2, 20); RecalculateValues(); } }

        [Tab(TabName = "Imbalance", TabOrder = 6)]
        [Display(Name = "Docht-Rand ignorieren (Level)", GroupName = "Erkennung", Order = 603,
            Description = "So viele Preislevel am Kerzen-Hoch UND -Tief werden ignoriert (Imbalances direkt am Docht = Finished-Auction-Tips, uninteressant). Gilt auch fuer den Reversal-Imbalance-Flip. Standard: 1.")]
        [Range(0, 10)]
        [NumericEditor(NumericEditorTypes.TrackBar, 0, 10, Step = 1)]
        public int ImbSkipEdgeLevels { get => _imbSkipEdgeLevels; set { _imbSkipEdgeLevels = Math.Clamp(value, 0, 10); RecalculateValues(); } }

        [Tab(TabName = "Imbalance", TabOrder = 6)]
        [Display(Name = "Single Prints / Volumen-Voids einbeziehen", GroupName = "Erkennung", Order = 605,
            Description = "An = Level ohne fairen Handel (kein/duennes Volumen) UEBERBRUECKEN den Stack (Ineffizienz), statt ihn zu unterbrechen. Ein Level mit echtem zweiseitigem Handel beendet die Zone. Default an.")]
        public bool ImbIncludeVoids { get => _imbIncludeVoids; set { _imbIncludeVoids = value; RecalculateValues(); } }

        [Tab(TabName = "Imbalance", TabOrder = 6)]
        [Display(Name = "Void-Schwelle (Volumen)", GroupName = "Erkennung", Order = 607,
            Description = "Ein Level gilt als Void, wenn sein Gesamtvolumen (Bid+Ask) <= diesem Wert ist. 0 = nur echte 0/0-Level. Hoeher = auch sehr duenne Level als Void werten. Standard: 0.")]
        [Range(0, 100)]
        [NumericEditor(NumericEditorTypes.TrackBar, 0, 100, Step = 1)]
        [VisibleWhen(nameof(ImbIncludeVoids), true)]
        public int ImbVoidThreshold { get => _imbVoidThreshold; set { _imbVoidThreshold = Math.Max(0, value); RecalculateValues(); } }

        [Tab(TabName = "Imbalance", TabOrder = 6)]
        [Display(Name = "Imbalance-Ratio", GroupName = "Erkennung", Order = 604,
            Description = "Ask[p] >= Ratio * Bid[p-Tick] (Buy) bzw. umgekehrt. 0 auf der Gegenseite = extreme Imbalance (Single Print). Standard: 3.0.")]
        [Range(1.0, 20.0)]
        [NumericEditor(NumericEditorTypes.TrackBar, 1.0, 20.0, Step = 0.5, DisplayFormat = "0.0")]
        public decimal ImbZoneRatio { get => _imbZoneRatio; set { _imbZoneRatio = Math.Max(1m, value); RecalculateValues(); } }

        [Tab(TabName = "Imbalance", TabOrder = 6)]
        [Display(Name = "Max. sichtbare Zonen", GroupName = "Erkennung", Order = 606,
            Description = "Obergrenze gleichzeitig gehaltener Zonen. Aeltere fallen raus. Standard: 20.")]
        [Range(1, 200)]
        public int ImbZoneMaxZones { get => _imbZoneMaxZones; set { _imbZoneMaxZones = Math.Clamp(value, 1, 200); RecalculateValues(); } }

        [Tab(TabName = "Imbalance", TabOrder = 6)]
        [Display(Name = "Arm-Distanz (Ticks)", GroupName = "Re-Test & Hit", Order = 610,
            Description = "So weit muss der Preis von der Zone weg sein, bevor ein erneutes Anlaufen als Re-Test/Hit zaehlt. Standard: 8.")]
        [Range(0, 1000)]
        public int ImbZoneArmTicks { get => _imbZoneArmTicks; set { _imbZoneArmTicks = Math.Max(0, value); RecalculateValues(); } }

        [Tab(TabName = "Imbalance", TabOrder = 6)]
        [Display(Name = "Alarm bei Zonen-Hit (Telegram)", GroupName = "Re-Test & Hit", Order = 612,
            Description = "ATAS-/Telegram-Alarm ausloesen, wenn der Preis eine offene Imbalance-Zone wieder anlaeuft.")]
        public bool ImbZoneAlertOnHit { get => _imbZoneAlertOnHit; set { _imbZoneAlertOnHit = value; } }

        [Tab(TabName = "Imbalance", TabOrder = 6)]
        [Display(Name = "Farbe Buy-Zone", GroupName = "Darstellung", Order = 620,
            Description = "Fuellfarbe fuer Buy-Imbalance-Zonen (Ask-Dominanz).")]
        public Color ColorImbBuy { get => _colorImbBuy; set { _colorImbBuy = value; RedrawChart(); } }

        [Tab(TabName = "Imbalance", TabOrder = 6)]
        [Display(Name = "Farbe Sell-Zone", GroupName = "Darstellung", Order = 622,
            Description = "Fuellfarbe fuer Sell-Imbalance-Zonen (Bid-Dominanz).")]
        public Color ColorImbSell { get => _colorImbSell; set { _colorImbSell = value; RedrawChart(); } }

        // ── Balance-Range ──────────────────────────────────────────────────
        [Tab(TabName = "Range", TabOrder = 4)]
        [Display(Name = "Balance-Range zeichnen", GroupName = "Referenzband", Order = 430,
            Description = "Value Area (VAH/VAL/vPOC) ueber ein rollendes Fenster zeichnen.")]
        public bool RangeEnabled { get => _rangeEnabled; set { _rangeEnabled = value; RecalculateValues(); } }

        [Tab(TabName = "Range", TabOrder = 4)]
        [Display(Name = "Range Lookback (Bars)", GroupName = "Referenzband", Order = 432,
            Description = "Anzahl Bars fuer das Volumen-Profil der Value Area.")]
        [Range(10, 2000)]
        public int RangeLookback { get => _rangeLookback; set { _rangeLookback = Math.Max(10, value); RecalculateValues(); } }

        [Tab(TabName = "Range", TabOrder = 4)]
        [Display(Name = "Value-Area Anteil (%)", GroupName = "Referenzband", Order = 434,
            Description = "Anteil des Volumens in der Value Area (Standard 70%). (Standard: 70)")]
        [Range(30, 95)]
        [NumericEditor(NumericEditorTypes.TrackBar, 30.0, 95.0, Step = 5.0)]
        public int RangeValuePct { get => _rangeValuePct; set { _rangeValuePct = Math.Clamp(value, 30, 95); RecalculateValues(); } }

        [Tab(TabName = "Range", TabOrder = 4)]
        [Display(Name = "Linien nach rechts verlaengern", GroupName = "Referenzband", Order = 436,
            Description = "VAH/VAL/vPOC-Linien nach rechts bis zum aktuellen Bar verlaengern.")]
        public bool RangeExtendRight { get => _rangeExtendRight; set { _rangeExtendRight = value; RedrawChart(); } }

        [Tab(TabName = "Range", TabOrder = 4)]
        [Display(Name = "Farbe Range-Band", GroupName = "Farben", Order = 450,
            Description = "Fuellfarbe des Referenzband-Bereichs (Value Area).")]
        public Color ColorRangeBand { get => _colorRangeBand; set { _colorRangeBand = value; RedrawChart(); } }

        [Tab(TabName = "Range", TabOrder = 4)]
        [Display(Name = "Farbe Range-Raender", GroupName = "Farben", Order = 452,
            Description = "Farbe der Referenzband-Raender (VAH/VAL).")]
        public Color ColorRangeEdge { get => _colorRangeEdge; set { _colorRangeEdge = value; RedrawChart(); } }

        [Tab(TabName = "Range", TabOrder = 4)]
        [Display(Name = "Farbe vPOC", GroupName = "Farben", Order = 454,
            Description = "Farbe der vPOC-Linie im Referenzband.")]
        public Color ColorRangePoc { get => _colorRangePoc; set { _colorRangePoc = value; RedrawChart(); } }

        // ── Range-Detektor ─────────────────────────────────────────────────
        [Tab(TabName = "Range", TabOrder = 4)]
        [Display(Name = "Range-Detektor aktiv", GroupName = "Detektor", Order = 400,
            Description = "Diskrete Konsolidierungen (Balance nach Imbalance) erkennen und als Box markieren.")]
        public bool DetectorEnabled { get => _detectorEnabled; set { _detectorEnabled = value; RecalculateValues(); } }

        [Tab(TabName = "Range", TabOrder = 4)]
        [Display(Name = "Detektor Lookback (Bars)", GroupName = "Detektor", Order = 402,
            Description = "Wie viele Bars der Detektor rueckwaerts nach Konsolidierungen absucht.")]
        [Range(20, 5000)]
        public int DetectorLookback { get => _detectorLookback; set { _detectorLookback = Math.Max(20, value); RecalculateValues(); } }

        [Tab(TabName = "Range", TabOrder = 4)]
        [Display(Name = "Min-Bars pro Range", GroupName = "Detektor", Order = 404,
            Description = "So viele Bars muss der Preis im engen Band bleiben, damit es als Balance zaehlt.")]
        [Range(3, 200)]
        public int DetectorMinBars { get => _detectorMinBars; set { _detectorMinBars = Math.Max(3, value); RecalculateValues(); } }

        [Tab(TabName = "Range", TabOrder = 4)]
        [Display(Name = "Breiten-Faktor (x ATR)", GroupName = "Detektor", Order = 406,
            Description = "Max. Range-Hoehe = Faktor * ATR (stabil, kein Drift). Kleiner = engere Balances. (Standard: 3.0)")]
        [Range(0.5, 20.0)]
        [NumericEditor(NumericEditorTypes.TrackBar, 0.5, 20.0, Step = 0.5, DisplayFormat = "0.0")]
        public decimal DetectorWidthFactor { get => _detectorWidthFactor; set { _detectorWidthFactor = value; RecalculateValues(); } }

        [Tab(TabName = "Range", TabOrder = 4)]
        [Display(Name = "Breiten-ATR Periode", GroupName = "Detektor", Order = 408,
            Description = "Periode fuer die ATR, die die Range-Breite bestimmt (fix je Range -> kein Repaint).")]
        [Range(2, 200)]
        public int DetectorAtrPeriod { get => _detectorAtrPeriod; set { _detectorAtrPeriod = Math.Max(2, value); RecalculateValues(); } }

        [Tab(TabName = "Range", TabOrder = 4)]
        [Display(Name = "Volumen-Akzeptanz (Auktions-Balance)", GroupName = "Detektor", Order = 410,
            Description = "Range nur gueltig, wenn sie genug Volumen UND einen klaren vPOC hat (echte Balance statt nur 'Preis war eng').")]
        public bool DetectorVolumeFilter { get => _detectorVolumeFilter; set { _detectorVolumeFilter = value; RecalculateValues(); } }

        [Tab(TabName = "Range", TabOrder = 4)]
        [Display(Name = "Klarer vPOC Faktor", GroupName = "Detektor", Order = 412,
            Description = "POC-Level-Volumen muss >= Faktor * Ø Level-Volumen sein (gepeakte Verteilung). Hoeher = strenger. (Standard: 1.5)")]
        [Range(1.0, 10.0)]
        [NumericEditor(NumericEditorTypes.TrackBar, 1.0, 10.0, Step = 0.5, DisplayFormat = "0.0")]
        [VisibleWhen(nameof(DetectorVolumeFilter), true)]
        public decimal DetectorPocFactor { get => _detectorPocFactor; set { _detectorPocFactor = value; RecalculateValues(); } }

        [Tab(TabName = "Range", TabOrder = 4)]
        [Display(Name = "Min-Volumen Faktor (vs Umfeld)", GroupName = "Detektor", Order = 414,
            Description = "Range-Ø-Bar-Volumen muss >= Faktor * Umfeld-Ø-Bar-Volumen sein (kein toter Drift). 0 = aus. (Standard: 0.7)")]
        [Range(0.0, 5.0)]
        [NumericEditor(NumericEditorTypes.TrackBar, 0.0, 5.0, Step = 0.25, DisplayFormat = "0.00")]
        [VisibleWhen(nameof(DetectorVolumeFilter), true)]
        public decimal DetectorMinVolFactor { get => _detectorMinVolFactor; set { _detectorMinVolFactor = value; RecalculateValues(); } }

        [Tab(TabName = "Range", TabOrder = 4)]
        [Display(Name = "Boxen zusammenführen (Merging)", GroupName = "Detektor", Order = 416,
            Description = "Benachbarte, preislich ueberlappende Balances zu einer Box vereinen (gegen Aufsplittung).")]
        public bool DetectorMerge { get => _detectorMerge; set { _detectorMerge = value; RecalculateValues(); } }

        [Tab(TabName = "Range", TabOrder = 4)]
        [Display(Name = "Merge max. Lücke (Bars)", GroupName = "Detektor", Order = 418,
            Description = "Hoechstens so viele Bars duerfen zwischen zwei Ranges liegen, damit sie vereint werden.")]
        [Range(0, 200)]
        [VisibleWhen(nameof(DetectorMerge), true)]
        public int DetectorMergeGapBars { get => _detectorMergeGapBars; set { _detectorMergeGapBars = Math.Max(0, value); RecalculateValues(); } }

        [Tab(TabName = "Range", TabOrder = 4)]
        [Display(Name = "Farbe Break Up", GroupName = "Farben", Order = 456,
            Description = "Farbe fuer nach oben aufgeloeste (gebrochene) Detektor-Boxen.")]
        public Color ColorBreakUp { get => _colorBreakUp; set { _colorBreakUp = value; RedrawChart(); } }

        [Tab(TabName = "Range", TabOrder = 4)]
        [Display(Name = "Farbe Break Down", GroupName = "Farben", Order = 458,
            Description = "Farbe fuer nach unten aufgeloeste (gebrochene) Detektor-Boxen.")]
        public Color ColorBreakDn { get => _colorBreakDn; set { _colorBreakDn = value; RedrawChart(); } }

        [Tab(TabName = "Range", TabOrder = 4)]
        [Display(Name = "Farbe Range aktiv/flat", GroupName = "Farben", Order = 460,
            Description = "Farbe fuer aktive/laufende Detektor-Boxen (noch nicht gebrochen).")]
        public Color ColorFlat { get => _colorFlat; set { _colorFlat = value; RedrawChart(); } }

        // ─────────────────────────────────────────────────────────────────
        //  PROPERTIES — Darstellung / Farben
        // ─────────────────────────────────────────────────────────────────
        [Tab(TabName = "Allgemein", TabOrder = 1)]
        [Display(Name = "Schriftgroesse", GroupName = "HUD & Panel", Order = 114,
            Description = "Schriftgroesse des HUD-Panels.")]
        [Range(8, 30)]
        public int FontSize { get => _fontSize; set { _fontSize = Math.Clamp(value, 8, 30); BuildFonts(); RedrawChart(); } }

        [Tab(TabName = "Allgemein", TabOrder = 1)]
        [Display(Name = "Oben Links (aus = Oben Rechts)", GroupName = "HUD & Panel", Order = 116,
            Description = "HUD oben links statt oben rechts andocken.")]
        public bool TopLeft { get => _topLeft; set { _topLeft = value; RedrawChart(); } }

        [Tab(TabName = "Allgemein", TabOrder = 1)]
        [Display(Name = "Abstand X (px)", GroupName = "HUD & Panel", Order = 118,
            Description = "Horizontaler Abstand des HUD vom Chartrand (px).")]
        [Range(0, 600)]
        public int OffsetX { get => _offsetX; set { _offsetX = value; RedrawChart(); } }

        [Tab(TabName = "Allgemein", TabOrder = 1)]
        [Display(Name = "Abstand Y (px)", GroupName = "HUD & Panel", Order = 120,
            Description = "Vertikaler Abstand des HUD vom Chartrand (px).")]
        [Range(0, 600)]
        public int OffsetY { get => _offsetY; set { _offsetY = value; RedrawChart(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Marker-Abstand (Ticks)", GroupName = "Marker", Order = 214,
            Description = "Abstand der Marker vom Kerzen-Extrem in Ticks.")]
        [Range(0, 100)]
        public int MarkerTickOffset { get => _markerTickOffset; set { _markerTickOffset = value; RedrawChart(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Farbe Bull", GroupName = "Marker", Order = 216,
            Description = "Farbe der Bull-Signal-Marker.")]
        public Color ColorBull { get => _colorBull; set { _colorBull = value; RedrawChart(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Farbe Bear", GroupName = "Marker", Order = 218,
            Description = "Farbe der Bear-Signal-Marker.")]
        public Color ColorBear { get => _colorBear; set { _colorBear = value; RedrawChart(); } }

        [Tab(TabName = "Signal", TabOrder = 2)]
        [Display(Name = "Farbe Neutral", GroupName = "Marker", Order = 220,
            Description = "Farbe fuer neutrale/schwache Signale.")]
        public Color ColorNeutral { get => _colorNeutral; set { _colorNeutral = value; RedrawChart(); } }

        [Tab(TabName = "Allgemein", TabOrder = 1)]
        [Display(Name = "Hintergrund", GroupName = "HUD & Panel", Order = 122,
            Description = "Hintergrundfarbe des HUD-Panels.")]
        public Color ColorBackground { get => _colorBackground; set { _colorBackground = value; RedrawChart(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Farbe Reversal Long", GroupName = "Farben", Order = 360,
            Description = "Farbe der Long-Reversal-Rauten (Boden).")]
        public Color ColorRevBull { get => _colorRevBull; set { _colorRevBull = value; RedrawChart(); } }

        [Tab(TabName = "Reversal", TabOrder = 3)]
        [Display(Name = "Farbe Reversal Short", GroupName = "Farben", Order = 362,
            Description = "Farbe der Short-Reversal-Rauten (Top).")]
        public Color ColorRevBear { get => _colorRevBear; set { _colorRevBear = value; RedrawChart(); } }

        // ─────────────────────────────────────────────────────────────────
        //  CTOR
        // ─────────────────────────────────────────────────────────────────
        public OrderflowSignal() : base(true)
        {
            EnableCustomDrawing = true;
            DrawAbovePrice = true;
            DataSeries[0].IsHidden = true;
            AlertsEnabled = true;   // ATAS-Alarme aktiv (Telegram laeuft ueber die ATAS-Anbindung)

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
            // Ab hier ist die Historie nachgeholt -> ab jetzt duerfen Alarme feuern
            // (waehrend der obigen Schleife war _histDone == false = stumm).
            _histDone = true;

            if (bar != CurrentBar - 1)
                return;

            // Big-Trade-Levels aus der Historie rekonstruieren (einmal pro Laden),
            // damit sie ein Chart-Reload / Hot-Reload ueberleben (Live-Stream wird
            // fuer die Historie nicht neu abgespielt).
            if (_bigEnabled && CurrentBar > 1 && (!_bigReqDone || _bigDirty))
            {
                _bigReqDone = true;
                _bigDirty = false;
                RequestBigLevelHistory();
            }

            ComputeLive();
            ComputeBalanceRange();
            ProcessDetector();

            string key = $"{_hudBull}|{_hudBear}|{_hudSignal}|{_hudRev}|{_hudTags}|{_hudWarn}|{_chartLabel}|" +
                         $"{_liveVolThr}|{_liveDeltaThr}|{_liveAbsThr}|{_freezeCalibration}|" +
                         $"{_rangeVah}|{_rangeVal}|{_rangeVpoc}|{CurrentBar}";
            if (key != _lastRenderKey)
            {
                _lastRenderKey = key;
                RedrawChart();
            }
        }

        // Live-Tape: jeder Cumulative-Trade ab Mindestgroesse wird dem aktuell
        // bildenden Bar netto-signiert gutgeschrieben (Buy +, Sell -). Laeuft auf
        // einem Fremd-Thread -> nur in die ConcurrentDictionary schreiben, kein
        // RedrawChart (OnCalculate liest die Werte beim naechsten Tick).
        protected override void OnCumulativeTrade(CumulativeTrade trade)
        {
            if (trade == null)
                return;
            int bar = CurrentBar - 1;
            if (bar < 0)
                return;

            int dir = trade.Direction == TradeDirection.Buy ? 1
                    : trade.Direction == TradeDirection.Sell ? -1 : 0;
            if (dir == 0)
                return;

            // Tape-Bedingung (eigene Schwelle).
            if (_tapeEnabled && trade.Volume >= _tapeMinContracts)
            {
                int signed = dir * (int)trade.Volume;
                _tapeNet.AddOrUpdate(bar, signed, (_, cur) => cur + signed);
            }

            // Big-Trade-Levels im KUMULATIVEN Modus (separiert laeuft ueber OnNewTrade).
            if (_bigEnabled && !_bigSeparated && trade.Volume >= BigThresholdFor(trade.Time))
                AddBigLevel(trade.Lastprice, (int)trade.Volume, dir, bar);
        }

        // Separierte Big Trades: jeder EINZELNE Print >= Schwelle wird ein Level
        // (nicht kumulativ aggregiert). Default-Modus.
        protected override void OnNewTrade(MarketDataArg trade)
        {
            if (!_bigEnabled || !_bigSeparated || trade == null)
                return;
            int dir = trade.Direction == TradeDirection.Buy ? 1
                    : trade.Direction == TradeDirection.Sell ? -1 : 0;
            if (dir == 0)
                return;
            int bar = CurrentBar - 1;
            if (bar < 0)
                return;
            if (trade.Volume >= BigThresholdFor(trade.Time))
                AddBigLevel(trade.Price, (int)trade.Volume, dir, bar);
        }

        // Session-abhaengige Mindestgroesse fuer ein Big-Trade-Level.
        private int BigThresholdFor(DateTime t)
        {
            int h = t.Hour;
            bool london = _bigLondonStartHour <= _bigLondonEndHour
                ? (h >= _bigLondonStartHour && h < _bigLondonEndHour)
                : (h >= _bigLondonStartHour || h < _bigLondonEndHour);
            return london ? _bigMinLondon : _bigMinDefault;
        }

        // Big-Trade als Level speichern. Dedupe am gleichen Preis unabhaengig von der
        // Richtung -> Buy und Sell am selben Preis werden EINE Linie (kein Ueberlappen).
        // Buy/Sell-Volumen getrennt gefuehrt, Richtung = dominante Seite.
        private void AddBigLevel(decimal price, int vol, int dir, int bar)
        {
            decimal tick = InstrumentInfo?.TickSize ?? 0m;
            decimal tol = tick * _bigHitTolerance;
            lock (_bigLock)
            {
                foreach (var l in _bigLevels)
                {
                    if (!l.Hit && Math.Abs(l.Price - price) <= tol)
                    {
                        if (dir > 0) l.BuyVol += vol; else l.SellVol += vol;
                        l.Volume = l.BuyVol + l.SellVol;
                        l.Dir = l.BuyVol >= l.SellVol ? 1 : -1;   // dominante Seite
                        l.Bar = bar;
                        return;
                    }
                }
                _bigLevels.Add(new BigLevel
                {
                    Price = price, Volume = vol, Dir = dir, Bar = bar,
                    BuyVol = dir > 0 ? vol : 0, SellVol = dir < 0 ? vol : 0
                });
                while (_bigLevels.Count > _bigMaxLevels && _bigLevels.Count > 0)
                    _bigLevels.RemoveAt(0);
            }
        }

        // Re-Test-Erkennung: ein „scharfgemachtes" Level (Preis war weg) wird wieder
        // angelaufen -> Hit + Alarm (einmalig). Wird live je Tick aus ComputeLive geprueft.
        private void CheckBigLevelHits(IndicatorCandle c, int bar)
        {
            if (!_bigEnabled || c == null)
                return;
            decimal tick = InstrumentInfo?.TickSize ?? 0m;
            decimal armDist = tick * _bigArmTicks;
            decimal tol = tick * _bigHitTolerance;

            lock (_bigLock)
            {
                foreach (var l in _bigLevels)
                {
                    if (l.Hit)
                        continue;
                    if (l.Bar > bar)
                        continue;         // Level existierte an diesem Bar noch nicht
                    if (!l.Armed)
                    {
                        if (Math.Abs(c.Close - l.Price) >= armDist)
                            l.Armed = true;   // Preis hat das Level verlassen -> scharf
                        continue;
                    }
                    if (c.Low - tol <= l.Price && l.Price <= c.High + tol)
                    {
                        l.Hit = true;
                        if (_histDone && !_bigReplaying && _bigAlertOnHit)
                        {
                            AlertsEnabled = true;
                            string side = l.Dir > 0 ? "Buy" : "Sell";
                            try { AddAlert(_alertSound, $"Big-Trade-Level {l.Price} ({l.Volume} {side}) angelaufen | {InstrumentInfo?.Instrument} | {c.LastTime:HH:mm}"); }
                            catch { }
                        }
                    }
                }
            }
        }

        // Historien-Anfrage: kumulative Trades ueber den geladenen Zeitraum holen,
        // um die Big-Trade-Levels nach einem Reload zu rekonstruieren.
        private void RequestBigLevelHistory()
        {
            try
            {
                var c0 = GetCandle(0);
                var cN = GetCandle(CurrentBar - 1);
                if (c0 == null || cN == null)
                    return;
                int minVol = Math.Min(_bigMinLondon, _bigMinDefault);
                if (minVol < 1) minVol = 1;
                var req = new CumulativeTradesRequest(c0.Time, cN.LastTime, minVol, int.MaxValue);
                _bigReqId = req.RequestId;
                RequestForCumulativeTrades(req);
            }
            catch { }
        }

        protected override void OnCumulativeTradesResponse(CumulativeTradesRequest request, IEnumerable<CumulativeTrade> cumulativeTrades)
        {
            if (cumulativeTrades == null)
                return;
            if (_bigReqId != 0 && request != null && request.RequestId != _bigReqId)
                return;   // gehoert zu einer anderen Anfrage
            RebuildBigLevelsFromHistory(cumulativeTrades);
        }

        // Levels aus historischen Trades neu aufbauen: separated = jeder EINZELNE Tick
        // >= Session-Schwelle wird ein Level (identisch zur Live-Logik in OnNewTrade).
        private void RebuildBigLevelsFromHistory(IEnumerable<CumulativeTrade> trades)
        {
            if (!_bigEnabled)
                return;
            int lastBar = CurrentBar - 1;
            if (lastBar < 0)
                return;

            lock (_bigLock) { _bigLevels.Clear(); }

            int barPtr = 0;
            int minLevelBar = lastBar;
            foreach (var ctd in trades.OrderBy(t => t.Time))
            {
                if (ctd?.Ticks == null)
                    continue;
                foreach (var tk in ctd.Ticks)
                {
                    if (tk == null)
                        continue;
                    int dir = tk.Direction == TradeDirection.Buy ? 1
                            : tk.Direction == TradeDirection.Sell ? -1 : 0;
                    if (dir == 0)
                        continue;
                    if (tk.Volume < BigThresholdFor(tk.Time))
                        continue;
                    int b = FindBarByTime(tk.Time, ref barPtr, lastBar);
                    AddBigLevel(tk.Price, (int)tk.Volume, dir, b);
                    if (b < minLevelBar) minLevelBar = b;
                }
            }

            // Hit-Status historisch nachspielen (ohne Alarm) -> gehittete Levels
            // erscheinen wieder gestrichelt, ungetestete durchgezogen.
            _bigReplaying = true;
            for (int b = Math.Max(0, minLevelBar); b <= lastBar; b++)
            {
                var cc = GetCandle(b);
                if (cc != null)
                    CheckBigLevelHits(cc, b);
            }
            _bigReplaying = false;

            RedrawChart();
        }

        // Ctrl + Links-Klick auf eine Big-Trade-Linie -> dieses Level einzeln loeschen.
        public override bool ProcessMouseClick(RenderControlMouseEventArgs e)
        {
            if (e == null || !_bigEnabled || !e.Control || e.Button != RenderControlMouseButtons.Left)
                return base.ProcessMouseClick(e);

            var cont = ChartInfo?.PriceChartContainer;
            if (cont == null)
                return false;
            var region = cont.Region;
            if (e.X < region.Left || e.X > region.Right)
                return false;   // nur im Kurschart-Bereich

            const int tolPx = 6;
            BigLevel target = null;
            int best = int.MaxValue;
            lock (_bigLock)
            {
                foreach (var l in _bigLevels)
                {
                    int y;
                    try { y = cont.GetYByPrice(l.Price, false); }
                    catch { continue; }
                    int d = Math.Abs(e.Y - y);
                    if (d <= tolPx && d < best) { best = d; target = l; }
                }
                if (target != null)
                    _bigLevels.Remove(target);
            }

            if (target != null)
            {
                RedrawChart();
                return true;   // Klick verbraucht
            }
            return false;
        }

        // Bar-Index zu einem Trade-Zeitpunkt (Trades + Bars aufsteigend -> laufender Pointer).
        private int FindBarByTime(DateTime t, ref int barPtr, int lastBar)
        {
            while (barPtr < lastBar)
            {
                var next = GetCandle(barPtr + 1);
                if (next == null || next.Time > t)
                    break;      // t liegt noch im aktuellen Bar
                barPtr++;
            }
            return barPtr;
        }

        // Imbalance-Zonen fuer eine Kerze verarbeiten: (1) offene Zonen gegen diese Kerze
        // auf Arm/Fill/Approach pruefen, (2) optional neue Zonen aus dem Footprint erkennen.
        // Fill = die Zone wird GEGEN-imbalanced (durchgenadelt), nicht schon beim Touch.
        private void ProcessImbZones(int bar, IndicatorCandle c, bool detectNew)
        {
            if (!_imbZonesEnabled || c == null)
                return;
            decimal tick = InstrumentInfo?.TickSize ?? 0m;
            if (tick <= 0)
                return;

            var bid = new Dictionary<decimal, decimal>();
            var ask = new Dictionary<decimal, decimal>();
            foreach (var pv in c.GetAllPriceLevels())
            {
                bid[pv.Price] = pv.Bid;
                ask[pv.Price] = pv.Ask;
            }
            if (bid.Count == 0)
                return;

            bool BuyImb(decimal p)
            {
                if (!ask.TryGetValue(p, out var a) || a <= 0) return false;
                decimal b = bid.TryGetValue(p - tick, out var bv) ? bv : 0m;
                return b <= 0 ? true : a >= _imbZoneRatio * b;   // 0-Gegenseite = extrem
            }
            bool SellImb(decimal p)
            {
                if (!bid.TryGetValue(p, out var b) || b <= 0) return false;
                decimal a = ask.TryGetValue(p + tick, out var av) ? av : 0m;
                return a <= 0 ? true : b >= _imbZoneRatio * a;
            }

            // (1) Arm / Fill / Approach gegen bestehende, aeltere offene Zonen.
            decimal armDist = tick * _imbZoneArmTicks;
            foreach (var z in _imbZones)
            {
                if (z.Filled || z.Bar >= bar)
                    continue;
                if (!z.Armed && (c.Low > z.High + armDist || c.High < z.Low - armDist))
                    z.Armed = true;   // Preis hat die Zone verlassen -> scharf

                // Fill: Gegen-Imbalance IN der Zone (buy-Zone durch sell-Imbalance gefuellt).
                bool counter = false;
                foreach (var p in bid.Keys)
                {
                    if (p < z.Low || p > z.High) continue;
                    if (z.Dir > 0 ? SellImb(p) : BuyImb(p)) { counter = true; break; }
                }
                if (counter) { z.Filled = true; continue; }

                // Approach-Alarm: scharf + Preis wieder in der Zone (einmalig).
                if (z.Armed && !z.Alerted && c.Low <= z.High && c.High >= z.Low)
                {
                    z.Alerted = true;
                    if (_histDone && _imbZoneAlertOnHit)
                        FireImbZoneAlert(z, c);
                }
            }

            // (2) Neue Zonen aus diesem (abgeschlossenen) Bar (Grid-basiert, Voids ueberbrueckt).
            if (detectNew)
            {
                foreach (var run in DetectImbRuns(c))
                    AddImbZone(run.Low, run.High, run.Dir, bar, run.Len);
            }
        }

        // Imbalance-Laeufe einer Kerze ueber das TICK-GRID erkennen.
        // Level-Klasse: Void (kein/duenner Handel), Buy-Imb, Sell-Imb, Fair (zweiseitig).
        // Ein Lauf = zusammenhaengende Nicht-Fair-Level gleicher Aggressor-Richtung;
        // Voids UEBERBRUECKEN (kein fairer Handel = Ineffizienz), Fair BEENDET. Docht-Rand
        // ausgeschlossen. Nur Laeufe mit >= 1 Aggressor-Level UND Laenge >= Min-Stack.
        private List<(decimal Low, decimal High, int Dir, int Len)> DetectImbRuns(IndicatorCandle c)
        {
            var runs = new List<(decimal, decimal, int, int)>();
            decimal tick = InstrumentInfo?.TickSize ?? 0m;
            if (tick <= 0 || c == null)
                return runs;
            var bid = new Dictionary<decimal, decimal>();
            var ask = new Dictionary<decimal, decimal>();
            decimal lo = decimal.MaxValue, hi = decimal.MinValue;
            foreach (var pv in c.GetAllPriceLevels())
            {
                bid[pv.Price] = pv.Bid; ask[pv.Price] = pv.Ask;
                if (pv.Price < lo) lo = pv.Price;
                if (pv.Price > hi) hi = pv.Price;
            }
            if (bid.Count == 0)
                return runs;

            decimal effLo = lo + _imbSkipEdgeLevels * tick;   // Docht-Rand weg
            decimal effHi = hi - _imbSkipEdgeLevels * tick;
            if (effHi < effLo)
                return runs;

            // 1 = Buy, -1 = Sell, 0 = Fair, 2 = Void
            int Classify(decimal p)
            {
                decimal b = bid.TryGetValue(p, out var bv) ? bv : 0m;
                decimal a = ask.TryGetValue(p, out var av) ? av : 0m;
                if (a + b <= _imbVoidThreshold)
                    return _imbIncludeVoids ? 2 : 0;   // Void ueberbrueckt nur, wenn aktiviert
                decimal bBelow = bid.TryGetValue(p - tick, out var bb) ? bb : 0m;
                bool buy = a > 0 && (bBelow <= 0 ? true : a >= _imbZoneRatio * bBelow);
                decimal aAbove = ask.TryGetValue(p + tick, out var aa) ? aa : 0m;
                bool sell = b > 0 && (aAbove <= 0 ? true : b >= _imbZoneRatio * aAbove);
                if (buy && !sell) return 1;
                if (sell && !buy) return -1;
                return 0;   // Fair oder ambivalent
            }

            bool inRun = false; int runDir = 0, len = 0, aggr = 0; decimal runLo = 0, runHi = 0;
            void Flush()
            {
                if (inRun && aggr > 0 && len >= _imbZoneMinStack)
                    runs.Add((runLo, runHi, runDir, len));
                inRun = false; runDir = 0; len = 0; aggr = 0;
            }
            for (decimal p = effLo; p <= effHi + tick * 0.5m; p += tick)
            {
                int cls = Classify(p);
                if (cls == 0) { Flush(); continue; }        // Fair beendet
                if (cls == 2) { if (inRun) { len++; runHi = p; } continue; }  // Void ueberbrueckt (startet nicht)
                if (inRun && cls != runDir) Flush();        // Gegenrichtung beendet
                if (!inRun) { inRun = true; runLo = p; }
                runDir = cls; aggr++; len++; runHi = p;
            }
            Flush();
            return runs;
        }

        // Zone speichern; ueberlappende offene Zonen gleicher Richtung verschmelzen.
        private void AddImbZone(decimal low, decimal high, int dir, int bar, int stack)
        {
            foreach (var z in _imbZones)
            {
                if (!z.Filled && z.Dir == dir && low <= z.High && z.Low <= high)
                {
                    z.Low = Math.Min(z.Low, low);
                    z.High = Math.Max(z.High, high);
                    z.Stack = Math.Max(z.Stack, stack);
                    return;
                }
            }
            _imbZones.Add(new ImbZone { Low = low, High = high, Dir = dir, Bar = bar, Stack = stack });
            while (_imbZones.Count > _imbZoneMaxZones && _imbZones.Count > 0)
                _imbZones.RemoveAt(0);
        }

        // (A) Hat die Kerze einen frischen, gestapelten Imbalance-Flip in Richtung dir?
        // dir > 0 = Buy-Stack (stuetzt Long-Umkehr am Tief), dir < 0 = Sell-Stack.
        // Docht-Rand ausgeschlossen (kein Finished-Auction-Tip).
        private bool HasImbStack(IndicatorCandle c, int dir)
        {
            foreach (var r in DetectImbRuns(c))
                if (r.Dir == dir) return true;
            return false;
        }

        // (B) Ist die Auktion am Extrem ABGESCHLOSSEN? Unfinished = Imbalance-Print am
        // aeussersten Level (Low: Sell-Imbalance, High: Buy-Imbalance) -> Momentum evtl.
        // weiter, Umkehr riskant. Finished = Gegenseite hat gestoppt -> stuetzt Umkehr.
        private bool AuctionFinishedAtExtreme(IndicatorCandle c, bool atLow)
        {
            decimal tick = InstrumentInfo?.TickSize ?? 0m;
            if (tick <= 0 || c == null)
                return false;
            var bid = new Dictionary<decimal, decimal>();
            var ask = new Dictionary<decimal, decimal>();
            decimal ext = atLow ? decimal.MaxValue : decimal.MinValue;
            foreach (var pv in c.GetAllPriceLevels())
            {
                bid[pv.Price] = pv.Bid; ask[pv.Price] = pv.Ask;
                if (atLow ? pv.Price < ext : pv.Price > ext) ext = pv.Price;
            }
            if (bid.Count == 0)
                return false;

            if (atLow)
            {
                decimal b = bid.TryGetValue(ext, out var bv) ? bv : 0m;
                decimal a = ask.TryGetValue(ext + tick, out var av) ? av : 0m;
                bool unfinished = b > 0 && (a <= 0 ? true : b >= _imbZoneRatio * a);
                return !unfinished;
            }
            else
            {
                decimal a = ask.TryGetValue(ext, out var av) ? av : 0m;
                decimal b = bid.TryGetValue(ext - tick, out var bv) ? bv : 0m;
                bool unfinished = a > 0 && (b <= 0 ? true : a >= _imbZoneRatio * b);
                return !unfinished;
            }
        }

        private void FireImbZoneAlert(ImbZone z, IndicatorCandle c)
        {
            string side = z.Dir > 0 ? "Buy" : "Sell";
            string instr = InstrumentInfo?.Instrument ?? "";
            AlertsEnabled = true;
            try { AddAlert(_alertSound, $"Imbalance-Zone {z.Low}-{z.High} ({side}, Stack {z.Stack}) angelaufen | {instr} | {c?.LastTime:HH:mm}"); }
            catch { }
        }

        private void ResetState()
        {
            _volArr.Clear();
            _absDArr.Clear();
            _mldArr.Clear();
            _cumDeltaArr.Clear();
            _cumDeltaRun = 0;
            _cumPv = 0;
            _cumVol = 0;
            _lastProcessedBar = -1;
            _signals.Clear();
            _revSignals.Clear();
            _lastSignalBar = -1;
            _lastRevBar = -1;
            _hoverRevBar = -1;
            _revDbgByBar.Clear();
            _btPending.Clear();
            _histDone = false;   // erst nach erneutem Historien-Nachladen wieder alarmieren
            // _bigReqDone NICHT zuruecksetzen: Levels ueberleben einen reinen Recalc.
            // Neu-Anfrage nur bei frischer Instanz (_bigReqDone==false) oder _bigDirty.
            _rangeValid = false;
            _detRanges.Clear();
            _lastDetBar = -1;
            _candActive = false;
            _imbZones.Clear();   // werden aus der Historie (Candle-Cluster) neu erkannt
            // _frz* NICHT zuruecksetzen -> eingefrorene Kalibrierung ueberlebt Recalc.
            _hudBull = _hudBear = _hudSignal = _hudRev = 0;
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

            UpdateBacktestLog(bar, c);   // offene Backtest-Eintraege mit diesem Bar fortschreiben/abschliessen

            // Metriken fuer das Perzentil-Fenster speichern.
            decimal signedMld = MaxLevelDeltaSigned(c);
            StoreMetric(_volArr, bar, c.Volume);
            StoreMetric(_absDArr, bar, Math.Abs(c.Delta));
            StoreMetric(_mldArr, bar, Math.Abs(signedMld));

            // Session-VWAP + kumuliertes Delta (CVD) fortschreiben.
            if (IsNewSession(bar))
            {
                _cumPv = 0;
                _cumVol = 0;
                _cumDeltaRun = 0;
            }
            _cumPv += BarPriceForVwap(c) * c.Volume;
            _cumVol += c.Volume;
            decimal vwap = _cumVol > 0 ? _cumPv / _cumVol : 0;

            _cumDeltaRun += c.Delta;
            StoreMetric(_cumDeltaArr, bar, _cumDeltaRun);

            var o = EvaluateBar(bar, c, signedMld, vwap, _cumVol > 0);
            int sig = DetermineSignal(o.Bull, o.Bear);
            if (sig != 0 && _lastSignalBar >= 0 && _signalCooldownBars > 0
                && (bar - _lastSignalBar) <= _signalCooldownBars)
                sig = 0;

            SetSignal(bar, SignedScore(sig, o));
            if (sig != 0)
                _lastSignalBar = bar;

            int rev = RevEvaluate(bar, c, signedMld, _cumDeltaRun);
            if (rev != 0 && _revConfirm && !RevConfirmed(bar, Math.Sign(rev)))
                rev = 0;   // 2-Kerzen-Bestaetigung fehlt
            if (rev != 0 && _lastRevBar >= 0 && _signalCooldownBars > 0
                && (bar - _lastRevBar) <= _signalCooldownBars)
                rev = 0;
            SetRevSignal(bar, rev);
            if (rev != 0)
            {
                _lastRevBar = bar;
                // Treiber-Aufschluesselung der ANGEZEIGTEN Raute fuer die Hover-Diagnose.
                _revDbgByBar[bar] = _revCand;
                AddBacktestPending(c, rev);   // Backtest-Log: neue Umkehr aufnehmen (Ausgang folgt)
            }

            // Alarm (Telegram via ATAS) — NUR live (nach Historien-Nachladen), nicht rueckwirkend.
            if (rev != 0 && _histDone && _alertOnReversal)
                FireReversalAlert(bar, rev, c);

            // Imbalance-Zonen: erst offene Zonen gegen diesen Bar pruefen, dann neue erkennen.
            ProcessImbZones(bar, c, detectNew: true);
        }

        // Loest einen ATAS-Alarm aus (geht automatisch durch die konfigurierte
        // Telegram-Anbindung). rev > 0 = Long-Umkehr, rev < 0 = Short-Umkehr.
        private void FireReversalAlert(int bar, int rev, IndicatorCandle c)
        {
            if (rev > 0 && !_alertLong) return;
            if (rev < 0 && !_alertShort) return;

            string dir = rev > 0 ? "LONG-Umkehr ▲" : "SHORT-Umkehr ▼";
            string instr = InstrumentInfo?.Instrument ?? "";
            string msg = $"Orderflow {instr}: {dir} | Score {Math.Abs(rev)}% | {c?.Close} | {c?.LastTime:HH:mm}";

            // Defensiv: AlertsEnabled direkt vor dem Senden setzen (falls beim Laden
            // ueberschrieben). Einfache Overload (ohne Color) -> kein WPF noetig.
            AlertsEnabled = true;
            try { AddAlert(_alertSound, msg); }
            catch { /* Alarm darf nie die Berechnung stoppen */ }
        }

        // Backtest-Log: neue Umkehr als offenen Eintrag aufnehmen (Treiber-Snapshot + Entry).
        private void AddBacktestPending(IndicatorCandle c, int rev)
        {
            if (!_btLog) return;
            int dir = Math.Sign(rev);
            decimal extreme = dir > 0 ? c.Low : c.High;
            _btPending.Add(new BtPending
            {
                Dir = dir, Age = 0, Pct = Math.Abs(rev),
                Entry = c.Close, Mfe = 0, Mae = 0, Time = c.LastTime,
                D = _revCand, Kl = NearestKeyLevel(extreme).HasValue
            });
        }

        // Offene Eintraege je geschlossenem Bar fortschreiben (MFE/MAE) und nach Horizont abschliessen.
        private void UpdateBacktestLog(int bar, IndicatorCandle c)
        {
            if (!_btLog || _btPending.Count == 0) return;
            decimal tick = InstrumentInfo?.TickSize ?? 0m;
            if (tick <= 0m) tick = 0.25m;
            for (int i = _btPending.Count - 1; i >= 0; i--)
            {
                var p = _btPending[i];
                decimal fav = p.Dir > 0 ? c.High - p.Entry : p.Entry - c.Low;   // fuer uns
                decimal adv = p.Dir > 0 ? p.Entry - c.Low : c.High - p.Entry;   // gegen uns
                if (fav > p.Mfe) p.Mfe = fav;
                if (adv > p.Mae) p.Mae = adv;
                p.Age++;
                if (p.Age >= _btHorizon)
                {
                    decimal net = p.Dir > 0 ? c.Close - p.Entry : p.Entry - c.Close;
                    WriteBacktestRow(p, tick, net);
                    _btPending.RemoveAt(i);
                }
                else _btPending[i] = p;
            }
        }

        // Eine abgeschlossene Umkehr als CSV-Zeile anhaengen.
        private void WriteBacktestRow(BtPending p, decimal tick, decimal net)
        {
            try
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ATAS", "ofs_backtest");
                System.IO.Directory.CreateDirectory(dir);
                string instr = InstrumentInfo?.Instrument ?? "instr";
                foreach (var ch in System.IO.Path.GetInvalidFileNameChars()) instr = instr.Replace(ch, '_');
                string path = System.IO.Path.Combine(dir, instr + ".csv");
                if (!System.IO.File.Exists(path))
                    System.IO.File.AppendAllText(path,
                        "Time,Chart,Dir,Entry,Pct,Eff,DIV,ABS,VP,EXH,SPD,IMB,AUC,ACn,PWR,CDl,KL,MFEt,MAEt,NETt\n");
                int B(bool b) => b ? 1 : 0;
                string row = string.Join(",",
                    p.Time.ToString("yyyy-MM-dd HH:mm:ss", inv),
                    BuildChartLabel().Trim('(', ')').Replace(',', ' '),
                    p.Dir > 0 ? "L" : "S",
                    p.Entry.ToString(inv),
                    p.Pct.ToString(inv),
                    p.D.Eff.ToString("0.00", inv),
                    B(p.D.Div), B(p.D.Abs), B(p.D.Vp), B(p.D.Exh),
                    p.D.SpdX.ToString("0.0", inv), B(p.D.Imb), B(p.D.Auc),
                    p.D.AcnN.ToString(inv), p.D.PwrX.ToString("0.0", inv), p.D.CdlX.ToString("0.0", inv),
                    B(p.Kl),
                    (p.Mfe / tick).ToString("0.0", inv),
                    (p.Mae / tick).ToString("0.0", inv),
                    (net / tick).ToString("0.0", inv));
                System.IO.File.AppendAllText(path, row + "\n");
            }
            catch { }
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
            SetSignal(last, SignedScore(sig, o));

            decimal baseCd = IsNewSession(last) ? 0m
                : (last - 1 >= 0 && last - 1 < _cumDeltaArr.Count ? _cumDeltaArr[last - 1] : 0m);
            decimal liveCumDelta = baseCd + c.Delta;

            int rev = RevEvaluate(last, c, signedMld, liveCumDelta);
            if (rev != 0 && _revConfirm)
                rev = 0;   // Folgekerze existiert noch nicht -> erst nach Bestaetigung (naechste Bar)
            if (rev != 0 && _lastRevBar >= 0 && _signalCooldownBars > 0
                && (last - _lastRevBar) <= _signalCooldownBars)
                rev = 0;
            SetRevSignal(last, rev);
            _hudRev = rev;

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
            _hudTags = $"Δ{Arrow(o.DirDelta)} Vol{Arrow(o.DirVol)} Abs{Arrow(o.DirAbs)} VW{Arrow(o.DirVwap)} " +
                       $"Im{Arrow(o.DirImb)} vP{Arrow(o.DirVpoc)}" + (_tapeEnabled ? $" Tp{Arrow(o.DirTape)}" : "");
            _hudWarn = BuildWarning();
            _chartLabel = BuildChartLabel();

            // Big-Trade-Levels: Re-Test/Hit gegen den Live-Bar pruefen (Alarm).
            CheckBigLevelHits(c, last);
            // Imbalance-Zonen: Live-Approach/Fill gegen den Live-Bar (keine neuen Zonen live).
            ProcessImbZones(last, c, detectNew: false);
        }

        // ─────────────────────────────────────────────────────────────────
        //  BEDINGUNGS-AUSWERTUNG
        // ─────────────────────────────────────────────────────────────────
        private struct EvalOut
        {
            public int Bull, Bear;
            public int DirDelta, DirVol, DirAbs, DirVwap, DirImb, DirVpoc, DirTape;
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

            // 5) Imbalance (diagonal, gestapelt).
            if (_imbEnabled)
            {
                decimal tick = InstrumentInfo?.TickSize ?? 0m;
                if (tick > 0 && EvaluateImbalance(c, tick, _imbRatio, _imbMinCount, out int idir))
                {
                    o.DirImb = idir;
                    Accumulate(ref o, idir, _imbWeight);
                }
            }

            // 6) vPOC-in-Wick (location-basiert).
            if (_vpocEnabled)
            {
                int vdir = VpocWickDir(c);
                if (vdir != 0)
                {
                    o.DirVpoc = vdir;
                    Accumulate(ref o, vdir, _vpocWeight);
                }
            }

            // 7) Tape / Big Trades (live erfasst, netto-signiert).
            if (_tapeEnabled && _tapeNet.TryGetValue(bar, out int tnet) && tnet != 0)
            {
                o.DirTape = Math.Sign(tnet);
                Accumulate(ref o, o.DirTape, _tapeWeight);
            }

            // Score auf Anteil der AKTIVEN Gewichte normieren (0-100 %).
            int totalW = TotalEnabledWeight();
            if (totalW > 0)
            {
                o.Bull = (int)Math.Round(100.0 * o.Bull / totalW);
                o.Bear = (int)Math.Round(100.0 * o.Bear / totalW);
            }

            return o;
        }

        private int TotalEnabledWeight()
        {
            int w = 0;
            if (_deltaEnabled) w += _deltaWeight;
            if (_volEnabled) w += _volWeight;
            if (_absEnabled) w += _absWeight;
            if (_vwapEnabled) w += _vwapWeight;
            if (_imbEnabled) w += _imbWeight;
            if (_vpocEnabled) w += _vpocWeight;
            if (_tapeEnabled) w += _tapeWeight;
            return w;
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

        // Signierter Score fuer die Speicherung: Richtung * Gewichtssumme der
        // dominanten Seite. 0 wenn kein Signal.
        private static int SignedScore(int dir, EvalOut o)
            => dir > 0 ? o.Bull : dir < 0 ? -o.Bear : 0;

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

        // Absorption Count: Anzahl Level mit absorbiertem Gegen-Aggressor-Delta
        // (>= Frac * staerkstem Level). Long-Umkehr -> absorbierte VERKAEUFER (d<0), Short -> KAEUFER (d>0).
        private int AbsorbedLevelCount(IndicatorCandle c, int dir, decimal mld)
        {
            if (mld == 0m) return 0;
            decimal thr = _revAcntFrac * Math.Abs(mld);
            if (thr <= 0m) return 0;
            int n = 0;
            foreach (var pv in c.GetAllPriceLevels())
            {
                decimal d = pv.Ask - pv.Bid;          // > 0 = Kaeufer-Aggressor
                if (dir > 0 && d <= -thr) n++;
                else if (dir < 0 && d >= thr) n++;
            }
            return n;
        }

        // Volume Power Bar: Volumen >= Mult * Ø-Bar-Volumen UND Kerzenkoerper in Umkehr-Richtung.
        private bool IsPowerBar(IndicatorCandle c, int dir, decimal avgVol)
        {
            if (avgVol <= 0m) return false;
            bool bigVol = c.Volume >= _revPwrVolMult * avgVol;
            bool body = dir > 0 ? c.Close > c.Open : c.Close < c.Open;
            return bigVol && body;
        }

        // Diagonale Imbalances zaehlen: Buy = Ask[p] >= Ratio * Bid[p-Tick],
        // Sell = Bid[p] >= Ratio * Ask[p+Tick]. Aktiv, wenn eine Seite >= minCount
        // erreicht und dominiert. Richtung = dominante Stapelseite.
        private static bool EvaluateImbalance(IndicatorCandle c, decimal tick, decimal ratio, int minCount, out int dir)
        {
            dir = 0;
            var bid = new Dictionary<decimal, decimal>();
            var ask = new Dictionary<decimal, decimal>();
            foreach (var pv in c.GetAllPriceLevels())
            {
                bid[pv.Price] = pv.Bid;
                ask[pv.Price] = pv.Ask;
            }
            if (bid.Count == 0)
                return false;

            int buy = 0, sell = 0;
            foreach (var kv in ask)
            {
                if (bid.TryGetValue(kv.Key - tick, out var bBelow) && bBelow > 0 && kv.Value >= ratio * bBelow)
                    buy++;
            }
            foreach (var kv in bid)
            {
                if (ask.TryGetValue(kv.Key + tick, out var aAbove) && aAbove > 0 && kv.Value >= ratio * aAbove)
                    sell++;
            }

            if (buy >= minCount && buy > sell) { dir = 1; return true; }
            if (sell >= minCount && sell > buy) { dir = -1; return true; }
            return false;
        }

        // POC (Preislevel mit max. Volumen) relativ zum Kerzenkoerper.
        // Unterer Docht = bullishe Rejection (+1), oberer Docht = bearish (-1).
        private static int VpocWickDir(IndicatorCandle c)
        {
            decimal pocPrice = 0, pocVol = -1;
            foreach (var pv in c.GetAllPriceLevels())
            {
                if (pv.Volume > pocVol) { pocVol = pv.Volume; pocPrice = pv.Price; }
            }
            if (pocVol < 0)
                return 0;

            decimal bodyHi = Math.Max(c.Open, c.Close);
            decimal bodyLo = Math.Min(c.Open, c.Close);
            if (pocPrice > bodyHi) return -1;
            if (pocPrice < bodyLo) return 1;
            return 0;
        }

        // ─────────────────────────────────────────────────────────────────
        //  BALANCE-RANGE (Value Area ueber rollendes Fenster)
        // ─────────────────────────────────────────────────────────────────
        private void ComputeBalanceRange()
        {
            _rangeValid = false;
            if (!_rangeEnabled)
                return;

            int last = CurrentBar - 1;
            int start = last - _rangeLookback + 1;
            if (start < 0)
                start = 0;
            if (last < start)
                return;

            var hist = new Dictionary<decimal, decimal>();
            for (int i = start; i <= last; i++)
            {
                var ci = GetCandle(i);
                if (ci != null)
                    AddCandleToHistogram(ci, hist);
            }
            if (hist.Count == 0)
                return;

            ComputeValueArea(hist, _rangeValuePct, out _rangeVpoc, out _rangeVah, out _rangeVal);
            _rangeStartBar = start;
            _rangeValid = _rangeVah > 0 && _rangeVal > 0;
        }

        // Range-Detektor: segmentiert die letzten N Bars in Konsolidierungs-Boxen.
        // Eine Range "laeuft", solange der Preis in einem Band <= maxWidth bleibt;
        // bricht er aus, wird sie eingefroren und nach Richtung gefaerbt.
        // Inkrementell: jede ABGESCHLOSSENE Range wird EINGEFROREN (kein Repaint).
        // Nur die aktive Kandidaten-Range waechst live. Break NUR per Close ausserhalb
        // des Bandes (Wicks brechen die Range nicht). Breite = fixe ATR je Range.
        private void ProcessDetector()
        {
            if (!_detectorEnabled)
                return;

            int lastClosed = CurrentBar - 2;   // nur abgeschlossene Bars
            if (_lastDetBar < 0)
                _lastDetBar = Math.Max(-1, lastClosed - _detectorLookback);

            while (_lastDetBar < lastClosed)
            {
                _lastDetBar++;
                DetectorStep(_lastDetBar);
            }
        }

        private void DetectorStep(int bar)
        {
            var c = GetCandle(bar);
            if (c == null)
                return;

            if (!_candActive)
            {
                StartCandidate(bar, c);
                return;
            }

            decimal nh = Math.Max(_candHi, c.High), nl = Math.Min(_candLo, c.Low);

            // Passt der Bar (inkl. Docht) noch ins Breitenband? -> Range WAECHST, kein Break.
            // (Erst dadurch kann sich die Range ueberhaupt auf volle Breite etablieren.)
            if (_candWidth <= 0 || nh - nl <= _candWidth)
            {
                _candHi = nh;
                _candLo = nl;
                return;
            }

            // Bar wuerde das Band sprengen:
            //   Close AUSSERHALB des Bandes -> echter, Close-bestaetigter Break.
            //   Close noch drin (nur Docht raus)  -> Range bleibt, Band unveraendert.
            if (c.Close > _candHi || c.Close < _candLo)
            {
                int len = bar - _candStart;   // [_candStart .. bar-1]
                if (len >= _detectorMinBars)
                {
                    int dir = c.Close > _candHi ? 1 : -1;
                    TryAddRange(_candStart, bar - 1, _candHi, _candLo, dir);
                }
                StartCandidate(bar, c);
            }
            // else: Docht ueber die Breite, Close drin -> ignorieren, Range laeuft weiter.
        }

        private void StartCandidate(int bar, IndicatorCandle c)
        {
            _candActive = true;
            _candStart = bar;
            _candHi = c.High;
            _candLo = c.Low;
            _candWidth = WidthAtBar(bar);
        }

        // Stabile Breite: Ø Bar-Range ueber die letzten AtrPeriod Bars (bis bar) * Faktor.
        // Wird je Range einmal bei deren Start fixiert -> kein nachtraegliches Driften.
        private decimal WidthAtBar(int bar)
        {
            int p = Math.Max(1, _detectorAtrPeriod);
            int s = Math.Max(0, bar - p + 1);
            decimal sum = 0;
            int n = 0;
            for (int i = s; i <= bar; i++)
            {
                var ci = GetCandle(i);
                if (ci != null) { sum += ci.High - ci.Low; n++; }
            }
            return n > 0 ? (sum / n) * _detectorWidthFactor : 0m;
        }

        // Friert eine fertige Range ein — bei aktiver Volumen-Akzeptanz nur, wenn sie
        // genug Volumen UND einen klaren vPOC hat (echte Auktions-Balance).
        private void TryAddRange(int start, int end, decimal hi, decimal lo, int dir)
        {
            decimal poc = 0m;
            if (_detectorVolumeFilter)
            {
                if (!RangePassesVolume(start, end, out poc))
                    return;
            }
            else
            {
                poc = PocOf(start, end);
            }

            // Merging: mit der letzten Range vereinen, wenn nah dran und ueberlappend.
            if (_detectorMerge && _detRanges.Count > 0)
            {
                var last = _detRanges[_detRanges.Count - 1];
                if (ShouldMerge(last, start, hi, lo))
                {
                    decimal mHi = Math.Max(last.High, hi), mLo = Math.Min(last.Low, lo);
                    _detRanges[_detRanges.Count - 1] = new DetRange
                    {
                        Start = last.Start,
                        End = end,
                        High = mHi,
                        Low = mLo,
                        Dir = dir,
                        Poc = PocOf(last.Start, end)   // vPOC ueber die ganze vereinte Spanne
                    };
                    return;
                }
            }

            _detRanges.Add(new DetRange { Start = start, End = end, High = hi, Low = lo, Dir = dir, Poc = poc });
        }

        // Merge-Kriterium: kleine Zeitluecke UND signifikante Preis-Ueberlappung
        // (>= 50 % der kleineren Box) -> dieselbe Balance.
        private bool ShouldMerge(DetRange last, int start, decimal hi, decimal lo)
        {
            int gap = start - last.End - 1;
            if (gap > _detectorMergeGapBars)
                return false;

            decimal ovHi = Math.Min(last.High, hi);
            decimal ovLo = Math.Max(last.Low, lo);
            decimal overlap = ovHi - ovLo;
            if (overlap <= 0)
                return false;

            decimal minH = Math.Min(last.High - last.Low, hi - lo);
            return minH > 0 && overlap >= 0.5m * minH;
        }

        // vPOC (Preislevel mit max. Volumen) ueber [start..end].
        private decimal PocOf(int start, int end)
        {
            var hist = new Dictionary<decimal, decimal>();
            for (int i = start; i <= end; i++)
            {
                var ci = GetCandle(i);
                if (ci != null) AddCandleToHistogram(ci, hist);
            }
            decimal poc = 0m, max = -1m;
            foreach (var kv in hist)
                if (kv.Value > max) { max = kv.Value; poc = kv.Key; }
            return poc;
        }

        // Auktions-Akzeptanz: klarer vPOC (gepeakte Verteilung) UND genug Volumen
        // relativ zum Umfeld. poc = Value-Center der Range.
        private bool RangePassesVolume(int start, int end, out decimal poc)
        {
            poc = 0m;
            var hist = new Dictionary<decimal, decimal>();
            for (int i = start; i <= end; i++)
            {
                var ci = GetCandle(i);
                if (ci != null) AddCandleToHistogram(ci, hist);
            }
            if (hist.Count == 0)
                return false;

            decimal total = 0m, maxVol = -1m;
            foreach (var kv in hist)
            {
                total += kv.Value;
                if (kv.Value > maxVol) { maxVol = kv.Value; poc = kv.Key; }
            }

            // (a) Klarer vPOC: POC-Level deutlich ueber dem Durchschnitt der Level.
            decimal meanLevel = total / hist.Count;
            if (!(meanLevel > 0 && maxVol >= _detectorPocFactor * meanLevel))
                return false;

            // (b) Genug Volumen: Range-Ø-Bar-Vol >= Faktor * Umfeld-Ø-Bar-Vol.
            if (_detectorMinVolFactor > 0)
            {
                int bars = end - start + 1;
                decimal rangeAvg = bars > 0 ? total / bars : 0m;
                decimal refAvg = AvgBarVolume(start - 1, _detectorAtrPeriod);
                if (refAvg > 0 && rangeAvg < _detectorMinVolFactor * refAvg)
                    return false;
            }
            return true;
        }

        private decimal AvgBarVolume(int refEnd, int period)
        {
            int s = Math.Max(0, refEnd - period + 1);
            decimal sum = 0m;
            int n = 0;
            for (int i = s; i <= refEnd; i++)
            {
                var ci = GetCandle(i);
                if (ci != null) { sum += ci.Volume; n++; }
            }
            return n > 0 ? sum / n : 0m;
        }

        private static void AddCandleToHistogram(IndicatorCandle candle, Dictionary<decimal, decimal> hist)
        {
            bool any = false;
            foreach (var pv in candle.GetAllPriceLevels())
            {
                any = true;
                hist[pv.Price] = (hist.TryGetValue(pv.Price, out var v) ? v : 0m) + pv.Volume;
            }
            if (!any && candle.Volume > 0)
                hist[candle.Close] = (hist.TryGetValue(candle.Close, out var v2) ? v2 : 0m) + candle.Volume;
        }

        // Standard-Value-Area: vPOC finden, dann greedy zu beiden Seiten den
        // groesseren Nachbarn aufnehmen, bis pct% des Volumens abgedeckt sind.
        private static void ComputeValueArea(Dictionary<decimal, decimal> hist, int pct,
                                             out decimal vpoc, out decimal vah, out decimal val)
        {
            vpoc = vah = val = 0;
            if (hist.Count == 0)
                return;

            var prices = new List<decimal>(hist.Keys);
            prices.Sort();
            decimal total = 0;
            foreach (var v in hist.Values) total += v;

            decimal maxVol = -1m;
            int pocIdx = 0;
            for (int i = 0; i < prices.Count; i++)
            {
                var vol = hist[prices[i]];
                if (vol > maxVol) { maxVol = vol; pocIdx = i; }
            }
            vpoc = prices[pocIdx];

            decimal target = total * (pct / 100m);
            decimal acc = hist[prices[pocIdx]];
            int lo = pocIdx, hi = pocIdx;
            while (acc < target && (lo > 0 || hi < prices.Count - 1))
            {
                decimal volBelow = lo > 0 ? hist[prices[lo - 1]] : -1m;
                decimal volAbove = hi < prices.Count - 1 ? hist[prices[hi + 1]] : -1m;
                if (volAbove >= volBelow)
                {
                    if (hi < prices.Count - 1) { hi++; acc += hist[prices[hi]]; }
                    else if (lo > 0) { lo--; acc += hist[prices[lo]]; }
                    else break;
                }
                else
                {
                    if (lo > 0) { lo--; acc += hist[prices[lo]]; }
                    else if (hi < prices.Count - 1) { hi++; acc += hist[prices[hi]]; }
                    else break;
                }
            }
            val = prices[lo];
            vah = prices[hi];
        }

        // ─────────────────────────────────────────────────────────────────
        //  REVERSAL-ENGINE
        // ─────────────────────────────────────────────────────────────────
        // Liefert den signierten Reversal-Score: > 0 Long-Umkehr (am Tief),
        // < 0 Short-Umkehr (am Hoch), 0 = keine. Gated auf neue lokale Extrema.
        private int RevEvaluate(int bar, IndicatorCandle c, decimal signedMld, decimal curCumDelta)
        {
            if (!_reversalEnabled)
                return 0;

            int start = bar - _reversalLookback;
            if (start < 0)
                return 0;
            if (!Thresholds(bar, out _, out _, out decimal absThr))
                return 0;

            // Referenz-Extrema + CVD am Extrem + Ø Tape-Speed im Fenster.
            decimal minLow = decimal.MaxValue, maxHigh = decimal.MinValue;
            decimal cdAtLow = 0, cdAtHigh = 0;
            decimal sumSpeed = 0, sumAbsDelta = 0, sumVol = 0;
            int speedN = 0;
            decimal firstClose = 0, prevClose = 0, path = 0;
            bool havePrev = false;
            for (int i = start; i < bar; i++)
            {
                var ci = GetCandle(i);
                if (ci == null)
                    return 0;
                decimal cd = i < _cumDeltaArr.Count ? _cumDeltaArr[i] : 0m;
                if (ci.Low < minLow) { minLow = ci.Low; cdAtLow = cd; }
                if (ci.High > maxHigh) { maxHigh = ci.High; cdAtHigh = cd; }
                sumSpeed += BarSpeed(ci);
                sumAbsDelta += Math.Abs(ci.Delta);
                sumVol += ci.Volume;
                speedN++;
                if (!havePrev) { firstClose = ci.Close; prevClose = ci.Close; havePrev = true; }
                else { path += Math.Abs(ci.Close - prevClose); prevClose = ci.Close; }
            }
            path += Math.Abs(c.Close - prevClose);   // aktuellen Bar in den Pfad einbeziehen
            decimal avgAbsDelta = speedN > 0 ? sumAbsDelta / speedN : 0m;
            decimal avgVol = speedN > 0 ? sumVol / speedN : 0m;

            int totalW = _revDivWeight + _revAbsWeight + _revVpocWeight + _revExhWeight + _revSpeedWeight
                       + _revImbWeight + _revAuctionWeight
                       + _revAcntWeight + _revPwrWeight + _revCdeltaWeight;
            if (totalW <= 0)
                totalW = 1;

            // Speed-Spike: Tape am Extrem deutlich schneller als der Schnitt = Klimax.
            decimal avgSpeed = speedN > 0 ? sumSpeed / speedN : 0m;
            bool speedSpike = avgSpeed > 0 && BarSpeed(c) >= _revSpeedFactor * avgSpeed;
            decimal spdX = avgSpeed > 0 ? BarSpeed(c) / avgSpeed : 0m;   // Speed-Ratio fuer Hover-Zahl

            // Impuls-Filter: Effizienz des Beins ins Extrem (|Netto-Weg| / Pfadlaenge).
            // Hoch + gerichtet = gesunder Impuls -> Gegentrend-Reversal nur mit Divergenz.
            decimal displacement = c.Close - firstClose;
            decimal eff = path > 0 ? Math.Abs(displacement) / path : 0m;

            decimal mid = (c.High + c.Low) / 2m;

            // Long-Umkehr: neues Tief, aber Orderflow dreht.
            if (c.Low <= minLow)
            {
                // (b) Echte Absorption: grosses Verkaeufer-Delta UND Tief abgelehnt
                //     (Close in der oberen Bar-Haelfte) -> Aggressor getrappt.
                bool absr = signedMld < 0 && Math.Abs(signedMld) >= absThr && c.Close >= mid;

                // Impuls-Filter: gesunder Abwaerts-Impuls -> Absorption allein reicht
                // nicht, UND Divergenz muss SIGNIFIKANT sein (kein Rauschen).
                bool strongDown = _revImpulseFilter && eff >= _revImpulseEff && displacement < 0;

                // (a) Kumulative Delta-Divergenz: tieferes Tief, aber CVD hoeher.
                //     Im Impuls: der CVD-Bruch muss >= Faktor * Ø-Bar-Delta sein.
                decimal divGap = curCumDelta - cdAtLow;
                decimal need = strongDown ? _revDivMinFactor * avgAbsDelta : 0m;
                bool divg = divGap > 0 && divGap >= need;

                if (divg || (absr && !strongDown))
                {
                    bool vpoc = VpocWickDir(c) > 0;                       // POC im unteren Docht
                    bool exh = ExhaustionAtExtreme(c, true);              // duennes Verkaufsvol am Tief
                    // A/B bei aktiver Diagnose IMMER auswerten (Anzeige), aber nur bei Gewicht > 0 werten.
                    bool imb = (_showRevDebug || _revImbWeight > 0) && HasImbStack(c, 1);
                    bool auc = (_showRevDebug || _revAuctionWeight > 0) && AuctionFinishedAtExtreme(c, true);
                    // semaPHorek-Ergaenzungen (nur werten, wenn Gewicht > 0; Anzeige + Zahl bei Diagnose).
                    int acntN = (_showRevDebug || _revAcntWeight > 0 || _btLog) ? AbsorbedLevelCount(c, 1, signedMld) : 0;
                    bool acnt = (_showRevDebug || _revAcntWeight > 0) && acntN >= _revAcntMin;
                    bool pwr  = (_showRevDebug || _revPwrWeight > 0) && IsPowerBar(c, 1, avgVol);
                    bool cdlt = (_showRevDebug || _revCdeltaWeight > 0) && c.Delta >= _revCdeltaFactor * avgAbsDelta;
                    decimal pwrX = avgVol > 0 ? c.Volume / avgVol : 0m;
                    decimal cdlX = avgAbsDelta > 0 ? c.Delta / avgAbsDelta : 0m;
                    int s = 0;
                    if (divg) s += _revDivWeight;
                    if (absr) s += _revAbsWeight;
                    if (vpoc) s += _revVpocWeight;
                    if (exh) s += _revExhWeight;
                    if (speedSpike) s += _revSpeedWeight;                 // Klimax-Tape am Tief
                    if (imb) s += _revImbWeight;
                    if (auc) s += _revAuctionWeight;
                    if (acnt) s += _revAcntWeight;
                    if (pwr) s += _revPwrWeight;
                    if (cdlt) s += _revCdeltaWeight;
                    int pct = (int)Math.Round(100.0 * s / totalW);
                    if (pct >= _reversalThreshold)
                    {
                        SetRevCand(1, pct, eff, strongDown, divg, absr, vpoc, exh, speedSpike, imb, auc, acnt, pwr, cdlt, acntN, pwrX, cdlX, spdX);
                        return pct;
                    }
                }
            }

            // Short-Umkehr: neues Hoch, aber Orderflow dreht.
            if (c.High >= maxHigh)
            {
                bool absr = signedMld > 0 && Math.Abs(signedMld) >= absThr && c.Close <= mid;

                // Impuls-Filter: gesunder Aufwaerts-Impuls -> Absorption allein reicht
                // nicht, UND Divergenz muss SIGNIFIKANT sein.
                bool strongUp = _revImpulseFilter && eff >= _revImpulseEff && displacement > 0;

                decimal divGap = cdAtHigh - curCumDelta;   // > 0 = bearishe Divergenz
                decimal need = strongUp ? _revDivMinFactor * avgAbsDelta : 0m;
                bool divg = divGap > 0 && divGap >= need;

                if (divg || (absr && !strongUp))
                {
                    bool vpoc = VpocWickDir(c) < 0;
                    bool exh = ExhaustionAtExtreme(c, false);
                    // A/B bei aktiver Diagnose IMMER auswerten (Anzeige), aber nur bei Gewicht > 0 werten.
                    bool imb = (_showRevDebug || _revImbWeight > 0) && HasImbStack(c, -1);
                    bool auc = (_showRevDebug || _revAuctionWeight > 0) && AuctionFinishedAtExtreme(c, false);
                    // semaPHorek-Ergaenzungen (nur werten, wenn Gewicht > 0; Anzeige + Zahl bei Diagnose).
                    int acntN = (_showRevDebug || _revAcntWeight > 0 || _btLog) ? AbsorbedLevelCount(c, -1, signedMld) : 0;
                    bool acnt = (_showRevDebug || _revAcntWeight > 0) && acntN >= _revAcntMin;
                    bool pwr  = (_showRevDebug || _revPwrWeight > 0) && IsPowerBar(c, -1, avgVol);
                    bool cdlt = (_showRevDebug || _revCdeltaWeight > 0) && c.Delta <= -_revCdeltaFactor * avgAbsDelta;
                    decimal pwrX = avgVol > 0 ? c.Volume / avgVol : 0m;
                    decimal cdlX = avgAbsDelta > 0 ? c.Delta / avgAbsDelta : 0m;
                    int s = 0;
                    if (divg) s += _revDivWeight;
                    if (absr) s += _revAbsWeight;
                    if (vpoc) s += _revVpocWeight;
                    if (exh) s += _revExhWeight;
                    if (speedSpike) s += _revSpeedWeight;                 // Klimax-Tape am Hoch
                    if (imb) s += _revImbWeight;
                    if (auc) s += _revAuctionWeight;
                    if (acnt) s += _revAcntWeight;
                    if (pwr) s += _revPwrWeight;
                    if (cdlt) s += _revCdeltaWeight;
                    int pct = (int)Math.Round(100.0 * s / totalW);
                    if (pct >= _reversalThreshold)
                    {
                        SetRevCand(-1, pct, eff, strongUp, divg, absr, vpoc, exh, speedSpike, imb, auc, acnt, pwr, cdlt, acntN, pwrX, cdlX, spdX);
                        return -pct;
                    }
                }
            }

            return 0;
        }

        // Diagnose: Treiber-Aufschluesselung des aktuellen Reversal-Kandidaten merken.
        private void SetRevCand(int dir, int pct, decimal eff, bool strong,
            bool div, bool abs, bool vpoc, bool exh, bool spd, bool imb, bool auc,
            bool acn, bool pwr, bool cdl, int acnN, decimal pwrX, decimal cdlX, decimal spdX)
        {
            _revCandDir = dir; _revCandPct = pct;
            _revCand = new RevDbg { Eff = eff, Strong = strong, Div = div, Abs = abs, Vp = vpoc, Exh = exh, Spd = spd, Imb = imb, Auc = auc, Acn = acn, Pwr = pwr, Cdl = cdl, AcnN = acnN, PwrX = pwrX, CdlX = cdlX, SpdX = spdX };
        }

        // Tape-Speed der Kerze: Trades pro Sekunde (Ticks / Dauer). Historisch
        // berechenbar aus den Candle-Zeiten -> kein Live-Trade-Stream noetig.
        private static decimal BarSpeed(IndicatorCandle c)
        {
            double secs = (c.LastTime - c.Time).TotalSeconds;
            if (secs < 1) secs = 1;   // Floor gegen Division durch ~0
            return c.Ticks / (decimal)secs;
        }

        // 2-Kerzen-Bestaetigung: die Folgekerze muss in Umkehr-Richtung schliessen.
        private bool RevConfirmed(int bar, int dir)
        {
            var cN = GetCandle(bar);
            var cN1 = GetCandle(bar + 1);
            if (cN == null || cN1 == null)
                return false;
            return dir > 0 ? cN1.Close > cN.Close
                 : dir < 0 ? cN1.Close < cN.Close
                 : false;
        }

        // Exhaustion: am Extrem-Preislevel ist das Aggressor-Volumen duenn
        // (am Tief = Bid/Verkaufen, am Hoch = Ask/Kaufen) relativ zum Bar-Schnitt.
        private bool ExhaustionAtExtreme(IndicatorCandle c, bool atLow)
        {
            decimal extPrice = atLow ? decimal.MaxValue : decimal.MinValue;
            decimal aggrAtExt = 0, total = 0;
            int n = 0;
            foreach (var pv in c.GetAllPriceLevels())
            {
                total += pv.Volume;
                n++;
                if (atLow)
                {
                    if (pv.Price < extPrice) { extPrice = pv.Price; aggrAtExt = pv.Bid; }
                }
                else
                {
                    if (pv.Price > extPrice) { extPrice = pv.Price; aggrAtExt = pv.Ask; }
                }
            }
            if (n == 0)
                return false;
            decimal avg = total / n;
            return avg > 0 && aggrAtExt <= _revExhFactor * avg;
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

        private void SetRevSignal(int bar, int v)
        {
            while (_revSignals.Count <= bar)
                _revSignals.Add(0);
            _revSignals[bar] = v;
        }

        private int GetRevSignal(int bar)
            => bar >= 0 && bar < _revSignals.Count ? _revSignals[bar] : 0;

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

            DrawBalanceRange(context);
            DrawDetectorRanges(context);
            DrawImbZones(context);
            DrawBigLevels(context);

            if (_showMarkers)
                DrawMarkers(context);
            if (_showReversalMarkers)
                DrawReversalMarkers(context);

            if (_showHud)
                DrawHud(context);
        }

        // Big-Trade-Levels als horizontale Linien (gruenlich Buy / roetlich Sell) +
        // Volumen-Label. Gehittete Levels werden nicht mehr gezeichnet.
        private void DrawBigLevels(RenderContext context)
        {
            if (!_bigEnabled)
                return;
            if (ChartInfo?.PriceChartContainer is not { } cont)
                return;

            var region = cont.Region;
            const int minGapPx = 4;   // Mindestabstand zweier Linien in Pixeln
            lock (_bigLock)
            {
                // Bevorzugt aktive (durchgezogene) vor gehitteten, groessere vor kleineren.
                // So gewinnt bei Pixel-Kollision das wichtigere Level.
                var ordered = _bigLevels
                    .Where(l => !l.Hit || _bigKeepAfterHit)
                    .OrderBy(l => l.Hit ? 1 : 0)
                    .ThenByDescending(l => l.Volume)
                    .ToList();

                var usedY = new List<int>();
                foreach (var l in ordered)
                {
                    int y, x1;
                    try
                    {
                        y = cont.GetYByPrice(l.Price, false);
                        x1 = Math.Max(region.Left, cont.GetXByBar(l.Bar, false));
                    }
                    catch { continue; }
                    if (x1 > region.Right)
                        x1 = region.Left;

                    // Pixel-Sperre: schon eine Linie auf (fast) gleicher Hoehe? -> ueberspringen.
                    if (usedY.Any(uy => Math.Abs(uy - y) < minGapPx))
                        continue;
                    usedY.Add(y);

                    // Label: bei gemischten Prints Buy/Sell getrennt, sonst Gesamtvolumen.
                    string label = (l.BuyVol > 0 && l.SellVol > 0)
                        ? $"{l.BuyVol}/{l.SellVol}"
                        : l.Volume.ToString();
                    int lblX = region.Right - 12 - (int)context.MeasureString(label, _font).Width;

                    var baseCol = l.Dir > 0 ? _colorBigBuy : _colorBigSell;
                    if (l.Hit)
                    {
                        // gehittet: gedimmt + gestrichelt (Reaktion bleibt sichtbar).
                        var dim = Color.FromArgb(Math.Max(60, baseCol.A / 2), baseCol.R, baseCol.G, baseCol.B);
                        context.DrawLine(new RenderPen(dim, 1, System.Drawing.Drawing2D.DashStyle.Dash), x1, y, region.Right, y);
                        context.DrawString(label, _font, dim, lblX, y - 14);
                    }
                    else
                    {
                        context.DrawLine(new RenderPen(baseCol, 2), x1, y, region.Right, y);
                        context.DrawString(label, _font, baseCol, lblX, y - 14);
                    }
                }
            }
        }

        private void DrawBalanceRange(RenderContext context)
        {
            if (!_rangeEnabled || !_rangeValid)
                return;
            if (ChartInfo?.PriceChartContainer is not { } cont)
                return;

            var region = cont.Region;
            int yVah, yVal, yPoc, xStart;
            try
            {
                yVah = cont.GetYByPrice(_rangeVah, false);
                yVal = cont.GetYByPrice(_rangeVal, false);
                yPoc = cont.GetYByPrice(_rangeVpoc, false);
                xStart = _rangeExtendRight ? region.Left
                       : Math.Max(region.Left, cont.GetXByBar(_rangeStartBar, false));
            }
            catch { return; }

            int xEnd = region.Right;
            if (xEnd <= xStart)
                return;

            // Band (VAL..VAH). Hoeherer Preis = kleineres y -> yVah < yVal.
            int top = Math.Min(yVah, yVal);
            int h = Math.Abs(yVal - yVah);
            if (h > 0)
                context.FillRectangle(_colorRangeBand, new Rectangle(xStart, top, xEnd - xStart, h));

            var edgePen = new RenderPen(_colorRangeEdge, 1);
            context.DrawLine(edgePen, xStart, yVah, xEnd, yVah);
            context.DrawLine(edgePen, xStart, yVal, xEnd, yVal);
            context.DrawLine(new RenderPen(_colorRangePoc, 1), xStart, yPoc, xEnd, yPoc);
        }

        // Offene Imbalance-Zonen als gefuellte Boxen (nach rechts verlaengert = Target).
        // Gefuellte (gegen-imbalanced) Zonen sind kein Target mehr -> nicht gezeichnet.
        private void DrawImbZones(RenderContext context)
        {
            if (!_imbZonesEnabled)
                return;
            if (ChartInfo?.PriceChartContainer is not { } cont)
                return;
            var region = cont.Region;
            foreach (var z in _imbZones)
            {
                if (z.Filled)
                    continue;
                int x1, yH, yL;
                try
                {
                    x1 = Math.Max(region.Left, cont.GetXByBar(z.Bar, false));
                    yH = cont.GetYByPrice(z.High, false);
                    yL = cont.GetYByPrice(z.Low, false);
                }
                catch { continue; }
                if (x1 > region.Right)
                    x1 = region.Left;
                int top = Math.Min(yH, yL);
                int h = Math.Max(1, Math.Abs(yL - yH));
                var box = new Rectangle(x1, top, region.Right - x1, h);
                var col = z.Dir > 0 ? _colorImbBuy : _colorImbSell;
                var edge = Color.FromArgb(Math.Min(255, col.A + 140), col.R, col.G, col.B);
                context.FillRectangle(col, box);
                context.DrawRectangle(new RenderPen(edge, 1), box);
            }
        }

        private void DrawDetectorRanges(RenderContext context)
        {
            if (!_detectorEnabled)
                return;
            if (ChartInfo?.PriceChartContainer is not { } cont)
                return;

            var region = cont.Region;

            // Eingefrorene Ranges enden an ihrem End (kein Repaint, kein Verlaengern).
            foreach (var r in _detRanges)
                DrawOneRange(context, cont, region, r.Start, r.End, r.High, r.Low, r.Dir, false, r.Poc);

            // Aktive Kandidaten-Range (grau, laeuft nach rechts), sobald sie lang genug ist.
            if (_candActive && (CurrentBar - 2) - _candStart + 1 >= _detectorMinBars)
                DrawOneRange(context, cont, region, _candStart, CurrentBar - 1, _candHi, _candLo, 0, true, 0m);
        }

        private void DrawOneRange(RenderContext context, IChartContainer cont, Rectangle region,
                                  int startBar, int endBar, decimal high, decimal low, int dir, bool extendRight, decimal poc)
        {
            int x1, x2, yH, yL, yP = 0;
            try
            {
                x1 = cont.GetXByBar(startBar, false);
                x2 = extendRight ? region.Right : cont.GetXByBar(endBar, false);
                yH = cont.GetYByPrice(high, false);
                yL = cont.GetYByPrice(low, false);
                if (poc > 0) yP = cont.GetYByPrice(poc, false);
            }
            catch { return; }

            if (x2 < region.Left || x1 > region.Right)
                return;
            x1 = Math.Max(x1, region.Left);
            x2 = Math.Min(x2, region.Right);
            if (x2 <= x1)
                return;

            var col = dir > 0 ? _colorBreakUp : dir < 0 ? _colorBreakDn : _colorFlat;
            int top = Math.Min(yH, yL), h = Math.Abs(yL - yH);
            context.DrawRectangle(new RenderPen(col, 1), new Rectangle(x1, top, x2 - x1, h));

            // vPOC-Linie der Range (Value-Center), gestrichelt.
            if (poc > 0)
                context.DrawLine(new RenderPen(_colorRangePoc, 1, System.Drawing.Drawing2D.DashStyle.Dash), x1, yP, x2, yP);
        }

        // Phase-2b-Filter: liegt das Reversal-Extrem nahe einer Range-Kante
        // (High/Low/vPOC einer erkannten Box oder der aktiven Kandidaten-Range)?
        private bool IsAtRangeEdge(int bar, int dir)
        {
            var c = GetCandle(bar);
            if (c == null)
                return false;

            decimal tick = InstrumentInfo?.TickSize ?? 0m;
            decimal tol = tick * _revEdgeTolerance;
            if (tol <= 0)
                return true;   // ohne gueltige Tick-Groesse nicht filtern

            decimal extreme = dir > 0 ? c.Low : c.High;

            foreach (var r in _detRanges)
            {
                if (Math.Abs(extreme - r.High) <= tol) return true;
                if (Math.Abs(extreme - r.Low) <= tol) return true;
                if (r.Poc > 0 && Math.Abs(extreme - r.Poc) <= tol) return true;
            }
            if (_candActive && (Math.Abs(extreme - _candHi) <= tol || Math.Abs(extreme - _candLo) <= tol))
                return true;

            return false;
        }

        private void DrawMarkers(RenderContext context)
        {
            if (ChartInfo?.PriceChartContainer is not { } cont)
                return;

            var region = cont.Region;
            decimal tick = InstrumentInfo?.TickSize ?? 0m;
            decimal offset = tick * _markerTickOffset;
            int lastBar = CurrentBar - 1;
            int from = Math.Max(0, FirstVisibleBarNumber);
            int to = Math.Min(lastBar, LastVisibleBarNumber);

            for (int b = from; b <= to; b++)
            {
                int v = GetSignal(b);
                if (v == 0)
                    continue;

                var c = GetCandle(b);
                if (c == null)
                    continue;

                int dir = Math.Sign(v);
                int strength = Math.Abs(v);
                if (strength < _minMarkerScore)
                    continue;

                decimal price = dir > 0 ? c.Low - offset : c.High + offset;

                int x, y;
                try
                {
                    x = cont.GetXByBar(b, false);
                    y = cont.GetYByPrice(price, false);
                }
                catch { continue; }

                if (x < region.Left || x > region.Right)
                    continue;

                string glyph = dir > 0 ? "▲" : "▼";
                var col = dir > 0 ? _colorBull : _colorBear;
                var sz = context.MeasureString(glyph, _fontMarker);

                // Long-Pfeil unter dem Low (Oberkante an y), Short-Pfeil ueber dem High.
                int drawY = dir > 0 ? y : y - sz.Height;
                context.DrawString(glyph, _fontMarker, col, x - sz.Width / 2, drawY);

                // Staerke-Zahl (Score) wie semaPHoreks Lichter-Zahl: Long darunter, Short darueber.
                string num = strength.ToString();
                var nsz = context.MeasureString(num, _font);
                int numY = dir > 0 ? drawY + sz.Height : drawY - nsz.Height;
                context.DrawString(num, _font, col, x - nsz.Width / 2, numY);
            }
        }

        // Austauschdatei mit den KeyLevels-Preisen (von KeyLevels geschrieben).
        private static string KlSyncFilePath(string instrument)
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ATAS", "keylevels_sync");
            string safe = instrument;
            foreach (var ch in System.IO.Path.GetInvalidFileNameChars())
                safe = safe.Replace(ch, '_');
            if (string.IsNullOrWhiteSpace(safe)) safe = "instrument";
            return System.IO.Path.Combine(dir, safe + ".txt");
        }

        // KeyLevels-Preise aus der Datei laden (gedrosselt ~3 s). Fehlt die Datei -> keine Konfluenz.
        private void LoadKeyLevels()
        {
            if (!_klConfluence) return;
            var now = DateTime.UtcNow;
            if ((now - _klLastRead).TotalSeconds < 3) return;
            _klLastRead = now;
            var instr = InstrumentInfo?.Instrument;
            if (string.IsNullOrEmpty(instr)) return;
            try
            {
                string path = KlSyncFilePath(instr);
                if (!System.IO.File.Exists(path)) { _klLevels.Clear(); return; }
                var lines = System.IO.File.ReadAllLines(path);
                _klLevels.Clear();
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split('\t');
                    if (decimal.TryParse(parts[0], System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var p))
                        _klLevels.Add((p, parts.Length > 1 ? parts[1] : ""));
                }
            }
            catch { }
        }

        // Naechstes KeyLevel innerhalb Toleranz zum Preis; null wenn keins.
        private (decimal Price, string Label)? NearestKeyLevel(decimal price)
        {
            if (!_klConfluence || _klLevels.Count == 0) return null;
            decimal tick = InstrumentInfo?.TickSize ?? 0m;
            if (tick <= 0m) return null;
            decimal best = tick * _klTolTicks;
            (decimal Price, string Label)? hit = null;
            foreach (var lv in _klLevels)
            {
                decimal dd = Math.Abs(lv.Price - price);
                if (dd <= best) { best = dd; hit = lv; }
            }
            return hit;
        }

        // Farbe aufhellen (Richtung Weiss) fuer die Konfluenz-Hervorhebung.
        private static Color BrightenColor(Color c, double f = 0.4)
        {
            int r = (int)(c.R + (255 - c.R) * f);
            int g = (int)(c.G + (255 - c.G) * f);
            int b = (int)(c.B + (255 - c.B) * f);
            return Color.FromArgb(c.A, Math.Min(255, r), Math.Min(255, g), Math.Min(255, b));
        }

        // Reversal-Marker: Rauten (◆), getrennt von den Momentum-Pfeilen und
        // weiter vom Kurs weg gezeichnet, damit an einer Wende beide sichtbar sind.
        private void DrawReversalMarkers(RenderContext context)
        {
            if (!_reversalEnabled)
                return;
            if (ChartInfo?.PriceChartContainer is not { } cont)
                return;

            LoadKeyLevels();   // KeyLevels-Preise (gedrosselt) fuer die Konfluenz-Markierung
            var region = cont.Region;
            decimal tick = InstrumentInfo?.TickSize ?? 0m;
            decimal offset = tick * (_markerTickOffset * 2 + 6);
            int lastBar = CurrentBar - 1;
            int from = Math.Max(0, FirstVisibleBarNumber);
            int to = Math.Min(lastBar, LastVisibleBarNumber);

            for (int b = from; b <= to; b++)
            {
                int v = GetRevSignal(b);
                if (v == 0)
                    continue;

                var c = GetCandle(b);
                if (c == null)
                    continue;

                int dir = Math.Sign(v);

                // Phase 2b: nur an Range-Kanten anzeigen (reiner Display-Filter).
                if (_revEdgeOnly && !IsAtRangeEdge(b, dir))
                    continue;

                int strength = Math.Abs(v);
                decimal price = dir > 0 ? c.Low - offset : c.High + offset;

                int x, y;
                try
                {
                    x = cont.GetXByBar(b, false);
                    y = cont.GetYByPrice(price, false);
                }
                catch { continue; }

                if (x < region.Left || x > region.Right)
                    continue;

                var col = dir > 0 ? _colorRevBull : _colorRevBear;

                // KeyLevels-Konfluenz: sitzt das Umkehr-Extrem nah an einem KeyLevel?
                decimal extreme = dir > 0 ? c.Low : c.High;
                var kl = NearestKeyLevel(extreme);
                bool klConf = kl.HasValue;

                // Raute als echtes Polygon zeichnen (Glyph ◆ rendert im ATAS-Font nicht).
                int r = Math.Max(5, _fontSize / 2 + 1);
                if (klConf)
                {
                    // heller, groesserer Halo hinter der Raute = konfluenter Reversal.
                    int rr = r + 3;
                    var halo = new[] { new Point(x, y - rr), new Point(x + rr, y), new Point(x, y + rr), new Point(x - rr, y) };
                    context.FillPolygon(BrightenColor(col, 0.55), halo);
                }
                var pts = new[]
                {
                    new Point(x, y - r),
                    new Point(x + r, y),
                    new Point(x, y + r),
                    new Point(x - r, y),
                };
                context.FillPolygon(col, pts);

                // Staerke-Zahl: Long unter der Raute, Short darueber.
                string num = strength.ToString();
                var nsz = context.MeasureString(num, _font);
                int numY = dir > 0 ? y + r + 1 : y - r - nsz.Height - 1;
                context.DrawString(num, _font, col, x - nsz.Width / 2, numY);

                // "KL"-Tag ausserhalb der Zahl bei Konfluenz.
                if (klConf)
                {
                    var klCol = Color.FromArgb(255, 120, 230, 160);
                    var ksz = context.MeasureString("KL", _font);
                    int kY = dir > 0 ? numY + nsz.Height : numY - ksz.Height;
                    context.DrawString("KL", _font, klCol, x - ksz.Width / 2, kY);
                }

                // Diagnose-Tooltip nur an der Raute unter dem Mauszeiger (farbcodiert).
                if (_showRevDebug && b == _hoverRevBar && _revDbgByBar.TryGetValue(b, out var dd))
                    DrawRevHover(context, x, y, dir, r, dd, kl);
            }
        }

        // Feste Treiber-Farben fuer die Diagnose.
        private static readonly (string Name, Color Col)[] RevDriverColors =
        {
            ("DIV", Color.FromArgb(255,  90, 180, 255)),  // hellblau  = Divergenz
            ("ABS", Color.FromArgb(255, 255, 150,  60)),  // orange    = Absorption
            ("VP",  Color.FromArgb(255, 200, 120, 255)),  // violett   = vPOC-Docht
            ("EXH", Color.FromArgb(255, 240, 220,  90)),  // gelb      = Exhaustion
            ("SPD", Color.FromArgb(255, 255,  90,  90)),  // rot       = Speed
            ("IMB", Color.FromArgb(255,  90, 220, 130)),  // gruen     = Imbalance-Flip
            ("AUC", Color.FromArgb(255, 235, 235, 235)),  // weiss     = Finished Auction
            ("AC#", Color.FromArgb(255, 255, 180, 120)),  // hellorange= Absorption Count
            ("PWR", Color.FromArgb(255, 120, 200, 255)),  // hellblau  = Volume Power Bar
            ("CDl", Color.FromArgb(255, 200, 255, 140)),  // hellgruen = Kerzen-Delta
        };

        // Farbiger Diagnose-Tooltip an der gehoverten Raute.
        private void DrawRevHover(RenderContext context, int x, int y, int dir, int r, RevDbg d, (decimal Price, string Label)? kl)
        {
            bool[] on = { d.Div, d.Abs, d.Vp, d.Exh, d.Spd, d.Imb, d.Auc, d.Acn, d.Pwr, d.Cdl };
            // Treiber-Tokens; die mit Magnitude bekommen ihre Zahl angehaengt.
            string[] tok = new string[RevDriverColors.Length];
            for (int i = 0; i < tok.Length; i++) tok[i] = RevDriverColors[i].Name;
            tok[4] = $"SPD{d.SpdX:0.0}";   // Speed-Ratio
            tok[7] = $"AC#{d.AcnN}";       // Anzahl absorbierter Level
            tok[8] = $"PWR{d.PwrX:0.0}";   // Volumen-Faktor
            tok[9] = $"CDl{d.CdlX:0.0}";   // Kerzen-Delta / Ø-|Delta|

            string head = $"eff{d.Eff:0.00}{(d.Strong ? "  IMP" : "")}";
            var hsz = context.MeasureString(head, _font);
            const int gap = 7, pad = 7;

            int lineW = 0;
            for (int i = 0; i < tok.Length; i++)
                lineW += context.MeasureString(tok[i], _font).Width + gap;

            string klLine = kl.HasValue
                ? ("KL " + kl.Value.Price.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                   + (string.IsNullOrEmpty(kl.Value.Label) ? "" : " " + kl.Value.Label))
                : null;
            int klW = klLine != null ? context.MeasureString(klLine, _font).Width : 0;

            int lineH = hsz.Height;
            int boxW = Math.Max(Math.Max(hsz.Width, lineW), klW) + pad * 2;
            int extra = klLine != null ? lineH + 2 : 0;
            int boxH = lineH * 2 + pad * 2 + 2 + extra;
            int bx = x - boxW / 2;
            int by = dir > 0 ? y + r + 6 : y - r - boxH - 6;

            var box = new Rectangle(bx, by, boxW, boxH);
            context.FillRectangle(Color.FromArgb(235, 18, 22, 30), box);
            context.DrawRectangle(new RenderPen(Color.FromArgb(255, 100, 100, 125), 1), box);

            context.DrawString(head, _font, _colorText, bx + pad, by + pad);

            int tx = bx + pad;
            int ty = by + pad + lineH + 2;
            for (int i = 0; i < tok.Length; i++)
            {
                // gefeuert = volle Treiber-Farbe, sonst klares Dunkelgrau (eindeutig "aus").
                var col = on[i] ? RevDriverColors[i].Col : Color.FromArgb(255, 78, 82, 92);
                context.DrawString(tok[i], _font, col, tx, ty);
                tx += context.MeasureString(tok[i], _font).Width + gap;
            }

            // Konfluenz-Zeile: naechstes KeyLevel (gruen).
            if (klLine != null)
                context.DrawString(klLine, _font, Color.FromArgb(255, 120, 230, 160), bx + pad, ty + lineH + 2);
        }

        // Maus-Hover: Raute unter dem Cursor bestimmen -> Tooltip einblenden.
        public override bool ProcessMouseMove(RenderControlMouseEventArgs e)
        {
            if (_showRevDebug && _reversalEnabled && e != null)
            {
                int hb = FindHoveredRevBar(e.X, e.Y);
                if (hb != _hoverRevBar) { _hoverRevBar = hb; RedrawChart(); }
            }
            return base.ProcessMouseMove(e);
        }

        private int FindHoveredRevBar(int mx, int my)
        {
            if (ChartInfo?.PriceChartContainer is not { } cont)
                return -1;
            decimal tick = InstrumentInfo?.TickSize ?? 0m;
            decimal offset = tick * (_markerTickOffset * 2 + 6);
            int lastBar = CurrentBar - 1;
            int from = Math.Max(0, FirstVisibleBarNumber);
            int to = Math.Min(lastBar, LastVisibleBarNumber);
            int best = -1, bestD = 22 * 22;   // Trefferradius in px
            for (int b = from; b <= to; b++)
            {
                if (GetRevSignal(b) == 0 || !_revDbgByBar.ContainsKey(b))
                    continue;
                var c = GetCandle(b);
                if (c == null)
                    continue;
                int dir = Math.Sign(GetRevSignal(b));
                if (_revEdgeOnly && !IsAtRangeEdge(b, dir))
                    continue;
                decimal price = dir > 0 ? c.Low - offset : c.High + offset;
                int x, yy;
                try { x = cont.GetXByBar(b, false); yy = cont.GetYByPrice(price, false); }
                catch { continue; }
                int dx = x - mx, dy = yy - my, dd = dx * dx + dy * dy;
                if (dd < bestD) { bestD = dd; best = b; }
            }
            return best;
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

            if (_reversalEnabled)
            {
                // Phase-2b-Filter konsistent auch im HUD (Live-Bar).
                int rev = _hudRev;
                if (rev != 0 && _revEdgeOnly && !IsAtRangeEdge(CurrentBar - 1, Math.Sign(rev)))
                    rev = 0;
                string rt = rev > 0 ? "LONG" : rev < 0 ? "SHORT" : "—";
                var rc = rev > 0 ? _colorRevBull : rev < 0 ? _colorRevBear : _colorNeutral;
                lines.Add(($"REV: {rt} {Math.Abs(rev)}", rc, _font));
            }

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

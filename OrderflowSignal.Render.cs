using System;
using System.Collections.Generic;
using System.Drawing;
using ATAS.Indicators;
using OFT.Rendering.Context;
using OFT.Rendering.Control;
using OFT.Rendering.Tools;

namespace OrderflowSignal
{
    // Rendering-Teil: HUD, Marker, Rauten, Boxen, Levels, Hover-Diagnose, Maus.
    // Partial-Datei - gleiche Klasse, nur Code-Organisation (kein Verhaltensunterschied).
    public partial class OrderflowSignal
    {
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
            if (_posTool)
                DrawPositionTools(context);

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

                // Staerke-Zahl (Score): Long darunter, Short darueber.
                string num = strength.ToString();
                var nsz = context.MeasureString(num, _font);
                int numY = dir > 0 ? drawY + sz.Height : drawY - nsz.Height;
                context.DrawString(num, _font, col, x - nsz.Width / 2, numY);
            }
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

        // Position-Tool: an jedem Reversal eine Entry-Linie + TP-Zone (gruen) + SL-Zone (rot) zeichnen.
        // Rein visuell (kein Rechnen, keine Order) — Entry = Close der Umkehrkerze.
        private void DrawPositionTools(RenderContext context)
        {
            if (ChartInfo?.PriceChartContainer is not { } cont)
                return;
            decimal tick = InstrumentInfo?.TickSize ?? 0m;
            if (tick <= 0m)
                return;
            var region = cont.Region;
            int lastBar = CurrentBar - 1;
            int from = Math.Max(0, FirstVisibleBarNumber);
            int to = Math.Min(lastBar, LastVisibleBarNumber);
            decimal slD = tick * _posSlTicks, tpD = tick * _posTpTicks;
            string rr = _posSlTicks > 0 ? ((double)_posTpTicks / _posSlTicks).ToString("0.0") : "-";

            for (int b = from; b <= to; b++)
            {
                int v = _posSource == PosSource.Reversal ? GetRevSignal(b) : GetSignal(b);
                if (v == 0)
                    continue;
                var c = GetCandle(b);
                if (c == null)
                    continue;
                int dir = Math.Sign(v);
                decimal entry = PosEntryPrice(b, c);   // identisch zur Rechnung (kein Look-ahead)
                decimal sl = dir > 0 ? entry - slD : entry + slD;
                decimal tp = dir > 0 ? entry + tpD : entry - tpD;
                int eb = PosEntryBar(b);               // Box beginnt, wo der Trade real startet

                int x1, xe, yE, ySl, yTp;
                try
                {
                    x1 = cont.GetXByBar(eb, false);
                    xe = cont.GetXByBar(eb + _posBoxBars, false);
                    yE = cont.GetYByPrice(entry, false);
                    ySl = cont.GetYByPrice(sl, false);
                    yTp = cont.GetYByPrice(tp, false);
                }
                catch { continue; }
                if (xe <= x1) xe = x1 + 60;
                if (x1 > region.Right || xe < region.Left)
                    continue;
                int w = xe - x1;

                context.FillRectangle(_posTpColor, RectFromY(x1, yE, yTp, w));   // TP-Zone
                context.FillRectangle(_posSlColor, RectFromY(x1, yE, ySl, w));   // SL-Zone
                context.DrawLine(new RenderPen(Color.FromArgb(255, 235, 235, 235), 1), x1, yE, xe, yE);

                // Rahmen nach Ausgang faerben (First-Touch).
                int oc = _posOutcome.TryGetValue(b, out var oo) ? oo : 0;
                Color fr = oc == 1 ? Color.FromArgb(255, 70, 210, 110)
                         : oc == 2 ? Color.FromArgb(255, 150, 175, 210)   // Breakeven-Scratch
                         : oc == -1 ? Color.FromArgb(255, 230, 75, 75)
                         : oc == -2 ? Color.FromArgb(255, 235, 165, 45)
                         : Color.FromArgb(150, 150, 150, 160);
                context.DrawRectangle(new RenderPen(fr, oc == 0 ? 1 : 2), RectFromY(x1, yTp, ySl, w));

                string lbl = (dir > 0 ? "L " : "S ") + rr + "R";
                context.DrawString(lbl, _font, Color.FromArgb(255, 235, 235, 235), x1 + 2, yE - context.MeasureString(lbl, _font).Height - 1);
            }

            if (_posShowStats)
            {
                ComputeStreaks();
                int px = region.Left + 8;
                int lineH = context.MeasureString("Ag", _font).Height + 3;
                var lines = new string[3];
                int maxW = 0;
                for (int si = 0; si < 3; si++)
                {
                    int wl = _psW[si] + _psL[si] + _psAmb[si];        // entschiedene (Win/Loss) fuer die Quote
                    int dec = wl + _psBe[si];                         // alle aufgeloesten Trades (mit Kosten)
                    double wr = wl > 0 ? 100.0 * _psW[si] / wl : 0;
                    decimal net = _psNet[si] - _posCostTicks * dec;  // Netto nach Kosten
                    decimal per = dec > 0 ? net / dec : 0m;
                    lines[si] = $"{SessNames[si]} {_posTpTicks}/{_posSlTicks}:  {_psW[si]}W/{_psL[si]}L" + (_psAmb[si] > 0 ? $" {_psAmb[si]}?" : "") + (_psBe[si] > 0 ? $" {_psBe[si]}BE" : "")
                        + $"  {wr:0}%  Serie {_psMaxW[si]}W/{_psMaxL[si]}L  maxDD -{_psMaxDD[si]:0}T  Netto {(net >= 0 ? "+" : "")}{net:0}T ({net * 0.25m:+0.0;-0.0} Pkt, {per:+0.0;-0.0}/Tr)  offen {_psOpen[si]}";
                    int w2 = context.MeasureString(lines[si], _font).Width;
                    if (w2 > maxW) maxW = w2;
                }
                int py0 = region.Bottom - lineH * 3 - 6;
                context.FillRectangle(Color.FromArgb(210, 15, 18, 26), new Rectangle(px - 4, py0 - 2, maxW + 8, lineH * 3 + 4));
                var sc = new[] { Color.FromArgb(255, 120, 200, 255), Color.FromArgb(255, 120, 230, 160), Color.FromArgb(255, 240, 180, 90) };
                for (int si = 0; si < 3; si++)
                    context.DrawString(lines[si], _font, sc[si], px, py0 + si * lineH);
            }
        }

        private static Rectangle RectFromY(int x, int y1, int y2, int w)
        {
            int top = Math.Min(y1, y2);
            return new Rectangle(x, top, w, Math.Abs(y2 - y1));
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
                ($"MOMENTUM: {sigText}", sigColor, _font),
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

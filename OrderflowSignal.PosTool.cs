using System;
using System.Collections.Generic;
using ATAS.Indicators;

namespace OrderflowSignal
{
    // Position-Tool (First-Touch-Auswertung, Sessions, BE, Streaks/Drawdown)
    // + Backtest-Logger (CSV mit Treiber-Snapshot und MFE/MAE/Net).
    public partial class OrderflowSignal
    {
        // Position-Tool: neuen Trade (Entry=Close, feste TP/SL) zur First-Touch-Auswertung aufnehmen.
        private void AddPosPending(int bar, IndicatorCandle c, int rev)
        {
            if (!_posTool) return;
            decimal tick = InstrumentInfo?.TickSize ?? 0m;
            if (tick <= 0m) return;
            int dir = Math.Sign(rev);
            int sess = SessionOf(c);
            decimal entry = PosEntryPrice(bar, c);   // gemeinsame Quelle mit der Zeichnung
            decimal tp = dir > 0 ? entry + tick * _posTpTicks : entry - tick * _posTpTicks;
            decimal sl = dir > 0 ? entry - tick * _posSlTicks : entry + tick * _posSlTicks;
            _posPend.Add((bar, dir, sess, entry, tp, sl, 0m, false));
            _posOutcome[bar] = 0;   // offen bis aufgeloest
            if (sess >= 0) _psOpen[sess]++;
        }

        // EINZIGE Quelle fuer den Position-Tool-Entry (Rechnung UND Zeichnung) -> kein Drift.
        // IMMER EHRLICH (kein Look-ahead): Entry am fruehesten Zeitpunkt, an dem das Signal
        // real existierte. Die Folgekerzen-Bestaetigung gibt es NUR bei Reversals -> die
        // Verschiebung (Close(N+1) bzw. Open(N+2)) gilt nur bei Signal-Quelle Reversal.
        // Momentum-Dreiecke sind am Close(N) final -> Close(N) bzw. Open(N+1).
        private decimal PosEntryPrice(int bar, IndicatorCandle c)
        {
            bool wait = _revConfirm && _posSource == PosSource.Reversal;
            decimal entry = c.Close;
            if (wait)
            {
                var cc = GetCandle(bar + 1);
                if (cc != null) entry = cc.Close;
            }
            if (_posLikeAuto)
            {
                var cn = GetCandle(bar + (wait ? 2 : 1));
                if (cn != null) entry = cn.Open;
            }
            return entry;
        }

        // Bar, an dem die Box gezeichnet wird = Signal-Bar.
        private int PosEntryBar(int bar) => bar;

        // Session anhand der (offset-korrigierten) Kerzenzeit: 0=Asia, 1=London, 2=NY, -1=ausserhalb.
        private int SessionOf(IndicatorCandle c)
        {
            int h = c.Time.AddHours(_sessTz).Hour;
            if (h >= _sessAsia && h < _sessLon) return 0;
            if (h >= _sessLon && h < _sessNy) return 1;
            if (h >= _sessNy && h < _sessNyEnd) return 2;
            return -1;
        }

        // Offene Position-Tool-Trades gegen diesen Bar pruefen: wurde TP oder SL zuerst beruehrt?
        private void ResolvePosPending(int bar, IndicatorCandle c)
        {
            if (!_posTool || _posPend.Count == 0) return;
            decimal beDist = _posBeTrigger > 0 ? (InstrumentInfo?.TickSize ?? 0m) * _posBeTrigger : 0m;
            for (int i = _posPend.Count - 1; i >= 0; i--)
            {
                var p = _posPend[i];
                if (p.Bar >= bar) continue;
                decimal fav = p.Dir > 0 ? c.High - p.Entry : p.Entry - c.Low;
                decimal stop = p.BeArmed ? p.Entry : p.Sl;   // Breakeven verschiebt den Stop auf Entry
                bool tpHit = p.Dir > 0 ? c.High >= p.Tp : c.Low <= p.Tp;
                bool slHit = p.Dir > 0 ? c.Low <= stop : c.High >= stop;
                // 1=Win, 2=Breakeven-Scratch, -1=Loss, -2=ambivalent (beide in 1 Kerze, pessimistisch)
                int oc;
                if (tpHit && slHit) oc = p.BeArmed ? 2 : -2;
                else if (tpHit) oc = 1;
                else if (slHit) oc = p.BeArmed ? 2 : -1;
                else
                {
                    if (fav > p.MaxFav) p.MaxFav = fav;
                    if (beDist > 0m && p.MaxFav >= beDist) p.BeArmed = true;
                    _posPend[i] = p;   // Zustand (MaxFav/BeArmed) zurueckschreiben
                    continue;
                }
                _posOutcome[p.Bar] = oc;
                int s = p.Sess;
                if (s >= 0)
                {
                    _psOpen[s]--;
                    if (oc == 1) { _psW[s]++; _psNet[s] += _posTpTicks; }
                    else if (oc == 2) { _psBe[s]++; }                  // Breakeven -> netto 0 (nur Kosten)
                    else if (oc == -1) { _psL[s]++; _psNet[s] -= _posSlTicks; }
                    else { _psAmb[s]++; _psNet[s] -= _posSlTicks; }    // ambivalent -> pessimistisch als SL
                    _posResolved.Add((p.Bar, s, oc));
                }
                _posPend.RemoveAt(i);
            }
        }

        // Max. Gewinn-/Verlust-Serie je Session (in Entry-Reihenfolge). Gecacht ueber die Trade-Anzahl.
        private void ComputeStreaks()
        {
            if (_posResolved.Count == _posStreakN && _posStreakCost == _posCostTicks) return;
            _posStreakN = _posResolved.Count; _posStreakCost = _posCostTicks;
            Array.Clear(_psMaxW, 0, 3); Array.Clear(_psMaxL, 0, 3); Array.Clear(_psMaxDD, 0, 3);
            var sorted = new List<(int Bar, int Sess, int Oc)>(_posResolved);
            sorted.Sort((a, b) => a.Bar.CompareTo(b.Bar));   // Entry-Reihenfolge (Aufloesung != Entry)
            var curW = new int[3]; var curL = new int[3];
            var eq = new decimal[3]; var peak = new decimal[3];   // Equity-Kurve fuer Drawdown
            foreach (var t in sorted)
            {
                int s = t.Sess;
                decimal pnl;
                if (t.Oc == 1) { curW[s]++; curL[s] = 0; if (curW[s] > _psMaxW[s]) _psMaxW[s] = curW[s]; pnl = _posTpTicks - _posCostTicks; }
                else if (t.Oc == 2) { curW[s] = 0; curL[s] = 0; pnl = -_posCostTicks; }   // Breakeven = nur Kosten
                else { curL[s]++; curW[s] = 0; if (curL[s] > _psMaxL[s]) _psMaxL[s] = curL[s]; pnl = -_posSlTicks - _posCostTicks; }
                eq[s] += pnl;
                if (eq[s] > peak[s]) peak[s] = eq[s];
                decimal dd = peak[s] - eq[s];
                if (dd > _psMaxDD[s]) _psMaxDD[s] = dd;
            }
        }

        // Backtest-Log: neue Umkehr als offenen Eintrag aufnehmen (Treiber-Snapshot + Entry).
        private void AddBacktestPending(int bar, IndicatorCandle c, int rev, RevDbg dbg)
        {
            if (!_btLog) return;
            int dir = Math.Sign(rev);
            decimal extreme = dir > 0 ? c.Low : c.High;
            // Ehrlicher Entry wie im Position-Tool: bei Bestaetigung ist das Signal erst
            // am Schluss von N+1 bekannt -> Close(N+1) statt Close(N).
            decimal entry = c.Close;
            if (_revConfirm)
            {
                var cc = GetCandle(bar + 1);
                if (cc != null) entry = cc.Close;
            }
            _btPending.Add(new BtPending
            {
                Dir = dir, Age = 0, Pct = Math.Abs(rev),
                Entry = entry, Mfe = 0, Mae = 0, Time = c.LastTime,
                D = dbg, Kl = NearestKeyLevel(extreme).HasValue   // dbg des EMITTIERTEN Bars, nicht _revCand
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
    }
}

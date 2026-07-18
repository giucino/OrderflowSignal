using System;
using System.Collections.Generic;
using ATAS.Indicators;

namespace OrderflowSignal
{
    // Datei-Schnittstellen: Auto-Bridge (Signal-Export an die OrderflowAuto-Strategie)
    // + KeyLevels-Sync (Konfluenz-Levels aus der KeyLevels-Exportdatei lesen).
    public partial class OrderflowSignal
    {
        // Schreibt eine Zeile "unixMillis\tmomentum\treversal" (letzter geschlossener Bar, ueberschreibend)
        // nach %APPDATA%\ATAS\ofs_signals\<Instrument>.txt. Die OrderflowAuto-Strategie liest das.
        private void WriteBridge(int bar, IndicatorCandle c, int mom, int rev)
        {
            try
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ATAS", "ofs_signals");
                System.IO.Directory.CreateDirectory(dir);
                string instr = InstrumentInfo?.Instrument ?? "instr";
                if (_bridgeKey.Trim().Length > 0) instr += "_" + _bridgeKey.Trim();   // mehrere Timeframes je Instrument trennen
                foreach (var ch in System.IO.Path.GetInvalidFileNameChars()) instr = instr.Replace(ch, '_');
                string line = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0}\t{1}\t{2}\t{3}", c.LastTime.Ticks, mom, rev, c.Close);   // 4. Feld = Signalpreis
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, instr + ".txt"), line);
            }
            catch { /* Datei-IO darf nie die Berechnung stoppen */ }
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
    }
}

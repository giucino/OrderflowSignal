# Orderflow Signal (Multi-Condition) — ATAS Indikator

Eigenentwickelter ATAS-Indikator (C#) für Futures (ES/MES, NQ/MNQ). Verrechnet
mehrere **Orderflow-Bedingungen** zu einem **richtungsgewichteten Bull/Bear-Score**
und markiert Konfluenz direkt an der Kerze. Für **Tick-Charts (500/900T)** als
Trigger-Layer und **Zeitcharts (M5)**. **Rein informativ — kein Entry-Signal.**

> Teil eines mehrstufigen Projekts (**Stufe 2 — Trigger/Execution**). Setzt unter
> der Kontext-Schicht (Market State / Bias Dashboard, M5/M15) an: M5 = Überblick,
> Tick = Entry.

## Was er macht

Pro Bar werden bis zu vier Bedingungen geprüft. Jede liefert eine **Richtung**
(bullish / bearish) und steuert ihr **Gewicht** zur jeweiligen Seite bei. Die Summen
ergeben `Bull` vs. `Bear`; ab der **Signal-Schwelle** auf der dominanten Seite feuert
ein Marker.

| # | Bedingung | Aktiv wenn | Richtung |
|---|---|---|---|
| 1 | **Delta signifikant** | \|Delta\| ≥ Faktor × Ø\|Delta\| (+ optionaler Absolut-Floor) | Vorzeichen des Deltas |
| 2 | **Relatives Volumen** | Volume ≥ Faktor × ØVolume | Kerzenkörper (Close vs Open) |
| 3 | **Absorption** | Volume ≥ Faktor × ØVolume **und** Range ≤ Faktor × ØRange | **Reversal** gegen den Aggressor (−sign Delta) |
| 4 | **VWAP-Lage** | Session-VWAP vorhanden | Close über/unter Session-VWAP |

- **Schwellen sind relativ** (Faktor × gleitender Ø der letzten *N* Bars) → derselbe
  Indikator funktioniert ohne Codeänderung auf ES 500T, NQ 500T/900T und M5.
- **Session-VWAP** ankert täglich über `IsNewSession` (läuft in London **und** NY,
  kein RTH-Fenster-Filter). Akkumulation aus dem echten Bar-VWAP.
- **Bull/Bear statt Roh-Count:** Konfluenz *in eine Richtung* statt nur „X Lichter".
- **Signal-Cooldown** (Bars zwischen Markern) als Rausch-Bremse für schnelle Ticks.
- **Charttyp-Warnung:** HUD warnt, wenn Volumen-Bars (RelVol ungültig) oder
  Range/Renko (Absorption ungültig) geladen sind.

Anzeige: **HUD** (Bull/Bear-Punkte, Signal-Flag, Bedingungs-Tags `Δ▲ Vol▲ Abs▼ VW▲`)
**+ Pfeil-Marker** an den Kerzen mit Konfluenz.

## Empfohlener Chart

**Tick-Chart 500/900T** (Entry) — alle vier Bedingungen gültig. Auch auf **M5**
nutzbar. **Nicht** auf Volumen-Bars (RelVol konstant) oder Range/Renko (Range
konstant → Absorption sinnlos); davor warnt das HUD.

> Kalibrierung zuerst auf **MES** (liquide, ruhiger), dann Portierung auf NQ/MNQ —
> Code identisch, ggf. nur Faktoren/Lookback anpassen.

## Einstellungen (Kurzüberblick)

| Gruppe | Einstellung |
|---|---|
| Allgemein | Lookback, Signal-Schwelle, Signal-Cooldown, HUD/Marker an |
| Bedingung: Delta | aktiv, Gewicht, Faktor, Mindest-Absolut |
| Bedingung: Volumen | aktiv, Gewicht, Faktor |
| Bedingung: Absorption | aktiv, Gewicht, Vol-Faktor, Range-Faktor |
| Bedingung: VWAP | aktiv, Gewicht |
| Darstellung | Schriftgröße, Position, Abstände, Marker-Abstand |
| Farben | Bull / Bear / Neutral / Hintergrund |

Default-Gewichte: Delta 30, Volumen 20, Absorption 25, VWAP 25 (Summe 100),
Signal-Schwelle 50. An echten Sessions kalibrieren, nicht am Schreibtisch festlegen.

## Hinweise

- Der **sich bildende Bar repaintet** bis zum Schluss (Signal kann kippen). Marker
  auf abgeschlossenen Bars sind stabil.
- Absorption ist als **Reversal** modelliert (Aggressor absorbiert → Gegenrichtung).
  Wer Absorption als Continuation lesen will, dreht das Vorzeichen / Gewicht.

## Build & Installation

- TargetFramework `net10.0-windows`, ATAS-DLLs per HintPath referenziert.
- `dotnet build -c Release`, dann `OrderflowSignal.dll` nach `%APPDATA%\ATAS\Indicators\` kopieren.
- ATAS neu starten bzw. Indikatorliste aktualisieren.

## Lizenz / Hinweis

Private Eigenentwicklung auf Basis allgemein verfügbarer Orderflow-Konzepte.
Kein Nachbau kommerzieller Fremdprodukte. **Kein Handelssignal, keine
Anlageberatung** — Nutzung auf eigenes Risiko.

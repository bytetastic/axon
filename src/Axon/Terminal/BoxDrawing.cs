using System;
using System.Windows;
using System.Windows.Media;

namespace Axon.Terminal;

/// <summary>
/// Zeichnet Rahmen- und Blockzeichen selbst, pixelgenau und lückenlos über die volle Zellhöhe.
/// Schriften lassen bei Zeilenabstand &gt; 1 sonst Lücken zwischen │-Zeilen.
/// </summary>
public static class BoxDrawing
{
    // Richtungen: oben, rechts, unten, links – Gewicht 0 = keine, 1 = dünn, 2 = dick, 3 = doppelt
    static readonly (int cp, byte u, byte r, byte d, byte l)[] Lines =
    {
        (0x2500, 0, 1, 0, 1), (0x2501, 0, 2, 0, 2), (0x2502, 1, 0, 1, 0), (0x2503, 2, 0, 2, 0),
        (0x2504, 0, 1, 0, 1), (0x2505, 0, 2, 0, 2), (0x2506, 1, 0, 1, 0), (0x2507, 2, 0, 2, 0),
        (0x2508, 0, 1, 0, 1), (0x2509, 0, 2, 0, 2), (0x250A, 1, 0, 1, 0), (0x250B, 2, 0, 2, 0),
        (0x250C, 0, 1, 1, 0), (0x250D, 0, 2, 1, 0), (0x250E, 0, 1, 2, 0), (0x250F, 0, 2, 2, 0),
        (0x2510, 0, 0, 1, 1), (0x2511, 0, 0, 1, 2), (0x2512, 0, 0, 2, 1), (0x2513, 0, 0, 2, 2),
        (0x2514, 1, 1, 0, 0), (0x2515, 1, 2, 0, 0), (0x2516, 2, 1, 0, 0), (0x2517, 2, 2, 0, 0),
        (0x2518, 1, 0, 0, 1), (0x2519, 1, 0, 0, 2), (0x251A, 2, 0, 0, 1), (0x251B, 2, 0, 0, 2),
        (0x251C, 1, 1, 1, 0), (0x251D, 1, 2, 1, 0), (0x2520, 2, 1, 2, 0), (0x2523, 2, 2, 2, 0),
        (0x2524, 1, 0, 1, 1), (0x2525, 1, 0, 1, 2), (0x2528, 2, 0, 2, 1), (0x252B, 2, 0, 2, 2),
        (0x252C, 0, 1, 1, 1), (0x252F, 0, 2, 1, 2), (0x2530, 0, 1, 2, 1), (0x2533, 0, 2, 2, 2),
        (0x2534, 1, 1, 0, 1), (0x2537, 1, 2, 0, 2), (0x2538, 2, 1, 0, 1), (0x253B, 2, 2, 0, 2),
        (0x253C, 1, 1, 1, 1), (0x253F, 1, 2, 1, 2), (0x2542, 2, 1, 2, 1), (0x254B, 2, 2, 2, 2),
        (0x254C, 0, 1, 0, 1), (0x254D, 0, 2, 0, 2), (0x254E, 1, 0, 1, 0), (0x254F, 2, 0, 2, 0),
        (0x2550, 0, 3, 0, 3), (0x2551, 3, 0, 3, 0), (0x2554, 0, 3, 3, 0), (0x2557, 0, 0, 3, 3),
        (0x255A, 3, 3, 0, 0), (0x255D, 3, 0, 0, 3), (0x2560, 3, 3, 3, 0), (0x2563, 3, 0, 3, 3),
        (0x2566, 0, 3, 3, 3), (0x2569, 3, 3, 0, 3), (0x256C, 3, 3, 3, 3),
        (0x2574, 0, 0, 0, 1), (0x2575, 1, 0, 0, 0), (0x2576, 0, 1, 0, 0), (0x2577, 0, 0, 1, 0),
        (0x2578, 0, 0, 0, 2), (0x2579, 2, 0, 0, 0), (0x257A, 0, 2, 0, 0), (0x257B, 0, 0, 2, 0),
    };

    static readonly System.Collections.Generic.Dictionary<int, (byte u, byte r, byte d, byte l)> LineMap = BuildMap();

    static System.Collections.Generic.Dictionary<int, (byte, byte, byte, byte)> BuildMap()
    {
        var m = new System.Collections.Generic.Dictionary<int, (byte, byte, byte, byte)>();
        foreach (var e in Lines) m[e.cp] = (e.u, e.r, e.d, e.l);
        return m;
    }

    public static bool Handles(int cp) => cp >= 0x2500 && cp <= 0x259F && (LineMap.ContainsKey(cp) || cp >= 0x256D && cp <= 0x2570 || cp >= 0x2580);

    public static void Draw(DrawingContext dc, int cp, double x, double y, double w, double h, Brush brush, double ppd)
    {
        double px = 1 / ppd;
        double Snap(double v) => Math.Round(v * ppd) / ppd;
        double t = Math.Max(px, Snap(w * 0.12));

        if (cp >= 0x2580)
        {
            DrawBlock(dc, cp, x, y, w, h, brush, Snap);
            return;
        }

        double cx = Snap(x + w / 2 - t / 2);
        double cy = Snap(y + h / 2 - t / 2);

        if (cp >= 0x256D && cp <= 0x2570)
        {
            // abgerundete Ecken ╭ ╮ ╯ ╰
            double sx = cx + t / 2, sy = cy + t / 2;
            double r = Math.Min(w, h) * 0.5;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                switch (cp)
                {
                    case 0x256D: // ╭ unten + rechts
                        ctx.BeginFigure(new Point(x + w, sy), false, false);
                        ctx.LineTo(new Point(sx + r, sy), true, false);
                        ctx.ArcTo(new Point(sx, sy + r), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, false);
                        ctx.LineTo(new Point(sx, y + h), true, false);
                        break;
                    case 0x256E: // ╮ unten + links
                        ctx.BeginFigure(new Point(x, sy), false, false);
                        ctx.LineTo(new Point(sx - r, sy), true, false);
                        ctx.ArcTo(new Point(sx, sy + r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                        ctx.LineTo(new Point(sx, y + h), true, false);
                        break;
                    case 0x256F: // ╯ oben + links
                        ctx.BeginFigure(new Point(sx, y), false, false);
                        ctx.LineTo(new Point(sx, sy - r), true, false);
                        ctx.ArcTo(new Point(sx - r, sy), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                        ctx.LineTo(new Point(x, sy), true, false);
                        break;
                    case 0x2570: // ╰ oben + rechts
                        ctx.BeginFigure(new Point(sx, y), false, false);
                        ctx.LineTo(new Point(sx, sy - r), true, false);
                        ctx.ArcTo(new Point(sx + r, sy), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, false);
                        ctx.LineTo(new Point(x + w, sy), true, false);
                        break;
                }
            }
            g.Freeze();
            dc.DrawGeometry(null, new Pen(brush, t), g);
            return;
        }

        var (u, ri, d, l) = LineMap[cp];
        Segment(dc, brush, u, 0, x, y, w, h, cx, cy, t, px);
        Segment(dc, brush, ri, 1, x, y, w, h, cx, cy, t, px);
        Segment(dc, brush, d, 2, x, y, w, h, cx, cy, t, px);
        Segment(dc, brush, l, 3, x, y, w, h, cx, cy, t, px);
    }

    static void Segment(DrawingContext dc, Brush b, byte weight, int dir, double x, double y, double w, double h, double cx, double cy, double t, double px)
    {
        if (weight == 0) return;
        if (weight == 3)
        {
            double o = t + px;
            for (int s = -1; s <= 1; s += 2)
            {
                double ocx = cx + s * o, ocy = cy + s * o;
                Rect r = dir switch
                {
                    0 => new Rect(ocx, y, t, cy + t + o - y),
                    1 => new Rect(cx - o, ocy, x + w - (cx - o), t),
                    2 => new Rect(ocx, cy - o, t, y + h - (cy - o)),
                    _ => new Rect(x, ocy, cx + t + o - x, t),
                };
                if (r.Width > 0 && r.Height > 0) dc.DrawRectangle(b, null, r);
            }
            return;
        }
        double tt = weight == 2 ? t * 2 : t;
        double ccx = weight == 2 ? cx - t / 2 : cx;
        double ccy = weight == 2 ? cy - t / 2 : cy;
        Rect rect = dir switch
        {
            0 => new Rect(ccx, y, tt, ccy + tt - y),
            1 => new Rect(ccx, ccy, x + w - ccx, tt),
            2 => new Rect(ccx, ccy, tt, y + h - ccy),
            _ => new Rect(x, ccy, ccx + tt - x, tt),
        };
        if (rect.Width > 0 && rect.Height > 0) dc.DrawRectangle(b, null, rect);
    }

    static void DrawBlock(DrawingContext dc, int cp, double x, double y, double w, double h, Brush b, Func<double, double> snap)
    {
        double hx = snap(x + w / 2), hy = snap(y + h / 2);
        switch (cp)
        {
            case 0x2580: dc.DrawRectangle(b, null, new Rect(x, y, w, hy - y)); return;
            case >= 0x2581 and <= 0x2588:
                {
                    double hh = snap(h * (cp - 0x2580) / 8.0);
                    dc.DrawRectangle(b, null, new Rect(x, y + h - hh, w, hh));
                    return;
                }
            case >= 0x2589 and <= 0x258F:
                {
                    double ww = snap(w * (0x2590 - cp) / 8.0);
                    dc.DrawRectangle(b, null, new Rect(x, y, ww, h));
                    return;
                }
            case 0x2590: dc.DrawRectangle(b, null, new Rect(hx, y, x + w - hx, h)); return;
            case 0x2591: case 0x2592: case 0x2593:
                dc.PushOpacity((cp - 0x2590) * 0.25);
                dc.DrawRectangle(b, null, new Rect(x, y, w, h));
                dc.Pop();
                return;
            case 0x2594: dc.DrawRectangle(b, null, new Rect(x, y, w, snap(h / 8))); return;
            case 0x2595: { double ww = snap(w / 8); dc.DrawRectangle(b, null, new Rect(x + w - ww, y, ww, h)); return; }
        }
        // Quadranten ▖▗▘▙▚▛▜▝▞▟ – Bits: 1 oben links, 2 oben rechts, 4 unten links, 8 unten rechts
        int q = cp switch
        {
            0x2596 => 4, 0x2597 => 8, 0x2598 => 1, 0x2599 => 1 | 4 | 8, 0x259A => 1 | 8,
            0x259B => 1 | 2 | 4, 0x259C => 1 | 2 | 8, 0x259D => 2, 0x259E => 2 | 4, 0x259F => 2 | 4 | 8,
            _ => 0,
        };
        if ((q & 1) != 0) dc.DrawRectangle(b, null, new Rect(x, y, hx - x, hy - y));
        if ((q & 2) != 0) dc.DrawRectangle(b, null, new Rect(hx, y, x + w - hx, hy - y));
        if ((q & 4) != 0) dc.DrawRectangle(b, null, new Rect(x, hy, hx - x, y + h - hy));
        if ((q & 8) != 0) dc.DrawRectangle(b, null, new Rect(hx, hy, x + w - hx, y + h - hy));
    }
}

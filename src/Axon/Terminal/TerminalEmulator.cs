using System;
using System.Collections.Generic;
using System.Text;

namespace Axon.Terminal;

/// <summary>
/// VT-/xterm-kompatibler Terminal-Emulator: Parser (Zustandsautomat nach Paul Williams) plus Bildschirmpuffer.
/// Thread-Sicherheit: Aufrufer halten <see cref="Sync"/>, während sie <see cref="Feed"/>, <see cref="Resize"/>
/// oder Lesezugriffe ausführen.
/// </summary>
public sealed class TerminalEmulator
{
    public readonly object Sync = new();

    public int Cols { get; private set; }
    public int Rows { get; private set; }

    TerminalLine[] _main;
    TerminalLine[] _alt;
    TerminalLine[] _screen;
    public readonly LineRing Scrollback;

    public bool AltActive => ReferenceEquals(_screen, _alt);

    public int CursorX;
    public int CursorY;
    bool _pendingWrap;
    CellAttr _attr;
    int _top, _bottom;
    bool _originMode, _autoWrap = true, _insertMode;
    bool[] _tabs;

    struct SavedCursor
    {
        public int X, Y;
        public CellAttr Attr;
        public bool PendingWrap, Origin, G0Gfx, G1Gfx, UseG1, Valid;
    }
    SavedCursor _savedMain, _savedAlt;

    bool _g0Gfx, _g1Gfx, _useG1;
    int _lastPrinted = ' ';

    // Modi
    public bool CursorVisible = true;
    public bool AppCursorKeys;
    public bool BracketedPaste;
    public bool Win32InputMode;
    public bool FocusEvents;
    public bool SyncOutput;
    public bool MouseSgr;
    public int MouseMode;      // 0, 1000, 1002, 1003
    public int CursorStyle;    // DECSCUSR

    public string Title { get; private set; } = "";

    /// <summary>Wird bei jeder Änderung erhöht.</summary>
    public int Version;

    readonly List<string> _links = new() { "" };
    readonly Dictionary<string, ushort> _linkIds = new();

    public event Action<string>? Reply;
    public event Action<string>? TitleChanged;
    public event Action? Bell;
    public event Action<int, int>? ProgressChanged;
    public event Action<string>? DesktopNotification;
    public event Action? ScrollbackCleared;

    public TerminalEmulator(int cols, int rows, int scrollback = 5000)
    {
        Cols = Math.Max(2, cols);
        Rows = Math.Max(1, rows);
        Scrollback = new LineRing(scrollback);
        _main = NewScreen(Cols, Rows);
        _alt = NewScreen(Cols, Rows);
        _screen = _main;
        _tabs = NewTabs(Cols);
        _top = 0;
        _bottom = Rows - 1;
    }

    static TerminalLine[] NewScreen(int cols, int rows)
    {
        var s = new TerminalLine[rows];
        for (int i = 0; i < rows; i++) s[i] = new TerminalLine(cols, default);
        return s;
    }

    static bool[] NewTabs(int cols)
    {
        var t = new bool[cols];
        for (int i = 8; i < cols; i += 8) t[i] = true;
        return t;
    }

    public string GetLink(ushort id) => id < _links.Count ? _links[id] : "";

    // ───────────────────────── Zugriff für Renderer ─────────────────────────

    /// <summary>Absoluter Index der ersten Bildschirmzeile.</summary>
    public long ScreenTopAbs => AltActive ? Scrollback.Dropped + Scrollback.Count : Scrollback.Dropped + Scrollback.Count;

    /// <summary>Kleinster gültiger absoluter Index (älteste Scrollback-Zeile).</summary>
    public long FirstAbs => AltActive ? ScreenTopAbs : Scrollback.Dropped;

    public TerminalLine? GetLineAbs(long abs)
    {
        long screenTop = ScreenTopAbs;
        if (abs >= screenTop)
        {
            long r = abs - screenTop;
            return r < Rows ? _screen[r] : null;
        }
        if (AltActive) return null;
        long i = abs - Scrollback.Dropped;
        if (i < 0 || i >= Scrollback.Count) return null;
        return Scrollback[(int)i];
    }

    public TerminalLine ScreenLine(int row) => _screen[row];

    // ───────────────────────────── Parser ─────────────────────────────

    enum PState { Ground, Esc, EscInter, Csi, CsiIgnore, Osc, OscEsc, Str, StrEsc }

    PState _state;
    readonly int[] _params = new int[32];
    readonly bool[] _sub = new bool[32];
    int _pc;
    char _priv;
    char _inter;
    readonly StringBuilder _osc = new();
    char _pendingHigh;

    public void Feed(string s)
    {
        foreach (char ch in s)
        {
            if (_state == PState.Ground && ch >= 0x20 && ch != 0x7F)
            {
                if (char.IsHighSurrogate(ch)) { _pendingHigh = ch; continue; }
                int cp = ch;
                if (char.IsLowSurrogate(ch))
                {
                    if (_pendingHigh == 0) continue;
                    cp = char.ConvertToUtf32(_pendingHigh, ch);
                    _pendingHigh = '\0';
                }
                Print(cp);
                continue;
            }
            Step(ch);
        }
        Version++;
    }

    void Step(char c)
    {
        switch (_state)
        {
            case PState.Ground:
                if (c < 0x20) Execute(c);
                break;

            case PState.Esc:
                if (c == 0x1B) break;
                if (c == 0x18 || c == 0x1A) { _state = PState.Ground; break; }
                if (c < 0x20) { Execute(c); break; }
                if (c <= 0x2F) { _inter = c; _state = PState.EscInter; break; }
                switch (c)
                {
                    case '[': ClearParams(); _state = PState.Csi; break;
                    case ']': _osc.Clear(); _state = PState.Osc; break;
                    case 'P': case 'X': case '^': case '_': _state = PState.Str; break;
                    default: EscDispatch(c); _state = PState.Ground; break;
                }
                break;

            case PState.EscInter:
                if (c == 0x1B) { _state = PState.Esc; break; }
                if (c < 0x20) { Execute(c); break; }
                if (c <= 0x2F) break;
                EscInterDispatch(_inter, c);
                _state = PState.Ground;
                break;

            case PState.Csi:
                if (c == 0x1B) { _state = PState.Esc; break; }
                if (c == 0x18 || c == 0x1A) { _state = PState.Ground; break; }
                if (c < 0x20) { Execute(c); break; }
                if (c >= '0' && c <= '9')
                {
                    if (_pc == 0) { _pc = 1; _params[0] = 0; _sub[0] = false; }
                    int v = _params[_pc - 1] * 10 + (c - '0');
                    _params[_pc - 1] = v > 65535 ? 65535 : v;
                }
                else if (c == ';' || c == ':')
                {
                    if (_pc == 0) { _pc = 1; _params[0] = 0; _sub[0] = false; }
                    if (_pc < _params.Length) { _params[_pc] = 0; _sub[_pc] = c == ':'; _pc++; }
                }
                else if (c >= '<' && c <= '?')
                {
                    if (_pc == 0 && _priv == 0 && _inter == 0) _priv = c;
                    else _state = PState.CsiIgnore;
                }
                else if (c >= 0x20 && c <= 0x2F) _inter = c;
                else if (c >= 0x40 && c <= 0x7E) { CsiDispatch(c); _state = PState.Ground; }
                break;

            case PState.CsiIgnore:
                if (c == 0x1B) _state = PState.Esc;
                else if (c >= 0x40 && c <= 0x7E) _state = PState.Ground;
                break;

            case PState.Osc:
                if (c == 0x07) { OscDispatch(); _state = PState.Ground; }
                else if (c == 0x1B) _state = PState.OscEsc;
                else if (c >= 0x20 && _osc.Length < 65536) _osc.Append(c);
                break;

            case PState.OscEsc:
                OscDispatch();
                if (c == '\\') _state = PState.Ground;
                else { _state = PState.Esc; Step(c); }
                break;

            case PState.Str:
                if (c == 0x1B) _state = PState.StrEsc;
                break;

            case PState.StrEsc:
                if (c == '\\') _state = PState.Ground;
                else if (c != 0x1B) _state = PState.Str;
                break;
        }
    }

    void ClearParams()
    {
        _pc = 0;
        _priv = '\0';
        _inter = '\0';
    }

    int P(int i, int def) => i < _pc && _params[i] != 0 ? _params[i] : def;
    int P0(int i) => i < _pc ? _params[i] : 0;

    void Execute(char c)
    {
        switch (c)
        {
            case '\a': Bell?.Invoke(); break;
            case '\b':
                if (_pendingWrap) _pendingWrap = false;
                else if (CursorX > 0) CursorX--;
                break;
            case '\t': TabForward(1); break;
            case '\n': case '\v': case '\f': LineFeed(); break;
            case '\r': CursorX = 0; _pendingWrap = false; break;
            case (char)0x0E: _useG1 = true; break;
            case (char)0x0F: _useG1 = false; break;
            case (char)0x1B: _state = PState.Esc; break;
        }
    }

    void EscDispatch(char c)
    {
        switch (c)
        {
            case '7': SaveCursor(); break;
            case '8': RestoreCursor(); break;
            case 'D': LineFeed(); break;
            case 'E': CursorX = 0; LineFeed(); break;
            case 'M': ReverseIndex(); break;
            case 'H': if (CursorX < Cols) _tabs[CursorX] = true; break;
            case 'c': FullReset(); break;
        }
    }

    void EscInterDispatch(char inter, char c)
    {
        switch (inter)
        {
            case '(': _g0Gfx = c == '0'; break;
            case ')': _g1Gfx = c == '0'; break;
        }
    }

    // ───────────────────────────── Ausgabe ─────────────────────────────

    CellAttr EraseAttr => new() { Bg = _attr.Bg };

    static readonly int[] DecGraphics =
    {
        0x00A0, 0x25C6, 0x2592, 0x2409, 0x240C, 0x240D, 0x240A, 0x00B0, 0x00B1, 0x2424, 0x240B, 0x2518, 0x2510, 0x250C,
        0x2514, 0x253C, 0x23BA, 0x23BB, 0x2500, 0x23BC, 0x23BD, 0x251C, 0x2524, 0x2534, 0x252C, 0x2502, 0x2264, 0x2265,
        0x03C0, 0x2260, 0x00A3, 0x00B7,
    };

    void Print(int cp)
    {
        if (cp >= 0x80 && cp < 0xA0) return;
        bool gfx = _useG1 ? _g1Gfx : _g0Gfx;
        if (gfx && cp >= 0x5F && cp <= 0x7E) cp = DecGraphics[cp - 0x5F];

        int w = CharWidth.Of(cp);
        if (w == 0) return;

        if (_pendingWrap)
        {
            if (_autoWrap)
            {
                _screen[CursorY].Wrapped = true;
                CursorX = 0;
                LineFeed();
            }
            _pendingWrap = false;
        }

        if (w == 2 && CursorX >= Cols - 1)
        {
            if (Cols < 2) return;
            if (_autoWrap)
            {
                EraseCells(_screen[CursorY], CursorX, 1);
                _screen[CursorY].Wrapped = true;
                CursorX = 0;
                LineFeed();
            }
            else CursorX = Cols - 2;
        }

        var line = _screen[CursorY];
        if (_insertMode) InsertCells(line, CursorX, w);
        BreakWide(line, CursorX);
        if (w == 2) BreakWide(line, CursorX + 1);

        line.Cells[CursorX] = new Cell(cp, w == 2 ? Cell.WideLead : Cell.Normal, _attr);
        if (w == 2) line.Cells[CursorX + 1] = new Cell(0, Cell.WideTail, _attr);
        line.Version++;
        _lastPrinted = cp;

        CursorX += w;
        if (CursorX >= Cols)
        {
            CursorX = Cols - 1;
            _pendingWrap = _autoWrap;
        }
    }

    /// <summary>Überschreibt man eine Hälfte eines breiten Zeichens, wird die andere Hälfte gelöscht.</summary>
    void BreakWide(TerminalLine line, int x)
    {
        if (x < 0 || x >= Cols) return;
        var cells = line.Cells;
        if (cells[x].Kind == Cell.WideTail && x > 0) cells[x - 1] = new Cell(0, Cell.Normal, cells[x - 1].Attr);
        if (cells[x].Kind == Cell.WideLead && x + 1 < Cols) cells[x + 1] = new Cell(0, Cell.Normal, cells[x + 1].Attr);
    }

    void EraseCells(TerminalLine line, int x, int n)
    {
        int end = Math.Min(Cols, x + n);
        if (x >= end) return;
        BreakWide(line, x);
        BreakWide(line, end - 1);
        var e = EraseAttr;
        for (int i = x; i < end; i++) line.Cells[i] = new Cell(0, Cell.Normal, e);
        line.Version++;
    }

    void InsertCells(TerminalLine line, int x, int n)
    {
        if (x >= Cols) return;
        n = Math.Min(n, Cols - x);
        BreakWide(line, x);
        Array.Copy(line.Cells, x, line.Cells, x + n, Cols - x - n);
        var e = EraseAttr;
        for (int i = x; i < x + n; i++) line.Cells[i] = new Cell(0, Cell.Normal, e);
        if (line.Cells[Cols - 1].Kind == Cell.WideLead) line.Cells[Cols - 1] = new Cell(0, Cell.Normal, e);
        line.Version++;
    }

    void DeleteCells(TerminalLine line, int x, int n)
    {
        if (x >= Cols) return;
        n = Math.Min(n, Cols - x);
        BreakWide(line, x);
        BreakWide(line, Math.Min(Cols - 1, x + n));
        Array.Copy(line.Cells, x + n, line.Cells, x, Cols - x - n);
        var e = EraseAttr;
        for (int i = Cols - n; i < Cols; i++) line.Cells[i] = new Cell(0, Cell.Normal, e);
        line.Version++;
    }

    void LineFeed()
    {
        _pendingWrap = false;
        if (CursorY == _bottom) ScrollUp(_top, _bottom, 1, true);
        else if (CursorY < Rows - 1) CursorY++;
    }

    void ReverseIndex()
    {
        _pendingWrap = false;
        if (CursorY == _top) ScrollDown(_top, _bottom, 1);
        else if (CursorY > 0) CursorY--;
    }

    void ScrollUp(int top, int bottom, int n, bool toScrollback)
    {
        n = Math.Min(n, bottom - top + 1);
        for (int k = 0; k < n; k++)
        {
            var removed = _screen[top];
            Array.Copy(_screen, top + 1, _screen, top, bottom - top);
            _screen[bottom] = new TerminalLine(Cols, EraseAttr);
            if (toScrollback && top == 0 && !AltActive) Scrollback.Add(removed);
        }
    }

    void ScrollDown(int top, int bottom, int n)
    {
        n = Math.Min(n, bottom - top + 1);
        for (int k = 0; k < n; k++)
        {
            Array.Copy(_screen, top, _screen, top + 1, bottom - top);
            _screen[top] = new TerminalLine(Cols, EraseAttr);
        }
    }

    void TabForward(int n)
    {
        _pendingWrap = false;
        for (int k = 0; k < n; k++)
        {
            int x = CursorX + 1;
            while (x < Cols - 1 && !_tabs[x]) x++;
            CursorX = Math.Min(x, Cols - 1);
        }
    }

    void TabBackward(int n)
    {
        _pendingWrap = false;
        for (int k = 0; k < n; k++)
        {
            int x = CursorX - 1;
            while (x > 0 && !_tabs[x]) x--;
            CursorX = Math.Max(0, x);
        }
    }

    // ───────────────────────────── CSI ─────────────────────────────

    void CsiDispatch(char f)
    {
        if (_priv == '?')
        {
            switch (f)
            {
                case 'h': SetPrivateModes(true); break;
                case 'l': SetPrivateModes(false); break;
                case 'J': EraseDisplay(P0(0)); break;
                case 'K': EraseLine(P0(0)); break;
                case 'p' when _inter == '$': ReportPrivateMode(P0(0)); break;
                case 'n' when P0(0) == 6: Reply?.Invoke($"\x1b[?{CursorY + 1};{CursorX + 1}R"); break;
            }
            return;
        }
        if (_priv == '>')
        {
            if (f == 'c') Reply?.Invoke("\x1b[>0;10;1c");
            else if (f == 'q') Reply?.Invoke("\x1bP>|Axon\x1b\\");
            return;
        }
        if (_priv != 0) return;

        if (_inter == ' ' && f == 'q') { CursorStyle = P0(0); return; }
        if (_inter == '!' && f == 'p') { SoftReset(); return; }
        if (_inter == '$' && f == 'p') { Reply?.Invoke($"\x1b[{P0(0)};{(P0(0) == 4 ? (_insertMode ? 1 : 2) : 0)}$y"); return; }
        if (_inter != 0) return;

        int n = P(0, 1);
        switch (f)
        {
            case '@': InsertCells(_screen[CursorY], CursorX, n); _pendingWrap = false; break;
            case 'A': CursorY = Math.Max(CursorY >= _top ? _top : 0, CursorY - n); _pendingWrap = false; break;
            case 'B': CursorY = Math.Min(CursorY <= _bottom ? _bottom : Rows - 1, CursorY + n); _pendingWrap = false; break;
            case 'C': case 'a': CursorX = Math.Min(Cols - 1, CursorX + n); _pendingWrap = false; break;
            case 'D': CursorX = Math.Max(0, CursorX - n); _pendingWrap = false; break;
            case 'E': CursorY = Math.Min(CursorY <= _bottom ? _bottom : Rows - 1, CursorY + n); CursorX = 0; _pendingWrap = false; break;
            case 'F': CursorY = Math.Max(CursorY >= _top ? _top : 0, CursorY - n); CursorX = 0; _pendingWrap = false; break;
            case 'G': case '`': CursorX = Math.Clamp(n - 1, 0, Cols - 1); _pendingWrap = false; break;
            case 'H': case 'f': SetCursor(P(1, 1) - 1, P(0, 1) - 1); break;
            case 'I': TabForward(n); break;
            case 'J': EraseDisplay(P0(0)); break;
            case 'K': EraseLine(P0(0)); break;
            case 'L':
                if (CursorY >= _top && CursorY <= _bottom) { ScrollDown(CursorY, _bottom, n); CursorX = 0; _pendingWrap = false; }
                break;
            case 'M':
                if (CursorY >= _top && CursorY <= _bottom) { ScrollUp(CursorY, _bottom, n, false); CursorX = 0; _pendingWrap = false; }
                break;
            case 'P': DeleteCells(_screen[CursorY], CursorX, n); _pendingWrap = false; break;
            case 'S': ScrollUp(_top, _bottom, n, false); break;
            case 'T': ScrollDown(_top, _bottom, n); break;
            case 'X': EraseCells(_screen[CursorY], CursorX, n); _pendingWrap = false; break;
            case 'Z': TabBackward(n); break;
            case 'b':
                for (int i = 0; i < Math.Min(n, 4096); i++) Print(_lastPrinted);
                break;
            case 'c':
                if (P0(0) == 0) Reply?.Invoke("\x1b[?62;22c");
                break;
            case 'd': SetCursor(CursorX, P(0, 1) - 1); break;
            case 'e': CursorY = Math.Min(Rows - 1, CursorY + n); _pendingWrap = false; break;
            case 'g':
                if (P0(0) == 0 && CursorX < Cols) _tabs[CursorX] = false;
                else if (P0(0) == 3) Array.Clear(_tabs);
                break;
            case 'h': if (P0(0) == 4) _insertMode = true; break;
            case 'l': if (P0(0) == 4) _insertMode = false; break;
            case 'm': Sgr(); break;
            case 'n':
                if (P0(0) == 5) Reply?.Invoke("\x1b[0n");
                else if (P0(0) == 6)
                {
                    int row = _originMode ? CursorY - _top : CursorY;
                    Reply?.Invoke($"\x1b[{row + 1};{CursorX + 1}R");
                }
                break;
            case 'r':
                {
                    int top = P(0, 1) - 1, bottom = P(1, Rows) - 1;
                    bottom = Math.Min(bottom, Rows - 1);
                    if (top < bottom)
                    {
                        _top = top;
                        _bottom = bottom;
                        SetCursor(0, 0);
                    }
                    break;
                }
            case 's': SaveCursor(); break;
            case 'u': RestoreCursor(); break;
            case 't':
                if (P0(0) == 18) Reply?.Invoke($"\x1b[8;{Rows};{Cols}t");
                break;
        }
    }

    void SetCursor(int x, int y)
    {
        if (_originMode)
        {
            y += _top;
            CursorY = Math.Clamp(y, _top, _bottom);
        }
        else CursorY = Math.Clamp(y, 0, Rows - 1);
        CursorX = Math.Clamp(x, 0, Cols - 1);
        _pendingWrap = false;
    }

    void EraseDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseCells(_screen[CursorY], CursorX, Cols - CursorX);
                for (int y = CursorY + 1; y < Rows; y++) ClearLine(y);
                break;
            case 1:
                for (int y = 0; y < CursorY; y++) ClearLine(y);
                EraseCells(_screen[CursorY], 0, CursorX + 1);
                break;
            case 2:
                for (int y = 0; y < Rows; y++) ClearLine(y);
                break;
            case 3:
                if (!AltActive)
                {
                    Scrollback.Clear();
                    ScrollbackCleared?.Invoke();
                }
                break;
        }
        _pendingWrap = false;
    }

    void ClearLine(int y)
    {
        _screen[y] = new TerminalLine(Cols, EraseAttr);
    }

    void EraseLine(int mode)
    {
        var line = _screen[CursorY];
        switch (mode)
        {
            case 0: EraseCells(line, CursorX, Cols - CursorX); line.Wrapped = false; break;
            case 1: EraseCells(line, 0, CursorX + 1); break;
            case 2: EraseCells(line, 0, Cols); line.Wrapped = false; break;
        }
        _pendingWrap = false;
    }

    void SetPrivateModes(bool on)
    {
        for (int i = 0; i < Math.Max(1, _pc); i++)
        {
            int m = P0(i);
            switch (m)
            {
                case 1: AppCursorKeys = on; break;
                case 6: _originMode = on; SetCursor(0, 0); break;
                case 7: _autoWrap = on; if (!on) _pendingWrap = false; break;
                case 25: CursorVisible = on; break;
                case 47:
                case 1047:
                    if (on) EnterAlt(m == 1047); else ExitAlt();
                    break;
                case 1048:
                    if (on) SaveCursor(); else RestoreCursor();
                    break;
                case 1049:
                    if (on) { SaveCursor(); EnterAlt(true); }
                    else { ExitAlt(); RestoreCursor(); }
                    break;
                case 1000: case 1002: case 1003: MouseMode = on ? m : 0; break;
                case 1006: MouseSgr = on; break;
                case 1004: FocusEvents = on; break;
                case 2004: BracketedPaste = on; break;
                case 2026: SyncOutput = on; break;
                case 9001: Win32InputMode = on; break;
            }
        }
    }

    void ReportPrivateMode(int m)
    {
        int state = m switch
        {
            1 => AppCursorKeys ? 1 : 2,
            6 => _originMode ? 1 : 2,
            7 => _autoWrap ? 1 : 2,
            25 => CursorVisible ? 1 : 2,
            1049 or 1047 or 47 => AltActive ? 1 : 2,
            1000 or 1002 or 1003 => MouseMode == m ? 1 : 2,
            1006 => MouseSgr ? 1 : 2,
            1004 => FocusEvents ? 1 : 2,
            2004 => BracketedPaste ? 1 : 2,
            2026 => SyncOutput ? 1 : 2,
            9001 => Win32InputMode ? 1 : 2,
            _ => 0,
        };
        Reply?.Invoke($"\x1b[?{m};{state}$y");
    }

    void EnterAlt(bool clear)
    {
        if (AltActive) return;
        _screen = _alt;
        if (clear) for (int y = 0; y < Rows; y++) _alt[y] = new TerminalLine(Cols, default);
        _top = 0;
        _bottom = Rows - 1;
        _pendingWrap = false;
    }

    void ExitAlt()
    {
        if (!AltActive) return;
        _screen = _main;
        _top = 0;
        _bottom = Rows - 1;
        _pendingWrap = false;
    }

    void SaveCursor()
    {
        var s = new SavedCursor
        {
            X = CursorX, Y = CursorY, Attr = _attr, PendingWrap = _pendingWrap, Origin = _originMode,
            G0Gfx = _g0Gfx, G1Gfx = _g1Gfx, UseG1 = _useG1, Valid = true,
        };
        if (AltActive) _savedAlt = s; else _savedMain = s;
    }

    void RestoreCursor()
    {
        var s = AltActive ? _savedAlt : _savedMain;
        if (!s.Valid)
        {
            CursorX = 0; CursorY = 0; _attr = default; _pendingWrap = false; _originMode = false;
            return;
        }
        CursorX = Math.Min(s.X, Cols - 1);
        CursorY = Math.Min(s.Y, Rows - 1);
        _attr = s.Attr;
        _pendingWrap = s.PendingWrap;
        _originMode = s.Origin;
        _g0Gfx = s.G0Gfx; _g1Gfx = s.G1Gfx; _useG1 = s.UseG1;
    }

    void SoftReset()
    {
        CursorVisible = true;
        _originMode = false;
        _autoWrap = true;
        _insertMode = false;
        _top = 0;
        _bottom = Rows - 1;
        _attr = default;
        AppCursorKeys = false;
        _savedMain = default;
        _savedAlt = default;
        _g0Gfx = _g1Gfx = _useG1 = false;
        _pendingWrap = false;
    }

    void FullReset()
    {
        SoftReset();
        ExitAlt();
        for (int y = 0; y < Rows; y++) { _main[y] = new TerminalLine(Cols, default); _alt[y] = new TerminalLine(Cols, default); }
        CursorX = CursorY = 0;
        _tabs = NewTabs(Cols);
        BracketedPaste = FocusEvents = SyncOutput = MouseSgr = false;
        MouseMode = 0;
        CursorStyle = 0;
    }

    // ───────────────────────────── SGR ─────────────────────────────

    void Sgr()
    {
        if (_pc == 0) { _attr = new CellAttr { Link = _attr.Link }; return; }
        for (int i = 0; i < _pc; i++)
        {
            int p = _params[i];
            switch (p)
            {
                case 0: _attr = new CellAttr { Link = _attr.Link }; break;
                case 1: _attr.Flags |= CellFlags.Bold; break;
                case 2: _attr.Flags |= CellFlags.Dim; break;
                case 3: _attr.Flags |= CellFlags.Italic; break;
                case 4:
                    _attr.Flags &= ~(CellFlags.Underline | CellFlags.DoubleUnderline | CellFlags.CurlyUnderline);
                    if (i + 1 < _pc && _sub[i + 1])
                    {
                        int style = _params[++i];
                        while (i + 1 < _pc && _sub[i + 1]) i++;
                        if (style == 2) _attr.Flags |= CellFlags.DoubleUnderline;
                        else if (style == 3) _attr.Flags |= CellFlags.CurlyUnderline;
                        else if (style != 0) _attr.Flags |= CellFlags.Underline;
                    }
                    else _attr.Flags |= CellFlags.Underline;
                    break;
                case 5: case 6: _attr.Flags |= CellFlags.Blink; break;
                case 7: _attr.Flags |= CellFlags.Inverse; break;
                case 8: _attr.Flags |= CellFlags.Hidden; break;
                case 9: _attr.Flags |= CellFlags.Strike; break;
                case 21: _attr.Flags |= CellFlags.DoubleUnderline; break;
                case 22: _attr.Flags &= ~(CellFlags.Bold | CellFlags.Dim); break;
                case 23: _attr.Flags &= ~CellFlags.Italic; break;
                case 24: _attr.Flags &= ~(CellFlags.Underline | CellFlags.DoubleUnderline | CellFlags.CurlyUnderline); break;
                case 25: _attr.Flags &= ~CellFlags.Blink; break;
                case 27: _attr.Flags &= ~CellFlags.Inverse; break;
                case 28: _attr.Flags &= ~CellFlags.Hidden; break;
                case 29: _attr.Flags &= ~CellFlags.Strike; break;
                case 53: _attr.Flags |= CellFlags.Overline; break;
                case 55: _attr.Flags &= ~CellFlags.Overline; break;
                case >= 30 and <= 37: _attr.Fg = CellAttr.Palette(p - 30); break;
                case 38: _attr.Fg = ExtendedColor(ref i, _attr.Fg); break;
                case 39: _attr.Fg = 0; break;
                case >= 40 and <= 47: _attr.Bg = CellAttr.Palette(p - 40); break;
                case 48: _attr.Bg = ExtendedColor(ref i, _attr.Bg); break;
                case 49: _attr.Bg = 0; break;
                case 58: ExtendedColor(ref i, 0); break; // Unterstreichungsfarbe: ignorieren, aber Parameter verbrauchen
                case >= 90 and <= 97: _attr.Fg = CellAttr.Palette(p - 90 + 8); break;
                case >= 100 and <= 107: _attr.Bg = CellAttr.Palette(p - 100 + 8); break;
            }
        }
    }

    uint ExtendedColor(ref int i, uint current)
    {
        if (i + 1 >= _pc) return current;
        bool colon = _sub[i + 1];
        int kind = _params[i + 1];
        if (colon)
        {
            // 38:5:n  bzw. 38:2:[cs]:r:g:b
            int start = i + 1;
            int end = start;
            while (end + 1 < _pc && _sub[end + 1]) end++;
            int count = end - start; // Werte nach dem Typ
            i = end;
            if (kind == 5 && count >= 1) return CellAttr.Palette(_params[start + 1]);
            if (kind == 2 && count >= 3)
            {
                int o = count >= 4 ? start + 2 : start + 1;
                return CellAttr.Rgb(_params[o], _params[o + 1], _params[o + 2]);
            }
            return current;
        }
        if (kind == 5 && i + 2 < _pc)
        {
            i += 2;
            return CellAttr.Palette(_params[i]);
        }
        if (kind == 2 && i + 4 < _pc)
        {
            uint c = CellAttr.Rgb(_params[i + 2], _params[i + 3], _params[i + 4]);
            i += 4;
            return c;
        }
        i = _pc;
        return current;
    }

    // ───────────────────────────── OSC ─────────────────────────────

    void OscDispatch()
    {
        string s = _osc.ToString();
        _osc.Clear();
        int semi = s.IndexOf(';');
        string num = semi < 0 ? s : s[..semi];
        string rest = semi < 0 ? "" : s[(semi + 1)..];
        switch (num)
        {
            case "0":
            case "2":
                Title = rest;
                TitleChanged?.Invoke(rest);
                break;
            case "8":
                {
                    int semi2 = rest.IndexOf(';');
                    string uri = semi2 < 0 ? "" : rest[(semi2 + 1)..];
                    if (uri.Length == 0) _attr.Link = 0;
                    else
                    {
                        if (!_linkIds.TryGetValue(uri, out var id))
                        {
                            if (_links.Count >= ushort.MaxValue) { _links.Clear(); _links.Add(""); _linkIds.Clear(); }
                            id = (ushort)_links.Count;
                            _links.Add(uri);
                            _linkIds[uri] = id;
                        }
                        _attr.Link = id;
                    }
                    break;
                }
            case "9":
                if (rest.StartsWith("4;"))
                {
                    var parts = rest.Split(';');
                    int st = parts.Length > 1 && int.TryParse(parts[1], out var a) ? a : 0;
                    int pr = parts.Length > 2 && int.TryParse(parts[2], out var b) ? b : 0;
                    ProgressChanged?.Invoke(st, pr);
                }
                else if (rest.Length > 0) DesktopNotification?.Invoke(rest);
                break;
            case "10":
                if (rest == "?") Reply?.Invoke("\x1b]10;rgb:d6d6/ecec/e1e1\x1b\\");
                break;
            case "11":
                if (rest == "?") Reply?.Invoke("\x1b]11;rgb:0808/1010/0d0d\x1b\\");
                break;
            case "777":
                if (rest.StartsWith("notify;"))
                {
                    var parts = rest.Split(';', 3);
                    DesktopNotification?.Invoke(parts.Length > 2 ? parts[2] : parts.Length > 1 ? parts[1] : "");
                }
                break;
        }
    }

    // ───────────────────────────── Größe ─────────────────────────────

    public void Resize(int cols, int rows)
    {
        cols = Math.Max(2, cols);
        rows = Math.Max(1, rows);
        if (cols == Cols && rows == Rows) return;
        bool wasAlt = AltActive;

        // Zeilen: beim Verkleinern erst leere Zeilen unter dem Cursor entfernen, dann oben in den Scrollback schieben.
        if (rows != Rows)
        {
            _main = ResizeRows(_main, rows, !wasAlt ? CursorY : -1, true, out int shiftMain);
            _alt = ResizeRows(_alt, rows, wasAlt ? CursorY : -1, false, out int shiftAlt);
            int shift = wasAlt ? shiftAlt : shiftMain;
            CursorY = Math.Clamp(CursorY - shift, 0, rows - 1);
            _savedMain.Y = Math.Clamp(_savedMain.Y - shiftMain, 0, rows - 1);
            _savedAlt.Y = Math.Clamp(_savedAlt.Y - shiftAlt, 0, rows - 1);
        }
        foreach (var l in _main) l.Resize(cols);
        foreach (var l in _alt) l.Resize(cols);

        _screen = wasAlt ? _alt : _main;
        Cols = cols;
        Rows = rows;
        var tabs = NewTabs(cols);
        Array.Copy(_tabs, tabs, Math.Min(_tabs.Length, cols));
        _tabs = tabs;
        _top = 0;
        _bottom = rows - 1;
        CursorX = Math.Min(CursorX, cols - 1);
        _pendingWrap = false;
        Version++;
    }

    TerminalLine[] ResizeRows(TerminalLine[] screen, int rows, int cursorY, bool isMain, out int shift)
    {
        shift = 0;
        int oldRows = screen.Length;
        if (rows == oldRows) return screen;
        var list = new List<TerminalLine>(screen);
        if (rows < oldRows)
        {
            int remove = oldRows - rows;
            // leere Zeilen unterhalb des Cursors zuerst
            for (int y = list.Count - 1; y > Math.Max(cursorY, -1) && remove > 0; y--)
            {
                if (IsBlank(list[y])) { list.RemoveAt(y); remove--; }
                else break;
            }
            while (remove > 0)
            {
                if (isMain) Scrollback.Add(list[0]);
                list.RemoveAt(0);
                shift++;
                remove--;
            }
        }
        else
        {
            int add = rows - oldRows;
            // beim Vergrößern Zeilen aus dem Scrollback zurückholen (nur Hauptbildschirm)
            while (isMain && add > 0 && Scrollback.Count > 0 && cursorY >= 0)
            {
                var l = Scrollback.PopLast();
                if (l == null) break;
                list.Insert(0, l);
                shift--;
                add--;
            }
            while (add-- > 0) list.Add(new TerminalLine(screen.Length > 0 ? screen[0].Cells.Length : Cols, default));
        }
        return list.ToArray();
    }

    static bool IsBlank(TerminalLine l)
    {
        foreach (var c in l.Cells) if (c.Rune != 0 && c.Rune != ' ' || c.Attr.Bg != 0) return false;
        return true;
    }

    // ───────────────────────────── Text ─────────────────────────────

    public static string LineText(TerminalLine line, int from = 0, int to = int.MaxValue)
    {
        var sb = new StringBuilder();
        to = Math.Min(to, line.Cells.Length);
        for (int x = Math.Max(0, from); x < to; x++)
        {
            var c = line.Cells[x];
            if (c.Kind == Cell.WideTail) continue;
            if (c.Rune == 0) sb.Append(' ');
            else sb.Append(char.ConvertFromUtf32(c.Rune));
        }
        return sb.ToString();
    }

    /// <summary>Text zwischen zwei Positionen (absolute Zeilen, Ende exklusiv in der Spalte).</summary>
    public string GetText(long startAbs, int startCol, long endAbs, int endCol)
    {
        var sb = new StringBuilder();
        for (long a = startAbs; a <= endAbs; a++)
        {
            var line = GetLineAbs(a);
            if (line == null) continue;
            int from = a == startAbs ? startCol : 0;
            int to = a == endAbs ? endCol : line.Cells.Length;
            string t = LineText(line, from, to);
            bool wrapped = line.Wrapped && a != endAbs;
            sb.Append(wrapped ? t : t.TrimEnd(' '));
            if (a != endAbs && !wrapped) sb.Append("\r\n");
        }
        return sb.ToString();
    }
}

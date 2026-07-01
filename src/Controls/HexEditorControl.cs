using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace UnifiedDDRFlasher
{
    public sealed class HexEditorControl : UserControl
    {
        public sealed class FieldHighlight
        {
            public int Start;
            public int Length;
            public Color Color;
            public string Label;

            public int End => Start + Length - 1;
            public bool Contains(int offset) => offset >= Start && offset <= End;
        }

        private byte[] _data = Array.Empty<byte>();
        private readonly List<FieldHighlight> _fields = new List<FieldHighlight>();
        private readonly VScrollBar _scroll = new VScrollBar { Dock = DockStyle.Right };
        private readonly ToolTip _toolTip = new ToolTip { ShowAlways = false, InitialDelay = 350, ReshowDelay = 100 };
        private string _lastTooltipText = "";

        // selection is an inclusive range: anchor is the fixed end, caret the moving end and edit cursor
        private int _selAnchor = -1;
        private int _selCaret = -1;
        private bool _selInAscii = false;
        private bool _dragging = false;
        private readonly Timer _autoScrollTimer = new Timer { Interval = 40 };
        private int _autoScrollDir = 0;
        private bool _readOnly = true;
        private bool _editingHighNibble = true;
        private int _topRow;

        private int _cellW;
        private int _rowH;
        private int _offsetW;
        private int _asciiCharW;
        private int _gutter;
        private const int BytesPerRow = 16;
        private const int GroupGap = 6;

        private static readonly Color ColHeaderBack = Color.FromArgb(238, 241, 247);
        private static readonly Color ColHeaderText = Color.FromArgb(0, 78, 152);
        private static readonly Color OffsetText = Color.FromArgb(90, 100, 120);
        private static readonly Color SelBack = Color.FromArgb(204, 224, 255);
        private static readonly Color SelText = Color.FromArgb(0, 40, 104);
        private static readonly Color ZeroByteText = Color.FromArgb(176, 182, 192);
        private static readonly Color AsciiText = Color.FromArgb(60, 66, 78);
        private static readonly Color GridLine = Color.FromArgb(228, 231, 238);

        private int _selected
        {
            get => _selCaret;
            set { _selCaret = value; }
        }

        private bool HasSelection => _selCaret >= 0 && _selAnchor >= 0;
        private int SelMin => HasSelection ? Math.Min(_selAnchor, _selCaret) : -1;
        private int SelMax => HasSelection ? Math.Max(_selAnchor, _selCaret) : -1;
        private bool InSelection(int offset) => HasSelection && offset >= SelMin && offset <= SelMax;

        public int SelectionLength => HasSelection ? SelMax - SelMin + 1 : 0;

        public byte[] GetSelectedBytes()
        {
            if (!HasSelection) return Array.Empty<byte>();
            int lo = SelMin, hi = Math.Min(SelMax, _data.Length - 1);
            if (hi < lo) return Array.Empty<byte>();
            var outp = new byte[hi - lo + 1];
            Array.Copy(_data, lo, outp, 0, outp.Length);
            return outp;
        }

        public event EventHandler<ByteChangedEventArgs> ByteChanged;
        public event EventHandler<int> SelectionChanged;

        public sealed class ByteChangedEventArgs : EventArgs
        {
            public int Offset { get; }
            public byte OldValue { get; }
            public byte NewValue { get; }
            public ByteChangedEventArgs(int offset, byte oldV, byte newV)
            { Offset = offset; OldValue = oldV; NewValue = newV; }
        }

        public HexEditorControl()
        {
            DoubleBuffered = true;
            BackColor = Color.White;
            Font = new Font("Consolas", 9.5F);
            TabStop = true;
            SetStyle(ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);

            _scroll.Scroll += (s, e) => { _topRow = _scroll.Value; Invalidate(); };
            Controls.Add(_scroll);

            _autoScrollTimer.Tick += AutoScrollTick;

            RecomputeMetrics();
        }


        public bool ReadOnly
        {
            get => _readOnly;
            set { _readOnly = value; Invalidate(); }
        }

        public byte[] Data => _data;

        public void SetData(byte[] data)
        {
            _data = data ?? Array.Empty<byte>();
            if (_data.Length == 0) { _selAnchor = _selCaret = -1; }
            else
            {
                if (_selCaret >= _data.Length) _selCaret = _data.Length - 1;
                if (_selAnchor >= _data.Length) _selAnchor = _data.Length - 1;
            }
            _editingHighNibble = true;
            UpdateScrollRange();
            Invalidate();
        }

        public void ClearFieldHighlights()
        {
            _fields.Clear();
            Invalidate();
        }

        public void AddFieldHighlight(int start, int length, Color color, string label)
        {
            if (length < 1) return;
            _fields.Add(new FieldHighlight { Start = start, Length = length, Color = color, Label = label });
            Invalidate();
        }

        public int SelectedOffset => _selCaret;

        public void SelectOffset(int offset, bool scrollIntoView = true)
        {
            if (_data.Length == 0) { _selAnchor = _selCaret = -1; }
            else { _selCaret = Math.Max(0, Math.Min(offset, _data.Length - 1)); _selAnchor = _selCaret; }
            _editingHighNibble = true;
            if (scrollIntoView && _selCaret >= 0) EnsureRowVisible(_selCaret / BytesPerRow);
            SelectionChanged?.Invoke(this, _selCaret);
            Invalidate();
        }

        public void SelectRange(int start, int end, bool scrollIntoView = true)
        {
            if (_data.Length == 0) { _selAnchor = _selCaret = -1; Invalidate(); return; }
            _selAnchor = Math.Max(0, Math.Min(start, _data.Length - 1));
            _selCaret = Math.Max(0, Math.Min(end, _data.Length - 1));
            _editingHighNibble = true;
            if (scrollIntoView) EnsureRowVisible(_selCaret / BytesPerRow);
            SelectionChanged?.Invoke(this, _selCaret);
            Invalidate();
        }


        private void RecomputeMetrics()
        {
            using (var g = CreateGraphics())
            {
                SizeF ch = g.MeasureString("00", Font);
                _asciiCharW = (int)Math.Ceiling(g.MeasureString("W", Font).Width) - 2;
                if (_asciiCharW < 7) _asciiCharW = 7;
                _rowH = (int)Math.Ceiling(ch.Height) + 4;
                _cellW = (int)Math.Ceiling(g.MeasureString("FF", Font).Width) + 7;
                _offsetW = (int)Math.Ceiling(g.MeasureString("0000", Font).Width) + 14;
                _gutter = 14;
            }
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            RecomputeMetrics();
            UpdateScrollRange();
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateScrollRange();
            Invalidate();
        }

        private int TotalRows => _data.Length <= 0 ? 0 : (_data.Length + BytesPerRow - 1) / BytesPerRow;
        private int HeaderH => _rowH + 2;
        private int VisibleRows => Math.Max(1, (ClientSize.Height - HeaderH) / Math.Max(1, _rowH));

        private void UpdateScrollRange()
        {
            int total = TotalRows;
            int vis = VisibleRows;
            if (total <= vis)
            {
                _scroll.Enabled = false;
                _scroll.Visible = false;
                _topRow = 0;
            }
            else
            {
                _scroll.Visible = true;
                _scroll.Enabled = true;
                _scroll.Minimum = 0;
                _scroll.Maximum = total - 1;
                _scroll.LargeChange = vis;
                _scroll.SmallChange = 1;
                if (_topRow > total - vis) _topRow = Math.Max(0, total - vis);
                if (_scroll.Value != _topRow)
                    _scroll.Value = Math.Min(_topRow, _scroll.Maximum);
            }
        }

        private void EnsureRowVisible(int row)
        {
            int vis = VisibleRows;
            if (row < _topRow) _topRow = row;
            else if (row >= _topRow + vis) _topRow = row - vis + 1;
            if (_topRow < 0) _topRow = 0;
            if (_scroll.Visible) _scroll.Value = Math.Min(_topRow, _scroll.Maximum);
        }

        private int HexCellX(int col) => _offsetW + col * _cellW + (col >= 8 ? GroupGap : 0);
        private int AsciiX => HexCellX(BytesPerRow) + _gutter;


        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.Clear(BackColor);

            DrawHeader(g);

            if (_data.Length == 0)
            {
                TextRenderer.DrawText(g, "No data", Font, new Point(_offsetW, HeaderH + 4), OffsetText);
                return;
            }

            int vis = VisibleRows;
            int firstRow = _topRow;
            int lastRow = Math.Min(TotalRows - 1, firstRow + vis);

            for (int row = firstRow; row <= lastRow; row++)
                DrawRow(g, row);

            DrawFieldHighlights(g, firstRow, lastRow);
        }

        private void DrawHeader(Graphics g)
        {
            var headerRect = new Rectangle(0, 0, ClientSize.Width, HeaderH);
            using (var b = new SolidBrush(ColHeaderBack)) g.FillRectangle(b, headerRect);
            using (var p = new Pen(GridLine)) g.DrawLine(p, 0, HeaderH - 1, ClientSize.Width, HeaderH - 1);

            var headerFont = new Font(Font, FontStyle.Bold);

            string corner = _selected >= 0 ? $"{_selected:X4}" : "Offset";
            var cornerRect = new Rectangle(0, 0, _offsetW, HeaderH);
            if (_selected >= 0)
            {
                using (var b = new SolidBrush(SelBack)) g.FillRectangle(b, cornerRect);
            }
            TextRenderer.DrawText(g, corner, headerFont,
                cornerRect, _selected >= 0 ? SelText : ColHeaderText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            int selCol = _selected >= 0 ? _selected % BytesPerRow : -1;
            for (int col = 0; col < BytesPerRow; col++)
            {
                int x = HexCellX(col);
                var cellRect = new Rectangle(x, 0, _cellW, HeaderH);
                if (col == selCol)
                    using (var b = new SolidBrush(SelBack)) g.FillRectangle(b, cellRect);
                TextRenderer.DrawText(g, $"{col:X2}", headerFont, cellRect,
                    col == selCol ? SelText : ColHeaderText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            var asciiHdr = new Rectangle(AsciiX, 0, BytesPerRow * _asciiCharW, HeaderH);
            TextRenderer.DrawText(g, "ASCII", headerFont, asciiHdr, ColHeaderText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            headerFont.Dispose();
        }

        private int RowY(int row) => HeaderH + (row - _topRow) * _rowH;

        private void DrawRow(Graphics g, int row)
        {
            int y = RowY(row);
            int baseOff = row * BytesPerRow;
            int selRow = _selected >= 0 ? _selected / BytesPerRow : -1;

            var offRect = new Rectangle(0, y, _offsetW, _rowH);
            if (row == selRow)
                using (var b = new SolidBrush(SelBack)) g.FillRectangle(b, offRect);
            TextRenderer.DrawText(g, $"{baseOff:X4}", Font, offRect,
                row == selRow ? SelText : OffsetText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            using (var p = new Pen(GridLine)) g.DrawLine(p, _offsetW - 1, y, _offsetW - 1, y + _rowH);

            for (int col = 0; col < BytesPerRow; col++)
            {
                int off = baseOff + col;
                if (off >= _data.Length) break;
                byte val = _data[off];
                bool isSel = InSelection(off);

                var cellRect = new Rectangle(HexCellX(col), y, _cellW, _rowH);
                if (isSel)
                    using (var b = new SolidBrush(SelBack)) g.FillRectangle(b, cellRect);

                Color txt = isSel ? SelText : (val == 0 ? ZeroByteText : ForeColor);
                TextRenderer.DrawText(g, $"{val:X2}", Font, cellRect, txt,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

                var aRect = new Rectangle(AsciiX + col * _asciiCharW, y, _asciiCharW, _rowH);
                if (isSel)
                    using (var b = new SolidBrush(SelBack)) g.FillRectangle(b, aRect);
                char c = (val >= 32 && val < 127) ? (char)val : '·';
                TextRenderer.DrawText(g, c.ToString(), Font, aRect,
                    isSel ? SelText : AsciiText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        private void DrawFieldHighlights(Graphics g, int firstRow, int lastRow)
        {
            foreach (var f in _fields)
            {
                if (f.Length < 1) continue;
                int s = Math.Max(0, f.Start);
                int eByte = Math.Min(_data.Length - 1, f.End);
                if (eByte < s) continue;

                using (var pen = new Pen(f.Color, 1.6f))
                using (var fill = new SolidBrush(Color.FromArgb(28, f.Color)))
                {
                    int rowStart = s / BytesPerRow, rowEnd = eByte / BytesPerRow;
                    for (int row = rowStart; row <= rowEnd; row++)
                    {
                        if (row < firstRow || row > lastRow) continue;
                        int c0 = (row == rowStart) ? s % BytesPerRow : 0;
                        int c1 = (row == rowEnd) ? eByte % BytesPerRow : BytesPerRow - 1;

                        int x0 = HexCellX(c0) + 1;
                        int x1 = HexCellX(c1) + _cellW - 1;
                        int y = RowY(row) + 1;
                        var rect = new Rectangle(x0, y, x1 - x0, _rowH - 2);
                        g.FillRectangle(fill, rect);
                        g.DrawRectangle(pen, rect);

                        int ax0 = AsciiX + c0 * _asciiCharW;
                        int ax1 = AsciiX + (c1 + 1) * _asciiCharW;
                        g.DrawRectangle(pen, new Rectangle(ax0, y, ax1 - ax0, _rowH - 2));
                    }
                }
            }
        }


        private int OffsetAtPoint(Point p, out bool inAscii)
        {
            inAscii = false;
            if (p.Y < HeaderH) return -1;
            int row = _topRow + (p.Y - HeaderH) / _rowH;
            if (row < 0 || row >= TotalRows) return -1;

            for (int col = 0; col < BytesPerRow; col++)
            {
                int x = HexCellX(col);
                if (p.X >= x && p.X < x + _cellW)
                {
                    int off = row * BytesPerRow + col;
                    return off < _data.Length ? off : -1;
                }
            }
            int aStart = AsciiX;
            if (p.X >= aStart && p.X < aStart + BytesPerRow * _asciiCharW)
            {
                int col = (p.X - aStart) / _asciiCharW;
                inAscii = true;
                int off = row * BytesPerRow + col;
                return (col < BytesPerRow && off < _data.Length) ? off : -1;
            }
            return -1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button != MouseButtons.Left) return;
            int off = OffsetAtPoint(e.Location, out bool inAscii);
            if (off < 0) return;

            bool extend = (ModifierKeys & Keys.Shift) != 0 && _selAnchor >= 0;
            if (extend)
            {
                _selCaret = off;
            }
            else
            {
                _selAnchor = off;
                _selCaret = off;
            }
            _selInAscii = inAscii;
            _dragging = true;
            _editingHighNibble = true;
            SelectionChanged?.Invoke(this, _selCaret);
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (_dragging && (e.Button & MouseButtons.Left) != 0)
            {
                if (e.Y < HeaderH) _autoScrollDir = -1;
                else if (e.Y > ClientSize.Height) _autoScrollDir = +1;
                else _autoScrollDir = 0;
                _autoScrollTimer.Enabled = _autoScrollDir != 0 && _scroll.Visible;

                int off = OffsetAtPointClamped(e.Location);
                if (off >= 0 && off != _selCaret)
                {
                    _selCaret = off;
                    SelectionChanged?.Invoke(this, _selCaret);
                    Invalidate();
                }
                return;
            }

            int hoff = OffsetAtPoint(e.Location, out _);
            string text = "";
            if (hoff >= 0)
            {
                var f = FieldAt(hoff);
                text = f != null
                    ? $"Offset 0x{hoff:X3} ({hoff})  •  {f.Label}"
                    : $"Offset 0x{hoff:X3} ({hoff})";
            }
            if (text != _lastTooltipText)
            {
                _lastTooltipText = text;
                if (text.Length == 0) _toolTip.Hide(this);
                else _toolTip.SetToolTip(this, text);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
            _autoScrollDir = 0;
            _autoScrollTimer.Enabled = false;
        }

        private int OffsetAtPointClamped(Point p)
        {
            if (_data.Length == 0) return -1;
            int relY = p.Y - HeaderH;
            int row = _topRow + (relY < 0 ? 0 : relY / _rowH);
            if (row < 0) row = 0;
            if (row >= TotalRows) row = TotalRows - 1;

            int col;
            if (p.X < HexCellX(0)) col = 0;
            else if (p.X >= AsciiX)
            {
                int ac = (p.X - AsciiX) / Math.Max(1, _asciiCharW);
                col = Math.Max(0, Math.Min(BytesPerRow - 1, ac));
            }
            else
            {
                col = BytesPerRow - 1;
                for (int c = 0; c < BytesPerRow; c++)
                {
                    if (p.X < HexCellX(c) + _cellW) { col = c; break; }
                }
            }
            int off = row * BytesPerRow + col;
            return Math.Max(0, Math.Min(off, _data.Length - 1));
        }

        private void AutoScrollTick(object sender, EventArgs e)
        {
            if (_autoScrollDir == 0 || !_scroll.Visible) { _autoScrollTimer.Enabled = false; return; }
            int v = Math.Max(_scroll.Minimum, Math.Min(_scroll.Maximum, _scroll.Value + _autoScrollDir));
            if (v == _scroll.Value) return;
            _scroll.Value = v;
            _topRow = v;
            int edgeRow = _autoScrollDir < 0 ? _topRow : _topRow + VisibleRows - 1;
            edgeRow = Math.Max(0, Math.Min(edgeRow, TotalRows - 1));
            int caretCol = _selCaret >= 0 ? _selCaret % BytesPerRow : 0;
            int off = Math.Min(edgeRow * BytesPerRow + caretCol, _data.Length - 1);
            if (off != _selCaret) { _selCaret = off; SelectionChanged?.Invoke(this, _selCaret); }
            Invalidate();
        }

        private FieldHighlight FieldAt(int offset)
        {
            for (int i = _fields.Count - 1; i >= 0; i--)
                if (_fields[i].Contains(offset)) return _fields[i];
            return null;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!_scroll.Visible) return;
            int lines = -(e.Delta / 120) * 3;
            int v = Math.Max(_scroll.Minimum, Math.Min(_scroll.Maximum, _scroll.Value + lines));
            _scroll.Value = v;
            _topRow = v;
            Invalidate();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            switch (k)
            {
                case Keys.Left:
                case Keys.Right:
                case Keys.Up:
                case Keys.Down:
                case Keys.PageUp:
                case Keys.PageDown:
                case Keys.Home:
                case Keys.End:
                    return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (_data.Length == 0) return;

            if (e.Control && e.KeyCode == Keys.C) { CopySelectionToClipboard(); e.Handled = true; return; }
            if (e.Control && e.KeyCode == Keys.A)
            {
                _selAnchor = 0; _selCaret = _data.Length - 1;
                SelectionChanged?.Invoke(this, _selCaret);
                Invalidate(); e.Handled = true; return;
            }

            int sel = _selCaret < 0 ? 0 : _selCaret;
            bool extend = e.Shift;

            switch (e.KeyCode)
            {
                case Keys.Left: MoveSelection(sel - 1, extend); e.Handled = true; return;
                case Keys.Right: MoveSelection(sel + 1, extend); e.Handled = true; return;
                case Keys.Up: MoveSelection(sel - BytesPerRow, extend); e.Handled = true; return;
                case Keys.Down: MoveSelection(sel + BytesPerRow, extend); e.Handled = true; return;
                case Keys.Home: MoveSelection(sel - (sel % BytesPerRow), extend); e.Handled = true; return;
                case Keys.End: MoveSelection(sel - (sel % BytesPerRow) + BytesPerRow - 1, extend); e.Handled = true; return;
                case Keys.PageUp: MoveSelection(sel - BytesPerRow * VisibleRows, extend); e.Handled = true; return;
                case Keys.PageDown: MoveSelection(sel + BytesPerRow * VisibleRows, extend); e.Handled = true; return;
            }
        }

        private void CopySelectionToClipboard()
        {
            var bytes = GetSelectedBytes();
            if (bytes.Length == 0) return;
            string text;
            if (_selInAscii)
            {
                var sb = new System.Text.StringBuilder(bytes.Length);
                foreach (var b in bytes) sb.Append((b >= 32 && b < 127) ? (char)b : '.');
                text = sb.ToString();
            }
            else
            {
                var parts = new string[bytes.Length];
                for (int i = 0; i < bytes.Length; i++) parts[i] = bytes[i].ToString("X2");
                text = string.Join(" ", parts);
            }
            try { if (text.Length > 0) Clipboard.SetText(text); } catch { }
        }

        private void MoveSelection(int newOff, bool extend = false)
        {
            newOff = Math.Max(0, Math.Min(newOff, _data.Length - 1));
            _selCaret = newOff;
            if (!extend) _selAnchor = newOff;
            else if (_selAnchor < 0) _selAnchor = newOff;
            _editingHighNibble = true;
            EnsureRowVisible(_selCaret / BytesPerRow);
            SelectionChanged?.Invoke(this, _selCaret);
            Invalidate();
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            if (_readOnly || _selected < 0 || _selected >= _data.Length) return;

            int nibble = HexVal(e.KeyChar);
            if (nibble < 0) return;
            e.Handled = true;

            byte old = _data[_selected];
            byte updated;
            if (_editingHighNibble)
            {
                updated = (byte)((nibble << 4) | (old & 0x0F));
                _data[_selected] = updated;
                _editingHighNibble = false;
                Invalidate();
                RaiseByteChanged(_selected, old, updated);
            }
            else
            {
                updated = (byte)((old & 0xF0) | nibble);
                _data[_selected] = updated;
                _editingHighNibble = true;
                RaiseByteChanged(_selected, old, updated);
                if (_selected < _data.Length - 1) MoveSelection(_selected + 1);
                else Invalidate();
            }
        }

        private void RaiseByteChanged(int off, byte oldV, byte newV)
        {
            if (oldV != newV)
                ByteChanged?.Invoke(this, new ByteChangedEventArgs(off, oldV, newV));
        }

        private static int HexVal(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _autoScrollTimer.Dispose(); _toolTip.Dispose(); _scroll.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
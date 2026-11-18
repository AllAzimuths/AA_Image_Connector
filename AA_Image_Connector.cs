using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

[assembly: AssemblyTitle("AA Image Connector")]
[assembly: AssemblyProduct("AA Image Connector")]
[assembly: AssemblyDescription("複数の画像を指定サイズに結合するツール")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace AAImageConnector
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    class SlotState
    {
        public Image Image;
        public string Path;
        public float Zoom = 1.0f;   // cover 表示を 1.0 とした倍率
        public float OffX;          // キャンバス座標での表示オフセット
        public float OffY;
    }

    // 結合キャンバス全体を描画し、スロットごとのズーム・パンを処理するパネル
    class CanvasPanel : Panel
    {
        public int OutputWidth = 1920;
        public int OutputHeight = 1080;
        public int Count = 2;
        public bool GridLayout;          // false: 縦割り / true: 碁盤の目
        public bool HorizontalLayout;    // 横割り（Count 枚を縦に積む）
        public int GridCols = 2;
        public int BorderThickness = 4;
        public Color BorderColor = Color.White;
        public readonly List<SlotState> Slots = new List<SlotState>();

        public event EventHandler SlotClicked;   // クリックされたスロットを ActiveSlotIndex に入れて通知
        public event EventHandler SlotsSwapped;  // ドラッグ入れ替え後（SwapFrom/SwapTo を設定）
        public int SwapFrom = -1, SwapTo = -1;
        public int ActiveSlotIndex = -1;

        // 右ドラッグによる画像パン用
        int panSlot = -1;
        Point panStart;
        float panOffX, panOffY;
        bool panMoved;
        public bool SuppressContextMenu;   // 右ドラッグ後はコンテキストメニューを抑制

        // 左ドラッグによるスロット入れ替え用
        int swapSource = -1;
        Point swapStart;
        bool swapDragging;
        int swapHover = -1;

        // 結合パターンの境界線ドラッグ用
        bool spanMove;
        int[] dragStartAssign;
        int dragCellA, dragCellB;
        bool dragMoved;
        int dragPreviewPattern = -1;   // ドラッグ中のプレビュー用パターン番号（-1 なら通常）
        Point spanStartPoint;

        public CanvasPanel()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
            AllowDrop = true;
            BackColor = Color.FromArgb(233, 233, 233);
        }

        public void EnsureSlots(int n)
        {
            while (Slots.Count < n) Slots.Add(new SlotState());
            while (Slots.Count > n)
            {
                SlotState s = Slots[Slots.Count - 1];
                if (s.Image != null) s.Image.Dispose();
                Slots.RemoveAt(Slots.Count - 1);
            }
        }

        // スロット i と j の中身（画像・ズーム・パン位置）を入れ替える
        public bool SwapSlots(int i, int j)
        {
            if (i < 0 || j < 0 || i >= Slots.Count || j >= Slots.Count) return false;
            SlotState tmp = Slots[i];
            Slots[i] = Slots[j];
            Slots[j] = tmp;
            ActiveSlotIndex = j;
            Invalidate();
            return true;
        }

        // 結合パターン：余ったマスをどう分割するかの全パターンを保持し、選択中のものを使う
        public int PatternIndex = 0;
        int[][] tilings;            // 各パターンの cell -> slot 割り当て
        int tilingCols, tilingRows;

        public event EventHandler PatternChanged;

        public int TilingCount { get { return tilings == null ? 0 : tilings.Length; } }

        public bool HasStretchedCell()
        {
            return GridLayout && tilings != null && tilings.Length > 1;
        }

        public void SetPattern(int index)
        {
            if (tilings == null || tilings.Length == 0) return;
            PatternIndex = Math.Min(Math.Max(index, 0), tilings.Length - 1);
            Invalidate();
        }

        public void ResetPattern() { PatternIndex = 0; }

        public string TilingText(int index)
        {
            if (tilings == null || index < 0 || index >= tilings.Length) return "";
            int[] a = tilings[index];
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int r = 0; r < tilingRows; r++)
            {
                if (r > 0) sb.Append(" / ");
                for (int c = 0; c < tilingCols; c++)
                {
                    if (c > 0) sb.Append(' ');
                    sb.Append((char)('a' + a[r * tilingCols + c]));
                }
            }
            return sb.ToString();
        }

        // 設定変更時に呼ぶ：パターンの列挙と初期化
        public void RebuildTilings()
        {
            tilings = null;
            PatternIndex = 0;
            if (!GridLayout) return;
            int n = Count;
            int cols = GridColsEffective();
            int rows = (n + cols - 1) / cols;
            int cells = rows * cols;
            tilingCols = cols;
            tilingRows = rows;

            if (cells == n)
            {
                tilings = new int[1][];
                int[] a = new int[cells];
                for (int i = 0; i < cells; i++) a[i] = i;
                tilings[0] = a;
                return;
            }

            List<int[]> list = new List<int[]>();
            int[] assign = new int[cells];
            for (int i = 0; i < cells; i++) assign[i] = -1;
            EnumerateTilings(list, assign, 0, 0, cols, rows);
            tilings = list.ToArray();
        }

        // 全セルを n 個の直線スパン（縦横一直線の矩形）で分割した全パターンを列挙
        void EnumerateTilings(List<int[]> list, int[] assign, int slot, int filled, int cols, int rows)
        {
            if (list.Count >= 2000) return;
            int n = Count;
            int cells = rows * cols;

            if (slot >= n)
            {
                if (filled == cells) list.Add((int[])assign.Clone());
                return;
            }

            int c = -1;
            for (int i = 0; i < cells; i++)
            {
                if (assign[i] < 0) { c = i; break; }
            }
            if (c < 0) return;
            int r = c / cols, col = c % cols;

            int unassigned = 0;
            for (int i = 0; i < cells; i++) if (assign[i] < 0) unassigned++;
            if (unassigned < n - slot) return;

            // 横方向スパン（長さ1以上）
            int maxH = 0;
            while (col + maxH < cols && assign[r * cols + col + maxH] < 0) maxH++;
            for (int len = 1; len <= maxH; len++)
            {
                for (int k = 0; k < len; k++) assign[c + k] = slot;
                EnumerateTilings(list, assign, slot + 1, filled + len, cols, rows);
                for (int k = 0; k < len; k++) assign[c + k] = -1;
                if (list.Count >= 2000) return;
            }
            // 縦方向スパン（長さ2以上）
            int maxV = 0;
            while (r + maxV < rows && assign[(r + maxV) * cols + col] < 0) maxV++;
            for (int len = 2; len <= maxV; len++)
            {
                for (int k = 0; k < len; k++) assign[(r + k) * cols + col] = slot;
                EnumerateTilings(list, assign, slot + 1, filled + len, cols, rows);
                for (int k = 0; k < len; k++) assign[(r + k) * cols + col] = -1;
                if (list.Count >= 2000) return;
            }
        }

        int GridColsEffective() { return Math.Max(1, Math.Min(GridCols, Count)); }

        int[] CurrentAssignment()
        {
            if (tilings == null) RebuildTilings();
            if (tilings == null || tilings.Length == 0) return null;
            if (dragPreviewPattern >= 0 && dragPreviewPattern < tilings.Length) return tilings[dragPreviewPattern];
            PatternIndex = Math.Min(Math.Max(PatternIndex, 0), tilings.Length - 1);
            return tilings[PatternIndex];
        }
        // グリッドの幾何情報とセル割り当てを求める
        bool ComputeAssignment(out int cols, out int rows, out float cellW, out float cellH, out int[] assign)
        {
            cols = 0; rows = 0; cellW = 0; cellH = 0; assign = null;
            if (!GridLayout) return false;
            float W = OutputWidth, H = OutputHeight, t = BorderThickness;
            int n = Count;
            cols = GridColsEffective();
            rows = (n + cols - 1) / cols;
            cellW = (W - (cols - 1) * t) / cols;
            cellH = (H - (rows - 1) * t) / rows;
            assign = CurrentAssignment();
            return assign != null;
        }

        // 碁盤の目の分割線セグメント（両側のセルが別スロットの場合のみ存在）
        class DividerSeg
        {
            public bool Vertical;   // 縦線（左右を分ける）
            public RectangleF Rect; // キャンバス座標
            public int CellA, CellB;// 線を挟む2つのセル
        }

        List<DividerSeg> GetDividerSegments(int cols, int rows, float cellW, float cellH, int[] assign)
        {
            float t = BorderThickness;
            List<DividerSeg> list = new List<DividerSeg>();
            for (int r = 0; r < rows; r++)
            {
                for (int c = 1; c < cols; c++)
                {
                    if (assign[r * cols + c - 1] != assign[r * cols + c])
                    {
                        DividerSeg seg = new DividerSeg();
                        seg.Vertical = true;
                        seg.CellA = r * cols + c - 1;
                        seg.CellB = r * cols + c;
                        seg.Rect = new RectangleF(c * (cellW + t) - t, r * (cellH + t), t, cellH);
                        list.Add(seg);
                    }
                }
            }
            for (int c = 0; c < cols; c++)
            {
                for (int r = 1; r < rows; r++)
                {
                    if (assign[(r - 1) * cols + c] != assign[r * cols + c])
                    {
                        DividerSeg seg = new DividerSeg();
                        seg.Vertical = false;
                        seg.CellA = (r - 1) * cols + c;
                        seg.CellB = r * cols + c;
                        seg.Rect = new RectangleF(c * (cellW + t), r * (cellH + t) - t, cellW, t);
                        list.Add(seg);
                    }
                }
            }
            return list;
        }

        // 結合セルの内部（描画されていない線）を列挙
        struct GapSeg
        {
            public int CellA, CellB;
            public PointF Mid;
        }

        List<GapSeg> GetGaps(int cols, int rows, float cellW, float cellH, int[] assign)
        {
            float t = BorderThickness;
            List<GapSeg> list = new List<GapSeg>();
            for (int r = 0; r < rows; r++)
            {
                for (int c = 1; c < cols; c++)
                {
                    if (assign[r * cols + c - 1] == assign[r * cols + c])
                    {
                        GapSeg g;
                        g.CellA = r * cols + c - 1;
                        g.CellB = r * cols + c;
                        g.Mid = new PointF(c * (cellW + t) - t / 2f, r * (cellH + t) + cellH / 2f);
                        list.Add(g);
                    }
                }
            }
            for (int c = 0; c < cols; c++)
            {
                for (int r = 1; r < rows; r++)
                {
                    if (assign[(r - 1) * cols + c] == assign[r * cols + c])
                    {
                        GapSeg g;
                        g.CellA = (r - 1) * cols + c;
                        g.CellB = r * cols + c;
                        g.Mid = new PointF(c * (cellW + t) + cellW / 2f, r * (cellH + t) - t / 2f);
                        list.Add(g);
                    }
                }
            }
            return list;
        }

        // プレビュー座標→キャンバス座標
        PointF ToCanvas(Point p)
        {
            float scale;
            RectangleF cam = GetCanvasRect(out scale);
            return new PointF((p.X - cam.X) / scale, (p.Y - cam.Y) / scale);
        }

        // 分割線上にカーソルがあるか（碁盤の目でマスが余る時のみ）
        bool HitTestDivider(Point p, out int cellA, out int cellB, out bool vertical)
        {
            cellA = -1; cellB = -1; vertical = true;
            int cols, rows; float cellW, cellH; int[] assign;
            if (!ComputeAssignment(out cols, out rows, out cellW, out cellH, out assign)) return false;
            if (rows * cols == Count) return false; // 余りなし

            float scale;
            GetCanvasRect(out scale);
            float tol = 8f / scale; // プレビュー上 8px 相当
            PointF cp = ToCanvas(p);
            List<DividerSeg> segs = GetDividerSegments(cols, rows, cellW, cellH, assign);
            foreach (DividerSeg seg in segs)
            {
                RectangleF r = seg.Rect;
                r.Inflate(tol, tol);
                if (r.Contains(cp))
                {
                    cellA = seg.CellA;
                    cellB = seg.CellB;
                    vertical = seg.Vertical;
                    return true;
                }
            }
            return false;
        }

        // カーソルに最も近い結合内部の隙間を返す
        bool FindGapNear(Point p, int[] assign, int cols, int rows, float cellW, float cellH, out int gapA, out int gapB)
        {
            gapA = -1; gapB = -1;
            float scale;
            GetCanvasRect(out scale);
            float tol = 28f / scale;
            PointF cp = ToCanvas(p);
            float best = float.MaxValue;
            List<GapSeg> gaps = GetGaps(cols, rows, cellW, cellH, assign);
            foreach (GapSeg g in gaps)
            {
                float dx = g.Mid.X - cp.X, dy = g.Mid.Y - cp.Y;
                float d = (float)Math.Sqrt(dx * dx + dy * dy);
                if (d < best) { best = d; gapA = g.CellA; gapB = g.CellB; }
            }
            return best <= tol;
        }

        static List<int> CellsOfSlot(int[] assign, int n, int slot)
        {
            List<int> list = new List<int>();
            for (int i = 0; i < assign.Length; i++) if (assign[i] == slot) list.Add(i);
            return list;
        }

        // 結合の移動: 線 X(cellA|cellB)を取り除き(=結合)、隙間 Y(gapA|gapB)に線を置く(=分割)
        // その結果の cell -> slot 割り当てを返す。無効なら null。
        int[] TryMoveDivider(int[] a0, int xA, int xB, int gapA, int gapB)
        {
            int n = Count;
            if (a0[xA] == a0[xB]) return null;       // 既に結合済み
            if (a0[gapA] != a0[gapB]) return null;   // ドロップ先が結合内部の隙間でない

            int cols = GridColsEffective();
            int sY = a0[gapA];

            // 1) 隙間 Y を含む領域を Y の位置で2分割（一直線スパンなので index 順で切れる）
            List<int> cellsY = CellsOfSlot(a0, n, sY);
            cellsY.Sort();
            int iGapB = cellsY.IndexOf(gapB);
            if (iGapB <= 0 || iGapB >= cellsY.Count) return null;
            List<int> part1 = cellsY.GetRange(0, iGapB);
            List<int> part2 = cellsY.GetRange(iGapB, cellsY.Count - iGapB);

            List<List<int>> regions = new List<List<int>>();
            for (int s = 0; s < n; s++)
            {
                if (s == sY) continue;
                regions.Add(CellsOfSlot(a0, n, s));
            }
            regions.Add(part1);
            regions.Add(part2);
            if (regions.Count != n + 1) return null;

            // 2) 線 X の両端セルが属する2つの領域を結合
            int ri = -1, rj = -1;
            for (int i = 0; i < regions.Count; i++)
            {
                if (regions[i].Contains(xA)) ri = i;
                if (regions[i].Contains(xB)) rj = i;
            }
            if (ri < 0 || rj < 0 || ri == rj) return null;

            List<int> union = new List<int>();
            union.AddRange(regions[ri]);
            union.AddRange(regions[rj]);
            // 一直線（同じ行か同じ列）であること
            bool horizontalAdjacency = (xA / cols == xB / cols);
            foreach (int i in union)
            {
                if (horizontalAdjacency)
                {
                    if (i / cols != xA / cols) return null;
                }
                else
                {
                    if (i % cols != xA % cols) return null;
                }
            }
            union.Sort();
            int keep = Math.Min(ri, rj), removeIdx = Math.Max(ri, rj);
            regions.RemoveAt(removeIdx);
            regions[keep] = union;
            if (regions.Count != n) return null;

            // 3) 左上セル順に並べ直して a〜 のラベルを振り直す
            regions.Sort(delegate(List<int> p, List<int> q)
            {
                int mp = p[0], mq = q[0];
                for (int i = 1; i < p.Count; i++) if (p[i] < mp) mp = p[i];
                for (int i = 1; i < q.Count; i++) if (q[i] < mq) mq = q[i];
                return mp - mq;
            });
            int[] result = new int[a0.Length];
            for (int s = 0; s < regions.Count; s++)
                foreach (int cell in regions[s]) result[cell] = s;
            return result;
        }

        // 生成した割り当てと同じパターンのインデックスを返す
        int FindTilingIndex(int[] assign)
        {
            if (tilings == null) return -1;
            for (int t = 0; t < tilings.Length; t++)
            {
                int[] a = tilings[t];
                if (a.Length != assign.Length) continue;
                bool same = true;
                for (int i = 0; i < a.Length; i++) if (a[i] != assign[i]) { same = false; break; }
                if (same) return t;
            }
            return -1;
        }

        // キャンバス座標でのスロット矩形を返す
        public List<RectangleF> ComputeSlotRects()
        {
            List<RectangleF> rects = new List<RectangleF>();
            float W = OutputWidth, H = OutputHeight;
            float t = BorderThickness;
            int n = Count;

            if (HorizontalLayout)
            {
                float cellH = (H - (n - 1) * t) / n;
                for (int i = 0; i < n; i++)
                    rects.Add(new RectangleF(0, i * (cellH + t), W, cellH));
            }
            else if (!GridLayout) // 縦割り: 縦線で区切って横に並べる
            {
                float cellW = (W - (n - 1) * t) / n;
                for (int i = 0; i < n; i++)
                    rects.Add(new RectangleF(i * (cellW + t), 0, cellW, H));
            }
            else // 碁盤の目
            {
                int cols, rows; float cellW, cellH; int[] assign;
                ComputeAssignment(out cols, out rows, out cellW, out cellH, out assign);
                int nGrid = Count;
                int cells = rows * cols;

                // 各スロットのバウンディングボックス（直線スパンなので矩形になる）
                int[] minC = new int[nGrid], maxC = new int[nGrid], minR = new int[nGrid], maxR = new int[nGrid];
                int[] cnt = new int[nGrid];
                for (int i = 0; i < nGrid; i++) { minC[i] = cols; minR[i] = rows; maxC[i] = -1; maxR[i] = -1; }
                for (int i = 0; i < cells; i++)
                {
                    int s = assign[i];
                    int r = i / cols, c = i % cols;
                    if (c < minC[s]) minC[s] = c;
                    if (c > maxC[s]) maxC[s] = c;
                    if (r < minR[s]) minR[s] = r;
                    if (r > maxR[s]) maxR[s] = r;
                    cnt[s]++;
                }
                for (int s = 0; s < nGrid; s++)
                {
                    if (cnt[s] == 0) { rects.Add(new RectangleF(0, 0, cellW, cellH)); continue; }
                    int wCells = maxC[s] - minC[s] + 1;
                    int hCells = maxR[s] - minR[s] + 1;
                    rects.Add(new RectangleF(
                        minC[s] * (cellW + t),
                        minR[s] * (cellH + t),
                        wCells * cellW + (wCells - 1) * t,
                        hCells * cellH + (hCells - 1) * t));
                }
            }
            return rects;
        }

        // プレビュー上のキャンバス矩形と縮小率
        public RectangleF GetCanvasRect(out float scale)
        {
            float pad = 10f;
            float aw = Math.Max(1, ClientSize.Width - pad * 2);
            float ah = Math.Max(1, ClientSize.Height - pad * 2);
            scale = Math.Max(0.0001f, Math.Min(aw / OutputWidth, ah / OutputHeight));
            float cw = OutputWidth * scale, ch = OutputHeight * scale;
            return new RectangleF((ClientSize.Width - cw) / 2f, (ClientSize.Height - ch) / 2f, cw, ch);
        }

        int HitTest(Point p, out List<RectangleF> rects, out float scale, out RectangleF cam)
        {
            cam = GetCanvasRect(out scale);
            rects = ComputeSlotRects();
            for (int i = 0; i < rects.Count; i++)
            {
                RectangleF r = new RectangleF(cam.X + rects[i].X * scale, cam.Y + rects[i].Y * scale,
                                              rects[i].Width * scale, rects[i].Height * scale);
                if (r.Contains(p)) return i;
            }
            return -1;
        }

        float PreviewScale()
        {
            float scale;
            GetCanvasRect(out scale);
            return scale;
        }

        static void ClampOffset(SlotState s, RectangleF slot)
        {
            if (s.Image == null) { s.OffX = 0; s.OffY = 0; return; }
            float s0 = Math.Max(slot.Width / s.Image.Width, slot.Height / s.Image.Height);
            float sc = s0 * s.Zoom;
            float dw = s.Image.Width * sc, dh = s.Image.Height * sc;
            if (dw >= slot.Width)
            {
                float max = (dw - slot.Width) / 2f;
                s.OffX = Math.Max(-max, Math.Min(max, s.OffX));
            }
            else s.OffX = 0;
            if (dh >= slot.Height)
            {
                float max = (dh - slot.Height) / 2f;
                s.OffY = Math.Max(-max, Math.Min(max, s.OffY));
            }
            else s.OffY = 0;
        }

        public void ResetView(int slotIndex)
        {
            if (slotIndex < 0)
            {
                foreach (SlotState s in Slots) { s.Zoom = 1f; s.OffX = 0; s.OffY = 0; }
            }
            else if (slotIndex < Slots.Count)
            {
                SlotState s = Slots[slotIndex];
                s.Zoom = 1f; s.OffX = 0; s.OffY = 0;
            }
            Invalidate();
        }

        public void ClearSlot(int i)
        {
            if (i < 0 || i >= Slots.Count) return;
            if (Slots[i].Image != null) Slots[i].Image.Dispose();
            Slots[i] = new SlotState();
            Invalidate();
        }

        public void ClearAll()
        {
            foreach (SlotState s in Slots)
                if (s.Image != null) s.Image.Dispose();
            Slots.Clear();
            EnsureSlots(Count);
            Invalidate();
        }

        public void SetImage(int i, string path)
        {
            if (i < 0 || i >= Slots.Count) return;
            Image loaded = null;
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                using (Image img = Image.FromStream(fs, false, false))
                {
                    loaded = new Bitmap(img);
                }
            }
            catch
            {
                throw new InvalidDataException("画像を読み込めませんでした: " + Path.GetFileName(path));
            }
            if (Slots[i].Image != null) Slots[i].Image.Dispose();
            SlotState s = new SlotState();
            s.Image = loaded;
            s.Path = path;
            Slots[i] = s;
            Invalidate();
        }

        public int LoadedCount()
        {
            int n = 0;
            foreach (SlotState s in Slots) if (s.Image != null) n++;
            return n;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            float scale;
            RectangleF cam = GetCanvasRect(out scale);
            List<RectangleF> rects = ComputeSlotRects();

            // キャンバスの影・枠
            using (SolidBrush shadow = new SolidBrush(Color.FromArgb(160, 160, 160)))
                g.FillRectangle(shadow, cam.X + 3, cam.Y + 3, cam.Width, cam.Height);
            using (SolidBrush borderBrush = new SolidBrush(BorderColor))
                g.FillRectangle(borderBrush, cam);

            for (int i = 0; i < rects.Count; i++)
            {
                RectangleF r = rects[i];
                RectangleF dest = new RectangleF(cam.X + r.X * scale, cam.Y + r.Y * scale,
                                                 r.Width * scale, r.Height * scale);
                SlotState s = (i < Slots.Count) ? Slots[i] : new SlotState();
                DrawSlot(g, dest, r, s, i, true);
            }

            // 選択中スロットの強調枠
            if (ActiveSlotIndex >= 0 && ActiveSlotIndex < rects.Count)
            {
                RectangleF r = rects[ActiveSlotIndex];
                RectangleF dest = new RectangleF(cam.X + r.X * scale, cam.Y + r.Y * scale,
                                                 r.Width * scale, r.Height * scale);
                using (Pen active = new Pen(Color.FromArgb(0, 120, 215), 3f))
                {
                    RectangleF inner = dest;
                    inner.Inflate(1.5f, 1.5f);
                    g.DrawRectangle(active, inner.X, inner.Y, inner.Width, inner.Height);
                }
            }

            // スロット入れ替えドラッグ中の表示
            if (swapDragging && swapSource >= 0 && swapSource < rects.Count)
            {
                RectangleF sr = rects[swapSource];
                RectangleF sDest = new RectangleF(cam.X + sr.X * scale, cam.Y + sr.Y * scale,
                                                  sr.Width * scale, sr.Height * scale);
                using (SolidBrush srcBrush = new SolidBrush(Color.FromArgb(60, 0, 120, 215)))
                    g.FillRectangle(srcBrush, sDest);

                if (swapHover >= 0 && swapHover != swapSource && swapHover < rects.Count)
                {
                    RectangleF hr = rects[swapHover];
                    RectangleF hDest = new RectangleF(cam.X + hr.X * scale, cam.Y + hr.Y * scale,
                                                      hr.Width * scale, hr.Height * scale);
                    using (Pen hover = new Pen(Color.FromArgb(0, 120, 215), 4f))
                    {
                        hover.DashStyle = DashStyle.Dash;
                        RectangleF inner = hDest;
                        inner.Inflate(-3, -3);
                        g.DrawRectangle(hover, inner.X, inner.Y, inner.Width, inner.Height);
                    }
                }
            }

            using (Pen frame = new Pen(Color.FromArgb(120, 120, 120)))
                g.DrawRectangle(frame, cam.X - 0.5f, cam.Y - 0.5f, cam.Width, cam.Height);
        }

        // プレビューと保存の両方で使うスロット描画。previewRect が preview 表示領域、slotRect はキャンバス座標のスロット領域。
        static void DrawSlot(Graphics g, RectangleF previewRect, RectangleF slotRect, SlotState s, int index, bool showPlaceholder)
        {
            GraphicsState state = g.Save();
            g.SetClip(previewRect);

            if (s.Image == null)
            {
                using (SolidBrush bg = new SolidBrush(Color.FromArgb(245, 245, 245)))
                    g.FillRectangle(bg, previewRect);
                using (Pen dash = new Pen(Color.FromArgb(175, 175, 175)))
                {
                    dash.DashStyle = DashStyle.Dash;
                    RectangleF inner = previewRect;
                    if (inner.Width > 8 && inner.Height > 8)
                    {
                        inner.Inflate(-4, -4);
                        g.DrawRectangle(dash, inner.X, inner.Y, inner.Width, inner.Height);
                    }
                }
                if (showPlaceholder && previewRect.Width > 120 && previewRect.Height > 60)
                {
                    string msg = "ここをクリックして画像を選択\nまたはドラッグ＆ドロップ";
                    using (Font f = new Font("Meiryo UI", Math.Max(8f, Math.Min(10f, previewRect.Height / 9f))))
                    using (SolidBrush tb = new SolidBrush(Color.FromArgb(140, 140, 140)))
                    using (StringFormat sf = new StringFormat())
                    {
                        sf.Alignment = StringAlignment.Center;
                        sf.LineAlignment = StringAlignment.Center;
                        g.DrawString(msg, f, tb, previewRect, sf);
                    }
                }
                g.Restore(state);
                return;
            }

            using (SolidBrush bg = new SolidBrush(Color.White))
                g.FillRectangle(bg, previewRect);

            ClampOffset(s, slotRect);
            float s0 = Math.Max(slotRect.Width / s.Image.Width, slotRect.Height / s.Image.Height);
            float sc = s0 * s.Zoom;
            float dw = s.Image.Width * sc, dh = s.Image.Height * sc;
            float dx = slotRect.X + (slotRect.Width - dw) / 2f + s.OffX;
            float dy = slotRect.Y + (slotRect.Height - dh) / 2f + s.OffY;

            // キャンバス座標→プレビュー座標の変換は呼び出し側の clip と描画先の対応で行う
            float kx = previewRect.Width / slotRect.Width;
            float px = previewRect.X + (dx - slotRect.X) * kx;
            float py = previewRect.Y + (dy - slotRect.Y) * kx;
            float pw = dw * kx, ph = dh * kx;
            g.DrawImage(s.Image, px, py, pw, ph);

            g.Restore(state);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            List<RectangleF> rects;
            float scale;
            RectangleF cam;
            int hit = HitTest(e.Location, out rects, out scale, out cam);
            if (hit < 0 || hit >= Slots.Count) return;
            ActiveSlotIndex = hit;
            SlotState s = Slots[hit];
            if (s.Image == null) return;

            float factor = (e.Delta > 0) ? 1.15f : (1f / 1.15f);
            float newZoom = s.Zoom * factor;
            newZoom = Math.Max(0.05f, Math.Min(30f, newZoom));

            // カーソル位置を中心に拡大縮小する
            RectangleF slot = rects[hit];
            float cx = (e.X - cam.X) / scale - slot.X - slot.Width / 2f;
            float cy = (e.Y - cam.Y) / scale - slot.Y - slot.Height / 2f;
            float ratio = newZoom / s.Zoom;
            s.OffX = cx - (cx - s.OffX) * ratio;
            s.OffY = cy - (cy - s.OffY) * ratio;
            s.Zoom = newZoom;
            ClampOffset(s, slot);
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left && e.Button != MouseButtons.Right) return;
            List<RectangleF> rects;
            float scale;
            RectangleF cam;
            int hit = HitTest(e.Location, out rects, out scale, out cam);
            if (hit >= 0) ActiveSlotIndex = hit;
            if (hit >= 0 && hit < Slots.Count)
            {
                if (e.Button == MouseButtons.Left)
                {
                    // 境界線のドラッグ = 結合の移動（掴んだ線が結合になり、ドロップ先の結合が分割）
                    int cellA, cellB;
                    bool vertical;
                    if (HasStretchedCell() && HitTestDivider(e.Location, out cellA, out cellB, out vertical))
                    {
                        spanMove = true;
                        dragCellA = cellA;
                        dragCellB = cellB;
                        dragStartAssign = CurrentAssignment();
                        dragMoved = false;
                        dragPreviewPattern = -1;
                        spanStartPoint = e.Location;
                        swapSource = -1;
                        return;
                    }
                    // 通常の左ドラッグ = スロット入れ替え
                    swapSource = hit;
                    swapStart = e.Location;
                    swapDragging = false;
                    swapHover = -1;
                }
                else if (e.Button == MouseButtons.Right)
                {
                    // 右ドラッグ = 画像の移動（パン）
                    SuppressContextMenu = false;
                    if (Slots[hit].Image != null)
                    {
                        panSlot = hit;
                        panStart = e.Location;
                        panOffX = Slots[hit].OffX;
                        panOffY = Slots[hit].OffY;
                        panMoved = false;
                    }
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            List<RectangleF> rects;
            float scale;
            RectangleF cam;

            // 右ドラッグ = 画像パン
            if (panSlot >= 0 && panSlot < Slots.Count && (e.Button & MouseButtons.Right) == MouseButtons.Right)
            {
                SlotState s = Slots[panSlot];
                int dx = e.X - panStart.X;
                int dy = e.Y - panStart.Y;
                if (Math.Abs(dx) > 3 || Math.Abs(dy) > 3) panMoved = true;
                if (panMoved)
                {
                    SuppressContextMenu = true;
                    HitTest(e.Location, out rects, out scale, out cam);
                    s.OffX = panOffX + dx / scale;
                    s.OffY = panOffY + dy / scale;
                    ClampOffset(s, ComputeSlotRects()[panSlot]);
                    Invalidate();
                }
                return;
            }

            // 境界線ドラッグ = 結合の移動（ライブプレビュー）
            if (spanMove && (e.Button & MouseButtons.Left) == MouseButtons.Left)
            {
                if (!dragMoved)
                {
                    Point sp = spanStartPoint;
                    if (Math.Abs(e.X - sp.X) > 4 || Math.Abs(e.Y - sp.Y) > 4) dragMoved = true;
                }
                if (dragMoved)
                {
                    int cols, rows; float cellW, cellH; int[] assign;
                    if (ComputeAssignment(out cols, out rows, out cellW, out cellH, out assign))
                    {
                        int gapA, gapB;
                        // ドラッグ開始時点の割り当てを基準に隙間を探す
                        int[] baseAssign = dragStartAssign;
                        if (baseAssign != null && FindGapNear(e.Location, baseAssign, cols, rows, cellW, cellH, out gapA, out gapB))
                        {
                            int[] moved = TryMoveDivider(baseAssign, dragCellA, dragCellB, gapA, gapB);
                            if (moved != null)
                            {
                                int idx = FindTilingIndex(moved);
                                if (idx >= 0 && idx != dragPreviewPattern)
                                {
                                    dragPreviewPattern = idx;
                                    Invalidate();
                                }
                            }
                        }
                    }
                }
                Cursor = Cursors.SizeAll;
                return;
            }

            // 左ドラッグ = スロット入れ替え
            if (swapSource >= 0 && (e.Button & MouseButtons.Left) == MouseButtons.Left)
            {
                int dx = e.X - swapStart.X;
                int dy = e.Y - swapStart.Y;
                if (!swapDragging && (Math.Abs(dx) > 4 || Math.Abs(dy) > 4)) swapDragging = true;
                if (swapDragging)
                {
                    int hit2 = HitTest(e.Location, out rects, out scale, out cam);
                    if (hit2 != swapHover) { swapHover = hit2; Invalidate(); }
                    Cursor = Cursors.SizeAll;
                    return;
                }
            }

            // カーソル形状
            int hit = HitTest(e.Location, out rects, out scale, out cam);
            int divCellA, divCellB;
            bool divVertical;
            bool onDivider = HitTestDivider(e.Location, out divCellA, out divCellB, out divVertical);
            if (onDivider)
                Cursor = divVertical ? Cursors.SizeWE : Cursors.SizeNS;
            else if (hit >= 0 && hit < Slots.Count && Slots[hit].Image != null)
                Cursor = Cursors.Hand;
            else
                Cursor = Cursors.Default;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            // 右ボタン: パン終了。ドラッグしていた場合はコンテキストメニューを抑制
            if (e.Button == MouseButtons.Right)
            {
                if (panMoved) SuppressContextMenu = true;
                panSlot = -1;
                panMoved = false;
                return;
            }

            // 左ボタン: 結合移動終了 / スロット入れ替え / クリック選択
            if (spanMove)
            {
                bool applied = dragPreviewPattern >= 0;
                if (applied)
                {
                    PatternIndex = dragPreviewPattern;
                    if (PatternChanged != null) PatternChanged(this, EventArgs.Empty);
                }
                spanMove = false;
                dragMoved = false;
                dragPreviewPattern = -1;
                dragStartAssign = null;
                Invalidate();
                return;
            }

            int source = swapSource;
            bool wasSwap = swapDragging;
            swapSource = -1;
            swapDragging = false;
            swapHover = -1;
            Invalidate();

            List<RectangleF> rects;
            float scale;
            RectangleF cam;
            int hit = HitTest(e.Location, out rects, out scale, out cam);

            if (wasSwap)
            {
                if (hit >= 0 && source >= 0 && hit != source)
                {
                    SwapSlots(source, hit);
                    SwapFrom = source;
                    SwapTo = hit;
                    if (SlotsSwapped != null) SlotsSwapped(this, EventArgs.Empty);
                }
                return;
            }

            if (source >= 0 && hit == source)
            {
                ActiveSlotIndex = source;
                // 画像が読み込み済みなら左クリックは選択のみ（ダイアログは出さない）
                if (Slots[source].Image == null)
                {
                    if (SlotClicked != null) SlotClicked(this, EventArgs.Empty);
                }
                Invalidate();
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button != MouseButtons.Left) return;
            List<RectangleF> rects;
            float scale;
            RectangleF cam;
            int hit = HitTest(e.Location, out rects, out scale, out cam);
            if (hit >= 0 && hit < Slots.Count && Slots[hit].Image != null)
            {
                ActiveSlotIndex = hit;
                if (SlotClicked != null) SlotClicked(this, EventArgs.Empty);
                Invalidate();
            }
        }

        protected override void OnDragEnter(DragEventArgs e)
        {
            base.OnDragEnter(e);
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
            else e.Effect = DragDropEffects.None;
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            string[] paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (paths == null || paths.Length == 0) return;

            Point p = PointToClient(new Point(e.X, e.Y));
            List<RectangleF> rects;
            float scale;
            RectangleF cam;
            int hit = HitTest(p, out rects, out scale, out cam);
            if (hit < 0) return;

            ActiveSlotIndex = hit;
            int slot = hit;
            foreach (string path in paths)
            {
                if (slot >= Slots.Count) break;
                if (!IsImageFile(path)) continue;
                try { SetImage(slot, path); }
                catch { }
                slot++;
            }
        }

        public static bool IsImageFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            string[] exts = new string[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };
            return Array.IndexOf(exts, ext) >= 0;
        }
    }

    class MainForm : Form
    {
        const string AppName = "AA Image Connector";
        const string HintText = "各スロットをクリック、または画像をドラッグ＆ドロップして読み込んでください。";

        static readonly int[][] SizePresets = new int[][]
        {
            new int[] { 1920, 1080 },
            new int[] { 1280, 720 },
            new int[] { 1080, 1080 },
            new int[] { 1080, 1350 },
            new int[] { 1080, 1920 },
            new int[] { 800, 800 },
        };
        static readonly string[] SizePresetLabels = new string[]
        {
            "1920 × 1080（フルHD）",
            "1280 × 720（HD）",
            "1080 × 1080（正方形）",
            "1080 × 1350（縦長）",
            "1080 × 1920（縦フルHD）",
            "800 × 800",
        };
        const string CustomLabel = "任意のサイズ";

        NumericUpDown numOutW, numOutH;
        ComboBox cboPreset;
        NumericUpDown numCount;
        RadioButton rbVertical, rbHorizontal, rbGrid;
        Label lblGridCols;
        NumericUpDown numGridCols;
        Label lblPattern;
        ComboBox cboPattern;
        Button btnPatternPrev, btnPatternNext;
        NumericUpDown numBorder;
        Button btnBorderColor;
        CanvasPanel canvas;
        Label lblStatus;
        Button btnResetView, btnClearAll, btnSave;
        Button btnMovePrev, btnMoveNext;
        ContextMenuStrip slotMenu;
        bool updating;

        public MainForm()
        {
            Text = AppName;
            Font = new Font("Meiryo UI", 9F);
            ClientSize = new Size(920, 664);
            MinimumSize = new Size(760, 540);
            StartPosition = FormStartPosition.CenterScreen;
            AllowDrop = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // ===== 設定グループ =====
            GroupBox grp = new GroupBox();
            grp.Text = "出力設定";
            grp.Bounds = new Rectangle(12, 12, 896, 124);
            grp.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(grp);

            // 1行目: 出力サイズ
            Label lblSize = new Label();
            lblSize.Text = "出力サイズ:";
            lblSize.Location = new Point(12, 26);
            lblSize.AutoSize = true;
            grp.Controls.Add(lblSize);

            numOutW = new NumericUpDown();
            numOutW.Bounds = new Rectangle(96, 22, 78, 24);
            numOutW.Minimum = 1; numOutW.Maximum = 10000; numOutW.Value = 1920;
            grp.Controls.Add(numOutW);

            Label lblX = new Label();
            lblX.Text = "×";
            lblX.Location = new Point(180, 26);
            lblX.AutoSize = true;
            grp.Controls.Add(lblX);

            numOutH = new NumericUpDown();
            numOutH.Bounds = new Rectangle(200, 22, 78, 24);
            numOutH.Minimum = 1; numOutH.Maximum = 10000; numOutH.Value = 1080;
            grp.Controls.Add(numOutH);

            Label lblPx1 = new Label();
            lblPx1.Text = "px";
            lblPx1.Location = new Point(284, 26);
            lblPx1.AutoSize = true;
            grp.Controls.Add(lblPx1);

            cboPreset = new ComboBox();
            cboPreset.DropDownStyle = ComboBoxStyle.DropDownList;
            cboPreset.Bounds = new Rectangle(320, 22, 220, 24);
            for (int i = 0; i < SizePresetLabels.Length; i++) cboPreset.Items.Add(SizePresetLabels[i]);
            cboPreset.Items.Add(CustomLabel);
            cboPreset.SelectedIndex = 0;
            grp.Controls.Add(cboPreset);

            // 2行目: 枚数・並べ方・境界線
            Label lblCount = new Label();
            lblCount.Text = "枚数:";
            lblCount.Location = new Point(12, 60);
            lblCount.AutoSize = true;
            grp.Controls.Add(lblCount);

            numCount = new NumericUpDown();
            numCount.Bounds = new Rectangle(50, 56, 52, 24);
            numCount.Minimum = 1; numCount.Maximum = 20; numCount.Value = 2;
            grp.Controls.Add(numCount);

            Label lblCountUnit = new Label();
            lblCountUnit.Text = "枚";
            lblCountUnit.Location = new Point(108, 60);
            lblCountUnit.AutoSize = true;
            grp.Controls.Add(lblCountUnit);

            Label lblLayout = new Label();
            lblLayout.Text = "並べ方:";
            lblLayout.Location = new Point(150, 60);
            lblLayout.AutoSize = true;
            grp.Controls.Add(lblLayout);

            rbVertical = new RadioButton();
            rbVertical.Text = "縦割り";
            rbVertical.Location = new Point(200, 58);
            rbVertical.AutoSize = true;
            rbVertical.Checked = true;
            grp.Controls.Add(rbVertical);

            rbHorizontal = new RadioButton();
            rbHorizontal.Text = "横割り";
            rbHorizontal.Location = new Point(268, 58);
            rbHorizontal.AutoSize = true;
            grp.Controls.Add(rbHorizontal);

            rbGrid = new RadioButton();
            rbGrid.Text = "碁盤の目";
            rbGrid.Location = new Point(336, 58);
            rbGrid.AutoSize = true;
            grp.Controls.Add(rbGrid);

            lblGridCols = new Label();
            lblGridCols.Text = "列数:";
            lblGridCols.Location = new Point(420, 60);
            lblGridCols.AutoSize = true;
            lblGridCols.Visible = false;
            grp.Controls.Add(lblGridCols);

            numGridCols = new NumericUpDown();
            numGridCols.Bounds = new Rectangle(462, 56, 48, 24);
            numGridCols.Minimum = 1; numGridCols.Maximum = 10; numGridCols.Value = 2;
            numGridCols.Visible = false;
            grp.Controls.Add(numGridCols);

            Label lblBorder = new Label();
            lblBorder.Text = "境界線:";
            lblBorder.Location = new Point(540, 60);
            lblBorder.AutoSize = true;
            grp.Controls.Add(lblBorder);

            numBorder = new NumericUpDown();
            numBorder.Bounds = new Rectangle(592, 56, 56, 24);
            numBorder.Minimum = 0; numBorder.Maximum = 100; numBorder.Value = 4;
            grp.Controls.Add(numBorder);

            Label lblBorderUnit = new Label();
            lblBorderUnit.Text = "px";
            lblBorderUnit.Location = new Point(654, 60);
            lblBorderUnit.AutoSize = true;
            grp.Controls.Add(lblBorderUnit);

            btnBorderColor = new Button();
            btnBorderColor.Text = "";
            btnBorderColor.Bounds = new Rectangle(684, 55, 56, 26);
            btnBorderColor.BackColor = Color.White;
            btnBorderColor.FlatStyle = FlatStyle.Flat;
            btnBorderColor.FlatAppearance.BorderColor = Color.FromArgb(120, 120, 120);
            grp.Controls.Add(btnBorderColor);

            // 3行目: 結合パターン（碁盤の目でマスが余る時のみ）
            lblPattern = new Label();
            lblPattern.Text = "結合パターン:";
            lblPattern.Location = new Point(12, 91);
            lblPattern.AutoSize = true;
            lblPattern.Visible = false;
            grp.Controls.Add(lblPattern);

            cboPattern = new ComboBox();
            cboPattern.DropDownStyle = ComboBoxStyle.DropDownList;
            cboPattern.Bounds = new Rectangle(120, 87, 420, 24);
            cboPattern.Visible = false;
            grp.Controls.Add(cboPattern);

            btnPatternPrev = new Button();
            btnPatternPrev.Text = "◀";
            btnPatternPrev.Bounds = new Rectangle(546, 86, 30, 26);
            btnPatternPrev.Visible = false;
            grp.Controls.Add(btnPatternPrev);

            btnPatternNext = new Button();
            btnPatternNext.Text = "▶";
            btnPatternNext.Bounds = new Rectangle(580, 86, 30, 26);
            btnPatternNext.Visible = false;
            grp.Controls.Add(btnPatternNext);

            // ===== キャンバス =====
            canvas = new CanvasPanel();
            canvas.Bounds = new Rectangle(12, 140, 896, 404);
            canvas.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            canvas.BorderThickness = 4;
            canvas.BorderColor = Color.White;
            Controls.Add(canvas);

            // 右クリックメニュー
            slotMenu = new ContextMenuStrip();
            ToolStripMenuItem miResetSlot = new ToolStripMenuItem("このスロットの表示をリセット");
            miResetSlot.Click += delegate { canvas.ResetView(canvas.ActiveSlotIndex); };
            ToolStripMenuItem miClearSlot = new ToolStripMenuItem("このスロットの画像を削除");
            miClearSlot.Click += delegate { canvas.ClearSlot(canvas.ActiveSlotIndex); UpdateStatus(); };
            ToolStripMenuItem miMovePrev = new ToolStripMenuItem("前のスロットと入れ替え");
            miMovePrev.Click += delegate { MoveActiveSlot(-1); };
            ToolStripMenuItem miMoveNext = new ToolStripMenuItem("次のスロットと入れ替え");
            miMoveNext.Click += delegate { MoveActiveSlot(1); };
            ToolStripMenuItem miResetAll = new ToolStripMenuItem("すべての表示をリセット");
            miResetAll.Click += delegate { canvas.ResetView(-1); };
            slotMenu.Items.Add(miResetSlot);
            slotMenu.Items.Add(miClearSlot);
            slotMenu.Items.Add(new ToolStripSeparator());
            slotMenu.Items.Add(miMovePrev);
            slotMenu.Items.Add(miMoveNext);
            slotMenu.Items.Add(new ToolStripSeparator());
            slotMenu.Items.Add(miResetAll);
            canvas.MouseUp += canvas_MouseUp;

            // ===== 下部バー =====
            btnResetView = new Button();
            btnResetView.Text = "表示をリセット";
            btnResetView.Bounds = new Rectangle(12, 552, 110, 28);
            btnResetView.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            Controls.Add(btnResetView);

            btnClearAll = new Button();
            btnClearAll.Text = "すべてクリア";
            btnClearAll.Bounds = new Rectangle(130, 552, 100, 28);
            btnClearAll.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            Controls.Add(btnClearAll);

            btnMovePrev = new Button();
            btnMovePrev.Text = "◀ 前へ";
            btnMovePrev.Bounds = new Rectangle(238, 552, 76, 28);
            btnMovePrev.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            Controls.Add(btnMovePrev);

            btnMoveNext = new Button();
            btnMoveNext.Text = "後へ ▶";
            btnMoveNext.Bounds = new Rectangle(318, 552, 76, 28);
            btnMoveNext.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            Controls.Add(btnMoveNext);

            lblStatus = new Label();
            lblStatus.Text = HintText;
            lblStatus.Location = new Point(12, 592);
            lblStatus.AutoSize = true;
            lblStatus.MaximumSize = new Size(660, 0);
            lblStatus.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            Controls.Add(lblStatus);

            btnSave = new Button();
            btnSave.Text = "保存...";
            btnSave.Bounds = new Rectangle(748, 552, 160, 36);
            btnSave.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnSave.Font = new Font("Meiryo UI", 10F, FontStyle.Bold);
            Controls.Add(btnSave);

            // ===== イベント =====
            numOutW.ValueChanged += delegate { ApplySizeFromNumbers(); };
            numOutH.ValueChanged += delegate { ApplySizeFromNumbers(); };
            cboPreset.SelectedIndexChanged += delegate { ApplyPreset(); };
            numCount.ValueChanged += delegate { ApplyStructure(); };
            rbVertical.CheckedChanged += delegate { if (rbVertical.Checked) ApplyStructure(); };
            rbHorizontal.CheckedChanged += delegate { if (rbHorizontal.Checked) ApplyStructure(); };
            rbGrid.CheckedChanged += delegate { if (rbGrid.Checked) ApplyStructure(); };
            numGridCols.ValueChanged += delegate { ApplyStructure(); };
            numBorder.ValueChanged += delegate { canvas.BorderThickness = (int)numBorder.Value; canvas.Invalidate(); };
            btnBorderColor.Click += delegate { PickBorderColor(); };
            canvas.SlotClicked += delegate { PickIntoActiveSlot(); };
            canvas.SlotsSwapped += delegate
            {
                lblStatus.Text = string.Format("スロット {0} と {1} を入れ替えました。", canvas.SwapFrom + 1, canvas.SwapTo + 1);
            };
            canvas.PatternChanged += delegate
            {
                updating = true;
                cboPattern.SelectedIndex = canvas.PatternIndex;
                updating = false;
                lblStatus.Text = string.Format("結合パターン {0} / {1}", canvas.PatternIndex + 1, canvas.TilingCount);
            };
            cboPattern.SelectedIndexChanged += delegate
            {
                if (updating) return;
                canvas.SetPattern(cboPattern.SelectedIndex);
                lblStatus.Text = string.Format("結合パターン {0} / {1}", cboPattern.SelectedIndex + 1, canvas.TilingCount);
            };
            btnPatternPrev.Click += delegate
            {
                int i = Math.Max(0, canvas.PatternIndex - 1);
                canvas.SetPattern(i);
                RefreshPatternUI();
                lblStatus.Text = string.Format("結合パターン {0} / {1}", canvas.PatternIndex + 1, canvas.TilingCount);
            };
            btnPatternNext.Click += delegate
            {
                int i = Math.Min(canvas.TilingCount - 1, canvas.PatternIndex + 1);
                canvas.SetPattern(i);
                RefreshPatternUI();
                lblStatus.Text = string.Format("結合パターン {0} / {1}", canvas.PatternIndex + 1, canvas.TilingCount);
            };
            btnResetView.Click += delegate { canvas.ResetView(-1); };
            btnClearAll.Click += delegate { canvas.ClearAll(); UpdateStatus(); };
            btnMovePrev.Click += delegate { MoveActiveSlot(-1); };
            btnMoveNext.Click += delegate { MoveActiveSlot(1); };
            btnSave.Click += delegate { SaveImage(); };
            DragEnter += FormDragEnter;
            DragDrop += FormDragDrop;

            canvas.EnsureSlots(2);
            canvas.RebuildTilings();
            RefreshPatternUI();
            UpdateStatus();
        }

        void FormDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
        }

        void FormDragDrop(object sender, DragEventArgs e)
        {
            string[] paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (paths == null) return;
            int slot = 0;
            foreach (string p in paths)
            {
                if (slot >= canvas.Slots.Count) break;
                if (!CanvasPanel.IsImageFile(p)) continue;
                try { canvas.SetImage(slot, p); slot++; }
                catch (Exception ex) { MessageBox.Show(ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
            UpdateStatus();
        }

        void canvas_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right && canvas.ActiveSlotIndex >= 0)
            {
                if (canvas.SuppressContextMenu) { canvas.SuppressContextMenu = false; return; }
                slotMenu.Show(canvas, e.Location);
            }
        }

        void ApplySizeFromNumbers()
        {
            if (updating) return;
            canvas.OutputWidth = (int)numOutW.Value;
            canvas.OutputHeight = (int)numOutH.Value;
            updating = true;
            int idx = Array.FindIndex(SizePresets, p => p[0] == canvas.OutputWidth && p[1] == canvas.OutputHeight);
            cboPreset.SelectedIndex = (idx >= 0) ? idx : cboPreset.Items.Count - 1;
            updating = false;
            canvas.Invalidate();
        }

        void ApplyPreset()
        {
            if (updating) return;
            int idx = cboPreset.SelectedIndex;
            if (idx < 0 || idx >= SizePresets.Length) return;
            updating = true;
            numOutW.Value = SizePresets[idx][0];
            numOutH.Value = SizePresets[idx][1];
            updating = false;
            canvas.OutputWidth = (int)numOutW.Value;
            canvas.OutputHeight = (int)numOutH.Value;
            canvas.Invalidate();
        }

        void ApplyStructure()
        {
            int n = (int)numCount.Value;
            canvas.Count = n;
            canvas.GridLayout = rbGrid.Checked;
            canvas.HorizontalLayout = rbHorizontal.Checked;
            canvas.GridCols = (int)numGridCols.Value;
            lblGridCols.Visible = rbGrid.Checked;
            numGridCols.Visible = rbGrid.Checked;
            if (rbGrid.Checked && numGridCols.Value > n) numGridCols.Value = n;
            canvas.RebuildTilings();
            canvas.EnsureSlots(n);
            canvas.Invalidate();
            RefreshPatternUI();
            UpdateStatus();
        }

        // 結合パターンのコンボボックスなどを現在の状態に合わせる
        void RefreshPatternUI()
        {
            bool show = canvas.HasStretchedCell();
            lblPattern.Visible = show;
            cboPattern.Visible = show;
            btnPatternPrev.Visible = show;
            btnPatternNext.Visible = show;
            if (!show) return;

            updating = true;
            cboPattern.Items.Clear();
            for (int i = 0; i < canvas.TilingCount; i++)
                cboPattern.Items.Add(string.Format("{0}: {1}", i + 1, canvas.TilingText(i)));
            cboPattern.SelectedIndex = canvas.PatternIndex;
            updating = false;
        }

        void PickBorderColor()
        {
            using (ColorDialog dlg = new ColorDialog())
            {
                dlg.Color = canvas.BorderColor;
                dlg.FullOpen = true;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    canvas.BorderColor = dlg.Color;
                    btnBorderColor.BackColor = dlg.Color;
                    canvas.Invalidate();
                }
            }
        }

        void PickIntoActiveSlot()
        {
            if (canvas.ActiveSlotIndex < 0) return;
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "スロット " + (canvas.ActiveSlotIndex + 1) + " に読み込む画像を選択";
                dlg.Filter = "画像ファイル|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|すべてのファイル|*.*";
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    try { canvas.SetImage(canvas.ActiveSlotIndex, dlg.FileName); }
                    catch (Exception ex) { MessageBox.Show(ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                }
            }
            UpdateStatus();
        }

        void MoveActiveSlot(int delta)
        {
            int i = canvas.ActiveSlotIndex;
            if (i < 0)
            {
                lblStatus.Text = "入れ替えるスロットをクリックで選択してください。";
                return;
            }
            int j = i + delta;
            if (j < 0 || j >= canvas.Slots.Count) return;
            canvas.SwapSlots(i, j);
            lblStatus.Text = string.Format("スロット {0} と {1} を入れ替えました。", i + 1, j + 1);
        }

        void UpdateStatus()
        {
            int loaded = canvas.LoadedCount();
            string spanHint = canvas.HasStretchedCell() ? " ／ 分割線をドラッグすると結合位置を切り替えられます" : "";
            if (loaded == 0) lblStatus.Text = HintText + spanHint;
            else lblStatus.Text = string.Format("{0} / {1} 枚読み込み済み{2}", loaded, canvas.Count, spanHint);
        }

        void SaveImage()
        {
            int loaded = canvas.LoadedCount();
            if (loaded == 0)
            {
                MessageBox.Show("画像を1枚以上読み込んでください。", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (loaded < canvas.Count)
            {
                DialogResult r = MessageBox.Show(
                    string.Format("未設定のスロットが {0} 個あります。このまま保存しますか？", canvas.Count - loaded),
                    AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) return;
            }

            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Title = "結合した画像を保存";
                dlg.Filter = "PNG 画像|*.png|JPEG 画像|*.jpg";
                dlg.FileName = "combined.png";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                Cursor = Cursors.WaitCursor;
                try
                {
                    RenderOutput(dlg.FileName);
                    lblStatus.Text = "保存しました: " + dlg.FileName;
                }
                catch (Exception ex)
                {
                    MessageBox.Show("保存に失敗しました:\n" + ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    Cursor = Cursors.Default;
                }
            }
        }

        void RenderOutput(string path)
        {
            int W = canvas.OutputWidth, H = canvas.OutputHeight;
            List<RectangleF> rects = canvas.ComputeSlotRects();
            bool isJpeg = Path.GetExtension(path).ToLowerInvariant() == ".jpg";

            using (Bitmap bmp = new Bitmap(W, H))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.Clear(canvas.BorderColor);

                for (int i = 0; i < rects.Count; i++)
                {
                    RectangleF slot = rects[i];
                    RectangleF dest = new RectangleF(slot.X, slot.Y, slot.Width, slot.Height);
                    SlotState s = canvas.Slots[i];

                    GraphicsState state = g.Save();
                    g.SetClip(dest);
                    if (s.Image == null)
                    {
                        using (SolidBrush bg = new SolidBrush(Color.FromArgb(245, 245, 245)))
                            g.FillRectangle(bg, dest);
                    }
                    else
                    {
                        using (SolidBrush bg = new SolidBrush(Color.White))
                            g.FillRectangle(bg, dest);
                        float s0 = Math.Max(slot.Width / s.Image.Width, slot.Height / s.Image.Height);
                        float sc = s0 * s.Zoom;
                        float dw = s.Image.Width * sc, dh = s.Image.Height * sc;
                        // オフセットをキャンバス座標のままクランプ（プレビューと同一ルール）
                        if (dw >= slot.Width) s.OffX = Math.Max(-(dw - slot.Width) / 2f, Math.Min((dw - slot.Width) / 2f, s.OffX)); else s.OffX = 0;
                        if (dh >= slot.Height) s.OffY = Math.Max(-(dh - slot.Height) / 2f, Math.Min((dh - slot.Height) / 2f, s.OffY)); else s.OffY = 0;
                        float dx = slot.X + (slot.Width - dw) / 2f + s.OffX;
                        float dy = slot.Y + (slot.Height - dh) / 2f + s.OffY;
                        g.DrawImage(s.Image, dx, dy, dw, dh);
                    }
                    g.Restore(state);
                }

                if (isJpeg)
                {
                    ImageCodecInfo enc = GetEncoder(ImageFormat.Jpeg);
                    if (enc != null)
                    {
                        using (EncoderParameters ep = new EncoderParameters(1))
                        {
                            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
                            bmp.Save(path, enc, ep);
                            return;
                        }
                    }
                    bmp.Save(path, ImageFormat.Jpeg);
                }
                else
                {
                    bmp.Save(path, ImageFormat.Png);
                }
            }
        }

        static ImageCodecInfo GetEncoder(ImageFormat format)
        {
            foreach (ImageCodecInfo codec in ImageCodecInfo.GetImageEncoders())
                if (codec.FormatID == format.Guid) return codec;
            return null;
        }
    }
}

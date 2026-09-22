using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LiraSlabZones.Core;
using Newtonsoft.Json;

namespace LiraSlabZones.Revit2023.UI
{
    public enum ZoneEditMode { Select, Move, Resize, Create, Split, Merge, Delete, PerpendicularToEdge, CreateGap }

    /// <summary>
    /// Векторный превью-холст: зум без размытия, зоны и контур по сетке КЭ.
    /// </summary>
    public sealed class PreviewViewport : FrameworkElement
    {
        private const int MaxUndoActions = 50;
        private enum ResizeEdge { None, Left, Right, Bottom, Top }
        private AnalysisResult? _result;
        private readonly List<string> _undo = new();
        private string? _pendingUndo;
        private AnalysisSettings _settings = new AnalysisSettings();
        private bool _showMesh = true;
        private bool _showIso;
        private bool _showAxes;

        private double _zoom = 1.0;
        private double _panX;
        private double _panY;
        private bool _panning;
        private Point _panLast;

        private double _modelMinX, _modelMinY, _modelMaxX, _modelMaxY;
        private double _fitScale = 1;

        private class CachedShape
        {
            public StreamGeometry Geometry = null!;
            public double MinX;
            public double MaxX;
            public double MinY;
            public double MaxY;

            public bool Intersects(double minX, double maxX, double minY, double maxY) =>
                !(MaxX < minX || MinX > maxX || MaxY < minY || MinY > maxY);
        }

        private sealed class CachedZoneShape : CachedShape
        {
            public AdditionalZone Zone = null!;
            public Point3[] Contour = Array.Empty<Point3>();
        }

        private readonly List<CachedShape> _plateShapes = new();
        private readonly List<CachedZoneShape> _drawZones = new();
        private static readonly Dictionary<int, Brush> DiameterFillCache = new();
        private static readonly Dictionary<int, Brush> DiameterStrokeCache = new();
        private int? _selectedZoneId;
        private ZoneEditMode _editMode;
        private AdditionalZone? _editZone;
        private AdditionalZone? _mergeZone;
        private AdditionalZone? _gapMovingZone;
        private Point3? _editStart;
        private ResizeEdge _resizeEdge;
        private (double MinX, double MaxX, double MinY, double MaxY) _resizeBounds;
        public event Action<AdditionalZone>? ZoneSelected;
        public event Action? ZonesEdited;
        public event Action<string>? StatusChanged;

        private static readonly Brush Bg = Brushes.White;
        private static readonly Pen OutlinePen = FreezePen(Color.FromRgb(29, 78, 216), 2.0, dash: true);
        private static readonly Pen MeshPen = FreezePen(Color.FromArgb(80, 55, 65, 81), 0.4);

        public double Zoom => _zoom;
        public bool UndoLastEdit()
        {
            if (_result == null || _undo.Count == 0) return false;
            var last = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            _result.Zones = JsonConvert.DeserializeObject<List<AdditionalZone>>(last) ?? new List<AdditionalZone>();
            _pendingUndo = null;
            _editStart = null;
            _editZone = null;
            _mergeZone = null;
            _gapMovingZone = null;
            CommitEdits();
            RaiseStatus($"Отменено действие · осталось {_undo.Count} из {MaxUndoActions}");
            return true;
        }

        private void BeginEdit() => _pendingUndo = _result == null ? null : JsonConvert.SerializeObject(_result.Zones);
        private AdditionalZone? SelectedZone => _result?.Zones.FirstOrDefault(z => z.ZoneId == _selectedZoneId);

        public bool ResizeSelectedZone(double lengthMm, double widthMm)
        {
            var zone = SelectedZone;
            if (zone == null || _result == null ||
                lengthMm <= 50 || widthMm <= 50) return false;
            BeginEdit();
            if (!ZoneEditor.ResizeByDimensions(zone, lengthMm, widthMm, _result.Outline))
            { _pendingUndo = null; return false; }
            CommitEdits(zone);
            return true;
        }

        public bool SetSelectedFamily(ZoneFamilyKind kind, string name)
        {
            var zone = SelectedZone;
            if (zone == null || string.IsNullOrWhiteSpace(name)) return false;
            BeginEdit();
            ZoneEditor.SetFamily(zone, kind, name);
            CommitEdits(zone);
            return true;
        }

        public bool SetSelectedDiameter(int diameterMm)
        {
            if (_result == null || !_selectedZoneId.HasValue) return false;
            var zone = _result.Zones.FirstOrDefault(z => z.ZoneId == _selectedZoneId.Value);
            if (zone == null) return false;
            var background = zone.Layer == RebarLayer.As1 || zone.Layer == RebarLayer.As2
                ? _settings.BgBottomDiameterMm
                : _settings.BgTopDiameterMm;
            if (diameterMm < background)
            {
                RaiseStatus($"Ø{diameterMm} меньше фонового Ø{background}");
                return false;
            }
            BeginEdit();
            ZoneEditor.SetDiameter(zone, diameterMm);
            CommitEdits(zone);
            return true;
        }

        public bool SetSelectedStep(int stepMm)
        {
            var zone = SelectedZone;
            if (zone == null || (stepMm != 100 && stepMm != 200)) return false;
            BeginEdit();
            ZoneEditor.SetStep(zone, stepMm);
            CommitEdits(zone);
            return true;
        }

        public void SetEditMode(ZoneEditMode mode)
        {
            _editMode = mode;
            _editZone = null;
            _editStart = null;
            _resizeEdge = ResizeEdge.None;
            if (mode != ZoneEditMode.Merge) _mergeZone = null;
            if (mode != ZoneEditMode.CreateGap) _gapMovingZone = null;
            Cursor = mode == ZoneEditMode.Move ? Cursors.SizeAll :
                mode == ZoneEditMode.Delete ? Cursors.No : Cursors.Cross;
            RaiseStatus($"Редактирование зон: {mode}");
        }

        public void SetData(AnalysisResult? result, AnalysisSettings settings, bool showMesh, bool showIso, bool showAxes = false, bool fitView = true)
        {
            if (!ReferenceEquals(_result, result)) { _undo.Clear(); _pendingUndo = null; }
            _result = result;
            _settings = settings;
            _showMesh = showMesh;
            _showIso = showIso;
            _showAxes = showAxes;
            ComputeModelExtents();
            RebuildGeometryCache();
            if (fitView) FitToView();
            InvalidateVisual();
        }

        public void RefreshDisplayFlags(bool showMesh, bool showIso, bool showAxes = false)
        {
            _showMesh = showMesh;
            _showIso = showIso;
            _showAxes = showAxes;
            ComputeModelExtents();
            InvalidateVisual();
        }

        /// <summary>Обновить смещение/поворот без повторной загрузки данных.</summary>
        public void RefreshTransform(AnalysisSettings settings, bool fit = true)
        {
            _settings = settings ?? _settings;
            ComputeModelExtents();
            RebuildGeometryCache();
            if (fit) FitToView();
            InvalidateVisual();
            RaiseStatus();
        }

        private Point Tx(Point3 p) => Tx(p.X, p.Y);

        private Point Tx(double x, double y)
        {
            double ox = _settings?.OffsetXM ?? 0;
            double oy = _settings?.OffsetYM ?? 0;
            double deg = _settings?.RotationDeg ?? 0;
            if (Math.Abs(deg) < 1e-9 && Math.Abs(ox) < 1e-12 && Math.Abs(oy) < 1e-12)
                return new Point(x, y);

            double rad = deg * Math.PI / 180.0;
            double c = Math.Cos(rad), s = Math.Sin(rad);
            double dx = x - _pivotX, dy = y - _pivotY;
            return new Point(c * dx - s * dy + _pivotX + ox, s * dx + c * dy + _pivotY + oy);
        }

        private Point3 UnTx(double x, double y)
        {
            double ox = _settings?.OffsetXM ?? 0;
            double oy = _settings?.OffsetYM ?? 0;
            double deg = _settings?.RotationDeg ?? 0;
            double xr = x - ox, yr = y - oy;
            if (Math.Abs(deg) < 1e-9)
                return new Point3(xr, yr, 0);

            double rad = -deg * Math.PI / 180.0;
            double c = Math.Cos(rad), s = Math.Sin(rad);
            double dx = xr - _pivotX, dy = yr - _pivotY;
            return new Point3(c * dx - s * dy + _pivotX, s * dx + c * dy + _pivotY, 0);
        }

        private double _pivotX, _pivotY;

        public void ZoomBy(double factor, Point? anchorScreen = null)
        {
            SetZoom(_zoom * factor, anchorScreen);
        }

        public void SetZoom(double zoom, Point? anchorScreen = null)
        {
            zoom = Math.Max(0.2, Math.Min(40.0, zoom));
            if (Math.Abs(zoom - _zoom) < 1e-6) return;

            var anchor = anchorScreen ?? new Point(ActualWidth / 2, ActualHeight / 2);
            var before = ScreenToModel(anchor);
            _zoom = zoom;
            var after = ScreenToModel(anchor);
            _panX += (after.X - before.X) * _fitScale * _zoom;
            _panY -= (after.Y - before.Y) * _fitScale * _zoom;
            InvalidateVisual();
            RaiseStatus();
        }

        public void ResetZoom() { _zoom = 1; _panX = 0; _panY = 0; FitToView(); InvalidateVisual(); RaiseStatus(); }

        public void FitToView()
        {
            if (ActualWidth < 10 || ActualHeight < 10) return;
            double mw = Math.Max(0.1, _modelMaxX - _modelMinX);
            double mh = Math.Max(0.1, _modelMaxY - _modelMinY);
            _fitScale = Math.Min((ActualWidth - 24) / mw, (ActualHeight - 24) / mh);
            _panX = 0;
            _panY = 0;
            _zoom = 1;
            RaiseStatus();
        }

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(Bg, null, new Rect(0, 0, ActualWidth, ActualHeight));
            if (_result == null || _result.Plates.Count == 0) return;

            double s = _fitScale * _zoom;
            if (s < 1e-9) return;

            // толщина пера в единицах модели → ~N пикселей на экране
            double penW = Math.Max(1e-4, 1.6 / s);

            var world = new Matrix();
            world.Translate(-_modelMinX, -_modelMinY);
            world.Scale(s, -s); // Y вверх → экран вниз
            world.Translate(12 + _panX + (ActualWidth - 24 - (_modelMaxX - _modelMinX) * s) * 0.5,
                            12 + _panY + (ActualHeight - 24 - (_modelMaxY - _modelMinY) * s) * 0.5 + (_modelMaxY - _modelMinY) * s);

            dc.PushTransform(new MatrixTransform(world));

            var tl = ScreenToTransformed(new Point(0, 0));
            var br = ScreenToTransformed(new Point(ActualWidth, ActualHeight));
            double vMinX = Math.Min(tl.X, br.X) - 1;
            double vMaxX = Math.Max(tl.X, br.X) + 1;
            double vMinY = Math.Min(tl.Y, br.Y) - 1;
            double vMaxY = Math.Max(tl.Y, br.Y) + 1;

            // --- Стиль активного вида ЛИРА: светлая заливка КЭ + тонкая сетка ---
            var plateFill = new SolidColorBrush(Color.FromRgb(248, 248, 250));
            plateFill.Freeze();
            var meshPen = new Pen(new SolidColorBrush(Color.FromRgb(120, 130, 145)), Math.Max(1e-4, 0.9 / s));
            meshPen.Freeze();

            // сетка КЭ как в ВИЗОРе
            {
                int m = 0;
                int step = (!_showMesh && _zoom < 1.2) ? 2 : (_zoom < 1.0 ? 2 : 1);
                if (!_showMesh) step = Math.Max(step, _result.Plates.Count > 8000 ? 3 : 1);

                for (int i = 0; i < _result.Plates.Count; i += step)
                {
                    var plate = _result.Plates[i];
                    var shape = _plateShapes[i];
                    if (!shape.Intersects(vMinX, vMaxX, vMinY, vMaxY)) continue;
                    if (m++ > 18000) break;
                    dc.DrawGeometry(plateFill, _showMesh ? meshPen : null, shape.Geometry);
                }
            }

            int drawn = 0;
            const int maxDraw = 12000;

            // изополя As: цвет закреплён за интервалом шкалы (как Ogibayushchaya)
            if (_showIso)
            {
                double step = _settings.VisualizationScale;
                if (step <= 0) step = 1.0;
                drawn = 0;
                bool isoLabels = _zoom >= 1.6;
                var isoTypeface = new Typeface("Segoe UI");
                for (var i = 0; i < _result.Plates.Count; i++)
                {
                    var plate = _result.Plates[i];
                    var shape = _plateShapes[i];
                    if (!shape.Intersects(vMinX, vMaxX, vMinY, vMaxY)) continue;
                    if (drawn++ > maxDraw) break;
                    if (!plate.Rebar.Ok) continue;

                    var asAdd = IsoAdditionalAs(plate.Rebar, _settings);
                    if (asAdd <= 0.01) continue;

                    var rgb = IsoColorScale.ColorForValue(asAdd, step);
                    var brush = new SolidColorBrush(Color.FromArgb(160, rgb.R, rgb.G, rgb.B));
                    brush.Freeze();
                    dc.DrawGeometry(brush, null, shape.Geometry);

                    if (isoLabels && drawn <= 4000)
                    {
                        var tc = Tx(plate.Centroid.X, plate.Centroid.Y);
                        double fontModel = Math.Max(0.055, 8.0 / s);
                        var ft = new FormattedText(
                            asAdd.ToString("0.#"),
                            System.Globalization.CultureInfo.InvariantCulture,
                            FlowDirection.LeftToRight,
                            isoTypeface,
                            fontModel,
                            new SolidColorBrush(Color.FromArgb(220, 20, 20, 20)),
                            1.0);
                        DrawUprightText(dc, ft, tc);
                    }
                }
            }

            // зоны доп.армирования поверх сетки
            drawn = 0;
            bool labels = _zoom >= 1.6;
            bool dims = _zoom >= 1.5;
            var typeface = new Typeface("Segoe UI");
            var dimPen = new Pen(new SolidColorBrush(Color.FromArgb(230, 30, 30, 30)), Math.Max(1e-4, 1.0 / s));
            dimPen.Freeze();

            var zoneBoxes = new List<(AdditionalZone Zone, double MinX, double MaxX, double MinY, double MaxY)>();
            foreach (var shape in _drawZones)
            {
                var zone = shape.Zone;
                if (!shape.Intersects(vMinX, vMaxX, vMinY, vMaxY)) continue;
                if (drawn++ > maxDraw) break;

                var fill = DiameterFill(zone.DiameterMm, 70);
                bool selected = _selectedZoneId == zone.ZoneId;
                var zoneOutline = new Pen(
                    selected ? Brushes.Black : DiameterStroke(zone.DiameterMm),
                    Math.Max(1e-4, (selected ? 2.2 : 1.35) / s))
                {
                    DashStyle = DashStyles.Dash
                };
                zoneOutline.Freeze();
                dc.DrawGeometry(fill, zoneOutline, shape.Geometry);

                double minX = shape.MinX, maxX = shape.MaxX;
                double minY = shape.MinY, maxY = shape.MaxY;
                double cx = (minX + maxX) * 0.5, cy = (minY + maxY) * 0.5;
                zoneBoxes.Add((zone, minX, maxX, minY, maxY));

                if (labels && zone.DiameterMm > 0)
                {
                    var tc = Tx(cx, cy);
                    double fontModel = Math.Max(0.065, 9.0 / s);
                    var (line1, line2) = BuildZoneLabelLines(zone);
                    var ft1 = new FormattedText(
                        line1,
                        System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        typeface,
                        fontModel,
                        Brushes.Black,
                        1.0);
                    FormattedText? ft2 = null;
                    if (!string.IsNullOrEmpty(line2))
                    {
                        ft2 = new FormattedText(
                            line2,
                            System.Globalization.CultureInfo.InvariantCulture,
                            FlowDirection.LeftToRight,
                            typeface,
                            fontModel * 0.92,
                            Brushes.Black,
                            1.0);
                    }
                    double tw = Math.Max(ft1.Width, ft2?.Width ?? 0);
                    double th = ft1.Height + (ft2?.Height ?? 0) + fontModel * 0.12;
                    var pad = fontModel * 0.28;
                    var bg = new Rect(tc.X - tw / 2 - pad, tc.Y - th / 2 - pad,
                        tw + pad * 2, th + pad * 2);
                    dc.PushTransform(new ScaleTransform(1, -1, tc.X, tc.Y));
                    dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(210, 255, 255, 255)), null, bg);
                    double y0 = tc.Y - th / 2;
                    dc.DrawText(ft1, new Point(tc.X - ft1.Width / 2, y0));
                    if (ft2 != null)
                        dc.DrawText(ft2, new Point(tc.X - ft2.Width / 2, y0 + ft1.Height + fontModel * 0.12));
                    dc.Pop();
                }
            }

            // Одна размерная цепочка по всем зонам + привязка к осям (без наложений)
            if (dims && zoneBoxes.Count > 0)
                DrawGlobalDimensionChains(dc, zoneBoxes, dimPen, typeface, s);

            // оси из ЛИРА
            DrawAxes(dc, s, penW);

            dc.Pop();

            // подпись отметки в экранных координатах (не масштабируется с моделью)
            DrawElevationBadge(dc);
            DrawDiameterLegend(dc);
            DrawDirectionAxes(dc);
        }

        private void DrawDirectionAxes(DrawingContext dc)
        {
            var origin = new Point(35, ActualHeight - 35);
            var pen = new Pen(Brushes.DarkSlateGray, 2);
            dc.DrawLine(pen, origin, new Point(origin.X + 32, origin.Y));
            dc.DrawLine(pen, origin, new Point(origin.X, origin.Y - 32));
            var face = new Typeface("Segoe UI");
            dc.DrawText(new FormattedText("X", System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, face, 13, Brushes.DarkSlateGray, 1),
                new Point(origin.X + 35, origin.Y - 10));
            dc.DrawText(new FormattedText("Y", System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, face, 13, Brushes.DarkSlateGray, 1),
                new Point(origin.X - 5, origin.Y - 51));
        }

        private void DrawDiameterLegend(DrawingContext dc)
        {
            var diameters = _drawZones
                .Where(item => item.Zone.DiameterMm > 0)
                .Select(item => item.Zone.DiameterMm)
                .Distinct()
                .OrderBy(d => d)
                .ToList();
            if (diameters.Count == 0) return;

            var typeface = new Typeface("Segoe UI");
            const double fontSize = 12;
            const double rowHeight = 22;
            const double width = 132;
            var height = 34 + diameters.Count * rowHeight;
            var x = Math.Max(8, ActualWidth - width - 12);
            const double y = 12;
            var panel = new Rect(x, y, width, height);
            var panelFill = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255));
            panelFill.Freeze();
            var panelPen = new Pen(new SolidColorBrush(Color.FromRgb(203, 213, 225)), 1);
            panelPen.Freeze();
            dc.DrawRoundedRectangle(panelFill, panelPen, panel, 4, 4);

            var title = new FormattedText(
                "Диаметр зон",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                fontSize,
                Brushes.Black,
                1.0);
            dc.DrawText(title, new Point(x + 10, y + 8));

            for (var i = 0; i < diameters.Count; i++)
            {
                var diameter = diameters[i];
                var rowY = y + 32 + i * rowHeight;
                dc.DrawRectangle(
                    DiameterFill(diameter, 170),
                    new Pen(DiameterStroke(diameter), 1),
                    new Rect(x + 10, rowY + 3, 16, 16));
                var label = new FormattedText(
                    $"Ø{diameter} мм",
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    fontSize,
                    Brushes.Black,
                    1.0);
                dc.DrawText(label, new Point(x + 34, rowY + 2));
            }
        }

        private void DrawAxes(DrawingContext dc, double s, double penW)
        {
            if (!_showAxes || _result?.Axes == null || _result.Axes.Count == 0) return;
            if (!TryGetSlabBounds(out double rawMinX, out double rawMaxX, out double rawMinY, out double rawMaxY))
                return;

            var axisPen = new Pen(new SolidColorBrush(Color.FromRgb(37, 99, 235)), Math.Max(1e-4, 1.15 / s))
            {
                DashStyle = DashStyles.Dash
            };
            axisPen.Freeze();
            var bubbleFill = Brushes.White;
            var bubblePen = new Pen(new SolidColorBrush(Color.FromRgb(37, 99, 235)), Math.Max(1e-4, 1.1 / s));
            bubblePen.Freeze();
            var typeface = new Typeface("Segoe UI");

            double span = Math.Max(rawMaxX - rawMinX, rawMaxY - rawMinY);
            double r = Math.Max(0.18, Math.Min(0.55, 13.0 / s));
            // вынос маркеров: одна линия сверху (вертикальные оси) и одна слева (горизонтальные)
            double gap = Math.Max(r * 2.8, span * 0.04 + 0.4);
            double bubbleY = rawMaxY + gap;   // ряд кружков сверху
            double bubbleX = rawMinX - gap;   // ряд кружков слева
            // линии только снаружи контура: от маркера до грани плиты
            double edgePad = Math.Max(0.02, r * 0.15);

            foreach (var ax in _result.Axes)
            {
                bool vertical = ax.Vertical;
                double pos = ax.Position;
                if (ax.IsSegment)
                {
                    double dx = Math.Abs(ax.X2 - ax.X1);
                    double dy = Math.Abs(ax.Y2 - ax.Y1);
                    vertical = dx <= dy;
                    pos = vertical ? (ax.X1 + ax.X2) * 0.5 : (ax.Y1 + ax.Y2) * 0.5;
                }

                if (vertical)
                {
                    // ось X=const: кружок сверху, штрих вниз до верхнего края плиты
                    if (pos < rawMinX - 1 || pos > rawMaxX + 1) continue;
                    var bubbleAt = Tx(pos, bubbleY);
                    var edge = Tx(pos, rawMaxY + edgePad);
                    dc.DrawLine(axisPen, bubbleAt, edge);
                    DrawAxisBubble(dc, bubbleAt, ax.Name, r, bubbleFill, bubblePen, typeface, s);
                }
                else
                {
                    // ось Y=const: кружок слева, штрих вправо до левого края плиты
                    if (pos < rawMinY - 1 || pos > rawMaxY + 1) continue;
                    var bubbleAt = Tx(bubbleX, pos);
                    var edge = Tx(rawMinX - edgePad, pos);
                    dc.DrawLine(axisPen, bubbleAt, edge);
                    DrawAxisBubble(dc, bubbleAt, ax.Name, r, bubbleFill, bubblePen, typeface, s);
                }
            }
        }

        private bool TryGetSlabBounds(out double minX, out double maxX, out double minY, out double maxY)
        {
            minX = double.MaxValue; maxX = double.MinValue;
            minY = double.MaxValue; maxY = double.MinValue;
            if (_result == null) return false;

            double x0 = double.MaxValue, x1 = double.MinValue;
            double y0 = double.MaxValue, y1 = double.MinValue;

            void Acc(Point3 p)
            {
                if (p.X < x0) x0 = p.X;
                if (p.X > x1) x1 = p.X;
                if (p.Y < y0) y0 = p.Y;
                if (p.Y > y1) y1 = p.Y;
            }

            if (_result.Outline != null && _result.Outline.Count >= 3)
            {
                foreach (var p in _result.Outline) Acc(p);
            }
            else
            {
                foreach (var plate in _result.Plates)
                foreach (var p in plate.Contour)
                    Acc(p);
            }

            if (x0 >= x1 || y0 >= y1) return false;
            minX = x0; maxX = x1; minY = y0; maxY = y1;
            return true;
        }

        private static void DrawAxisBubble(
            DrawingContext dc, Point center, string name, double r,
            Brush fill, Pen pen, Typeface typeface, double s)
        {
            dc.DrawEllipse(fill, pen, center, r, r);
            double fontModel = Math.Max(0.08, 10.0 / s);
            var ft = new FormattedText(
                name ?? "",
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                fontModel,
                new SolidColorBrush(Color.FromRgb(30, 64, 175)),
                1.0);
            DrawUprightText(dc, ft, center);
        }

        /// <summary>
        /// Мир рисуется с Scale(s,-s), поэтому обычный DrawText получается вверх ногами.
        /// Локальный Scale(1,-1) вокруг точки возвращает текст «лицом вверх».
        /// </summary>
        private static void DrawUprightText(DrawingContext dc, FormattedText ft, Point center)
        {
            dc.PushTransform(new ScaleTransform(1, -1, center.X, center.Y));
            dc.DrawText(ft, new Point(center.X - ft.Width / 2, center.Y - ft.Height / 2));
            dc.Pop();
        }

        private void DrawElevationBadge(DrawingContext dc)
        {
            if (_result == null || string.IsNullOrWhiteSpace(_result.ElevationLabel)) return;
            var text = "Отметка КЭ: " + _result.ElevationLabel;
            var ft = new FormattedText(
                text,
                System.Globalization.CultureInfo.GetCultureInfo("ru-RU"),
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI Semibold"),
                14,
                Brushes.White,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            double pad = 8;
            var rect = new Rect(12, 12, ft.Width + pad * 2, ft.Height + pad);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(220, 17, 24, 39)), null, rect, 4, 4);
            dc.DrawText(ft, new Point(rect.X + pad, rect.Y + pad * 0.5));
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            ZoomBy(e.Delta > 0 ? 1.2 : 1 / 1.2, e.GetPosition(this));
            e.Handled = true;
            base.OnMouseWheel(e);
        }

        protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
        {
            _panning = true;
            _panLast = e.GetPosition(this);
            CaptureMouse();
            Cursor = Cursors.SizeAll;
            e.Handled = true;
            base.OnMouseRightButtonDown(e);
        }

        protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
        {
            _panning = false;
            ReleaseMouseCapture();
            Cursor = Cursors.Arrow;
            e.Handled = true;
            base.OnMouseRightButtonUp(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_panning)
            {
                var p = e.GetPosition(this);
                _panX += p.X - _panLast.X;
                _panY += p.Y - _panLast.Y;
                _panLast = p;
                InvalidateVisual();
            }
            else
            {
                var m = ScreenToModel(e.GetPosition(this));
                if (_resizeEdge != ResizeEdge.None && _editZone != null && _result != null)
                {
                    if (ResizeDraggedEdge(_editZone, m))
                    {
                        RebuildZoneGeometryCache();
                        InvalidateVisual();
                    }
                }
                else if (_editMode == ZoneEditMode.Select || _editMode == ZoneEditMode.Resize)
                {
                    var edge = HitResizeEdge(m);
                    Cursor = edge.Edge == ResizeEdge.Left || edge.Edge == ResizeEdge.Right
                        ? Cursors.SizeWE : edge.Edge == ResizeEdge.Top || edge.Edge == ResizeEdge.Bottom
                            ? Cursors.SizeNS : Cursors.Arrow;
                }
                var edit = _editStart != null ? $" | {_editMode}: отпустите ЛКМ" : "";
                RaiseStatus($"X={m.X:F2} Y={m.Y:F2} м | зум {_zoom * 100:0}%{edit} | колесо зум, ПКМ пан");
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            var m = ScreenToModel(e.GetPosition(this));
            if (_result != null && (_editMode == ZoneEditMode.Select || _editMode == ZoneEditMode.Resize))
            {
                var edge = HitResizeEdge(m);
                if (edge.Zone != null && edge.Edge != ResizeEdge.None)
                {
                    _editZone = edge.Zone;
                    _resizeEdge = edge.Edge;
                    _resizeBounds = (edge.Zone.Contour.Min(p => p.X), edge.Zone.Contour.Max(p => p.X),
                        edge.Zone.Contour.Min(p => p.Y), edge.Zone.Contour.Max(p => p.Y));
                    _editStart = m;
                    _selectedZoneId = edge.Zone.ZoneId;
                    BeginEdit();
                    CaptureMouse();
                    ZoneSelected?.Invoke(edge.Zone);
                    e.Handled = true;
                    base.OnMouseLeftButtonDown(e);
                    return;
                }
            }
            var hit = HitZone(m);
            if (_result != null && _editMode != ZoneEditMode.Select)
            {
                if (_editMode == ZoneEditMode.Delete && hit != null)
                {
                    BeginEdit();
                    _result.Zones.Remove(hit);
                    CommitEdits();
                }
                else if (_editMode == ZoneEditMode.Split && hit != null)
                {
                    var pieces = ZoneEditor.Split(hit,
                        hit.Direction == ZoneDirection.X ? m.X : m.Y,
                        hit.Direction == ZoneDirection.X, _result.Outline);
                    if (pieces.Count == 2)
                    {
                        BeginEdit();
                        _result.Zones.Remove(hit);
                        _result.Zones.AddRange(pieces);
                        CommitEdits();
                    }
                }
                else if (_editMode == ZoneEditMode.Merge && hit != null)
                {
                    if (_mergeZone == null)
                    {
                        _mergeZone = hit;
                        _selectedZoneId = hit.ZoneId;
                        ZoneSelected?.Invoke(hit);
                        RaiseStatus("Объединение: выберите вторую зону того же слоя");
                        InvalidateVisual();
                    }
                    else if (!ReferenceEquals(_mergeZone, hit))
                    {
                        var merged = ZoneEditor.Merge(_mergeZone, hit, _result.Outline);
                        if (merged != null)
                        {
                            BeginEdit();
                            _result.Zones.Remove(_mergeZone);
                            _result.Zones.Remove(hit);
                            _result.Zones.Add(merged);
                            _mergeZone = null;
                            CommitEdits();
                        }
                        else RaiseStatus("Объединять можно зоны одного слоя и направления");
                    }
                }
                else if (_editMode == ZoneEditMode.PerpendicularToEdge && hit != null)
                {
                    var minX = hit.Contour.Min(p => p.X);
                    var maxX = hit.Contour.Max(p => p.X);
                    var minY = hit.Contour.Min(p => p.Y);
                    var maxY = hit.Contour.Max(p => p.Y);
                    var distanceToVertical = Math.Min(Math.Abs(m.X - minX), Math.Abs(m.X - maxX));
                    var distanceToHorizontal = Math.Min(Math.Abs(m.Y - minY), Math.Abs(m.Y - maxY));
                    var pieces = ZoneEditor.SplitPerpendicularToEdge(
                        hit, m.X, m.Y, distanceToVertical <= distanceToHorizontal, _result.Outline);
                    if (pieces.Count == 2)
                    {
                        BeginEdit();
                        var index = _result.Zones.IndexOf(hit);
                        _result.Zones.RemoveAt(index);
                        _result.Zones.InsertRange(index, pieces);
                        CommitEdits(pieces[0]);
                    }
                    else RaiseStatus("Разрез должен проходить внутри зоны, на расстоянии от края");
                }
                else if (_editMode == ZoneEditMode.CreateGap && hit != null)
                {
                    if (_gapMovingZone == null)
                    {
                        _gapMovingZone = hit;
                        _selectedZoneId = hit.ZoneId;
                        ZoneSelected?.Invoke(hit);
                        RaiseStatus("Зазор: выберите зону, от которой нужно отодвинуть");
                        InvalidateVisual();
                    }
                    else if (!ReferenceEquals(_gapMovingZone, hit))
                    {
                        var moving = _gapMovingZone;
                        _gapMovingZone = null;
                        BeginEdit();
                        if (ZoneEditor.CreateGap(moving, hit, _result.Outline))
                            CommitEdits(moving);
                        else
                        {
                            _pendingUndo = null;
                            RaiseStatus("Не удалось создать зазор внутри контура плиты");
                        }
                    }
                }
                else if (_editMode == ZoneEditMode.Create || (_editMode == ZoneEditMode.Move && hit != null))
                {
                    _editZone = hit;
                    _editStart = m;
                    BeginEdit();
                    CaptureMouse();
                }
                e.Handled = true;
                base.OnMouseLeftButtonDown(e);
                return;
            }
            for (int i = _drawZones.Count - 1; i >= 0; i--)
            {
                var shape = _drawZones[i];
                if (PointInPoly(m, shape.Contour))
                {
                    _selectedZoneId = shape.Zone.ZoneId;
                    ZoneSelected?.Invoke(shape.Zone);
                    InvalidateVisual();
                    e.Handled = true;
                    break;
                }
            }
            base.OnMouseLeftButtonDown(e);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (_result != null && _editStart != null)
            {
                var end = ScreenToModel(e.GetPosition(this));
                var start = _editStart;
                var resizedZone = _resizeEdge != ResizeEdge.None ? _editZone : null;
                if (resizedZone != null)
                    ResizeDraggedEdge(resizedZone, end);
                else if (_editMode == ZoneEditMode.Move && _editZone != null)
                    ZoneEditor.Move(_editZone, end.X - start.X, end.Y - start.Y, _result.Outline);
                else if (_editMode == ZoneEditMode.Create)
                {
                    var template = _editZone ?? _result.Zones.FirstOrDefault();
                    if (template != null)
                    {
                        var created = ZoneEditor.Create(template,
                            Math.Min(start.X, end.X), Math.Max(start.X, end.X),
                            Math.Min(start.Y, end.Y), Math.Max(start.Y, end.Y), _result.Outline);
                        if (created != null) _result.Zones.Add(created);
                    }
                }
                _editStart = null;
                _editZone = null;
                _resizeEdge = ResizeEdge.None;
                ReleaseMouseCapture();
                CommitEdits(resizedZone);
                e.Handled = true;
            }
            base.OnMouseLeftButtonUp(e);
        }

        private AdditionalZone? HitZone(Point3 point)
        {
            for (var i = _drawZones.Count - 1; i >= 0; i--)
                if (PointInPoly(point, _drawZones[i].Contour)) return _drawZones[i].Zone;
            return null;
        }

        private (AdditionalZone? Zone, ResizeEdge Edge) HitResizeEdge(Point3 point)
        {
            var tolerance = 8.0 / Math.Max(1e-6, _fitScale * _zoom);
            var transformed = Tx(point);
            for (var i = _drawZones.Count - 1; i >= 0; i--)
            {
                var shape = _drawZones[i];
                var z = shape.Zone;
                if (z.Contour.Count < 3 || transformed.X < shape.MinX - tolerance ||
                    transformed.X > shape.MaxX + tolerance || transformed.Y < shape.MinY - tolerance ||
                    transformed.Y > shape.MaxY + tolerance) continue;
                var minX = z.Contour.Min(p => p.X);
                var maxX = z.Contour.Max(p => p.X);
                var minY = z.Contour.Min(p => p.Y);
                var maxY = z.Contour.Max(p => p.Y);
                var nearest = new[]
                {
                    (Distance: Math.Abs(point.X - minX), Edge: ResizeEdge.Left),
                    (Distance: Math.Abs(point.X - maxX), Edge: ResizeEdge.Right),
                    (Distance: Math.Abs(point.Y - minY), Edge: ResizeEdge.Bottom),
                    (Distance: Math.Abs(point.Y - maxY), Edge: ResizeEdge.Top)
                }.Where(item => item.Edge == ResizeEdge.Left || item.Edge == ResizeEdge.Right
                    ? point.Y >= minY - tolerance && point.Y <= maxY + tolerance
                    : point.X >= minX - tolerance && point.X <= maxX + tolerance)
                 .OrderBy(item => item.Distance).FirstOrDefault();
                if (nearest.Distance <= tolerance && nearest.Edge != ResizeEdge.None)
                    return (z, nearest.Edge);
            }
            return (null, ResizeEdge.None);
        }

        private bool ResizeDraggedEdge(AdditionalZone zone, Point3 point)
        {
            if (_result == null) return false;
            var (minX, maxX, minY, maxY) = _resizeBounds;
            switch (_resizeEdge)
            {
                case ResizeEdge.Left: minX = point.X; break;
                case ResizeEdge.Right: maxX = point.X; break;
                case ResizeEdge.Bottom: minY = point.Y; break;
                case ResizeEdge.Top: maxY = point.Y; break;
                default: return false;
            }
            if (maxX - minX <= 0.05 || maxY - minY <= 0.05) return false;
            return ZoneEditor.Resize(zone, minX, maxX, minY, maxY, _result.Outline);
        }

        private void CommitEdits(AdditionalZone? keepSelected = null)
        {
            if (_result == null) return;
            if (_pendingUndo != null)
            {
                var before = JsonConvert.DeserializeObject<List<AdditionalZone>>(_pendingUndo) ?? new List<AdditionalZone>();
                var unchanged = new HashSet<string>(before.Select(JsonConvert.SerializeObject));
                for (var i = _result.Zones.Count - 1; i >= 0; i--)
                {
                    var zone = _result.Zones[i];
                    if (unchanged.Contains(JsonConvert.SerializeObject(zone)) ||
                        !ZoneEditor.IntersectsOpening(zone, _result.Openings)) continue;
                    var parts = ZoneEditor.SplitAtOpenings(
                        zone, _result.Openings, _settings, _result.Plates);
                    _result.Zones.RemoveAt(i);
                    _result.Zones.InsertRange(i, parts);
                    if (ReferenceEquals(keepSelected, zone)) keepSelected = parts.FirstOrDefault();
                }
                if (_pendingUndo != JsonConvert.SerializeObject(_result.Zones))
                {
                    _undo.Add(_pendingUndo);
                    if (_undo.Count > MaxUndoActions) _undo.RemoveAt(0);
                }
                _pendingUndo = null;
            }
            for (var i = 0; i < _result.Zones.Count; i++) _result.Zones[i].ZoneId = i + 1;
            RebuildZoneGeometryCache();
            _selectedZoneId = keepSelected?.ZoneId;
            if (keepSelected != null) ZoneSelected?.Invoke(keepSelected);
            ZonesEdited?.Invoke();
            InvalidateVisual();
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            if (_result != null && _zoom <= 1.01)
                FitToView();
            InvalidateVisual();
        }

        private void RebuildGeometryCache()
        {
            _plateShapes.Clear();
            if (_result != null)
            {
                foreach (var plate in _result.Plates)
                    _plateShapes.Add(BuildShape(plate.Contour));
            }
            RebuildZoneGeometryCache();
        }

        private void RebuildZoneGeometryCache()
        {
            _drawZones.Clear();
            if (_result == null) return;
            foreach (var z in _result.Zones)
            {
                if (!z.IsValid && z.StatusColor == "error") continue;
                if (z.Contour == null || z.Contour.Count < 3) continue;
                var arr = new Point3[z.Contour.Count];
                for (int i = 0; i < z.Contour.Count; i++) arr[i] = z.Contour[i];
                var cached = BuildShape(arr);
                _drawZones.Add(new CachedZoneShape
                {
                    Zone = z,
                    Contour = arr,
                    Geometry = cached.Geometry,
                    MinX = cached.MinX,
                    MaxX = cached.MaxX,
                    MinY = cached.MinY,
                    MaxY = cached.MaxY
                });
            }
        }

        private CachedShape BuildShape(IList<Point3> contour)
        {
            var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;
            using (var ctx = geometry.Open())
            {
                for (var i = 0; i < contour.Count; i++)
                {
                    var p = Tx(contour[i]);
                    if (p.X < minX) minX = p.X;
                    if (p.X > maxX) maxX = p.X;
                    if (p.Y < minY) minY = p.Y;
                    if (p.Y > maxY) maxY = p.Y;
                    if (i == 0) ctx.BeginFigure(p, true, true);
                    else ctx.LineTo(p, true, false);
                }
            }
            geometry.Freeze();
            return new CachedShape
            {
                Geometry = geometry,
                MinX = minX,
                MaxX = maxX,
                MinY = minY,
                MaxY = maxY
            };
        }

        private void ComputeModelExtents()
        {
            _modelMinX = _modelMinY = double.MaxValue;
            _modelMaxX = _modelMaxY = double.MinValue;
            _pivotX = _pivotY = 0;
            if (_result == null) return;

            // pivot = центр исходной геометрии (до поворота)
            double sx = 0, sy = 0;
            int n = 0;
            void AccRaw(Point3 p) { sx += p.X; sy += p.Y; n++; }
            if (_result.Outline != null)
                foreach (var p in _result.Outline) AccRaw(p);
            foreach (var plate in _result.Plates)
            foreach (var p in plate.Contour)
                AccRaw(p);
            if (n > 0) { _pivotX = sx / n; _pivotY = sy / n; }

            void Acc(Point3 p)
            {
                var t = Tx(p);
                if (t.X < _modelMinX) _modelMinX = t.X;
                if (t.Y < _modelMinY) _modelMinY = t.Y;
                if (t.X > _modelMaxX) _modelMaxX = t.X;
                if (t.Y > _modelMaxY) _modelMaxY = t.Y;
            }

            if (_result.Outline != null)
                foreach (var p in _result.Outline) Acc(p);

            foreach (var plate in _result.Plates)
            foreach (var p in plate.Contour)
                Acc(p);

            if (_showAxes && _result.Axes != null && _result.Axes.Count > 0 &&
                TryGetSlabBounds(out var bx0, out var bx1, out var by0, out var by1))
            {
                double span = Math.Max(bx1 - bx0, by1 - by0);
                double gap = Math.Max(0.8, span * 0.04 + 0.5);
                double bubbleY = by1 + gap;
                double bubbleX = bx0 - gap;
                foreach (var ax in _result.Axes)
                {
                    bool vertical = ax.Vertical;
                    double pos = ax.Position;
                    if (ax.IsSegment)
                    {
                        double dx = Math.Abs(ax.X2 - ax.X1);
                        double dy = Math.Abs(ax.Y2 - ax.Y1);
                        vertical = dx <= dy;
                        pos = vertical ? (ax.X1 + ax.X2) * 0.5 : (ax.Y1 + ax.Y2) * 0.5;
                    }
                    if (vertical)
                        Acc(new Point3(pos, bubbleY, 0));
                    else
                        Acc(new Point3(bubbleX, pos, 0));
                }
            }

            if (_modelMinX > _modelMaxX)
            {
                _modelMinX = 0; _modelMinY = 0; _modelMaxX = 10; _modelMaxY = 10;
            }
        }

        private Point ScreenToTransformed(Point screen)
        {
            double s = _fitScale * _zoom;
            if (s < 1e-12) return new Point();
            double ox = 12 + _panX + (ActualWidth - 24 - (_modelMaxX - _modelMinX) * s) * 0.5;
            double oy = 12 + _panY + (ActualHeight - 24 - (_modelMaxY - _modelMinY) * s) * 0.5 + (_modelMaxY - _modelMinY) * s;
            double mx = (screen.X - ox) / s + _modelMinX;
            double my = _modelMinY - (screen.Y - oy) / s;
            return new Point(mx, my);
        }

        private Point3 ScreenToModel(Point screen)
        {
            var t = ScreenToTransformed(screen);
            return UnTx(t.X, t.Y);
        }

        private static Brush DiameterFill(int diameterMm, byte alpha)
        {
            var key = (diameterMm << 8) | alpha;
            if (DiameterFillCache.TryGetValue(key, out var cached)) return cached;
            var c = DiameterColor(diameterMm);
            var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
            b.Freeze();
            DiameterFillCache[key] = b;
            return b;
        }

        private static Brush DiameterStroke(int diameterMm)
        {
            if (DiameterStrokeCache.TryGetValue(diameterMm, out var cached)) return cached;
            var c = DiameterColor(diameterMm);
            var b = new SolidColorBrush(Color.FromArgb(230, c.R, c.G, c.B));
            b.Freeze();
            DiameterStrokeCache[diameterMm] = b;
            return b;
        }

        private static Color DiameterColor(int d) => d switch
        {
            8 => Color.FromRgb(148, 163, 184),
            10 => Color.FromRgb(56, 189, 248),
            12 => Color.FromRgb(34, 197, 94),
            16 => Color.FromRgb(234, 179, 8),
            20 => Color.FromRgb(249, 115, 22),
            22 => Color.FromRgb(239, 68, 68),
            25 => Color.FromRgb(168, 85, 247),
            28 => Color.FromRgb(236, 72, 153),
            32 => Color.FromRgb(99, 102, 241),
            36 => Color.FromRgb(20, 184, 166),
            _ => Color.FromRgb(100, 116, 139)
        };

        private static Brush ZoneFill(AdditionalZone z, double step)
        {
            return DiameterFill(z.DiameterMm, 75);
        }

        private static double IsoAdditionalAs(PlateReinforcement r, AnalysisSettings s)
        {
            double best = 0;
            void Acc(double asVal, double main, bool show)
            {
                if (!show) return;
                best = Math.Max(best, asVal - main);
            }
            Acc(r.As1, s.AsMainAs1, s.ShowAs1);
            Acc(r.As2, s.AsMainAs2, s.ShowAs2);
            Acc(r.As3, s.AsMainAs3, s.ShowAs3);
            Acc(r.As4, s.AsMainAs4, s.ShowAs4);
            return best;
        }

        private static string BuildZoneLabel(AdditionalZone zone)
        {
            var (a, b) = BuildZoneLabelLines(zone);
            return string.IsNullOrEmpty(b) ? a : a + "\n" + b;
        }

        /// <summary>Ø-длина и поперечная раскладка: число стержней, шаг, ширина зоны.</summary>
        private static (string Line1, string Line2) BuildZoneLabelLines(AdditionalZone zone)
        {
            if (zone.DiameterMm <= 0) return ("", "");
            var lenMm = zone.LengthMm > 0
                ? (int)Math.Round(zone.LengthMm)
                : (int)Math.Round(UnitConversion.MetersToMm(zone.LengthM));
            if (lenMm < 1) lenMm = zone.BarCount > 0 ? zone.BarCount : 0;
            var step = zone.BarStepMm > 0 ? zone.BarStepMm : 200;
            var count = Math.Max(1, zone.BarCount);
            return ($"{zone.DiameterMm}-{lenMm} ×{count}", $"шаг {step}");
        }

        /// <summary>
        /// Глобальные размерные цепочки по всем зонам:
        /// сверху — все X-грани + ближайшие вертикальные оси;
        /// слева — все Y-грани + ближайшие горизонтальные оси.
        /// Одна линия на направление → без наложений отдельных выносок.
        /// </summary>
        private void DrawGlobalDimensionChains(
            DrawingContext dc,
            List<(AdditionalZone Zone, double MinX, double MaxX, double MinY, double MaxY)> boxes,
            Pen pen,
            Typeface typeface,
            double s)
        {
            if (boxes.Count == 0) return;
            double gap = Math.Max(0.18, 22.0 / s);
            double tick = Math.Max(0.05, 7.0 / s);
            double fontModel = Math.Max(0.06, 8.0 / s);
            var brush = new SolidColorBrush(Color.FromRgb(30, 30, 30));
            brush.Freeze();

            double slabMaxY = boxes.Max(b => b.MaxY);
            double slabMinX = boxes.Min(b => b.MinX);
            double slabMinY = boxes.Min(b => b.MinY);
            double slabMaxX = boxes.Max(b => b.MaxX);

            // --- Горизонтальная цепочка сверху ---
            var xs = new SortedSet<double>();
            foreach (var b in boxes)
            {
                xs.Add(RoundCoord(b.MinX));
                xs.Add(RoundCoord(b.MaxX));
                if (!string.IsNullOrEmpty(b.Zone.AxisNameX) && b.Zone.AxisPosXM >= slabMinX - 12 && b.Zone.AxisPosXM <= slabMaxX + 12)
                    xs.Add(RoundCoord(b.Zone.AxisPosXM));
            }
            // Ближайшие вертикальные оси из результата
            if (_result?.Axes != null)
            {
                foreach (var ax in _result.Axes)
                {
                    bool vert = ax.Vertical;
                    double pos = ax.Position;
                    if (ax.IsSegment)
                    {
                        vert = Math.Abs(ax.X2 - ax.X1) <= Math.Abs(ax.Y2 - ax.Y1);
                        pos = vert ? (ax.X1 + ax.X2) * 0.5 : (ax.Y1 + ax.Y2) * 0.5;
                    }
                    if (!vert) continue;
                    if (pos < slabMinX - 0.5 || pos > slabMaxX + 0.5) continue;
                    // только оси, близкие к какой-то грани зоны
                    if (boxes.Any(b => Math.Abs(b.MinX - pos) < 8 || Math.Abs(b.MaxX - pos) < 8))
                        xs.Add(RoundCoord(pos));
                }
            }

            var xList = xs.ToList();
            if (xList.Count >= 2)
            {
                double yDim = slabMaxY + gap;
                foreach (var x in xList)
                    dc.DrawLine(pen, Tx(x, slabMaxY), Tx(x, yDim + tick * 0.35));
                dc.DrawLine(pen, Tx(xList[0], yDim), Tx(xList[xList.Count - 1], yDim));
                for (int i = 0; i < xList.Count; i++)
                    DrawDimTick(dc, pen, xList[i], yDim, tick, horizontal: true);
                for (int i = 0; i < xList.Count - 1; i++)
                {
                    var a = xList[i];
                    var b = xList[i + 1];
                    var segMm = Math.Round(Math.Abs(UnitConversion.MetersToMm(b - a)) / 10.0) * 10.0;
                    if (segMm < 1) continue;
                    DrawDimText(dc, typeface, brush, fontModel,
                        Tx((a + b) * 0.5, yDim + tick * 0.9), FormatMm(segMm));
                }
            }

            // --- Вертикальная цепочка слева ---
            var ys = new SortedSet<double>();
            foreach (var b in boxes)
            {
                ys.Add(RoundCoord(b.MinY));
                ys.Add(RoundCoord(b.MaxY));
                if (!string.IsNullOrEmpty(b.Zone.AxisNameY) && b.Zone.AxisPosYM >= slabMinY - 12 && b.Zone.AxisPosYM <= slabMaxY + 12)
                    ys.Add(RoundCoord(b.Zone.AxisPosYM));
            }
            if (_result?.Axes != null)
            {
                foreach (var ax in _result.Axes)
                {
                    bool vert = ax.Vertical;
                    double pos = ax.Position;
                    if (ax.IsSegment)
                    {
                        vert = Math.Abs(ax.X2 - ax.X1) <= Math.Abs(ax.Y2 - ax.Y1);
                        pos = vert ? (ax.X1 + ax.X2) * 0.5 : (ax.Y1 + ax.Y2) * 0.5;
                    }
                    if (vert) continue;
                    if (pos < slabMinY - 0.5 || pos > slabMaxY + 0.5) continue;
                    if (boxes.Any(b => Math.Abs(b.MinY - pos) < 8 || Math.Abs(b.MaxY - pos) < 8))
                        ys.Add(RoundCoord(pos));
                }
            }

            var yList = ys.ToList();
            if (yList.Count >= 2)
            {
                double xDim = slabMinX - gap;
                foreach (var y in yList)
                    dc.DrawLine(pen, Tx(slabMinX, y), Tx(xDim - tick * 0.35, y));
                dc.DrawLine(pen, Tx(xDim, yList[0]), Tx(xDim, yList[yList.Count - 1]));
                for (int i = 0; i < yList.Count; i++)
                    DrawDimTick(dc, pen, xDim, yList[i], tick, horizontal: false);
                for (int i = 0; i < yList.Count - 1; i++)
                {
                    var a = yList[i];
                    var b = yList[i + 1];
                    var segMm = Math.Round(Math.Abs(UnitConversion.MetersToMm(b - a)) / 10.0) * 10.0;
                    if (segMm < 1) continue;
                    DrawDimText(dc, typeface, brush, fontModel,
                        Tx(xDim - tick * 0.9, (a + b) * 0.5), FormatMm(segMm));
                }
            }
        }

        private static double RoundCoord(double m)
            => Math.Round(m * 1000.0 / 10.0) * 10.0 / 1000.0;

        /// <summary>
        /// Цепочка размеров одной зоны (fallback / выбранная).
        /// </summary>
        private void DrawZoneDimensions(
            DrawingContext dc,
            double minX, double maxX, double minY, double maxY,
            AdditionalZone zone,
            Pen pen,
            Typeface typeface,
            double s)
        {
            var boxes = new List<(AdditionalZone, double, double, double, double)>
            {
                (zone, minX, maxX, minY, maxY)
            };
            DrawGlobalDimensionChains(dc, boxes, pen, typeface, s);
        }

        private static bool NearMm(double a, double b, double tol = 15)
            => Math.Abs(a - b) <= tol;

        private static string FormatMm(double mm)
            => Math.Round(mm).ToString("0", System.Globalization.CultureInfo.InvariantCulture);

        private void DrawDimTick(DrawingContext dc, Pen pen, double x, double y, double tick, bool horizontal)
        {
            double d = tick * 0.55;
            if (horizontal)
                dc.DrawLine(pen, Tx(x - d * 0.35, y - d), Tx(x + d * 0.35, y + d));
            else
                dc.DrawLine(pen, Tx(x - d, y - d * 0.35), Tx(x + d, y + d * 0.35));
        }

        private void DrawDimText(
            DrawingContext dc,
            Typeface typeface,
            Brush brush,
            double fontModel,
            Point center,
            string text)
        {
            var ft = new FormattedText(
                text,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                fontModel,
                brush,
                1.0);
            DrawUprightText(dc, ft, center);
        }

        private static RebarLayer DominantLayer(PlateReinforcement r)
        {
            double m = r.As1;
            var layer = RebarLayer.As1;
            if (r.As2 > m) { m = r.As2; layer = RebarLayer.As2; }
            if (r.As3 > m) { m = r.As3; layer = RebarLayer.As3; }
            if (r.As4 > m) { layer = RebarLayer.As4; }
            return layer;
        }

        private static Brush LayerBrush(RebarLayer layer, byte alpha)
        {
            Color c = layer switch
            {
                RebarLayer.As1 => Color.FromRgb(37, 99, 235),
                RebarLayer.As2 => Color.FromRgb(5, 150, 105),
                RebarLayer.As3 => Color.FromRgb(217, 119, 6),
                RebarLayer.As4 => Color.FromRgb(220, 38, 38),
                _ => Colors.Gray
            };
            var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
            b.Freeze();
            return b;
        }

        /// <summary>
        /// Уникальные отличимые цвета ступеней (без повторов). До 26 ступеней + запас.
        /// </summary>
        private static readonly Color[] DistinctPalette = BuildDistinctPalette(26);

        private static Color[] BuildDistinctPalette(int count)
        {
            // Явно заданные хорошо различимые цвета + равномерный HSL для остатка
            var baseColors = new[]
            {
                Color.FromRgb(37, 99, 235),   // синий
                Color.FromRgb(5, 150, 105),   // зелёный
                Color.FromRgb(217, 119, 6),   // оранжевый
                Color.FromRgb(220, 38, 38),   // красный
                Color.FromRgb(124, 58, 237),  // фиолетовый
                Color.FromRgb(8, 145, 178),   // циан
                Color.FromRgb(202, 138, 4),   // жёлто-янтарный
                Color.FromRgb(219, 39, 119),  // розовый
                Color.FromRgb(22, 163, 74),   // ярко-зелёный
                Color.FromRgb(79, 70, 229),   // индиго
                Color.FromRgb(234, 88, 12),   // глубокий оранж
                Color.FromRgb(13, 148, 136),  // teal
                Color.FromRgb(185, 28, 28),   // тёмно-красный
                Color.FromRgb(67, 56, 202),   // сине-фиолет
                Color.FromRgb(161, 98, 7),    // коричнево-жёлтый
                Color.FromRgb(190, 24, 93),   // маджента
                Color.FromRgb(21, 128, 61),   // лесной
                Color.FromRgb(29, 78, 216),   // ярко-синий
                Color.FromRgb(180, 83, 9),    // охра
                Color.FromRgb(126, 34, 206),  // пурпур
                Color.FromRgb(15, 118, 110),  // тёмный teal
                Color.FromRgb(153, 27, 27),   // бордо
                Color.FromRgb(30, 64, 175),   // navy
                Color.FromRgb(146, 64, 14),   // коричневый
                Color.FromRgb(157, 23, 77),   // вишня
                Color.FromRgb(20, 83, 45),    // тёмно-зелёный
            };

            var list = new List<Color>(count);
            for (int i = 0; i < count; i++)
            {
                if (i < baseColors.Length)
                    list.Add(baseColors[i]);
                else
                {
                    // дополнительные через HSL — сдвиг по оттенку, высокая насыщенность
                    double h = (i * 137.508) % 360.0; // золотой угол → без близких соседей
                    list.Add(HslToRgb(h, 0.72, 0.42));
                }
            }
            return list.ToArray();
        }

        private static Color HslToRgb(double h, double s, double l)
        {
            h = (h % 360 + 360) % 360;
            double c = (1 - Math.Abs(2 * l - 1)) * s;
            double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
            double m = l - c / 2;
            double r1, g1, b1;
            if (h < 60) { r1 = c; g1 = x; b1 = 0; }
            else if (h < 120) { r1 = x; g1 = c; b1 = 0; }
            else if (h < 180) { r1 = 0; g1 = c; b1 = x; }
            else if (h < 240) { r1 = 0; g1 = x; b1 = c; }
            else if (h < 300) { r1 = x; g1 = 0; b1 = c; }
            else { r1 = c; g1 = 0; b1 = x; }
            return Color.FromRgb(
                (byte)Math.Round((r1 + m) * 255),
                (byte)Math.Round((g1 + m) * 255),
                (byte)Math.Round((b1 + m) * 255));
        }

        private static Brush DistinctBandBrush(int band, byte alpha)
        {
            if (band < 0) band = 0;
            if (band >= DistinctPalette.Length)
                band = DistinctPalette.Length - 1; // без зацикливания — последний цвет
            var c = DistinctPalette[band];
            var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
            b.Freeze();
            return b;
        }

        private static Brush ZoneStroke(AdditionalZone z)
        {
            return LayerBrush(z.Layer, 255);
        }

        private static Pen FreezePen(Color c, double t, bool dash = false)
        {
            var p = new Pen(new SolidColorBrush(c), t);
            if (dash) p.DashStyle = DashStyles.Dash;
            p.Freeze();
            return p;
        }

        private static bool PointInPoly(Point3 p, Point3[] poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                if (((poly[i].Y > p.Y) != (poly[j].Y > p.Y)) &&
                    (p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y + 1e-12) + poly[i].X))
                    inside = !inside;
            }
            return inside;
        }

        private void RaiseStatus(string? msg = null)
        {
            StatusChanged?.Invoke(msg ?? $"зум {_zoom * 100:0}%");
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using StructuralTools.Models;

namespace StructuralTools.UI
{
    public enum WallLoadDialogResult { Cancel, PickWalls, PickHost, Generate }

    public class WallLoadDialog : Window
    {
        public WallLoadDialogResult Result { get; private set; } = WallLoadDialogResult.Cancel;
        public bool ApplyFudge { get; private set; }
        public string FudgePctText { get; private set; }

        private readonly CheckBox _chkFudge;
        private readonly System.Windows.Controls.TextBox _txtFudge;

        public WallLoadDialog(
            List<WallEntry> walls,
            Element host,
            (Autodesk.Revit.DB.Structure.LoadCase lc, bool matched) lcInfo,
            List<Autodesk.Revit.DB.Structure.LoadCase> allCases,
            string lastStatus,
            bool applyFudge,
            string fudgePctText)
        {
            ApplyFudge = applyFudge;
            FudgePctText = fudgePctText;

            Title = "Wall → Line Load Generator";
            Width = 400;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Brush(245, 247, 250);
            ResizeMode = ResizeMode.NoResize;

            var root = new StackPanel { Margin = new Thickness(16, 16, 16, 12) };
            Content = root;

            // Header
            root.Children.Add(new TextBlock
            {
                Text = "Wall  →  Line Load Generator",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = Brush(30, 40, 60),
                Margin = new Thickness(0, 0, 0, 4)
            });
            root.Children.Add(new TextBlock
            {
                Text = "Select walls — click or box-select, host model or linked — and a host analytical element, then generate.",
                FontSize = 11,
                Foreground = Brush(120, 130, 150),
                Margin = new Thickness(0, 0, 0, 8),
                TextWrapping = TextWrapping.Wrap
            });

            // STEP 1: Walls
            var (sec1, sp1) = MakeSection("STEP 1  —  Walls  (host model or linked models)");
            string wallsTxt;
            if (walls.Count > 0)
            {
                int linked = walls.Count(w => w.Source != null);
                int hostN = walls.Count - linked;
                wallsTxt = linked > 0
                    ? $"{walls.Count} wall(s) selected  ({hostN} host, {linked} linked)"
                    : $"{walls.Count} wall(s) selected";
            }
            else
            {
                wallsTxt = "No walls selected";
            }
            sp1.Children.Add(Lbl(wallsTxt, walls.Count > 0 ? ((int, int, int)?)(40, 140, 70) : (180, 50, 50)));

            var btnPickWalls = Btn("📐  Pick Walls (click or box-select)", bg: (220, 235, 255), bold: true);
            sp1.Children.Add(btnPickWalls);
            root.Children.Add(sec1);

            // STEP 2: Host
            var (sec2, sp2) = MakeSection("STEP 2  —  Host Element  (Beam or Floor / Analytical Panel — current model only)");
            sp2.Children.Add(Lbl(host != null ? HostLabel(host) : "No host element selected",
                host != null ? ((int, int, int)?)(40, 140, 70) : (180, 50, 50)));

            var btnPickHost = Btn("🏗  Pick Host in Revit", bg: (220, 235, 255), bold: true);
            sp2.Children.Add(btnPickHost);
            root.Children.Add(sec2);

            // Load Case info
            var (sec3, sp3) = MakeSection("Load Case");
            if (lcInfo.lc != null)
            {
                string lcText = ElemLabel(lcInfo.lc);
                var lcColor = lcInfo.matched ? (40, 140, 70) : (200, 140, 20);
                if (!lcInfo.matched)
                    lcText += "  (⚠ auto-picked — no case named 'Dead'/'DL' found, please verify)";
                sp3.Children.Add(Lbl(lcText, lcColor));
            }
            else
            {
                sp3.Children.Add(Lbl("⚠ No load cases found in model", (180, 50, 50)));
            }
            root.Children.Add(sec3);

            // Conservatism / fudge factor
            var (sec4, sp4) = MakeSection("Conservatism");
            _chkFudge = new CheckBox
            {
                Content = "Apply a fudge factor (allowance for poor/incomplete modelling)",
                FontSize = 12,
                IsChecked = applyFudge,
                Margin = new Thickness(0, 2, 0, 4)
            };
            sp4.Children.Add(_chkFudge);

            var fudgeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
            fudgeRow.Children.Add(Lbl("Factor:", size: 12));
            _txtFudge = new System.Windows.Controls.TextBox
            {
                Text = fudgePctText,
                Width = 60,
                FontSize = 12,
                Margin = new Thickness(6, 0, 4, 0)
            };
            fudgeRow.Children.Add(_txtFudge);
            fudgeRow.Children.Add(Lbl("%  (positive number)", size: 12));
            sp4.Children.Add(fudgeRow);
            root.Children.Add(sec4);

            // Generate button
            bool canGenerate = walls.Count > 0 && host != null;
            var btnGen = Btn("⚡  Generate Line Loads",
                bg: canGenerate ? (30, 100, 200) : ((int, int, int)?)(180, 185, 195),
                fg: (255, 255, 255), bold: true, enabled: canGenerate);
            btnGen.Height = 42;
            btnGen.FontSize = 14;
            btnGen.Margin = new Thickness(0, 14, 0, 4);
            root.Children.Add(btnGen);

            // Status message
            if (!string.IsNullOrEmpty(lastStatus))
            {
                var statusBorder = new Border
                {
                    Background = Brush(235, 255, 240),
                    BorderBrush = Brush(100, 200, 120),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 6, 0, 0),
                    Child = new TextBlock { Text = lastStatus, FontSize = 12, TextWrapping = TextWrapping.Wrap }
                };
                root.Children.Add(statusBorder);
            }

            // Close button
            var btnClose = Btn("Close", fg: (100, 110, 130));
            btnClose.HorizontalAlignment = HorizontalAlignment.Right;
            btnClose.Margin = new Thickness(0, 10, 0, 0);
            root.Children.Add(btnClose);

            // Event wiring
            _chkFudge.Checked += (s, e) => ApplyFudge = true;
            _chkFudge.Unchecked += (s, e) => ApplyFudge = false;
            _txtFudge.TextChanged += (s, e) => FudgePctText = _txtFudge.Text;

            btnPickWalls.Click += (s, e) => { Result = WallLoadDialogResult.PickWalls; Close(); };
            btnPickHost.Click += (s, e) => { Result = WallLoadDialogResult.PickHost; Close(); };
            btnGen.Click += (s, e) => { Result = WallLoadDialogResult.Generate; Close(); };
            btnClose.Click += (s, e) => { Result = WallLoadDialogResult.Cancel; Close(); };
        }

        private static SolidColorBrush Brush(byte r, byte g, byte b) => new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));

        private static Button Btn(string text, (int r, int g, int b)? bg = null, (int r, int g, int b)? fg = null,
            bool bold = false, bool enabled = true, double width = double.NaN)
        {
            var b = new Button { Content = text, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 4, 0, 4), FontSize = 13, IsEnabled = enabled };
            if (!double.IsNaN(width)) b.Width = width;
            if (bg.HasValue) b.Background = Brush((byte)bg.Value.r, (byte)bg.Value.g, (byte)bg.Value.b);
            if (fg.HasValue) b.Foreground = Brush((byte)fg.Value.r, (byte)fg.Value.g, (byte)fg.Value.b);
            if (bold) b.FontWeight = FontWeights.SemiBold;
            return b;
        }

        private static TextBlock Lbl(string text, (int r, int g, int b)? color = null, int size = 12, bool bold = false)
        {
            var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };
            if (bold) t.FontWeight = FontWeights.SemiBold;
            if (color.HasValue) t.Foreground = Brush((byte)color.Value.r, (byte)color.Value.g, (byte)color.Value.b);
            return t;
        }

        private static (Border section, StackPanel inner) MakeSection(string title)
        {
            var border = new Border
            {
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(210, 215, 225)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 6, 0, 0),
                Padding = new Thickness(12, 10, 12, 10)
            };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = title, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 110, 130)), Margin = new Thickness(0, 0, 0, 6) });
            border.Child = sp;
            return (border, sp);
        }

        private static string ElemLabel(Element elem)
        {
            if (elem == null) return "—";
            try { return $"{elem.Name} (ID {elem.Id})"; }
            catch { return $"{elem.GetType().Name} (ID {elem.Id})"; }
        }

        private static string HostLabel(Element elem)
        {
            if (elem == null) return "—";
            string kind;
            if (elem is Floor) kind = "Floor/Panel";
            else if (elem.Category != null && elem.Category.Id == new ElementId(BuiltInCategory.OST_StructuralFraming))
                kind = "Beam";
            else kind = "Element";
            return $"{kind} · {ElemLabel(elem)}";
        }
    }
}

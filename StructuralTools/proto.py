"""
Flag Hanging Line Loads (Diagnostic Mode)
=========================================
Dynamo Python Script node (Revit API).
"""

import math
import clr
clr.AddReference('RevitAPI')
clr.AddReference('RevitServices')

from Autodesk.Revit.DB import (
    FilteredElementCollector, XYZ, Color, 
    OverrideGraphicSettings, UnitUtils, UnitTypeId, View
)
from Autodesk.Revit.DB.Structure import LineLoad, AnalyticalPanel, AnalyticalMember

from RevitServices.Persistence import DocumentManager
from RevitServices.Transactions import TransactionManager

doc = DocumentManager.Instance.CurrentDBDocument

# ---- Tunables & Constants ------------------------------------------------
TOL_MM = 50.0  # Tolerance in mm for intersection checks


def mm_to_ft(mm_value):
    return UnitUtils.ConvertToInternalUnits(mm_value, UnitTypeId.Millimeters)


def get_all_warned_line_loads(document, candidate_ids):
    """Finds ALL warnings associated with Line Loads, returning their IDs and the warning texts."""
    flagged = set()
    texts = set()
    for w in document.GetWarnings():
        try:
            text = w.GetDescriptionText()
            failing_ids = w.GetFailingElements()
            for eid in failing_ids:
                if eid in candidate_ids:
                    flagged.add(eid)
                    if text:
                        texts.add(text)
        except Exception:
            continue
    return flagged, texts


def is_point_in_poly(pt, poly):
    """2D Point-in-Polygon Ray Casting Algorithm"""
    x, y = pt.X, pt.Y
    inside = False
    n = len(poly)
    if n < 3: return False
    j = n - 1
    for i in range(n):
        xi, yi = poly[i].X, poly[i].Y
        xj, yj = poly[j].X, poly[j].Y
        if ((yi > y) != (yj > y)) and (x < (xj - xi) * (y - yi) / (yj - yi) + xi):
            inside = not inside
        j = i
    return inside


def get_poly_pts(contour):
    pts = []
    for c in contour:
        ts = c.Tessellate()
        for i in range(len(ts)):
            pts.append(ts[i])
    return pts


def gather_analytical_supports(document):
    supports = []
    
    panels = FilteredElementCollector(document).OfClass(AnalyticalPanel).ToElements()
    for panel in panels:
        try:
            contour = panel.GetOuterContour()
            if contour and contour.Count > 2:
                poly_pts = get_poly_pts(contour)
                if len(poly_pts) >= 3:
                    min_x = min(p.X for p in poly_pts)
                    max_x = max(p.X for p in poly_pts)
                    min_y = min(p.Y for p in poly_pts)
                    max_y = max(p.Y for p in poly_pts)
                    supports.append({
                        'type': 'panel',
                        'pts': poly_pts,
                        'bbox': (min_x, min_y, max_x, max_y)
                    })
                    
            openings = panel.GetAnalyticalOpenings()
            if openings:
                for op in openings:
                    op_contour = op.GetOuterContour()
                    if op_contour and op_contour.Count > 2:
                        op_pts = get_poly_pts(op_contour)
                        if len(op_pts) >= 3:
                            min_x = min(p.X for p in op_pts)
                            max_x = max(p.X for p in op_pts)
                            min_y = min(p.Y for p in op_pts)
                            max_y = max(p.Y for p in op_pts)
                            supports.append({
                                'type': 'opening',
                                'pts': op_pts,
                                'bbox': (min_x, min_y, max_x, max_y)
                            })
        except Exception:
            continue
            
    members = FilteredElementCollector(document).OfClass(AnalyticalMember).ToElements()
    for m in members:
        try:
            curve = m.GetCurve()
            if curve:
                bbox = curve.GetBoundingBox()
                supports.append({
                    'type': 'member',
                    'curve': curve,
                    'bbox': (bbox.Min.X, bbox.Min.Y, bbox.Max.X, bbox.Max.Y)
                })
        except Exception:
            continue
            
    return supports


def point_is_supported(pt, supports, tol_ft):
    x, y = pt.X, pt.Y
    
    is_inside_panel = False
    is_inside_opening = False
    is_on_member = False
    
    for s in supports:
        bx_min, by_min, bx_max, by_max = s['bbox']
        if x < bx_min - tol_ft or x > bx_max + tol_ft or y < by_min - tol_ft or y > by_max + tol_ft:
            continue
            
        if s['type'] == 'panel':
            if is_point_in_poly(pt, s['pts']):
                is_inside_panel = True
                
        elif s['type'] == 'opening':
            if is_point_in_poly(pt, s['pts']):
                is_inside_opening = True
                
        elif s['type'] == 'member':
            try:
                result = s['curve'].Project(pt)
                if result:
                    p_proj = result.XYZPoint
                    dx = pt.X - p_proj.X
                    dy = pt.Y - p_proj.Y
                    if math.sqrt(dx*dx + dy*dy) < tol_ft:
                        is_on_member = True
            except:
                pass

    if is_inside_opening:
        return False
        
    return is_inside_panel or is_on_member


# ---- Gather Inputs ------------------------------------------------------
line_loads_in = IN[0] if len(IN) > 0 else None
view_in = IN[2] if len(IN) > 2 else None
run_point_check = (len(IN) > 3 and IN[3] is not None and bool(IN[3]))

if not line_loads_in:
    all_line_loads = list(FilteredElementCollector(doc).OfClass(LineLoad).WhereElementIsNotElementType().ToElements())
else:
    if not isinstance(line_loads_in, list): line_loads_in = [line_loads_in]
    all_line_loads = [UnwrapElement(x) for x in line_loads_in if isinstance(UnwrapElement(x), LineLoad)]

target_view = UnwrapElement(view_in) if view_in else doc.ActiveView
if target_view is None or not isinstance(target_view, View):
    raise ValueError("No target view: pass one in IN[2] or open a plan/3D view.")

# ---- Execution ----------------------------------------------------------
line_load_id_set = set(ll.Id for ll in all_line_loads)
warned_ids, warning_texts = get_all_warned_line_loads(doc, line_load_id_set)

hanging = []
supported = []
reasons = {}

# DIAGNOSTIC MODE: Temporarily force all warned loads to turn red to verify pipeline
for ll in all_line_loads:
    if ll.Id in warned_ids:
        hanging.append(ll)
        reasons[str(ll.Id)] = "Has Warning"
    else:
        supported.append(ll)
        reasons[str(ll.Id)] = "No Warning"

# Apply Overrides
red = Color(255, 0, 0)
ogs_red = OverrideGraphicSettings().SetProjectionLineColor(red)
ogs_clear = OverrideGraphicSettings()

TransactionManager.Instance.EnsureInTransaction(doc)
try:
    for ll in hanging:
        target_view.SetElementOverrides(ll.Id, ogs_red)
    for ll in supported:
        target_view.SetElementOverrides(ll.Id, ogs_clear)
finally:
    TransactionManager.Instance.TransactionTaskDone()

OUT = (hanging, supported, reasons, list(warning_texts))
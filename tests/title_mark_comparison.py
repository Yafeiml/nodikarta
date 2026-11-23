"""Pixel comparison for the two live title marks against a direct, single SVG rasterization."""
import hashlib
import json
import os
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFont, ImageStat


PAGES = {'overview': ('设备概览', 24), 'access': ('智能体接入', 23)}
GREEN = (23, 95, 78)
SVG = 'http://www.w3.org/2000/svg'


def screenshot(folder, page, info):
    frame = Image.open(folder / f'{page}.png').convert('RGB')
    mark = Image.open(folder / f'{page}-mark.png').convert('RGB')
    entry = next(row for row in info['logos'] if row['file'] == f'{page}-mark.png')
    if 'leftPx' in entry:
        xy = (entry['leftPx'], entry['topPx'])
    else:
        # Earlier captures predate the stored position. Locate their byte-identical ROI.
        anchor = next((x,y) for y in range(mark.height) for x in range(mark.width)
                      if mark.getpixel((x,y)) == GREEN)
        positions = [(x,y) for y in range(70) for x in range(100)
                     if frame.getpixel((x+anchor[0],y+anchor[1])) == GREEN
                     and frame.crop((x,y,x+mark.width,y+mark.height)).tobytes() == mark.tobytes()]
        assert len(positions) == 1, 'Archived control does not have a unique location'
        xy = positions[0]
    x, y = xy
    assert frame.crop((x,y,x+mark.width,y+mark.height)).tobytes() == mark.tobytes()
    assert mark.width == mark.height
    return frame, mark, xy


def reference_tiles(path, dimensions, render_atlas):
    ET.register_namespace('', SVG)
    entries = []
    shifts = [n/4 for n in range(-3,4)]
    for n, nominal, background in sorted(dimensions):
        # Controls may occupy a rounded number of pixels at fractional Windows DPI.
        # Test both the declared physical extent and the rounded extent for BOTH releases.
        for width in sorted({float(n), nominal}):
            for dx in shifts:
                for dy in shifts:
                    outer = ET.Element(f'{{{SVG}}}svg', viewBox=f'0 0 {n} {n}', width=str(n), height=str(n))
                    ET.SubElement(outer, f'{{{SVG}}}rect', width='100%', height='100%',
                                  fill='#%02x%02x%02x' % background)
                    original = ET.parse(path).getroot()
                    original.attrib.update(x=str((n-width)/2+dx), y=str((n-width)/2+dy),
                                           width=str(width), height=str(width))
                    original[-1].set('fill', '#175F4E')
                    outer.append(original)
                    entries.append(((n,nominal,background,width,dx,dy),n,outer))
    with tempfile.TemporaryDirectory(prefix='nodicarta-title-vectors-',ignore_cleanup_errors=True) as tmp:
        return render_atlas(Path(tmp),entries)


def mean_difference(left, right):
    return ImageStat.Stat(ImageChops.difference(left,right)).mean[0]/255


def metrics(mark, nominal, references, captured_mask):
    bg = mark.getpixel((0,0))
    corners = [(0,0), (0,mark.height-1), (mark.width-1,0), (mark.width-1,mark.height-1)]
    assert all(mark.getpixel(pt) == bg for pt in corners)
    mask = captured_mask(mark,GREEN,bg)
    best = None
    for (n,dip,b,width,dx,dy),ref in references.items():
        if (n,dip,b) != (mark.width,nominal,bg):
            continue
        reference = captured_mask(ref,GREEN,bg)
        error = mean_difference(mask,reference)
        if width == n and dx == dy == 0:
            aligned_error = error
        if best is None or error < best[0]:
            best = (error, width, dx, dy, reference)
    error, width, dx, dy, reference = best
    samples = list(mask.tobytes())
    ideal = list(reference.tobytes())
    solid = next((count for count,rgb in mark.getcolors(mark.width**2) if rgb == GREEN),0)
    bg_leak = [abs(a-b)/255 for a,b in zip(samples,ideal) if b <= 4]
    edge = [abs(a-b)/255 for a,b in zip(samples,ideal) if 4 < b < 251]
    transitions = sum(4*v/255*(1-v/255) for v in samples)
    target_transitions = sum(4*v/255*(1-v/255) for v in ideal)
    assert solid > mark.width**2/8, 'Expected dark green fill is missing'
    return {'sizePx':mark.width,'background':'#%02X%02X%02X'%bg,'foreground':'#175F4E',
            'greenPixels':solid,
            'gridAlignedMAE':round(aligned_error,6),'bestPhaseMAE':round(error,6),
            'matchedVectorWidthPx':width,'bestShiftPx':[dx,dy],
            'bestPhaseEdgeMAE':round(sum(edge)/len(edge),6),
            'transparentRegionMAE':round(sum(bg_leak)/len(bg_leak),6),
            'mixedCoveragePx':round(transitions,3),'referenceMixedCoveragePx':round(target_transitions,3)}


def comparison_image(shots, output):
    canvas = Image.new('RGB',(738,505),'white')
    draw = ImageDraw.Draw(canvas)
    fontfile = str(Path(os.environ['WINDIR'])/'Fonts/msyh.ttc')
    normal = ImageFont.truetype(fontfile,16)
    small = ImageFont.truetype(fontfile,13)
    for col, version in enumerate(shots):
        x = 16 + col*365
        draw.text((x,8), version, font=normal, fill='#223345')
        for i,(page,(label,_)) in enumerate(PAGES.items()):
            y = 43 + i*226
            frame, mark, (px,py) = shots[version][page]
            draw.text((x,y),f'{label} · 1:1',font=small,fill='#45616F')
            canvas.paste(frame.crop((0,0,340,min(py+mark.height+7,65))), (x,y+21))
            magnification = 4
            enlarged = mark.resize((mark.width*magnification,mark.height*magnification),Image.Resampling.NEAREST)
            canvas.paste(enlarged,(x,y+79))
            draw.text((x+enlarged.width+13,y+85),'4× 最近邻',font=small,fill='#45616F')
    canvas.save(output)


def compare(new, old, svg_path, render_atlas, captured_mask):
    folders = {'1.3.3':old.resolve(),'1.3.4':new.resolve()}
    shots, dimensions, measurements = {},set(),{}
    dpi = None
    for version,folder in folders.items():
        info = json.loads((folder/'capture.json').read_text(encoding='utf-8-sig'))
        if dpi is None: dpi = info['windowDpi']
        assert dpi == info['windowDpi'], 'Windows DPI must be identical for this comparison'
        shots[version] = {}
        for page,(_,dip) in PAGES.items():
            shot = screenshot(folder,page,info)
            shots[version][page] = shot
            mark = shot[1]
            dimensions.add((mark.width,dip*dpi/96,mark.getpixel((0,0))))
    references = reference_tiles(svg_path,dimensions,render_atlas)
    for version, pages in shots.items():
        measurements[version] = {}
        for page,(frame,mark,(x,y)) in pages.items():
            m = metrics(mark,PAGES[page][1]*dpi/96,references,captured_mask)
            m['controlRectPx'] = [x,y,mark.width,mark.height]
            m['windowSizePx'] = list(frame.size)
            measurements[version][page] = m
    comparison_image(shots,new/'title-comparison.png')
    report = {'windowDpi':dpi,'sourceSvgSha256':hashlib.sha256(svg_path.read_bytes()).hexdigest(),
              'measurements':measurements}
    (new/'title-metrics.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    for page in PAGES:
        before = measurements['1.3.3'][page]
        after = measurements['1.3.4'][page]
        assert before['windowSizePx'] == after['windowSizePx'], f'{page} windows were captured at different sizes'
        assert before['controlRectPx'] == after['controlRectPx'], f'{page} title mark position or size changed'
        assert before['background'] == after['background'], f'{page} title bar background changed'
        assert after['bestPhaseMAE'] < before['bestPhaseMAE'], f'{page} is no closer to the vector reference'
        assert after['bestPhaseEdgeMAE'] < before['bestPhaseEdgeMAE'], f'{page} edge coverage did not improve'
        print(f"{page}: corrected coverage error {before['bestPhaseMAE']:.5f} -> {after['bestPhaseMAE']:.5f}")
    return report

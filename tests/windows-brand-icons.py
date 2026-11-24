"""python windows-brand-icons.py <packaged desktop.exe> <evidence directory>
Run on Windows with Pillow. windows-access-fixture.cjs --brand supplies captured runtime PNGs.
"""
import base64
import ctypes as ct
import hashlib
import io
import json
import os
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
from PIL import Image, ImageChops, ImageStat, IcoImagePlugin

repo = Path(__file__).resolve().parents[1]
bin_dir = Path(sys.argv[1]).resolve().parent
proof = Path(sys.argv[2]).resolve()
proof.mkdir(parents=True, exist_ok=True)
assets = repo / 'src/ProjectHub.Edge.Desktop/Assets'
brand_dir = repo / 'docs/design/brand'
sources = {'standard': brand_dir / 'nodicarta.svg', 'micro': brand_dir / 'nodicarta-micro.svg'}
sizes = [16,20,24,32,40,48,64,96,128,256]
svg_files = {'standard': 'edge.svg', 'micro': 'edge-micro.svg'}

def digest(data):
    return hashlib.sha256(data).hexdigest()

def signature(node):
    return node.tag, sorted(node.attrib.items()), tuple(signature(child) for child in node)

def render_atlas(folder, entries):
    """Rasterize independent SVG documents directly into integer-aligned browser tiles."""
    positions = {}
    markup=[]
    x=y=row=0
    for key,n,svg in entries:
        if x+n>1600: x=0; y+=row; row=0
        positions[key] = (x,y,x+n,y+n)
        data=base64.b64encode(ET.tostring(svg,encoding='utf-8')).decode()
        markup.append(f'<img style="position:absolute;left:{x}px;top:{y}px;width:{n}px;height:{n}px" src="data:image/svg+xml;base64,{data}">')
        x+=n;row=max(row,n)
    page=folder/'reference.html'
    page.write_text('<!DOCTYPE html><body style="margin:0;background:white">'+''.join(markup),encoding='utf-8')
    png=folder/'reference.png'
    browser=Path(os.environ['ProgramFiles(x86)'])/'Microsoft/Edge/Application/msedge.exe'
    args=[str(browser),'--headless=new','--disable-gpu','--hide-scrollbars','--disable-extensions','--no-first-run',
        '--disable-background-networking','--force-device-scale-factor=1',f'--user-data-dir={folder/"browser"}',
        f'--window-size=1640,{max(y+row+100,800)}','--virtual-time-budget=1500',f'--screenshot={png}',page.as_uri()]
    subprocess.run(args,creationflags=subprocess.CREATE_NO_WINDOW,check=True,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,timeout=45)
    with Image.open(png) as whole:
        assert all(box[3] <= whole.height for box in positions.values()), 'reference atlas truncated'
        return {key:whole.crop(box).convert('RGB') for key,box in positions.items()}

def reference_images(folder):
    ET.register_namespace('', 'http://www.w3.org/2000/svg')
    frames = sorted(set(sizes + list(range(16,97))))
    entries = []
    for kind,path in sources.items():
        for n in frames:
            svg=ET.parse(path).getroot()
            svg.set('width',str(n));svg.set('height',str(n))
            entries.append(((kind,n),n,svg))
    # White background; original paths are #111111.
    return {key:tile.convert('L').point(lambda p: min(255,round((255-p)*255/238)))
            for key,tile in render_atlas(folder,entries).items()}

def pe_group(exe):
    api=ct.WinDLL('kernel32',use_last_error=True); P=ct.c_void_p
    def signature(name,params,ret):
        f=getattr(api,name);f.argtypes=params;f.restype=ret;return f
    load=signature('LoadLibraryExW',[ct.c_wchar_p,P,ct.c_uint],P)
    find=signature('FindResourceW',[P,P,P],P)
    size=signature('SizeofResource',[P,P],ct.c_uint)
    resource=signature('LoadResource',[P,P],P)
    lock=signature('LockResource',[P],P)
    release=signature('FreeLibrary',[P],ct.c_int)
    callback=ct.WINFUNCTYPE(ct.c_int,P,P,P,ct.c_ssize_t)
    names=signature('EnumResourceNamesW',[P,P,callback,ct.c_ssize_t],ct.c_int)
    h=load(str(exe),None,0x02|0x20)
    if not h: raise ct.WinError(ct.get_last_error())
    def read(kind,name):
        found=find(h,name,kind)
        if not found: raise ct.WinError(ct.get_last_error())
        return ct.string_at(lock(resource(h,found)),size(h,found))
    found=[]
    try:
        @callback
        def collect(_h,_kind,name,_extra):
            found.append(read(14,name));return True
        assert names(h,14,collect,0) and len(found)==1, 'expected one PE application icon group'
        group=found[0]
        images={}
        for index in range(struct.unpack_from('<H',group,4)[0]):
            w,hgt,_,_,_,_,byte_length,number=struct.unpack_from('<BBBBHHIH',group,6+14*index)
            data=read(3,number); assert len(data)==byte_length and w==hgt
            images[w or 256]=data
        return images
    finally: release(h)

def ico_contents(data):
    assert struct.unpack_from('<HH',data)==(0,1)
    count=struct.unpack_from('<H',data,4)[0]; result={}
    for i in range(count):
        w,h,_,_,_,_,n,offset=struct.unpack_from('<BBBBHHII',data,6+16*i)
        assert w==h
        result[w or 256]=data[offset:offset+n]
    return result

def native_icon_alpha(path, pixels):
    """Ask Windows to pick and rasterize the supplied ICO at the requested DPI size."""
    user=ct.WinDLL('user32',use_last_error=True)
    gdi=ct.WinDLL('gdi32',use_last_error=True)
    ptr=ct.c_void_p
    class IconInfo(ct.Structure):
        _fields_=[('icon',ct.c_int),('x',ct.c_uint),('y',ct.c_uint),('mask',ptr),('colour',ptr)]
    user.LoadImageW.argtypes=[ptr,ct.c_wchar_p,ct.c_uint,ct.c_int,ct.c_int,ct.c_uint]
    user.LoadImageW.restype=ptr
    user.GetIconInfo.argtypes=[ptr,ct.POINTER(IconInfo)]
    user.DestroyIcon.argtypes=[ptr]
    gdi.CreateCompatibleDC.argtypes=[ptr]
    gdi.CreateCompatibleDC.restype=ptr
    gdi.GetDIBits.argtypes=[ptr,ptr,ct.c_uint,ct.c_uint,ptr,ptr,ct.c_uint]
    gdi.DeleteDC.argtypes=[ptr]
    gdi.DeleteObject.argtypes=[ptr]
    handle=user.LoadImageW(None,str(path),1,pixels,pixels,0x10)
    info=IconInfo()
    dc=gdi.CreateCompatibleDC(None)
    try:
        assert handle and dc and user.GetIconInfo(handle,ct.byref(info)), 'Windows could not load icon'
        header=ct.create_string_buffer(struct.pack('<IiiHHIIiiII',40,pixels,-pixels,1,32,0,0,0,0,0,0))
        rgba=ct.create_string_buffer(4*pixels*pixels)
        assert gdi.GetDIBits(dc,info.colour,0,pixels,rgba,header,0)==pixels
        return Image.frombytes('L',(pixels,pixels),rgba.raw[3::4])
    finally:
        if info.colour: gdi.DeleteObject(info.colour)
        if info.mask: gdi.DeleteObject(info.mask)
        if dc: gdi.DeleteDC(dc)
        if handle: user.DestroyIcon(handle)

def distance(left,right):
    return ImageStat.Stat(ImageChops.difference(left,right)).mean[0]/255

def score(mask,refs,n,expected):
    other='micro' if expected=='standard' else 'standard'
    good=distance(mask,refs[expected,n]);wrong=distance(mask,refs[other,n])
    assert good<0.065 and good<wrong, f'{n}px expected {expected}: MAE {good:.4f}, other {wrong:.4f}'
    return {'sizePx':n,'shape':expected,'mae':round(good,5),'otherMarkMae':round(wrong,5)}

def closest_colour(rgba):
    flat=rgba.convert('RGBA')
    opaque=[a[:3] for a in pixels(flat) if a[3]>=250]
    return tuple(sorted(p[i] for p in opaque)[len(opaque)//2] for i in range(3))

def hex_colour(rgb):
    return '#%02X%02X%02X'%tuple(rgb)

def pixels(image):
    return image.get_flattened_data() if hasattr(image,'get_flattened_data') else image.getdata()

def captured_mask(image,fg,bg):
    # Project anti-aliased RGB onto the known foreground/background line.
    vector=[f-b for f,b in zip(fg,bg)];den=sum(x*x for x in vector)
    mask=Image.new('L',image.size)
    mask.putdata([max(0,min(255,round(255*sum((c-b)*v for c,b,v in zip(px,bg,vector))/den))) for px in pixels(image.convert('RGB'))])
    return mask

def run():
    checks={'svg':{},'ico':{},'pe':{},'runtime':{}}
    dll=(bin_dir/'ProjectHub.Edge.Desktop.dll').read_bytes();checks['desktopDllSha256']=digest(dll)
    for kind,name in svg_files.items():
        original=ET.parse(sources[kind]).getroot()
        root=ET.parse(assets/name).getroot()
        assert original[-1].get('fill').upper()=='#111111' and root[-1].get('fill').upper()=='#175F4E'
        original[-1].set('fill',root[-1].get('fill'))
        assert signature(original)==signature(root), f'SVG geometry mismatch: {name}'
        raw=(assets/name).read_bytes();assert raw in dll, f'{name} missing from packaged DLL'
        checks['svg'][kind]={'sourceSha256':digest(sources[kind].read_bytes()),'packagedSha256':digest(raw)}
    with tempfile.TemporaryDirectory(prefix='nodicarta-icon-reference-',ignore_cleanup_errors=True) as tmp:
        refs=reference_images(Path(tmp))
        for kind, name in [('standard','edge.png'),('micro','edge-micro.png')]:
            # 1.3.3 used embedded bitmaps. Vector releases are checked from actual controls.
            if not (assets/name).exists():
                continue
            raw=(assets/name).read_bytes()
            if raw not in dll:
                continue
            with Image.open(io.BytesIO(raw)) as bitmap:
                assert bitmap.size==(256,256) and closest_colour(bitmap)==(23,95,78)
                checks['svg'][kind]['renderedAsset']=score(bitmap.convert('RGBA').getchannel('A'),refs,256,kind)
        for name,colour in [('edge.ico',(57,136,110)),('edge-tray.ico',(23,95,78)),('edge-tray-dark.ico',(181,229,207))]:
            raw=(assets/name).read_bytes();assert raw in dll, f'{name} missing from packaged DLL'
            frames=ico_contents(raw);assert sorted(frames)==sizes
            decoded=IcoImagePlugin.IcoFile(io.BytesIO(raw))
            result=[]
            for n in sizes:
                kind='micro' if n<=32 or 'tray' in name else 'standard'
                image=decoded.getimage((n,n)).convert('RGBA')
                assert closest_colour(image)==colour,f'{name} at {n}px has unexpected fill'
                result.append(score(image.getchannel('A'),refs,n,kind))
            checks['ico'][name]={'colour':hex_colour(colour),'frames':result}
            if 'tray' in name:
                checks['ico'][name]['windowsLoadImage']=[
                    score(native_icon_alpha(assets/name,n),refs,n,'micro') for n in [16,20,24,32,40,48,64]
                ]
        expected=ico_contents((assets/'edge.ico').read_bytes())
        for name in ['ProjectHub.Edge.Desktop.exe','ProjectHub.Edge.exe']:
            assert pe_group(bin_dir/name)==expected,f'{name} native PE icon mismatch'
            checks['pe'][name]={'sha256':digest((bin_dir/name).read_bytes()),'matchingFrames':sizes}
        path=proof/'capture.json'
        if path.exists():
            captures=json.loads(path.read_text(encoding='utf-8-sig'))
            for entry in captures['logos']:
                with Image.open(proof/entry['file']) as image:
                    img=image.convert('RGB');n=img.width
                    assert img.height==n or abs(img.height-n)==1
                    bg=img.getpixel((0,0));fg=(23,95,78)
                    # Fractional DPI can place an Image at a half-pixel; use a square reference at the nearest physical size.
                    mask=captured_mask(img,fg,bg).resize((n,n))
                    assert sum(p==fg for p in pixels(img))>n*n//10,'expected brand fill absent'
                    checks['runtime'][entry['id']]=score(mask,refs,n,entry['shape'])
                    checks['runtime'][entry['id']]['colour']=hex_colour(fg)
            for size in ([] if captures.get('scope')=='titlesOnly' else ['small','large']):
                with Image.open(proof/f'exe-{size}.png') as image, Image.open(proof/f'link-{size}.png') as link:
                    icon=image.convert('RGBA')
                    n=icon.width
                    assert link.size==icon.size and closest_colour(icon)==(57,136,110)
                    result=score(icon.getchannel('A'),refs,n,'micro' if n<=32 else 'standard')
                    difference=ImageChops.difference(icon,link.convert('RGBA'))
                    # Explorer reserves the lower-left quadrant for the shortcut arrow.
                    for area in [(0,0,n,n//2),(n//2,0,n,n)]:
                        assert not any(difference.crop(area).getextrema()[c][1] for c in range(4)), 'Shortcut image changed outside its arrow'
                    checks['runtime'][f'windowsShellShortcut-{size}']=result
            if captures.get('trayCaptured'):
                with Image.open(proof/'tray-mark.png') as image:
                    image=image.convert('RGB')
                    n=image.width
                    foreground=(23,95,78) if captures['shellThemeLight'] else (181,229,207)
                    assert sum(pixel==foreground for pixel in pixels(image))>n*n//10
                    observed=captured_mask(image,foreground,image.getpixel((0,0)))
                    result=score(observed,refs,n,'micro')
                    checks['runtime']['windowsNotificationArea']={
                        **result,'colour':hex_colour(foreground),'method':captures['trayCaptureMethod'],
                        'dpi':captures['shellDpi']
                    }
    (proof/'icons.json').write_text(json.dumps(checks,indent=2)+'\n',encoding='utf-8')
    print('PASS SVG geometry, colours, 30 ICO frames, both native PE groups and supplied window captures')
if __name__=='__main__':
    if '--compare-titles' in sys.argv:
        from title_mark_comparison import compare
        compare(proof,Path(sys.argv[sys.argv.index('--compare-titles')+1]),sources['micro'],render_atlas,captured_mask)
    else:
        run()
